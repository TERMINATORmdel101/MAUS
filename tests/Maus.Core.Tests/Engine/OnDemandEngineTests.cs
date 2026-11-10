using System.Reflection;
using Maus.Core.Engine;
using Maus.Core.Engine.Broker;
using Maus.Core.Fixes;
using Maus.Core.Modules.M06Visual;
using Maus.Core.Preferences;
using Maus.Core.Tests.Fixes;
using Microsoft.Win32;

namespace Maus.Core.Tests.Engine;

/// <summary>0.8.1 (Microsoft Store) : rien au lancement, autorisation demandée à la première action qui en a besoin.</summary>
public sealed class OnDemandEngineTests
{
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    /// <summary>Moteur d'essai : note chaque opération ; la transmet à un vrai moteur, ou rend une valeur vide.</summary>
    public class Recorder : DispatchProxy
    {
        public List<string> Calls { get; } = [];

        public IMausEngine? Target { get; set; }

        public TimeSpan Delay { get; set; }

        /// <summary>Opération qui échoue comme un canal fermé (moteur arrêté juste avant).</summary>
        public string? Unavailable { get; set; }

        public Action? OnUnavailable { get; set; }

        /// <summary>Opération qui échoue comme un canal coupé après son ouverture.</summary>
        public string? Cut { get; set; }

        /// <summary>Accepte de refermer les droits, comme un moteur sans demande en cours.</summary>
        public bool Closes { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            lock (Calls)
            {
                Calls.Add(targetMethod.Name);
            }

            if (Closes && targetMethod.Name == nameof(IMausEngine.CloseRightsAsync))
            {
                return Task.FromResult(true);
            }

            if (targetMethod.Name == Unavailable)
            {
                OnUnavailable?.Invoke();
                return Failed(targetMethod.ReturnType, new EngineUnavailableException("canal fermé"));
            }

            if (targetMethod.Name == Cut)
            {
                OnUnavailable?.Invoke();
                return Failed(targetMethod.ReturnType, new EngineException("canal coupé"));
            }

            if (Delay > TimeSpan.Zero && targetMethod.ReturnType.IsGenericType)
            {
                Thread.Sleep(Delay);
            }

            if (Target is not null)
            {
                return targetMethod.Invoke(Target, args);
            }

            if (targetMethod.ReturnType == typeof(void))
            {
                return null;
            }

            if (targetMethod.ReturnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            var result = targetMethod.ReturnType.GetGenericArguments()[0];
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result)
                .Invoke(null, [result.IsValueType ? Activator.CreateInstance(result) : null]);
        }

        private static object Failed(Type returnType, Exception error)
        {
            var source = returnType.IsGenericType ? returnType.GetGenericArguments()[0] : typeof(object);
            var method = typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!.MakeGenericMethod(source);
            return method.Invoke(null, [error])!;
        }
    }

    private static (IMausEngine Engine, Recorder Recorder) Recorded(IMausEngine? target = null)
    {
        var engine = DispatchProxy.Create<IMausEngine, Recorder>();
        var recorder = (Recorder)(object)engine;
        recorder.Target = target;
        return (engine, recorder);
    }

