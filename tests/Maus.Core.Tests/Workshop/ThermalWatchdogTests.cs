using Maus.Core.Workshop;

namespace Maus.Core.Tests.Workshop;

/// <summary>
/// Pendant un test de plusieurs heures, l'arrêt automatique en cas de surchauffe ne doit jamais disparaître sans prévenir :
/// mesures en échec ou bloquées, température du processeur qui n'est plus lue (pilote PawnIO occupé), alarme figée.
/// </summary>
public class ThermalWatchdogTests
{
    private const int Limit = 95;

    private static readonly DateTimeOffset Start = new(2026, 9, 30, 22, 0, 0, TimeSpan.FromHours(2));

    private static SensorSnapshot Sample(double? cpuC = null, int? tjMax = null, double? gpuC = null, int? gpuSlowdown = null) => new()
    {
        At = Start,
        CpuPercent = 100,
        CpuTemperatureC = cpuC,
        CpuTjMaxC = tjMax,
        Gpus = gpuC is null ? [] : [new GpuSensor("RTX 4070", 99, gpuC, 2000, 60, 90, 200, 2600, 6L << 30, gpuSlowdown)],
    };

    [Fact]
    public void Danger_alarm_of_the_last_sample_stops_the_test_and_a_cleared_one_does_not_linger()
    {
        var clock = new FakeClock();
        var watchdog = new ThermalWatchdog(clock.Now);
        watchdog.Begin(ThermalTarget.Cpu, CpuTemperatureWatch.Watched);

        watchdog.Record(Sample(cpuC: Limit + 6), Limit, pawnIoInstalled: true);
        Assert.Contains("dépasse sa limite", watchdog.AbortReason(), StringComparison.Ordinal);

        clock.Advance(1);
        watchdog.Record(Sample(cpuC: 70), Limit, pawnIoInstalled: true);
        Assert.Null(watchdog.AbortReason());

        watchdog.Record(Sample(cpuC: Limit + 6), Limit, pawnIoInstalled: true);
        watchdog.End();
        Assert.Null(watchdog.AbortReason());
        Assert.False(watchdog.IsRunning);
    }

    [Fact]
    public void Failing_measurements_stop_the_test_as_a_precaution_after_the_grace_delay_only()
    {
        var clock = new FakeClock();
        var watchdog = new ThermalWatchdog(clock.Now);
        watchdog.Begin(ThermalTarget.Cpu, CpuTemperatureWatch.Watched);
        watchdog.Record(Sample(cpuC: 70), Limit, pawnIoInstalled: true);

        // Avant : une mesure qui lève une exception arrêtait les mesures pour de bon, et le test continuait sans protection.
        var failures = 0;
        for (var second = 1; second <= 15; second++)
        {
            clock.Advance(1);
            failures = watchdog.RecordFailure("PDH en panne");
            Assert.Null(watchdog.AbortReason());
        }

        Assert.Equal(15, failures);
        clock.Advance(1);
        watchdog.RecordFailure("PDH en panne");
        var reason = watchdog.AbortReason();
        Assert.NotNull(reason);
        Assert.StartsWith("Les mesures en direct ne répondent plus depuis 16 s", reason, StringComparison.Ordinal);
        Assert.EndsWith("Dernière erreur : PDH en panne", reason, StringComparison.Ordinal);

        // Les mesures reviennent : le test peut continuer, et le nombre d'échecs est rendu pour le journal.
        Assert.Equal(16, watchdog.Record(Sample(cpuC: 70), Limit, pawnIoInstalled: true));
        Assert.Null(watchdog.AbortReason());
    }

