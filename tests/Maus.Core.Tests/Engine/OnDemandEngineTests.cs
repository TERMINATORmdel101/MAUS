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

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            lock (Calls)
            {
                Calls.Add(targetMethod.Name);
            }

            if (targetMethod.Name == Unavailable)
            {
                OnUnavailable?.Invoke();
                return Failed(targetMethod.ReturnType, new EngineUnavailableException("canal fermé"));
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
    public async Task First_audit_asks_once_then_reads_use_the_elevated_engine()
    {
        var (limited, limitedCalls) = Recorded();
        using var local = Local();
        var (elevated, elevatedCalls) = Recorded(local);
        var launcher = new Launcher((elevated, () => false)) { Delay = TimeSpan.FromMilliseconds(100) };
        using var engine = new OnDemandMausEngine(limited, launcher.StartAsync);
        var started = 0;
        engine.ElevatedStarted += (_, _) => started++;

        // Deux actions en même temps : une seule demande d'autorisation.
        await Task.WhenAll(engine.RunAuditAsync(null, CancellationToken.None), engine.BrowseJournalAsync(CancellationToken.None));
        await engine.RunAuditAsync(null, CancellationToken.None);
        elevatedCalls.Target = null;
        await engine.ReadInventoryAsync(CancellationToken.None);

        Assert.Equal(1, launcher.Starts);
        Assert.Equal(1, started);
        Assert.True(engine.IsElevatedRunning);
        Assert.Contains("ReadInventoryAsync", elevatedCalls.Calls);
        Assert.Empty(limitedCalls.Calls);
    }

    [Fact]
    public async Task Refused_permission_cancels_only_this_action_and_is_asked_again_next_time()
    {
        var (limited, limitedCalls) = Recorded();
        var launcher = new Launcher { Refuse = new EngineDeclinedException(EngineDeclinedException.DefaultMessage) };
        using var engine = new OnDemandMausEngine(limited, launcher.StartAsync);

        await Assert.ThrowsAsync<EngineDeclinedException>(() => engine.RunAuditAsync(null, CancellationToken.None));
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
        slowCalls.Delay = TimeSpan.FromMilliseconds(800);
        var name = "MAUS-essai-" + Guid.NewGuid().ToString("N");
        var server = new EngineServer(slow, name, Environment.ProcessId) { IdleTimeout = TimeSpan.FromMilliseconds(300) }.RunAsync(CancellationToken.None);
        var remote = new RemoteMausEngine(name, Environment.ProcessId);

        // Demande plus longue que le délai d'inactivité : elle va jusqu'au bout.
        var run = await remote.RunAuditAsync(null, CancellationToken.None);
        Assert.NotEmpty(run.Results);

        // Puis plus rien : le moteur s'arrête seul.
        var stopped = await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(server, stopped);
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