    private static LocalMausEngine Local()
    {
        var registry = new FakeRegistry().Set(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton", 1);
        var parameters = new FakeSystemParameters { MinimizeAnimation = true };
        var journal = new InMemoryJournalStore();
        var audit = TestContext.Create(registry, parameters: parameters, elevated: true);
        return new LocalMausEngine(
            new AuditEngine([new VisualEffectsModule()]),
            () => audit,
            context => TestFixContext.Create(context, registry, parameters, journal, new FakeSystemRestore(), new FakeNotifier(), false),
            new MemoryPreferencesStore(),
            journal);
    }

    /// <summary>Lanceur d'essai : compte les demandes d'autorisation et rend le moteur suivant de la liste.</summary>
    private sealed class Launcher(params (IMausEngine Engine, Func<bool> Exited)[] engines)
    {
        public int Starts { get; private set; }

        public Exception? Refuse { get; set; }

        public TimeSpan Delay { get; set; }

        public async Task<ElevatedEngineSession> StartAsync(CancellationToken cancellationToken)
        {
            Starts++;
            await Task.Delay(Delay, cancellationToken);
            if (Refuse is not null)
            {
                throw Refuse;
            }

            var (engine, exited) = engines[Math.Min(Starts - 1, engines.Length - 1)];
            return new ElevatedEngineSession(engine, exited);
        }
    }

    [Fact]
    public async Task Opening_asks_nothing_and_reads_go_through_the_engine_without_rights()
    {
        var (limited, limitedCalls) = Recorded();
        var launcher = new Launcher();
        using var engine = new OnDemandMausEngine(limited, launcher.StartAsync);

        await engine.ReadInventoryAsync(CancellationToken.None);
        await engine.SampleSensorsAsync(CancellationToken.None);
        await engine.ReadProcessesAsync(CancellationToken.None);

        Assert.Equal(0, launcher.Starts);
        Assert.False(engine.IsElevatedRunning);
        Assert.Equal(["ReadInventoryAsync", "SampleSensorsAsync", "ReadProcessesAsync"], limitedCalls.Calls);
    }

    [Fact]
    public async Task First_action_needing_rights_asks_once_then_the_audit_and_reads_use_the_elevated_engine()
    {
        var (limited, limitedCalls) = Recorded();
        using var local = Local();
        var (elevated, elevatedCalls) = Recorded(local);
        var launcher = new Launcher((elevated, () => false)) { Delay = TimeSpan.FromMilliseconds(100) };
        using var engine = new OnDemandMausEngine(limited, launcher.StartAsync);
        var started = 0;
        engine.ElevatedStarted += (_, _) => started++;

        // Deux actions en même temps : une seule demande d'autorisation.
        await Task.WhenAll(engine.EnsureElevatedAsync(CancellationToken.None), engine.BrowseJournalAsync(CancellationToken.None));
        await engine.RunAuditAsync(null, CancellationToken.None);
        elevatedCalls.Target = null;
        await engine.ReadInventoryAsync(CancellationToken.None);

        Assert.Equal(1, launcher.Starts);
        Assert.Equal(1, started);
        Assert.True(engine.IsElevatedRunning);
        Assert.Contains("ReadInventoryAsync", elevatedCalls.Calls);
        Assert.Empty(limitedCalls.Calls);
    }

    /// <summary>powercfg écrit dans le profil de l'utilisateur : le rapport de batterie n'est pas une opération du moteur.</summary>
    [Fact]
    public void The_battery_report_is_not_an_engine_operation() =>
        Assert.DoesNotContain(typeof(IMausEngine).GetMethods(), m => m.Name.Contains("Battery", StringComparison.Ordinal));

    [Fact]
    public async Task A_read_cut_because_the_engine_stopped_is_done_again_without_rights()
    {
        var (limited, limitedCalls) = Recorded();
        var exited = false;
        var (elevated, elevatedCalls) = Recorded();
        elevatedCalls.Cut = nameof(IMausEngine.ReadInventoryAsync);
        elevatedCalls.OnUnavailable = () => exited = true;
        var launcher = new Launcher((elevated, () => exited));
        using var engine = new OnDemandMausEngine(limited, launcher.StartAsync);
        await engine.EnsureElevatedAsync(CancellationToken.None);

        await engine.ReadInventoryAsync(CancellationToken.None);

        Assert.Equal(["ReadInventoryAsync"], limitedCalls.Calls);
        Assert.Equal(1, launcher.Starts);
    }

    [Fact]
    public async Task An_error_from_an_engine_still_running_is_not_hidden_by_a_second_read()
    {
        var (limited, limitedCalls) = Recorded();
        var (elevated, elevatedCalls) = Recorded();
        elevatedCalls.Cut = nameof(IMausEngine.ReadInventoryAsync);
        using var engine = new OnDemandMausEngine(limited, new Launcher((elevated, () => false)).StartAsync);
        await engine.EnsureElevatedAsync(CancellationToken.None);

        await Assert.ThrowsAsync<EngineException>(() => engine.ReadInventoryAsync(CancellationToken.None));

        Assert.Empty(limitedCalls.Calls);
    }

    /// <summary>Une action a peut-être déjà eu lieu : elle n'est jamais refaite parce que le canal a été coupé.</summary>
    [Fact]
    public async Task An_action_cut_because_the_engine_stopped_is_never_done_again()
    {
        var (limited, limitedCalls) = Recorded();
        var exited = false;
        var (elevated, elevatedCalls) = Recorded();
        elevatedCalls.Cut = nameof(IMausEngine.TerminateProcessAsync);
        elevatedCalls.OnUnavailable = () => exited = true;
        using var engine = new OnDemandMausEngine(limited, new Launcher((elevated, () => exited)).StartAsync);
        await engine.EnsureElevatedAsync(CancellationToken.None);

        await Assert.ThrowsAsync<EngineException>(() => engine.TerminateProcessAsync(4242, null, CancellationToken.None));
        exited = false;
        elevatedCalls.Cut = nameof(IMausEngine.RunBenchmarkAsync);
        await Assert.ThrowsAsync<EngineException>(() => engine.RunBenchmarkAsync(new BenchmarkLaunch("d3d12", "1080p", "all", false, "fr"), CancellationToken.None));

        Assert.Empty(limitedCalls.Calls);
    }

    [Fact]
    public async Task Refused_permission_cancels_only_this_action_and_is_asked_again_next_time()
    {
        var (limited, limitedCalls) = Recorded();
        var launcher = new Launcher { Refuse = new EngineDeclinedException(EngineDeclinedException.DefaultMessage) };
        using var engine = new OnDemandMausEngine(limited, launcher.StartAsync);

        await Assert.ThrowsAsync<EngineDeclinedException>(() => engine.BrowseJournalAsync(CancellationToken.None));
        await engine.ReadDriversAsync(CancellationToken.None);
        await Assert.ThrowsAsync<EngineDeclinedException>(() => engine.ApplyAsync(["M06.x"], new ApplyOptions(), CancellationToken.None));

        Assert.Equal(2, launcher.Starts);
        Assert.Equal(["ReadDriversAsync"], limitedCalls.Calls);
        Assert.False(engine.IsElevatedRunning);
    }

    [Fact]
    public async Task Engine_stopped_after_idle_is_relaunched_and_audits_again_before_planning()
    {
        var (limited, limitedCalls) = Recorded();
        using var first = Local();
        using var second = Local();
        var (one, oneCalls) = Recorded(first);
        var (two, twoCalls) = Recorded(second);
        var exited = false;
        var launcher = new Launcher((one, () => exited), (two, () => false));
        using var engine = new OnDemandMausEngine(limited, launcher.StartAsync);

        await engine.EnsureElevatedAsync(CancellationToken.None);
        await engine.RunAuditAsync(null, CancellationToken.None);
        exited = true;

        // Arrêté : les lectures se font sans droits, sans rien demander…
        await engine.ReadInventoryAsync(CancellationToken.None);
        Assert.Equal(1, launcher.Starts);
        Assert.Equal(["ReadInventoryAsync"], limitedCalls.Calls);

        // … et la préparation des corrections relance le moteur, qui refait d'abord son propre audit.
        var plan = await engine.PlanAsync(CancellationToken.None);
        Assert.Equal(2, launcher.Starts);
        Assert.Equal(["RunAuditAsync", "PlanAsync"], twoCalls.Calls);
        Assert.NotEmpty(plan);
        Assert.Equal(["RunAuditAsync"], oneCalls.Calls);
    }

    [Fact]
    public async Task Audit_without_a_running_engine_asks_nothing_and_does_not_count_for_the_fixes()
    {
        using var own = Local();
        var (limited, limitedCalls) = Recorded(own);
        using var local = Local();
        var (elevated, elevatedCalls) = Recorded(local);
        var launcher = new Launcher((elevated, () => false));
        using var engine = new OnDemandMausEngine(limited, launcher.StartAsync);

        await engine.RunAuditAsync(null, CancellationToken.None);

        Assert.Equal(0, launcher.Starts);
        Assert.False(engine.IsElevatedRunning);
        Assert.Equal(["RunAuditAsync"], limitedCalls.Calls);

        // Les corrections, elles, demandent l'autorisation, et le moteur refait son propre audit avant de les préparer.
        await engine.PlanAsync(CancellationToken.None);
        Assert.Equal(1, launcher.Starts);
        Assert.Equal(["RunAuditAsync", "PlanAsync"], elevatedCalls.Calls);
        Assert.Equal(["RunAuditAsync"], limitedCalls.Calls);
    }

    [Fact]
    public async Task Audit_with_a_running_engine_is_its_full_audit_and_the_fixes_do_not_audit_again()
    {
        var (limited, limitedCalls) = Recorded();
        using var local = Local();
        var (elevated, elevatedCalls) = Recorded(local);
        var launcher = new Launcher((elevated, () => false));
        using var engine = new OnDemandMausEngine(limited, launcher.StartAsync);
        await engine.EnsureElevatedAsync(CancellationToken.None);

        await engine.RunAuditAsync(null, CancellationToken.None);
        await engine.PlanAsync(CancellationToken.None);

        Assert.Equal(1, launcher.Starts);
        Assert.Equal(["RunAuditAsync", "PlanAsync"], elevatedCalls.Calls);
        Assert.Empty(limitedCalls.Calls);
    }

    [Fact]
    public async Task Audit_sent_just_after_the_engine_stopped_goes_on_without_rights_and_asks_nothing()
    {
        var (limited, limitedCalls) = Recorded();
        var exited = false;
        var (one, oneCalls) = Recorded();
        oneCalls.Unavailable = nameof(IMausEngine.RunAuditAsync);
        oneCalls.OnUnavailable = () => exited = true;
        var launcher = new Launcher((one, () => exited));
        using var engine = new OnDemandMausEngine(limited, launcher.StartAsync);
        await engine.EnsureElevatedAsync(CancellationToken.None);

        await engine.RunAuditAsync(null, CancellationToken.None);

        Assert.Equal(1, launcher.Starts);
        Assert.Equal(["RunAuditAsync"], limitedCalls.Calls);
    }

    [Fact]
    public async Task Frame_reading_never_asks_for_permission()
    {
        var (limited, _) = Recorded();
        var launcher = new Launcher();
        using var engine = new OnDemandMausEngine(limited, launcher.StartAsync);

        await Assert.ThrowsAsync<EngineException>(() => engine.ReadFramesAsync("MAUS_Compteur", null, null, 20, CancellationToken.None));
        Assert.Null(await engine.StopFramesAsync("MAUS_Compteur", CancellationToken.None));
        Assert.Equal(0, launcher.Starts);
    }

    [Fact]
    public async Task Request_sent_just_after_the_engine_stopped_is_sent_again_to_a_new_engine()
    {
        var (limited, _) = Recorded();
        var exited = false;
        var (one, oneCalls) = Recorded();
        oneCalls.Unavailable = nameof(IMausEngine.BrowseJournalAsync);
        oneCalls.OnUnavailable = () => exited = true;
        var (two, twoCalls) = Recorded();
        var launcher = new Launcher((one, () => exited), (two, () => false));
        using var engine = new OnDemandMausEngine(limited, launcher.StartAsync);

        await engine.BrowseJournalAsync(CancellationToken.None);

        Assert.Equal(2, launcher.Starts);
        Assert.Equal(["BrowseJournalAsync"], twoCalls.Calls);
    }

    [Fact]
    public async Task Server_stops_after_its_idle_delay_but_never_during_a_request()
    {
        using var local = Local();
        var (slow, slowCalls) = Recorded(local);
        slowCalls.Delay = TimeSpan.FromSeconds(3);
        var name = "MAUS-essai-" + Guid.NewGuid().ToString("N");
        var key = EngineKey.Create();
        var server = new EngineServer(slow, name, Environment.ProcessId, key.Fingerprint()) { IdleTimeout = TimeSpan.FromSeconds(2) }.RunAsync(CancellationToken.None);
        var remote = new RemoteMausEngine(name, Environment.ProcessId, key);

        // Demande plus longue que le délai d'inactivité : elle va jusqu'au bout.
        var run = await remote.RunAuditAsync(null, CancellationToken.None);
        Assert.NotEmpty(run.Results);

        // Puis plus rien : le moteur s'arrête seul.
        var stopped = await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(server, stopped);
    }

    [Fact]
    public async Task Server_stops_when_the_user_closes_the_rights_but_not_during_another_request()
    {
        using var local = Local();
        var (slow, slowCalls) = Recorded(local);
        slowCalls.Delay = TimeSpan.FromSeconds(2);
        var name = "MAUS-essai-" + Guid.NewGuid().ToString("N");
        var key = EngineKey.Create();
        var server = new EngineServer(slow, name, Environment.ProcessId, key.Fingerprint()).RunAsync(CancellationToken.None);
        var remote = new RemoteMausEngine(name, Environment.ProcessId, key);

        // Une demande en cours va jusqu'au bout : refermer attend.
        var audit = remote.RunAuditAsync(null, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(await remote.CloseRightsAsync(CancellationToken.None));
        Assert.NotEmpty((await audit).Results);

        Assert.True(await remote.CloseRightsAsync(CancellationToken.None));
        Assert.Same(server, await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(10))));
    }

