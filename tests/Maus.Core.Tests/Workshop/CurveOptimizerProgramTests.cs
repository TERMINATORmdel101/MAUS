using Maus.Core.Workshop;

namespace Maus.Core.Tests.Workshop;

public class CurveOptimizerProgramTests
{
    private sealed class FakeTopology(int cores) : ICoreTopology
    {
        public List<int> Pinned { get; } = [];

        public IReadOnlyList<CpuCore> Cores() => Enumerable.Range(0, cores).Select(i => new CpuCore(i, 0, 1UL << (2 * i), 0)).ToList();

        public bool PinCurrentThread(CpuCore core)
        {
            lock (Pinned)
            {
                Pinned.Add(core.Index);
            }

            return true;
        }
    }

    private sealed class FakeCheckpoint : ICoreTestCheckpoint
    {
        public List<CoreTestCheckpoint> Saved { get; } = [];

        public CoreTestCheckpoint? Current { get; private set; }

        public CoreTestCheckpoint? Load() => Current;

        public void Save(CoreTestCheckpoint state)
        {
            lock (Saved)
            {
                Saved.Add(state);
            }

            Current = state;
        }

        public void Clear() => Current = null;
    }

    /// <summary>Programme accéléré : pics et chutes de 60 ms, rafales de 150 à 300 ms, repos de 100 ms.</summary>
    private static CurveProgramOptions Quick(TimeSpan cycling, TimeSpan transients, CpuStressMode load = CpuStressMode.Automatic) => new(cycling, transients, load)
    {
        Spike = TimeSpan.FromMilliseconds(60),
        Drop = TimeSpan.FromMilliseconds(60),
        MinBurst = TimeSpan.FromMilliseconds(150),
        MaxBurst = TimeSpan.FromMilliseconds(300),
        Rest = TimeSpan.FromMilliseconds(100),
        FreezeAfter = TimeSpan.FromMilliseconds(800),
        Threads = 3,
    };

    [Fact]
    public async Task Both_phases_run_rotate_over_every_core_and_clear_the_trace()
    {
        var topology = new FakeTopology(3);
        var checkpoint = new FakeCheckpoint();
        var reports = new List<CurveProgramProgress>();

        var result = await CurveOptimizerProgram.RunAsync(topology, checkpoint, Quick(TimeSpan.FromMilliseconds(900), TimeSpan.FromMilliseconds(900)),
            new SyncProgress<CurveProgramProgress>(reports.Add));

        Assert.True(result.Stable);
        Assert.False(result.Aborted);
        Assert.All(result.Cores, c => Assert.True(c.Rounds > 0));
        Assert.Equal([0, 1, 2], topology.Pinned.Take(3));
        Assert.True(result.Bursts >= 2);
        Assert.True(result.TransientRounds > 0);
        Assert.Null(checkpoint.Current);
        Assert.Contains(checkpoint.Saved, s => s is { Phase: 1, Core: 1 });
        Assert.Contains(checkpoint.Saved, s => s is { Phase: 2, Core: -1 });
        Assert.Contains(reports, r => r.Phase == 1);
        Assert.Contains(reports, r => r.Phase == 2);
    }

    [Fact]
    public async Task Wrong_result_in_phase_1_names_the_core()
    {
        var options = Quick(TimeSpan.FromMilliseconds(700), TimeSpan.Zero) with { Fault = (core, round, value) => core == 2 ? value ^ 1 : value };

        var result = await CurveOptimizerProgram.RunAsync(new FakeTopology(3), null, options);

        Assert.False(result.Stable);
        Assert.True(result.Cores.Single(c => c.Core == 2).Errors > 0);
        Assert.True(result.Cores.Where(c => c.Core != 2).All(c => c.Stable));
        Assert.Equal(0, result.Bursts);
    }

    [Fact]
    public async Task Silent_core_in_phase_1_is_declared_frozen_and_the_program_goes_on()
    {
        var options = Quick(TimeSpan.FromMilliseconds(2500), TimeSpan.Zero) with { Stall = core => core == 1 };

        var result = await CurveOptimizerProgram.RunAsync(new FakeTopology(3), null, options);

        Assert.True(result.Cores.Single(c => c.Core == 1).Froze);
        Assert.True(result.Cores.Single(c => c.Core == 0).Stable);
    }

    [Fact]
    public async Task Wrong_result_during_transients_is_reported_for_the_whole_processor()
    {
        // Une seule erreur, sur le fil 1 (le compteur de tours est partagé par tous les fils).
        var injected = new int[1];
        var options = Quick(TimeSpan.Zero, TimeSpan.FromMilliseconds(700), CpuStressMode.Fma) with
        {
            Fault = (thread, round, value) => thread == 1 && Interlocked.Exchange(ref injected[0], 1) == 0 ? value ^ 8 : value,
        };

        var result = await CurveOptimizerProgram.RunAsync(new FakeTopology(2), null, options);

        Assert.Equal(1, result.TransientErrors);
        Assert.False(result.TransientsStable);
        Assert.False(result.Stable);
    }

    [Fact]
    public async Task Frozen_thread_during_transients_stops_the_phase()
    {
        var options = Quick(TimeSpan.Zero, TimeSpan.FromSeconds(20)) with { Stall = id => id == 1001 };

        var result = await CurveOptimizerProgram.RunAsync(new FakeTopology(2), null, options);

        Assert.True(result.TransientFroze);
        Assert.True(result.Duration < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Safety_alarm_and_user_stop_end_the_program_with_their_reason()
    {
        var alarm = await CurveOptimizerProgram.RunAsync(new FakeTopology(2), null, Quick(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)), danger: () => "trop chaud");
        Assert.True(alarm.Aborted);
        Assert.Contains("trop chaud", alarm.AbortReason, StringComparison.Ordinal);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var stopped = await CurveOptimizerProgram.RunAsync(new FakeTopology(2), null, Quick(TimeSpan.Zero, TimeSpan.FromSeconds(30)), cancellationToken: cancellation.Token);
        Assert.True(stopped.Aborted);
        Assert.True(stopped.Duration < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Bursts_vary_between_five_and_ten_seconds()
    {
        var options = new CurveProgramOptions(TimeSpan.FromHours(2), TimeSpan.FromHours(2));

        var lengths = Enumerable.Range(0, 6).Select(options.BurstLength).ToList();

        Assert.Equal(TimeSpan.FromSeconds(5), lengths.Min());
        Assert.Equal(TimeSpan.FromSeconds(10), lengths.Max());
        Assert.Equal(6, lengths.Distinct().Count());
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            lock (this)
            {
                report(value);
            }
        }
    }
}
