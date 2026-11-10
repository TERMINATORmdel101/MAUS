using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maus.Core.Engine;
using Maus.Core.Engine.Broker;
using Maus.Core.Fixes;
using Maus.Core.Localization;
using Maus.Core.Modules.M06Visual;
using Maus.Core.Platform;
using Maus.Core.Preferences;
using Maus.Core.Workshop;
using Microsoft.Win32;

namespace Maus.Core.Tests.Engine;

/// <summary>
/// Promesses du canal privé, éprouvées par un vrai canal nommé : bornes, refus, signature des réponses, abandon d'une demande,
/// arrêt du moteur, et transport fidèle de chaque opération d'<see cref="IMausEngine"/>.
/// </summary>
public sealed class EngineChannelTests
{
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string TaskView = "ShowTaskViewButton";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>Journal dont le premier enregistrement attend le feu vert : la correction est alors en cours d'écriture.</summary>
    private sealed class GatedJournal(IJournalStore inner) : IJournalStore, IDisposable
    {
        private int _first;

        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public void Save(JournalSession session)
        {
            if (Interlocked.Exchange(ref _first, 1) == 0)
            {
                Entered.Set();
                Release.Wait(Patience);
            }

            inner.Save(session);
        }

        public JournalSession? Load(string id) => inner.Load(id);

        public IReadOnlyList<JournalSession> List() => inner.List();

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
    }

    /// <summary>Journal qui reste illisible : une erreur de l'opération, et non une coupure du canal.</summary>
    private sealed class UnreadableJournal : IJournalStore
    {
        public void Save(JournalSession session) => throw new JournalUnreadableException("illisible");

        public JournalSession? Load(string id) => throw new JournalUnreadableException("illisible");

        public IReadOnlyList<JournalSession> List() => [];
    }