    [Fact]
    public async Task Once_the_rights_are_closed_the_engine_counts_as_stopped_and_nothing_asks_again()
    {
        var (limited, _) = Recorded();
        var (running, calls) = Recorded();
        calls.Closes = true;
        var launcher = new Launcher((running, () => false));
        using var engine = new OnDemandMausEngine(limited, launcher.StartAsync);
        await engine.EnsureElevatedAsync(CancellationToken.None);

        Assert.True(await engine.CloseRightsAsync(CancellationToken.None));

        // Le processus vit encore un instant : il ne compte plus, et le journal « si le moteur tourne » ne demande rien.
        Assert.False(engine.IsElevatedRunning);
        Assert.Null(await engine.BrowseJournalIfRunningAsync(CancellationToken.None));
        Assert.Equal(1, launcher.Starts);
        Assert.DoesNotContain(nameof(IMausEngine.BrowseJournalAsync), calls.Calls);
    }

    [Fact]
    public async Task The_journal_read_that_asks_nothing_does_not_restart_an_engine_that_stops_during_the_read()
    {
        var (limited, _) = Recorded();
        var exited = false;
        var (running, calls) = Recorded();
        calls.Unavailable = nameof(IMausEngine.BrowseJournalAsync);
        calls.OnUnavailable = () => exited = true;
        var launcher = new Launcher((running, () => exited));
        using var engine = new OnDemandMausEngine(limited, launcher.StartAsync);
        await engine.EnsureElevatedAsync(CancellationToken.None);

        Assert.Null(await engine.BrowseJournalIfRunningAsync(CancellationToken.None));

        Assert.Equal(1, launcher.Starts);
    }

