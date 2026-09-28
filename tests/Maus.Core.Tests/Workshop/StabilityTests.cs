using Maus.Core.Workshop;

namespace Maus.Core.Tests.Workshop;

public class StabilityTests
{
    [Fact]
    public void Cpu_round_is_deterministic()
    {
        Assert.Equal(CpuTest.Round(0), CpuTest.Round(0));
        Assert.NotEqual(CpuTest.Round(0), CpuTest.Round(1));
    }

    [Fact]
    public async Task Healthy_cpu_has_no_calculation_error()
    {
        var result = await CpuTest.RunAsync(new CpuTestOptions(TimeSpan.FromMilliseconds(400), Threads: 2));

        Assert.True(result.Stable);
        Assert.True(result.Rounds > 0);
        Assert.True(result.Score > 0);
        Assert.False(result.Aborted);
    }

    [Theory]
    [InlineData(CpuStressMode.Avx)]
    [InlineData(CpuStressMode.Heavy)]
    public void Vector_loads_are_deterministic_and_bounded(CpuStressMode mode)
    {
        var first = CpuStress.Round(mode);

        Assert.Equal(first, CpuStress.Round(mode));
        Assert.NotEqual(0UL, first);
        Assert.False(string.IsNullOrWhiteSpace(CpuStress.Instructions(mode)));
    }

    [Theory]
    [InlineData(CpuStressMode.Automatic, "cpu-multi")]
    [InlineData(CpuStressMode.Avx, "cpu-avx")]
    [InlineData(CpuStressMode.Heavy, "cpu-heavy")]
    public async Task Every_load_mode_runs_verified_rounds_and_keeps_its_own_score(CpuStressMode mode, string kind)
    {
        var result = await CpuTest.RunAsync(new CpuTestOptions(TimeSpan.FromMilliseconds(300), Threads: 2, mode));

        Assert.True(result.Stable);
        Assert.True(result.Rounds > 0);
        Assert.Equal(mode, result.Mode);
        Assert.Equal(kind, ScoreTrends.CpuKind(mode));
        Assert.NotEqual(kind, ScoreTrends.Label(kind));
    }

    [Fact]
    public async Task A_wrong_vector_result_marks_the_cpu_unstable()
    {
        var options = new CpuTestOptions(TimeSpan.FromMilliseconds(300), Threads: 2, CpuStressMode.Heavy) { Fault = (thread, round, value) => thread == 0 && round == 1 ? value ^ 4 : value };

        var result = await CpuTest.RunAsync(options);

        Assert.Equal(1, result.Errors);
    }

    [Fact]
    public async Task A_single_wrong_result_marks_the_cpu_unstable()
    {
        var options = new CpuTestOptions(TimeSpan.FromMilliseconds(400), Threads: 2) { Fault = (thread, round, value) => thread == 1 && round == 3 ? value ^ 1 : value };

        var result = await CpuTest.RunAsync(options);

        Assert.Equal(1, result.Errors);
        Assert.False(result.Stable);
    }

    [Fact]
    public async Task Safety_alarm_stops_the_test_with_its_reason()
    {
        var result = await CpuTest.RunAsync(new CpuTestOptions(TimeSpan.FromSeconds(30), Threads: 1), abortCheck: () => "trop chaud");

        Assert.True(result.Aborted);
        Assert.Equal("trop chaud", result.AbortReason);
        Assert.True(result.Duration < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Healthy_memory_passes_every_pattern_and_is_measured()
    {
        var steps = new List<string>();
        var result = await MemoryTest.RunAsync(new MemoryTestOptions(32L << 20), new SyncProgress<MemoryTestProgress>(p => steps.Add(p.Step)));

        Assert.True(result.Stable);
        Assert.Equal(4, steps.Count);
        Assert.True(result.CopyGigabytesPerSecond > 0);
        Assert.True(result.LatencyNanoseconds > 0);
    }

    [Fact]
    public async Task Ram_test_stops_itself_on_an_overheat_alarm()
    {
        var checks = 0;

        var result = await MemoryTest.RunAsync(new MemoryTestOptions(256L << 20), abortCheck: () => ++checks >= 2 ? "trop chaud" : null);

        Assert.True(result.Aborted);
        Assert.Equal("trop chaud", result.AbortReason);
        Assert.Null(result.CopyGigabytesPerSecond);
    }

    [Fact]
    public async Task Flipped_bit_is_found_with_its_offset()
    {
        var options = new MemoryTestOptions(8L << 20) { Fault = (block, pattern) => { if (pattern == 2) { block[1000] ^= 1UL << 7; } } };

        var result = await MemoryTest.RunAsync(options);

        Assert.Equal(1, result.Errors);
        Assert.Equal(8000, result.FirstErrorOffsets.Single());
    }

    [Theory]
    [InlineData(100L << 20, 256L << 20)]
    [InlineData(8L << 30, 4L << 30)]
    [InlineData(64L << 30, 16L << 30)]
    public void Suggested_size_is_half_the_free_memory_within_limits(long available, long expected)
    {
        Assert.Equal(expected, MemoryTest.SuggestedBytes(available));
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

public sealed class BenchmarkHistoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"maus-bench-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Scores_are_kept_and_compared_to_previous_stable_runs()
    {
        var history = new BenchmarkHistory(_path);
        var start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        history.Add(new BenchmarkEntry("cpu-multi", 100, true, start));
        history.Add(new BenchmarkEntry("cpu-multi", 50, false, start.AddDays(1)));
        history.Add(new BenchmarkEntry("ram", 30, true, start.AddDays(2)));

        var current = new BenchmarkEntry("cpu-multi", 110, true, start.AddDays(3));

        Assert.Equal(3, history.Load().Count);
        Assert.Equal(10, BenchmarkHistory.CompareToPrevious(history.Load(), current));
        Assert.Null(BenchmarkHistory.CompareToPrevious([], current));
    }
}
