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
            new BenchmarkTestResult("ring", "gpu", 32.4, "images par seconde", gpu, 29.1) { Sensors = new BenchmarkSensorSummary { Samples = 90, MaxTemperatureC = 74, AverageClockMhz = 1875 } },
            new BenchmarkTestResult("cpu-render", "cpu", 6.6, "millions de rayons par seconde", 9500),
        ],
    };

    [Theory]
    [InlineData("0.7.1", 1)]
    [InlineData("0.7.4", 1)]
    [InlineData("0.7.5", 2)]
    [InlineData("0.8.0", 2)]
    [InlineData("0.8.5", 2)]
    [InlineData("0.8.6", 3)]
    [InlineData("0.6.2", 0)]
    public void Benchmark_edition_follows_the_version_that_measured_it(string version, int edition) =>
        Assert.Equal(edition, BenchmarkScoring.EditionOf(Sample() with { Version = version }));

    [Fact]
    public void The_edition_written_in_a_result_wins_over_its_version_and_survives_the_share_code()
    {
        var measured = Sample() with { Version = "0.8.6", Edition = BenchmarkScoring.CurrentEdition };
        var before = Sample() with { Version = "0.8.6" };

        Assert.Equal(4, BenchmarkScoring.EditionOf(measured));
        Assert.Equal(3, BenchmarkScoring.EditionOf(before));
        Assert.Equal(4, BenchmarkScoring.EditionOf(BenchmarkShareCode.Decode(BenchmarkShareCode.Encode(measured))!));
        Assert.Equal(3, BenchmarkScoring.EditionOf(BenchmarkShareCode.Decode(BenchmarkShareCode.Encode(before))!));
        Assert.False(BenchmarkComparison.Comparable(measured, before));
        Assert.Contains(BenchmarkComparison.Differences(measured, before), n => n.Contains("Éditions du benchmark", StringComparison.Ordinal));

        var relu = System.Text.Json.JsonSerializer.Deserialize<BenchmarkReport>(BenchmarkHistoryStore.Serialize(measured), BenchmarkHistoryStore.Json)!;
        Assert.Equal(4, BenchmarkScoring.EditionOf(relu));
    }

    [Fact]
    public void An_old_result_read_back_does_not_become_the_current_edition()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<BenchmarkReport>("{\"version\":\"0.8.5\",\"api\":\"Direct3D 12\"}", BenchmarkHistoryStore.Json)!;

        Assert.Equal(2, BenchmarkScoring.EditionOf(old));
    }

    [Fact]
    public void Results_of_different_editions_or_test_sets_are_flagged_and_the_overall_score_is_not_compared()
    {
        var mine = Sample();
        var older = Sample() with { Version = "0.7.1" };
        var gpuOnly = Sample() with { Tests = [Sample().Tests[0]] };

        Assert.True(BenchmarkComparison.Comparable(mine, Sample(gpu: 8000)));
        Assert.Empty(BenchmarkComparison.Differences(mine, Sample(gpu: 8000)));
        Assert.False(BenchmarkComparison.Comparable(mine, older));
        Assert.Contains(BenchmarkComparison.Differences(mine, older), n => n.Contains("Versions du benchmark", StringComparison.Ordinal));
        Assert.Contains(BenchmarkComparison.Differences(mine, gpuOnly), n => n.Contains("mêmes tests", StringComparison.Ordinal));
        Assert.DoesNotContain(BenchmarkComparison.Compare(mine, gpuOnly), r => r.Id == "overall");
        Assert.Contains(BenchmarkComparison.Differences(mine, Sample() with { Api = "Direct3D 11" }), n => n.Contains("Interfaces", StringComparison.Ordinal));
        Assert.Contains(BenchmarkComparison.Differences(mine, Sample() with { Completed = false }), n => n.Contains("interrompue", StringComparison.Ordinal));
    }

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
    public void No_gap_is_given_between_two_editions_but_the_points_stay_readable()
    {
        var current = Sample(gpu: 10000) with { Version = "0.8.6", Edition = BenchmarkScoring.CurrentEdition, OverallScore = 10600, CpuScore = 11000 };
        var older = Sample(gpu: 10500) with { Version = "0.8.5", OverallScore = 12968, CpuScore = 44000 };

        var rows = BenchmarkComparison.Compare(current, older);

        Assert.All(rows, row => Assert.Null(row.Difference));
        Assert.Equal(44000, rows.Single(r => r.Id == "cpu").Theirs);
        Assert.Equal(11000, rows.Single(r => r.Id == "cpu").Mine);
        Assert.NotNull(BenchmarkComparison.Compare(current, current with { GpuScore = 12000 }).Single(r => r.Id == "gpu").Difference);
    }

    [Fact]
    public void The_page_says_when_a_result_comes_from_an_older_edition()
    {
        var current = Sample() with { Version = "0.8.6", Edition = BenchmarkScoring.CurrentEdition };
        var older = Sample() with { Version = "0.8.5" };

        Assert.Empty(BenchmarkTexts.OldEditionWarning(current));
        Assert.Contains("ancienne version", BenchmarkTexts.OldEditionWarning(older), StringComparison.Ordinal);
        Assert.Contains("4 fois trop", BenchmarkTexts.OldEditionWarning(older), StringComparison.Ordinal);
        Assert.DoesNotContain("4 fois trop", BenchmarkTexts.OldEditionWarning(older with { Edition = BenchmarkScoring.CpuRecalibratedEdition }), StringComparison.Ordinal);
        Assert.Contains("éditions précédentes", BenchmarkTexts.EditionNote(current), StringComparison.Ordinal);
        Assert.Contains("l'édition actuelle (4)", BenchmarkTexts.EditionNote(older), StringComparison.Ordinal);
    }

    [Fact]
    public void Ray_tracing_keeps_its_own_score_in_the_code_and_the_comparison()
    {
        var mine = Sample() with
        {
            RayTracingScore = 10000,
            Tests = [.. Sample().Tests, new BenchmarkTestResult("raytracing", "rt", 40.2, "images par seconde", 10000, 35.0)],
        };
        var theirs = Sample();

        var decoded = BenchmarkShareCode.Decode(BenchmarkShareCode.Encode(mine))!;
        var rows = BenchmarkComparison.Compare(mine, theirs);

        Assert.Equal(10000, decoded.RayTracingScore);
        Assert.Equal("rt", decoded.Tests.Single(t => t.Id == "raytracing").Device);
        Assert.Equal(0, BenchmarkShareCode.Decode(BenchmarkShareCode.Encode(theirs))!.RayTracingScore);
        Assert.Null(rows.Single(r => r.Id == "rt").Difference);
        Assert.DoesNotContain(BenchmarkComparison.Compare(theirs, theirs), r => r.Id == "rt");
    }

    [Fact]
    public void A_different_screen_no_longer_matters_since_the_final_image_is_computed_at_the_render_size()
    {
        var mine = Sample() with { DisplayResolution = "1920×1080", Edition = BenchmarkScoring.CurrentEdition };
        var theirs = Sample() with { DisplayResolution = "3840×2160", Edition = BenchmarkScoring.CurrentEdition };

        Assert.True(BenchmarkComparison.Comparable(mine, theirs));
        Assert.Empty(BenchmarkComparison.Differences(mine, theirs));
    }

    [Fact]
    public void A_different_screen_is_still_announced_between_older_passes_but_does_not_block_the_comparison()
    {
        var mine = Sample() with { DisplayResolution = "1920×1080" };
        var theirs = Sample() with { DisplayResolution = "3840×2160" };

        Assert.Equal("3840×2160", BenchmarkShareCode.Decode(BenchmarkShareCode.Encode(theirs))!.DisplayResolution);
        Assert.True(BenchmarkComparison.Comparable(mine, theirs));
        Assert.Contains(BenchmarkComparison.Differences(mine, theirs), n => n.Contains("3840×2160", StringComparison.Ordinal));
        Assert.Empty(BenchmarkComparison.Differences(mine, Sample()));
    }

    [Fact]
    public void A_test_missing_on_one_side_has_no_gap()
    {
        var theirs = Sample() with { Tests = [] };

        var row = BenchmarkComparison.Compare(Sample(), theirs).Single(r => r.Id == "ring");

        Assert.Null(row.Difference);
        Assert.Equal(0, row.Theirs);
    }

    [Theory]
    [InlineData(@"C:\u\MAUS\benchmark\MAUS-benchmark-20261031-120000.png", true)]
    [InlineData(@"C:\u\MAUS\BENCHMARK\maus-benchmark-1.PNG", true)]
    [InlineData(@"C:\u\MAUS\benchmark\autre.png", false)]
    [InlineData(@"C:\u\MAUS\benchmark\MAUS-benchmark-1.exe", false)]
    [InlineData(@"C:\u\MAUS\benchmark\sous\MAUS-benchmark-1.png", false)]
    [InlineData(@"C:\u\MAUS\benchmark\..\MAUS-benchmark-1.png", false)]
    [InlineData(@"C:\Windows\MAUS-benchmark-1.png", false)]
    [InlineData(@"\\serveur\partage\MAUS-benchmark-1.png", false)]
    [InlineData("//serveur/partage/MAUS-benchmark-1.png", false)]
    public void Only_an_image_of_the_maus_folder_is_opened(string path, bool accepted) =>
        Assert.Equal(accepted, new Maus.Core.Workshop.Benchmark.BenchmarkHistoryStore(@"C:\u\MAUS\benchmark-history.json").IsOwnImage(path));
}
