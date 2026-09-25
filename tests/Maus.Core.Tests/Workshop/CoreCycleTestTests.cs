using Maus.Core.Workshop;

namespace Maus.Core.Tests.Workshop;

public class CoreCycleTestTests
{
    private sealed class FakeTopology(int cores, bool pin = true) : ICoreTopology
    {
        public List<int> Pinned { get; } = [];

        public IReadOnlyList<CpuCore> Cores() => Enumerable.Range(0, cores).Select(i => new CpuCore(i, 0, 1UL << (2 * i), 0)).ToList();

        public bool PinCurrentThread(CpuCore core)
        {
            lock (Pinned)
            {
                Pinned.Add(core.Index);
            }

            return pin;
        }
    }

    private sealed class FakeCheckpoint : ICoreTestCheckpoint
    {
        public List<CoreTestCheckpoint> Saved { get; } = [];

        public CoreTestCheckpoint? Current { get; private set; }

        public CoreTestCheckpoint? Load() => Current;

        public void Save(CoreTestCheckpoint state)
        {
            Saved.Add(state);
            Current = state;
        }

        public void Clear() => Current = null;
    }

    private static readonly CoreCycleOptions Quick = new(TimeSpan.FromMilliseconds(250));

    [Fact]
    public async Task Every_core_is_tested_alone_and_the_trace_is_cleared()
    {
        var topology = new FakeTopology(3);
        var checkpoint = new FakeCheckpoint();

        var result = await CoreCycleTest.RunAsync(topology, checkpoint, Quick);

        Assert.True(result.Stable);
        Assert.Equal([0, 1, 2], result.Cores.Select(c => c.Core));
        Assert.All(result.Cores, c => Assert.True(c.Rounds > 0));
        Assert.Equal([0, 1, 2], topology.Pinned.Order());
        Assert.Equal([0, 1, 2], checkpoint.Saved.Select(s => s.Core));
        Assert.Null(checkpoint.Current);
    }

    [Fact]
    public async Task A_calculation_error_points_to_its_core()
    {
        var options = Quick with { Fault = (core, round, value) => core == 1 && round == 3 ? value ^ 1 : value };

        var result = await CoreCycleTest.RunAsync(new FakeTopology(3), null, options);

        Assert.False(result.Stable);
        Assert.Equal(1, result.Cores[1].Errors);
        Assert.True(result.Cores[0].Stable);
        Assert.True(result.Cores[2].Stable);
    }

    [Fact]
    public async Task A_core_that_stops_answering_is_reported_as_frozen_and_the_test_moves_on()
    {
        var options = new CoreCycleOptions(TimeSpan.FromSeconds(3)) { FreezeAfter = TimeSpan.FromMilliseconds(300), Stall = core => core == 0 };

        var result = await CoreCycleTest.RunAsync(new FakeTopology(2), null, options with { PerCore = TimeSpan.FromMilliseconds(900) });

        Assert.True(result.Cores[0].Froze);
        Assert.False(result.Cores[1].Froze);
        Assert.True(result.Cores[1].Rounds > 0);
    }

    [Fact]
    public async Task A_danger_alarm_stops_everything()
    {
        var result = await CoreCycleTest.RunAsync(new FakeTopology(4), null, Quick, danger: () => "processeur à 96 °C");

        Assert.True(result.Aborted);
        Assert.Contains("96 °C", result.AbortReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stopping_the_test_also_clears_the_trace()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var checkpoint = new FakeCheckpoint();

        var result = await CoreCycleTest.RunAsync(new FakeTopology(2), checkpoint, Quick, cancellationToken: cancellation.Token);

        Assert.True(result.Aborted);
        Assert.Null(checkpoint.Current);
    }

    [Fact]
    public async Task Hardware_errors_logged_during_the_test_make_it_unstable()
    {
        var result = await CoreCycleTest.RunAsync(new FakeTopology(1), null, Quick, wheaSince: _ => 2);

        Assert.All(result.Cores, c => Assert.True(c.Stable));
        Assert.Equal(2, result.WheaEvents);
        Assert.False(result.Stable);
    }

    [Fact]
    public async Task A_refused_pinning_is_reported()
    {
        var result = await CoreCycleTest.RunAsync(new FakeTopology(1, pin: false), null, Quick);

        Assert.False(result.Cores[0].Pinned);
    }

    [Theory]
    [InlineData(10, 8, 75, 1)]
    [InlineData(1, 16, 15, 1)]
    [InlineData(240, 8, 120, 15)]
    [InlineData(20, 16, 75, 1)]
    public void A_long_test_comes_back_several_times_to_each_core(int minutes, int cores, int secondsPerCore, int rounds)
    {
        var plan = CoreCycleTest.Plan(TimeSpan.FromMinutes(minutes), cores);

        Assert.Equal(TimeSpan.FromSeconds(secondsPerCore), plan.PerCore);
        Assert.Equal(rounds, plan.Rounds);
    }
}
