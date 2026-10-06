using Maus.Core.Workshop.Benchmark;

namespace Maus.Core.Tests.Workshop;

public sealed class BenchmarkShareTests
{
    private static BenchmarkReport Sample(double gpu = 9000, string resolution = "1920×1080") => new()
    {
        Date = new DateTimeOffset(2026, 10, 6, 15, 0, 0, TimeSpan.FromHours(2)),
        Version = "0.7.5",
        Api = "Direct3D 12",
        Gpu = "NVIDIA GeForce RTX 2080 Ti",
        Cpu = "Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz",
        Threads = 12,
        RenderResolution = resolution,
        GpuScore = gpu,
        CpuScore = 9500,
        OverallScore = 9120,
        Completed = true,
        Tests =
        [
            new BenchmarkTestResult("ring", "gpu", "geometry", 32.4, "images par seconde", gpu, 29.1) { Sensors = new BenchmarkSensorSummary { Samples = 90, MaxTemperatureC = 74, AverageClockMhz = 1875 } },
            new BenchmarkTestResult("cpu-render", "cpu", "multicore", 6.6, "millions de rayons par seconde", 9500),
        ],
    };

    [Fact]
    public void A_result_survives_the_share_code_even_inside_a_forum_message()
    {
        var report = Sample();
        var message = "Salut ! Voilà mon résultat :\r\nScore combiné : 9 120\r\n" + BenchmarkShareCode.Encode(report) + "\r\nÀ toi !";

        var decoded = BenchmarkShareCode.Decode(message)!;

        Assert.Equal(report.Gpu, decoded.Gpu);
        Assert.Equal(report.RenderResolution, decoded.RenderResolution);
        Assert.Equal(9000, decoded.GpuScore);
        Assert.Equal(2, decoded.Tests.Count);
        Assert.Equal(29.1, decoded.Tests[0].Low1);
        Assert.Equal(74, decoded.Tests[0].Sensors!.MaxTemperatureC);
        Assert.True(BenchmarkShareCode.Encode(report).Length < 800);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pas de code ici")]
    [InlineData("MAUS-BENCH-1:ceci-n-est-pas-du-base64-valable-!!!")]
    [InlineData("MAUS-BENCH-1:AAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Anything_else_is_refused_without_error(string? text) => Assert.Null(BenchmarkShareCode.Decode(text));

    [Fact]
    public void Absurd_values_are_refused()
    {
        var code = BenchmarkShareCode.Encode(Sample(gpu: double.MaxValue / 2));

        Assert.Null(BenchmarkShareCode.Decode(code));
    }

    [Fact]
    public void Comparison_gives_the_gap_of_the_other_result_test_by_test()
    {
        var mine = Sample(gpu: 10000);
        var theirs = Sample(gpu: 12500) with { CpuScore = 9500 };

        var rows = BenchmarkComparison.Compare(mine, theirs);

        Assert.Equal(25, rows.Single(r => r.Id == "gpu").Difference);
        Assert.Equal(0, rows.Single(r => r.Id == "cpu").Difference);
        Assert.Equal(25, rows.Single(r => r.Id == "ring").Difference);
        Assert.True(BenchmarkComparison.Comparable(mine, theirs));
        Assert.False(BenchmarkComparison.Comparable(mine, Sample(resolution: "1280×720")));
    }

    [Fact]
    public void A_test_missing_on_one_side_has_no_gap()
    {
        var theirs = Sample() with { Tests = [] };

        var row = BenchmarkComparison.Compare(Sample(), theirs).Single(r => r.Id == "ring");

        Assert.Null(row.Difference);
        Assert.Equal(0, row.Theirs);
    }
}