    /// <summary>Moteur d'essai : chaque opération est confiée à <see cref="Handler"/>, qui note ce qu'il reçoit.</summary>
    public class Scripted : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler!(targetMethod!, args!);
    }

    private static (IMausEngine Engine, Scripted Script) NewScripted()
    {
        var engine = DispatchProxy.Create<IMausEngine, Scripted>();
        return (engine, (Scripted)(object)engine);
    }

    private static object Completed(Type returnType, object? value)
    {
        if (!returnType.IsGenericType)
        {
            return Task.CompletedTask;
        }

        return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(returnType.GetGenericArguments()[0]).Invoke(null, [value])!;
    }

    private static (LocalMausEngine Engine, FakeRegistry Registry) Local(IJournalStore journal)
    {
        var registry = new FakeRegistry().Set(RegistryHive.CurrentUser, Advanced, TaskView, 1);
        var parameters = new FakeSystemParameters { MinimizeAnimation = true };
        parameters.Values[SpiGet.MenuAnimation] = true;
        var audit = TestContext.Create(registry, parameters: parameters, elevated: true);
        var engine = new LocalMausEngine(
            new AuditEngine([new VisualEffectsModule()]),
            () => audit,
            context => TestFixContext.Create(context, registry, parameters, journal, new FakeSystemRestore(), new FakeNotifier(), false),
            new MemoryPreferencesStore(),
            journal);
        return (engine, registry);
    }

    /// <summary>Un vrai serveur et un vrai canal, et de quoi y envoyer des messages bruts, signés ou non.</summary>
    private sealed class Channel : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private long _sequence = 1000;
        private bool _introduced;

        public Channel(IMausEngine engine, TimeSpan? unclaimed = null, TimeSpan? drain = null, TimeSpan? idle = null)
        {
            Server = new EngineServer(engine, Name, Environment.ProcessId, Key.Fingerprint())
            {
                UnclaimedTimeout = unclaimed ?? TimeSpan.FromMinutes(5),
                DrainTimeout = drain ?? TimeSpan.FromSeconds(90),
                IdleTimeout = idle ?? TimeSpan.FromMinutes(15),
            };
            Running = Server.RunAsync(_stop.Token);
            Remote = new RemoteMausEngine(Name, Environment.ProcessId, Key);
        }

        public string Name { get; } = "MAUS-essai-" + Guid.NewGuid().ToString("N");

        public EngineKey Key { get; } = EngineKey.Create();

        public EngineServer Server { get; }

        public Task Running { get; }

        public RemoteMausEngine Remote { get; }

        public Task StopAsync() => _stop.CancelAsync();

        public async Task<NamedPipeClientStream> ConnectAsync()
        {
            var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            return pipe;
        }

        /// <summary>Demande signée, bien formée : rend la réponse finale, dont la signature est vérifiée pour ce numéro.</summary>
        public async Task<EngineResponse?> CallAsync(string op, object? args = null)
        {
            await using var pipe = await ConnectAsync();
            if (!_introduced)
            {
                await EngineProtocol.WriteAsync(pipe, Key.Introduction(), CancellationToken.None);
                Assert.NotNull(await EngineProtocol.ReadAsync(pipe, CancellationToken.None));
                _introduced = true;
            }

            var sequence = Interlocked.Increment(ref _sequence);
            var request = new EngineRequest(op, Texts.Language, args is null ? null : EngineProtocol.ToElement(args), sequence);
            await EngineProtocol.WriteAsync(pipe, Key.SignRequest(JsonSerializer.SerializeToUtf8Bytes(request, EngineProtocol.Json)), CancellationToken.None);
            while (await EngineProtocol.ReadAsync(pipe, CancellationToken.None) is { } signed)
            {
                Assert.True(Key.VerifyResponse(sequence, signed));
                var response = JsonSerializer.Deserialize<EngineResponse>(signed.Body, EngineProtocol.Json)!;
                if (response.Kind != "progress")
                {
                    return response;
                }
            }

            return null;
        }

        /// <summary>Envoie un message et lit la réponse ; <c>null</c> si le moteur ferme sans répondre.</summary>
        public async Task<SignedMessage?> RawAsync(SignedMessage message)
        {
            await using var pipe = await ConnectAsync();
            return await ExchangeAsync(pipe, message);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await Running;
            _stop.Dispose();
        }
    }

    private static async Task<SignedMessage?> ExchangeAsync(Stream pipe, SignedMessage message)
    {
        try
        {
            await EngineProtocol.WriteAsync(pipe, message, CancellationToken.None);
            return await EngineProtocol.ReadAsync(pipe, CancellationToken.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<bool> Finishes(Task task) =>
        await Task.WhenAny(task, Task.Delay(Patience)) == task;

    private static Task<bool> WaitFor(ManualResetEventSlim signal) => Task.Run(() => signal.Wait(Patience));

    private static async Task<bool> Eventually(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + (long)Patience.TotalMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                return false;
            }

            await Task.Delay(20);
        }

        return true;
    }

    private static ApplyOptions NoRestorePoint => new() { CreateRestorePoint = false };

    // ---------- « jamais de force par le canal »

    [Fact]
    public async Task A_value_changed_since_the_fix_is_left_as_it_is_even_when_the_channel_asks_to_force()
    {
        var journal = new InMemoryJournalStore();
        var (local, registry) = Local(journal);
        using var _ = local;
        await using var channel = new Channel(local);

        await channel.Remote.RunAuditAsync(null, CancellationToken.None);
        var applied = await channel.Remote.ApplyAsync(["M06.taskview"], NoRestorePoint, CancellationToken.None);
        registry.Set(RegistryHive.CurrentUser, Advanced, TaskView, 5);

        var reverted = await channel.Remote.RevertAsync(applied.Session!.Id, null, force: true, CancellationToken.None);
        Assert.Contains(reverted.Entries, e => e.Status == RevertStatus.ChangedSince);
        Assert.Equal(5, registry.GetDword(RegistryHive.CurrentUser, Advanced, TaskView));

        // Même une demande bien signée qui réclame « force » n'y change rien : le serveur ne lit pas ce paramètre.
        var forced = await channel.CallAsync("revert", new { sessionId = applied.Session.Id, force = true });
        Assert.Equal("result", forced!.Kind);
        Assert.Equal(5, registry.GetDword(RegistryHive.CurrentUser, Advanced, TaskView));
    }

    // ---------- erreurs de l'opération

    [Fact]
    public async Task An_unreadable_journal_is_reported_as_an_error_and_the_engine_keeps_serving()
    {
        var (local, _) = Local(new UnreadableJournal());
        using var _1 = local;
        await using var channel = new Channel(local);

        var revert = await Assert.ThrowsAsync<EngineException>(() => channel.Remote.RevertAsync("s1", null, false, CancellationToken.None));
        Assert.Contains("illisible", revert.Message, StringComparison.Ordinal);
        var session = await Assert.ThrowsAsync<EngineException>(() => channel.Remote.LoadSessionAsync("s1", CancellationToken.None));
        Assert.Contains("illisible", session.Message, StringComparison.Ordinal);

        Assert.Equal(Environment.ProcessId, (await channel.Remote.PingAsync(CancellationToken.None)).ProcessId);
    }

    // ---------- bornes du document du Store

    private static readonly string[] TaskViewOnly = ["M06.taskview"];
    private static readonly string[] BadIdentifier = ["M06/taskview"];

    public static TheoryData<string, object> RefusedRequests() => new()
    {
        { "inconnue", new { } },
        { null!, new { } },
        { "battery.report", new { } },

        // winget n'est plus lancé par le moteur (0.8.6) : la fenêtre lit elle-même la liste, sans droits administrateur.
        { "software", new { } },
        { "apply", new { changeIds = Enumerable.Range(0, 500).Select(i => "M06.x" + i).Append("M06.taskview").ToArray(), options = new ApplyOptions { CreateRestorePoint = false } } },
        { "apply", new { changeIds = TaskViewOnly, options = new ApplyOptions { CreateRestorePoint = false, RestorePointDescription = new string('a', 201) } } },
        { "apply", new { changeIds = TaskViewOnly, options = new ApplyOptions { CreateRestorePoint = false, RestorePointDescription = "a\nb" } } },
        { "apply", new { changeIds = BadIdentifier, options = new ApplyOptions { CreateRestorePoint = false } } },
        { "apply", new { options = new ApplyOptions { CreateRestorePoint = false } } },
        { "revert", new { sessionId = "s1", changeId = "a/b" } },
        { "revert", new { sessionId = "s1", changeId = 12 } },
        { "tool", new { request = new { kind = "DriverAction", action = 99, instanceId = "x" } } },
        { "tool", new { request = new { kind = "DriverAction", action = "Inexistante", instanceId = "x" } } },
        { "tool", new { request = new { kind = 1 } } },
        { "session", new { sessionId = @"..\..\Windows\win.ini" } },
        { "verify", new { sessionId = "s1", verified = Enumerable.Range(0, 501).Select(_ => new { message = "m" }).ToArray() } },
        { "verify", new { sessionId = "s1", verified = new[] { new { message = new string('m', 2001) } } } },
        { "prefs.save", new { preferences = UserPreferences.Default with { GameBarProfile = 9 } } },
    };

    [Theory]
    [MemberData(nameof(RefusedRequests))]
    public async Task A_signed_request_outside_the_bounds_gets_a_signed_error_and_changes_nothing(string op, object args)
    {
        var journal = new InMemoryJournalStore();
        var (local, registry) = Local(journal);
        using var _ = local;
        await using var channel = new Channel(local);
        await channel.Remote.RunAuditAsync(null, CancellationToken.None);

        // Plusieurs fois : une erreur n'est pas un refus de connexion, elle n'arrête pas le moteur.
        for (var attempt = 0; attempt <= EngineServer.MaxRefusals; attempt++)
        {
            var response = await channel.CallAsync(op, args);
            Assert.Equal("error", response!.Kind);
            Assert.False(string.IsNullOrEmpty(response.Error));
        }

        Assert.False(channel.Server.StoppedForSecurity);
        Assert.Equal(1, registry.GetDword(RegistryHive.CurrentUser, Advanced, TaskView));
        Assert.Equal(0, journal.SaveCount);
        Assert.Equal(Environment.ProcessId, (await channel.Remote.PingAsync(CancellationToken.None)).ProcessId);
    }

    [Fact]
    public async Task A_second_key_presentation_on_a_connection_is_closed_and_counts_as_a_refusal()
    {
        var (local, _) = Local(new InMemoryJournalStore());
        using var _1 = local;
        await using var channel = new Channel(local);

        for (var attempt = 1; attempt <= EngineServer.MaxRefusals; attempt++)
        {
            Assert.False(channel.Server.StoppedForSecurity);
            await using var pipe = await channel.ConnectAsync();
            Assert.NotNull(await ExchangeAsync(pipe, channel.Key.Introduction()));
            Assert.Null(await ExchangeAsync(pipe, channel.Key.Introduction()));
        }

        Assert.True(await Finishes(channel.Running));
        Assert.True(channel.Server.StoppedForSecurity);
    }

    /// <summary>Le corps n'est jamais analysé : ce qui n'est pas signé compte comme un refus, qu'il soit du JSON ou non.</summary>
    [Fact]
    public async Task An_unsigned_body_is_refused_without_being_read_and_a_short_signature_too()
    {
        var (local, _) = Local(new InMemoryJournalStore());
        using var _1 = local;
        await using var channel = new Channel(local);
        Assert.NotNull(await channel.RawAsync(channel.Key.Introduction()));

        Assert.Null(await channel.RawAsync(new SignedMessage(Encoding.UTF8.GetBytes("ce n'est pas du JSON"), new byte[EngineKey.MacSize])));
        Assert.False(channel.Server.StoppedForSecurity);
        Assert.Null(await channel.RawAsync(new SignedMessage([], [1, 2, 3])));
        Assert.Null(await channel.RawAsync(new SignedMessage([], [])));

        Assert.True(await Finishes(channel.Running));
        Assert.True(channel.Server.StoppedForSecurity);
    }

    [Fact]
    public async Task A_wrong_key_of_any_size_counts_as_a_refusal()
    {
        var (local, _) = Local(new InMemoryJournalStore());
        using var _1 = local;
        await using var channel = new Channel(local);

        Assert.Null(await channel.RawAsync(new SignedMessage([], [], [1, 2, 3])));
        Assert.Null(await channel.RawAsync(EngineKey.Create().Introduction()));
        Assert.Null(await channel.RawAsync(new SignedMessage([], [], new byte[64])));

        Assert.True(await Finishes(channel.Running));
        Assert.True(channel.Server.StoppedForSecurity);
    }

    // ---------- signature des réponses, côté interface

    private static byte[] ResultBody(object value) =>
        JsonSerializer.SerializeToUtf8Bytes(new EngineResponse("result", EngineProtocol.ToElement(value)), EngineProtocol.Json);

    /// <summary>Faux moteur : accepte la clé, lit la demande, puis répond ce que le test lui dit.</summary>
    private static async Task FakeServerAsync(string name, EngineKey welcomeKey, Func<long, SignedMessage> reply)
    {
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await pipe.WaitForConnectionAsync();
        await EngineProtocol.ReadAsync(pipe, CancellationToken.None);
        await EngineProtocol.WriteAsync(pipe, welcomeKey.SignResponse(0, []), CancellationToken.None);
        if (await EngineProtocol.ReadAsync(pipe, CancellationToken.None) is not { } signed)
        {
            return;
        }

        var request = JsonSerializer.Deserialize<EngineRequest>(signed.Body, EngineProtocol.Json)!;
        await EngineProtocol.WriteAsync(pipe, reply(request.Sequence), CancellationToken.None);

        // Le faux moteur reste en place jusqu'au départ de l'interface : elle lit sa réponse jusqu'au bout.
        await Record.ExceptionAsync(() => EngineProtocol.ReadAsync(pipe, CancellationToken.None));
    }

    private static readonly EnginePing Ping = new("essai", 1, true);

    [Fact]
    public async Task The_interface_accepts_an_answer_signed_for_its_request()
    {
        var key = EngineKey.Create();
        var name = "MAUS-essai-" + Guid.NewGuid().ToString("N");
        var server = FakeServerAsync(name, key, sequence => key.SignResponse(sequence, ResultBody(Ping)));

        var answer = await new RemoteMausEngine(name, Environment.ProcessId, key).PingAsync(CancellationToken.None);

        Assert.Equal("essai", answer.Version);
        await server;
    }

    public static TheoryData<string> ForgedAnswers() => ["autre clé", "autre numéro", "signée comme une demande", "progression d'une autre clé"];

    [Theory]
    [MemberData(nameof(ForgedAnswers))]
    public async Task The_interface_refuses_an_answer_that_is_not_signed_for_its_request(string kind)
    {
        var key = EngineKey.Create();
        var other = EngineKey.Create();
        var name = "MAUS-essai-" + Guid.NewGuid().ToString("N");
        var progressBody = JsonSerializer.SerializeToUtf8Bytes(new EngineResponse("progress", null), EngineProtocol.Json);
        var server = FakeServerAsync(name, key, sequence => kind switch
        {
            "autre clé" => other.SignResponse(sequence, ResultBody(Ping)),
            "autre numéro" => key.SignResponse(sequence + 1, ResultBody(Ping)),
            "signée comme une demande" => key.SignRequest(ResultBody(Ping)),
            _ => other.SignResponse(sequence, progressBody),
        });
        var progressed = 0;

        await Assert.ThrowsAsync<EngineException>(() => new RemoteMausEngine(name, Environment.ProcessId, key)
            .RunAuditAsync(new SyncProgress<ModuleResult>(_ => progressed++), CancellationToken.None));

        Assert.Equal(0, progressed);
        await server;
    }

    [Fact]
    public async Task The_interface_refuses_a_welcome_from_a_server_without_the_key()
    {
        var name = "MAUS-essai-" + Guid.NewGuid().ToString("N");
        var server = FakeServerAsync(name, EngineKey.Create(), _ => throw new InvalidOperationException("demande envoyée à tort"));

        await Assert.ThrowsAsync<EngineException>(() => new RemoteMausEngine(name, Environment.ProcessId, EngineKey.Create()).PingAsync(CancellationToken.None));

        // L'interface s'en va sans avoir rien envoyé : le faux serveur lit une fin de canal, pas une demande.
        await server;
    }

    // ---------- création et fin du moteur

    [Fact]
    public async Task The_engine_fails_instead_of_sharing_a_name_already_taken()
    {
        var (local, _) = Local(new InMemoryJournalStore());
        using var _1 = local;
        var name = "MAUS-essai-" + Guid.NewGuid().ToString("N");
        await using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var server = new EngineServer(local, name, Environment.ProcessId, EngineKey.Create().Fingerprint());

        var error = await Record.ExceptionAsync(() => server.RunAsync(CancellationToken.None));
        Assert.True(error is UnauthorizedAccessException or IOException, error?.ToString());
        Assert.False(server.StoppedForSecurity);
    }

    [Fact]
    public async Task The_engine_stops_with_the_interface_and_this_is_not_a_security_stop()
    {
        var (local, _) = Local(new InMemoryJournalStore());
        using var _1 = local;
        using var interfaceProcess = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
        var server = new EngineServer(local, "MAUS-essai-" + Guid.NewGuid().ToString("N"), interfaceProcess.Id, EngineKey.Create().Fingerprint());
        var running = server.RunAsync(CancellationToken.None);
        Assert.False(running.IsCompleted);

        interfaceProcess.Kill();

        Assert.True(await Finishes(running));
        Assert.False(server.StoppedForSecurity);
    }

    [Fact]
    public async Task An_engine_whose_interface_never_presents_itself_stops_quickly_even_with_a_silent_connection()
    {
        var (local, _) = Local(new InMemoryJournalStore());
        using var _1 = local;
        await using var channel = new Channel(local, unclaimed: TimeSpan.FromMilliseconds(400));
        await using var silent = await channel.ConnectAsync();

        Assert.True(await Finishes(channel.Running));
        Assert.False(channel.Server.StoppedForSecurity);
    }

    [Fact]
    public async Task Releasing_what_the_engine_holds_is_bounded_and_survives_an_error()
    {
        using var never = new ManualResetEventSlim();
        Assert.False(await EngineServer.ReleaseWithinAsync(() => never.Wait(TimeSpan.FromSeconds(5)), TimeSpan.FromMilliseconds(50)));
        Assert.False(await EngineServer.ReleaseWithinAsync(() => throw new InvalidOperationException("panne"), TimeSpan.FromSeconds(5)));
        Assert.True(await EngineServer.ReleaseWithinAsync(() => { }, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task An_engine_whose_interface_presented_itself_keeps_running()
    {
        var (local, _) = Local(new InMemoryJournalStore());
        using var _1 = local;
        await using var channel = new Channel(local, unclaimed: TimeSpan.FromMilliseconds(400));

        await channel.Remote.PingAsync(CancellationToken.None);
        await Task.Delay(1500);

        Assert.False(channel.Running.IsCompleted);
    }

    [Fact]
    public async Task A_silent_connection_does_not_keep_the_engine_alive_after_its_key()
    {
        var (local, _) = Local(new InMemoryJournalStore());
        using var _1 = local;
        await using var channel = new Channel(local, idle: TimeSpan.FromMilliseconds(400));
        await channel.Remote.PingAsync(CancellationToken.None);

        await using var silent = await channel.ConnectAsync();

        Assert.True(await Finishes(channel.Running));
        Assert.False(channel.Server.StoppedForSecurity);
    }

    [Fact]
    public async Task Malformed_messages_count_as_refusals()
    {
        var (local, _) = Local(new InMemoryJournalStore());
        using var _1 = local;
        await using var channel = new Channel(local);

        for (var i = 0; i < EngineServer.MaxRefusals; i++)
        {
            await using var pipe = await channel.ConnectAsync();
            await pipe.WriteAsync(Length(-1));
            await pipe.FlushAsync();
            Assert.Equal(0, await pipe.ReadAsync(new byte[1]));
        }

        Assert.True(await Finishes(channel.Running));
        Assert.True(channel.Server.StoppedForSecurity);
    }

    [Fact]
    public async Task After_the_rights_are_closed_no_new_request_starts()
    {
        var (local, _) = Local(new InMemoryJournalStore());
        using var _1 = local;
        await using var channel = new Channel(local);

        Assert.Equal("result", (await channel.CallAsync("engine.close"))!.Kind);

        Assert.Null(await channel.CallAsync("ping"));
        Assert.True(await Finishes(channel.Running));
    }

    // ---------- demande abandonnée

    [Fact]
    public async Task A_request_given_up_by_the_interface_is_cancelled_in_the_engine()
    {
        var (engine, script) = NewScripted();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        script.Handler = (method, args) =>
        {
            Assert.Equal(nameof(IMausEngine.ScanSpaceAsync), method.Name);
            var token = (CancellationToken)args[2]!;
            return Task.Run<SpaceReport>(async () =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    cancelled.SetResult();
                    throw;
                }

                throw new InvalidOperationException("jamais annulée");
            });
        };
        await using var channel = new Channel(engine);
        using var giveUp = new CancellationTokenSource();

        var scan = channel.Remote.ScanSpaceAsync(@"C:\", null, giveUp.Token);
        Assert.True(await Finishes(started.Task));
        await giveUp.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        Assert.True(await Finishes(cancelled.Task));
        Assert.False(channel.Server.StoppedForSecurity);
    }

    [Fact]
    public async Task A_fix_already_being_written_goes_to_the_end_when_the_interface_gives_up()
    {
        using var journal = new GatedJournal(new InMemoryJournalStore());
        var (local, registry) = Local(journal);
        using var _ = local;
        await using var channel = new Channel(local);
        await channel.Remote.RunAuditAsync(null, CancellationToken.None);
        using var giveUp = new CancellationTokenSource();

        var apply = channel.Remote.ApplyAsync(["M06.taskview"], NoRestorePoint, giveUp.Token);
        Assert.True(await WaitFor(journal.Entered));
        await giveUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
        await Task.Delay(200);
        journal.Release.Set();

        Assert.True(await Eventually(() => registry.GetDword(RegistryHive.CurrentUser, Advanced, TaskView) == 0));
    }

    // ---------- fin du moteur pendant une écriture

    [Fact]
    public async Task When_the_interface_closes_the_engine_finishes_the_fix_being_written_before_stopping()
    {
        using var journal = new GatedJournal(new InMemoryJournalStore());
        var (local, registry) = Local(journal);
        using var _ = local;
        await using var channel = new Channel(local);
        await channel.Remote.RunAuditAsync(null, CancellationToken.None);

        var apply = channel.Remote.ApplyAsync(["M06.taskview"], NoRestorePoint, CancellationToken.None);
        Assert.True(await WaitFor(journal.Entered));
        await channel.StopAsync();
        await Task.Delay(500);
        Assert.False(channel.Running.IsCompleted);

        journal.Release.Set();

        Assert.True(await Finishes(channel.Running));
        Assert.Equal(0, registry.GetDword(RegistryHive.CurrentUser, Advanced, TaskView));
        Assert.False(channel.Server.StoppedForSecurity);
        await Record.ExceptionAsync(() => apply);
    }

    [Fact]
    public async Task The_wait_for_a_fix_being_written_has_a_limit()
    {
        using var journal = new GatedJournal(new InMemoryJournalStore());
        var (local, registry) = Local(journal);
        using var _ = local;
        await using var channel = new Channel(local, drain: TimeSpan.FromMilliseconds(200));
        await channel.Remote.RunAuditAsync(null, CancellationToken.None);

        var apply = channel.Remote.ApplyAsync(["M06.taskview"], NoRestorePoint, CancellationToken.None);
        Assert.True(await WaitFor(journal.Entered));
        await channel.StopAsync();
        try
        {
            Assert.True(await Finishes(channel.Running));
        }
        finally
        {
            journal.Release.Set();
        }

        Assert.True(await Eventually(() => registry.GetDword(RegistryHive.CurrentUser, Advanced, TaskView) == 0));
        await Record.ExceptionAsync(() => apply);
    }

    [Fact]
    public async Task A_security_stop_does_not_wait_for_a_fix_being_written()
    {
        using var journal = new GatedJournal(new InMemoryJournalStore());
        var (local, registry) = Local(journal);
        using var _ = local;
        await using var channel = new Channel(local);
        await channel.Remote.RunAuditAsync(null, CancellationToken.None);

        var apply = channel.Remote.ApplyAsync(["M06.taskview"], NoRestorePoint, CancellationToken.None);
        Assert.True(await WaitFor(journal.Entered));
        try
        {
            for (var attempt = 0; attempt < EngineServer.MaxRefusals; attempt++)
            {
                Assert.Null(await channel.RawAsync(new SignedMessage([1], new byte[EngineKey.MacSize])));
            }

            Assert.True(await Finishes(channel.Running));
            Assert.True(channel.Server.StoppedForSecurity);
            Assert.False(journal.Release.IsSet);
        }
        finally
        {
            journal.Release.Set();
        }

        Assert.True(await Eventually(() => registry.GetDword(RegistryHive.CurrentUser, Advanced, TaskView) == 0));
        await Record.ExceptionAsync(() => apply);
    }

    // ---------- réponse au « ping »

    [Fact]
    public void Only_an_elevated_engine_with_the_expected_process_is_accepted()
    {
        Assert.True(new EnginePing("v", 42, true).IsElevatedEngine(42));
        Assert.False(new EnginePing("v", 42, false).IsElevatedEngine(42));
        Assert.False(new EnginePing("v", 43, true).IsElevatedEngine(42));
    }

    // ---------- bornes des messages

    private sealed class OneByteAtATime(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }

    private static byte[] Length(int value) => BitConverter.GetBytes(value);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData((32 << 20) + 1)]
    public async Task A_length_outside_the_limits_is_refused_before_reading(int length)
    {
        using var stream = new MemoryStream(Length(length));
        await Assert.ThrowsAsync<InvalidDataException>(() => EngineProtocol.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task A_stream_that_ends_gives_nothing_or_a_cut_message()
    {
        using var empty = new MemoryStream();
        Assert.Null(await EngineProtocol.ReadAsync(empty, CancellationToken.None));

        using var shortHeader = new MemoryStream(new byte[2]);
        Assert.Null(await EngineProtocol.ReadAsync(shortHeader, CancellationToken.None));

        using var cut = new MemoryStream([.. Length(10), 2, 0, 0]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => EngineProtocol.ReadAsync(cut, CancellationToken.None));
    }

    [Fact]
    public async Task An_unknown_message_type_is_refused()
    {
        using var stream = new MemoryStream([.. Length(2), 9, 0]);
        await Assert.ThrowsAsync<InvalidDataException>(() => EngineProtocol.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Messages_are_read_whole_even_one_byte_at_a_time()
    {
        var key = EngineKey.Create();
        var body = Encoding.UTF8.GetBytes("{\"op\":\"é\"}");
        using var written = new MemoryStream();
        await EngineProtocol.WriteAsync(written, key.SignRequest(body), CancellationToken.None);
        await EngineProtocol.WriteAsync(written, key.Introduction(), CancellationToken.None);

        using var slow = new OneByteAtATime(written.ToArray());
        var signed = await EngineProtocol.ReadAsync(slow, CancellationToken.None);
        var introduction = await EngineProtocol.ReadAsync(slow, CancellationToken.None);

        Assert.Equal(body, signed!.Body);
        Assert.Null(signed.Key);
        Assert.True(key.VerifyRequest(signed));
        Assert.Empty(introduction!.Body);
        Assert.True(EngineKey.Parse(introduction.Key)!.HasFingerprint(key.Fingerprint()));
        Assert.Null(await EngineProtocol.ReadAsync(slow, CancellationToken.None));
    }

    [Fact]
    public async Task A_short_signature_stays_short_so_that_its_check_refuses_it()
    {
        using var stream = new MemoryStream([.. Length(4), 2, 1, 2, 3]);
        var message = await EngineProtocol.ReadAsync(stream, CancellationToken.None);

        Assert.Equal([1, 2, 3], message!.Mac);
        Assert.Empty(message.Body);
        Assert.False(EngineKey.Create().VerifyRequest(message));
    }

    [Fact]
    public async Task A_message_too_long_is_refused_before_anything_is_written()
    {
        using var stream = new MemoryStream();
        var tooLong = new SignedMessage(new byte[33 << 20], new byte[EngineKey.MacSize]);

        await Assert.ThrowsAsync<InvalidDataException>(() => EngineProtocol.WriteAsync(stream, tooLong, CancellationToken.None));

        Assert.Equal(0, stream.Length);
    }

    // ---------- chaque opération, par le vrai canal

    public static TheoryData<string> Operations()
    {
        var names = new TheoryData<string>();
        foreach (var method in typeof(IMausEngine).GetMethods())
        {
            // Refermer les droits arrête le serveur sans passer par le moteur : voir OnDemandEngineTests.
            if (method.Name == nameof(IMausEngine.CloseRightsAsync))
            {
                continue;
            }

            names.Add(method.Name);
        }

        return names;
    }

    private static object? ArgumentFor(ParameterInfo parameter)
    {
        if (parameter.ParameterType == typeof(CancellationToken))
        {
            return CancellationToken.None;
        }

        if (parameter.ParameterType.IsGenericType && parameter.ParameterType.GetGenericTypeDefinition() == typeof(IProgress<>))
        {
            return null;
        }

        // Le serveur vérifie les choix avant de les transmettre : un exemple qui respecte leurs bornes.
        return parameter.ParameterType == typeof(UserPreferences) ? UserPreferences.Default with { GameBarProfile = 2 } : Sample.Of(parameter.ParameterType);
    }

    private static string Json(object? value, Type type) => JsonSerializer.Serialize(value, type, EngineProtocol.Json);

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Every_operation_carries_its_arguments_and_its_value_through_the_channel(string name)
    {
        var method = typeof(IMausEngine).GetMethod(name)!;
        var parameters = method.GetParameters();
        var sent = parameters.Select(ArgumentFor).ToArray();
        var value = method.ReturnType.IsGenericType ? Sample.Of(method.ReturnType.GetGenericArguments()[0]) : null;
        var calls = new List<(string Name, object?[] Arguments)>();
        var (engine, script) = NewScripted();
        script.Handler = (called, args) =>
        {
            lock (calls)
            {
                calls.Add((called.Name, args));
            }

            return Completed(called.ReturnType, value);
        };
        await using var channel = new Channel(engine);

        var task = (Task)method.Invoke(channel.Remote, sent)!;
        await task;

        var call = Assert.Single(calls);
        Assert.Equal(name, call.Name);
        for (var i = 0; i < parameters.Length; i++)
        {
            var type = parameters[i].ParameterType;
            if (type == typeof(CancellationToken) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IProgress<>)))
            {
                continue;
            }

            // « force » ne passe jamais par le canal : le moteur reçoit toujours « non ».
            var expected = parameters[i].Name == "force" ? false : sent[i];
            Assert.Equal(Json(expected, type), Json(call.Arguments[i], type));
        }

        if (method.ReturnType.IsGenericType)
        {
            var type = method.ReturnType.GetGenericArguments()[0];
            var received = task.GetType().GetProperty("Result")!.GetValue(task);
            Assert.Equal(Json(value, type), Json(received, type));
        }
    }

    [Fact]
    public async Task Progress_of_a_disk_analysis_and_of_a_tool_reaches_the_interface_before_the_result()
    {
        var (engine, script) = NewScripted();
        script.Handler = (method, args) =>
        {
            if (method.Name == nameof(IMausEngine.ScanSpaceAsync))
            {
                ((IProgress<long>)args[1]!).Report(42);
                return Task.FromResult((SpaceReport)Sample.Of(typeof(SpaceReport))!);
            }

            ((IProgress<string>)args[1]!).Report("ligne");
            return Task.FromResult(new ToolOutcome(true, "fini"));
        };
        await using var channel = new Channel(engine);
        var files = new List<long>();
        var lines = new List<string>();

        await channel.Remote.ScanSpaceAsync(@"C:\", new SyncProgress<long>(files.Add), CancellationToken.None);
        var outcome = await channel.Remote.RunToolAsync(new ToolRequest(ToolKind.Repair), new SyncProgress<string>(lines.Add), CancellationToken.None);

        Assert.Equal([42], files);
        Assert.Equal(["ligne"], lines);
        Assert.Equal("fini", outcome.Message);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>Fabrique un exemple rempli de n'importe quel type transporté par le canal, sans liste à tenir à jour.</summary>
    private static class Sample
    {
        private const int MaxDepth = 6;

        public static object? Of(Type type, int depth = 0)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (Simple(type) is { } simple)
            {
                return simple;
            }

            if (type.IsEnum)
            {
                var values = Enum.GetValues(type);
                return values.GetValue(values.Length - 1);
            }

            if (depth > MaxDepth)
            {
                return type.IsValueType ? Activator.CreateInstance(type) : null;
            }

            if (type.IsArray)
            {
                var array = Array.CreateInstance(type.GetElementType()!, 1);
                array.SetValue(Of(type.GetElementType()!, depth + 1), 0);
                return array;
            }

            return Collection(type, depth) ?? Instance(type, depth);
        }

        private static object? Simple(Type type)
        {
            object? value = Type.GetTypeCode(type) switch
            {
                TypeCode.String => "exemple",
                TypeCode.Boolean => true,
                TypeCode.Byte => (byte)7,
                TypeCode.Int16 => (short)7,
                TypeCode.UInt16 => (ushort)7,
                TypeCode.Int32 when !type.IsEnum => 7,
                TypeCode.UInt32 when !type.IsEnum => 7u,
                TypeCode.Int64 => 7L,
                TypeCode.UInt64 => 7UL,
                TypeCode.Single => 1.5f,
                TypeCode.Double => 1.5,
                TypeCode.Decimal => 1.5m,
                TypeCode.DateTime => new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                _ => null,
            };
            if (value is not null)
            {
                return value;
            }

            if (type == typeof(TimeSpan))
            {
                return TimeSpan.FromSeconds(3);
            }

            if (type == typeof(DateTimeOffset))
            {
                return new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
            }

            if (type == typeof(Guid))
            {
                return new Guid("01234567-89ab-cdef-0123-456789abcdef");
            }

            if (type == typeof(JsonElement))
            {
                using var document = JsonDocument.Parse("1");
                return document.RootElement.Clone();
            }

            return null;
        }

        private static object? Collection(Type type, int depth)
        {
            if (!type.IsGenericType)
            {
                return null;
            }

            var definition = type.GetGenericTypeDefinition();
            var arguments = type.GetGenericArguments();
            if (definition == typeof(IReadOnlyList<>) || definition == typeof(IList<>) || definition == typeof(ICollection<>) || definition == typeof(IEnumerable<>)
                || definition == typeof(IReadOnlyCollection<>) || definition == typeof(List<>))
            {
                var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(arguments))!;
                if (Of(arguments[0], depth + 1) is { } item)
                {
                    list.Add(item);
                }

                return list;
            }

            if (definition == typeof(IReadOnlyDictionary<,>) || definition == typeof(IDictionary<,>) || definition == typeof(Dictionary<,>))
            {
                var dictionary = (System.Collections.IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(arguments))!;
                if (Of(arguments[0], depth + 1) is { } key)
                {
                    dictionary[key] = Of(arguments[1], depth + 1);
                }

                return dictionary;
            }

            return null;
        }

        private static object? Instance(Type type, int depth)
        {
            if (type.IsInterface || type.IsAbstract)
            {
                return null;
            }

            var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
            object instance;
            if (constructor is not null)
            {
                instance = constructor.Invoke([.. constructor.GetParameters().Select(p => Of(p.ParameterType, depth + 1))]);
            }
            else if (type.IsValueType)
            {
                instance = Activator.CreateInstance(type)!;
            }
            else
            {
                return null;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0 || property.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
                {
                    continue;
                }

                var isIncluded = property.GetCustomAttribute<JsonIncludeAttribute>() is not null;
                if (property.GetSetMethod(nonPublic: isIncluded) is { } setter)
                {
                    setter.Invoke(instance, [Of(property.PropertyType, depth + 1)]);
                }
                else if (property.GetCustomAttribute<JsonObjectCreationHandlingAttribute>() is not null
                    && property.GetValue(instance) is System.Collections.IList list
                    && Of(property.PropertyType.GetGenericArguments()[0], depth + 1) is { } item)
                {
                    list.Add(item);
                }
            }

            return instance;
        }
    }
}