    [Fact]
    public void Measurements_that_hang_without_error_stop_the_test_too()
    {
        var clock = new FakeClock();
        var watchdog = new ThermalWatchdog(clock.Now);
        watchdog.Begin(ThermalTarget.Cpu, CpuTemperatureWatch.NoDriver);

        // Aucune mesure réussie (première mesure bloquée) : l'alarme n'est pas « aucune » par défaut pour toujours.
        clock.Advance(15);
        Assert.Null(watchdog.AbortReason());
        clock.Advance(1);
        var reason = watchdog.AbortReason();
        Assert.NotNull(reason);
        Assert.Contains("d'où cet arrêt par prudence", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Dernière erreur", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Processor_temperature_that_disappears_during_the_test_stops_it_after_the_grace_delay()
    {
        var clock = new FakeClock();
        var watchdog = new ThermalWatchdog(clock.Now);
        watchdog.Begin(ThermalTarget.Cpu, CpuTemperatureWatch.Watched);
        watchdog.Record(Sample(cpuC: 80, tjMax: 100), null, pawnIoInstalled: true);

        // PawnIO ne répond plus : les mesures continuent, mais sans la température du processeur.
        for (var second = 1; second <= 15; second++)
        {
            clock.Advance(1);
            watchdog.Record(Sample(), null, pawnIoInstalled: true);
            Assert.Null(watchdog.AbortReason());
        }

        clock.Advance(1);
        watchdog.Record(Sample(), null, pawnIoInstalled: true);
        var reason = watchdog.AbortReason();
        Assert.NotNull(reason);
        Assert.StartsWith("La température du processeur n'est plus lue depuis 16 s", reason, StringComparison.Ordinal);
        Assert.Contains("PawnIO", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Temperature_read_before_the_test_must_keep_arriving()
    {
        var clock = new FakeClock();
        var watchdog = new ThermalWatchdog(clock.Now);
        watchdog.Begin(ThermalTarget.Cpu, CpuTemperatureWatch.Watched);

        clock.Advance(10);
        watchdog.Record(Sample(), Limit, pawnIoInstalled: true);
        Assert.Null(watchdog.AbortReason());
        clock.Advance(6);
        watchdog.Record(Sample(), Limit, pawnIoInstalled: true);
        Assert.Contains("La température du processeur n'est plus lue", watchdog.AbortReason(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CpuTemperatureWatch.NoDriver, false, null)]
    [InlineData(CpuTemperatureWatch.NoTemperature, true, null)]
    [InlineData(CpuTemperatureWatch.NoLimit, true, 70.0)]
    public void Temperature_that_was_not_watched_at_the_start_changes_nothing(CpuTemperatureWatch atStart, bool pawnIo, double? cpuC)
    {
        // L'utilisateur en a été prévenu à la confirmation (Caveat) : le test continue, comme avant.
        Assert.NotNull(ThermalWatchdog.Caveat(atStart));
        var clock = new FakeClock();
        var watchdog = new ThermalWatchdog(clock.Now);
        watchdog.Begin(ThermalTarget.Cpu, atStart);
        for (var second = 0; second < 3600; second += 5)
        {
            clock.Advance(5);
            watchdog.Record(Sample(cpuC: cpuC), null, pawnIo);
            Assert.Null(watchdog.AbortReason());
            Assert.Null(watchdog.Notice());
        }
    }

    [Fact]
    public void Expected_temperature_that_never_arrives_is_announced_without_stopping_the_test()
    {
        // Aucune mesure avant la confirmation, PawnIO installé : rien n'a été annoncé, la température est attendue.
        var clock = new FakeClock();
        var watchdog = new ThermalWatchdog(clock.Now);
        watchdog.Begin(ThermalTarget.Cpu, CpuTemperatureWatch.Pending);

        for (var second = 1; second <= 15; second++)
        {
            clock.Advance(1);
            watchdog.Record(Sample(), Limit, pawnIoInstalled: true);
            Assert.Null(watchdog.Notice());
        }

        clock.Advance(1);
        watchdog.Record(Sample(), Limit, pawnIoInstalled: true);
        Assert.Null(watchdog.AbortReason());
        Assert.Equal(ThermalWatchdog.Caveat(CpuTemperatureWatch.NoTemperature), watchdog.Notice());

        // Lue avec sa limite, mais sans limite connue : l'avertissement le dit.
        watchdog.Record(Sample(cpuC: 70), null, pawnIoInstalled: true);
        Assert.Equal(ThermalWatchdog.Caveat(CpuTemperatureWatch.NoLimit), watchdog.Notice());
    }

    [Fact]
    public void Expected_temperature_that_arrives_then_disappears_stops_the_test()
    {
        var clock = new FakeClock();
        var watchdog = new ThermalWatchdog(clock.Now);
        watchdog.Begin(ThermalTarget.Cpu, CpuTemperatureWatch.Pending);

        // Le pilote s'ouvre en arrière-plan : la température arrive au bout de quelques secondes.
        clock.Advance(4);
        watchdog.Record(Sample(), Limit, pawnIoInstalled: true);
        clock.Advance(1);
        watchdog.Record(Sample(cpuC: 75), Limit, pawnIoInstalled: true);
        clock.Advance(20);
        watchdog.Record(Sample(cpuC: 76), Limit, pawnIoInstalled: true);
        Assert.Null(watchdog.AbortReason());
        Assert.Null(watchdog.Notice());

        clock.Advance(16);
        watchdog.Record(Sample(), Limit, pawnIoInstalled: true);
        Assert.Contains("La température du processeur n'est plus lue", watchdog.AbortReason(), StringComparison.Ordinal);
    }

    [Fact]
    public void Graphics_test_watches_the_card_temperature_not_the_processor()
    {
        var clock = new FakeClock();
        var watchdog = new ThermalWatchdog(clock.Now);
        watchdog.Begin(ThermalTarget.Gpu, CpuTemperatureWatch.Watched);
        watchdog.Record(Sample(cpuC: 60, gpuC: 70, gpuSlowdown: 90), Limit, pawnIoInstalled: true);

        // Température du processeur perdue pendant un test de la mémoire vidéo : pas d'arrêt pour ça.
        clock.Advance(20);
        watchdog.Record(Sample(gpuC: 72, gpuSlowdown: 90), Limit, pawnIoInstalled: true);
        Assert.Null(watchdog.AbortReason());

        // Température de la carte perdue (pilote graphique réinitialisé) : arrêt par prudence.
        clock.Advance(16);
        watchdog.Record(Sample(), Limit, pawnIoInstalled: true);
        Assert.StartsWith("La température de la carte graphique n'est plus lue depuis 16 s", watchdog.AbortReason(), StringComparison.Ordinal);
    }

    [Fact]
    public void Begin_forgets_the_previous_test()
    {
        var clock = new FakeClock();
        var watchdog = new ThermalWatchdog(clock.Now);
        watchdog.Begin(ThermalTarget.Cpu, CpuTemperatureWatch.Watched);
        watchdog.Record(Sample(cpuC: Limit + 6), Limit, pawnIoInstalled: true);
        watchdog.End();

        clock.Advance(600);
        watchdog.Begin(ThermalTarget.Cpu, CpuTemperatureWatch.NoDriver);
        Assert.True(watchdog.IsRunning);
        Assert.Null(watchdog.AbortReason());
    }

    [Fact]
    public void Assessment_follows_the_same_limit_as_the_alarms()
    {
        Assert.Equal(CpuTemperatureWatch.Pending, ThermalWatchdog.Assess(null, null, pawnIoInstalled: true));
        Assert.Equal(CpuTemperatureWatch.NoDriver, ThermalWatchdog.Assess(null, Limit, pawnIoInstalled: false));
        Assert.Equal(CpuTemperatureWatch.NoDriver, ThermalWatchdog.Assess(Sample(), Limit, pawnIoInstalled: false));
        Assert.Equal(CpuTemperatureWatch.NoTemperature, ThermalWatchdog.Assess(Sample(), Limit, pawnIoInstalled: true));
        Assert.Equal(CpuTemperatureWatch.NoLimit, ThermalWatchdog.Assess(Sample(cpuC: 60), null, pawnIoInstalled: true));
        Assert.Equal(CpuTemperatureWatch.Watched, ThermalWatchdog.Assess(Sample(cpuC: 60, tjMax: 100), null, pawnIoInstalled: true));
        Assert.Equal(CpuTemperatureWatch.Watched, ThermalWatchdog.Assess(Sample(cpuC: 60), Limit, pawnIoInstalled: true));

        // Rien à signaler quand la température est lue avec sa limite, ou attendue.
        Assert.Null(ThermalWatchdog.Caveat(CpuTemperatureWatch.Watched));
        Assert.Null(ThermalWatchdog.Caveat(CpuTemperatureWatch.Pending));
        Assert.Contains("limite publiée", ThermalWatchdog.Caveat(CpuTemperatureWatch.NoLimit), StringComparison.Ordinal);
    }

    [Fact]
    public void Driver_that_stops_answering_in_the_combined_source_stops_the_test()
    {
        // Chaîne réelle : lecture PawnIO qui se bloque, valeurs retirées après 6 s, arrêt par prudence après le délai de grâce.
        using var hang = new ManualResetEventSlim(true);
        var basic = new StepBasic();
        using var source = new CombinedSensorSource(basic, new BlockingAdvanced(hang), TimeSpan.FromMilliseconds(100));
        var clock = new FakeClock();
        var watchdog = new ThermalWatchdog(clock.Now);

        var first = source.Sample();
        Assert.Equal(ThermalWatchdog.Assess(first, Limit, pawnIoInstalled: true), CpuTemperatureWatch.Watched);
        watchdog.Begin(ThermalTarget.Cpu, CpuTemperatureWatch.Watched);
        watchdog.Record(first, Limit, pawnIoInstalled: true);

        hang.Reset();
        string? reason = null;
        for (var second = 1; second <= 30 && reason is null; second++)
        {
            clock.Advance(1);
            basic.At = Start.AddSeconds(second);
            watchdog.Record(source.Sample(), Limit, pawnIoInstalled: true);
            reason = watchdog.AbortReason();
        }

        hang.Set();
        Assert.NotNull(reason);
        Assert.Contains("La température du processeur n'est plus lue", reason, StringComparison.Ordinal);
        Assert.True(clock.Elapsed > ThermalWatchdog.Grace);
        Assert.True(clock.Elapsed <= ThermalWatchdog.Grace + TimeSpan.FromSeconds(1));
    }

    private sealed class FakeClock
    {
        public TimeSpan Elapsed { get; private set; }

        public TimeSpan Now() => Elapsed;

        public void Advance(double seconds) => Elapsed += TimeSpan.FromSeconds(seconds);
    }

    private sealed class StepBasic : ISensorSource
    {
        public DateTimeOffset At { get; set; } = Start;

        public SensorSnapshot Sample() => new() { At = At, CpuPercent = 100 };

        public void Dispose()
        {
        }
    }

    private sealed class BlockingAdvanced(ManualResetEventSlim gate) : IAdvancedSensors
    {
        public IReadOnlyList<HardwareReading> Read()
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return [new HardwareReading("AMD Ryzen 7 7800X3D", ReadingGroup.Cpu, "Core (Tctl/Tdie)", ReadingKind.Temperature, 72)];
        }

        public void Dispose()
        {
        }
    }
}