    [Fact]
    public async Task Copying_choices_never_starts_an_engine_that_is_stopped()
    {
        var (limited, _) = Recorded();
        var (running, _) = Recorded();
        var launcher = new Launcher((running, () => false));
        using var engine = new OnDemandMausEngine(limited, launcher.StartAsync);

        Assert.Null(await engine.SavePreferencesIfRunningAsync(UserPreferences.Default, CancellationToken.None));

        Assert.Equal(0, launcher.Starts);
    }

    [Fact]
    public async Task Copying_choices_does_not_restart_an_engine_that_stops_during_the_copy()
    {
        var (limited, _) = Recorded();
        var exited = false;
        var (running, calls) = Recorded();
        calls.Unavailable = nameof(IMausEngine.SavePreferencesAsync);
        calls.OnUnavailable = () => exited = true;
        var launcher = new Launcher((running, () => exited));
        using var engine = new OnDemandMausEngine(limited, launcher.StartAsync);
        await engine.EnsureElevatedAsync(CancellationToken.None);

        Assert.Null(await engine.SavePreferencesIfRunningAsync(UserPreferences.Default, CancellationToken.None));

        Assert.Equal(1, launcher.Starts);
    }

    [Fact]
    public async Task Closing_the_rights_never_starts_the_engine()
    {
        using var local = Local();
        var starts = 0;
        using var engine = new OnDemandMausEngine(local, _ =>
        {
            starts++;
            throw new InvalidOperationException("ne doit pas démarrer");
        });

        Assert.False(await engine.CloseRightsAsync(CancellationToken.None));
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Restore_points_are_listed_and_a_manual_point_is_created_and_checked()
    {
        using var local = Local();

        var reading = await local.ReadRestorePointsAsync(CancellationToken.None);
        var outcome = await local.CreateRestorePointAsync(CancellationToken.None);

        Assert.Equal(ProtectionState.Enabled, reading.Protection);
        Assert.Contains(reading.Points, p => p.Description == "Windows Update");
        Assert.Null(reading.UsedBytes);
        Assert.True(outcome.Succeeded, outcome.Message);
    }
}
