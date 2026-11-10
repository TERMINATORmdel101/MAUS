using Maus.App.ViewModels.Workshop;
using Maus.Core.Workshop.Benchmark;

namespace Maus.App.Tests;

public sealed class BenchmarkDetailTests
{
    private static BenchmarkTestResult Test(string id, string device, double score) => new(id, device, score / 100, "u", score);

    [Fact]
    public void The_longest_bar_is_the_best_score_of_the_list()
    {
        var report = new BenchmarkReport { Tests = [Test("a", "gpu", 10000), Test("b", "gpu", 5000), Test("c", "cpu", 40000)] };

        var rows = WorkshopViewModel.TestRows(report, "gpu");

        Assert.Equal([1, 0.5], rows.Select(r => r.Fill));
    }

    [Fact]
    public void A_scene_is_not_called_slow_because_the_processor_scores_more()
    {
        var report = new BenchmarkReport { Tests = [Test("a", "gpu", 9500), Test("b", "gpu", 9900), Test("c", "cpu", 40000), Test("d", "cpu", 41000)] };

        Assert.All(WorkshopViewModel.TestRows(report, "gpu"), row => Assert.Empty(row.Mark));
    }

    [Fact]
    public void A_real_gap_is_named_without_calling_anything_weak()
    {
        var report = new BenchmarkReport { Tests = [Test("a", "gpu", 16000), Test("b", "gpu", 8000)] };

        var rows = WorkshopViewModel.TestRows(report, "gpu");

        Assert.Equal("Le plus rapide", rows[0].Mark);
        Assert.True(rows[0].IsStrong);
        Assert.Equal("Le moins rapide", rows[1].Mark);
    }

    [Fact]
    public void A_safety_stop_tells_the_reason_written_with_this_pass_and_never_an_older_one()
    {
        var startedAt = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        var stopped = new BenchmarkReport { Date = startedAt.AddMinutes(3), Completed = false, Error = "raison de cette passe" };

        Assert.Equal("raison de cette passe", WorkshopViewModel.SafetyStopText(stopped, startedAt));
        Assert.DoesNotContain("raison", WorkshopViewModel.SafetyStopText(stopped with { Date = startedAt.AddDays(-1) }, startedAt), StringComparison.Ordinal);
        Assert.NotEmpty(WorkshopViewModel.SafetyStopText(null, startedAt));
    }

    [Fact]
    public void Each_row_shows_everything_that_was_measured_and_nothing_else()
    {
        var measured = new BenchmarkTestResult("a", "gpu", 34.1, "u", 9628, 28.4)
        {
            Sensors = new BenchmarkSensorSummary { MaxTemperatureC = 80, AverageClockMhz = 1719, AveragePowerWatts = 233, LimitTemperatureC = 93 },
        };
        var bare = new BenchmarkTestResult("b", "gpu", 34.8, "u", 9429);

        var rows = WorkshopViewModel.TestRows(new BenchmarkReport { Tests = [measured, bare] }, "gpu");

        Assert.Equal(5, rows[0].Values.Count);
        Assert.Single(rows[1].Values);
    }

    [Fact]
    public void The_reassuring_sentence_needs_a_proof_from_the_sensors()
    {
        Assert.NotEmpty(WorkshopViewModel.Health(new BenchmarkSensorSummary { ThermalSlowdownPercent = 0, HardwareBrakePercent = 0 }, "gpu"));
        Assert.Empty(WorkshopViewModel.Health(new BenchmarkSensorSummary { ThermalSlowdownPercent = 3, HardwareBrakePercent = 0 }, "gpu"));
        Assert.Empty(WorkshopViewModel.Health(new BenchmarkSensorSummary { MaxTemperatureC = 80 }, "gpu"));
        Assert.Empty(WorkshopViewModel.Health(null, "gpu"));
        Assert.NotEmpty(WorkshopViewModel.Health(new BenchmarkSensorSummary { MaxTemperatureC = 80, LimitTemperatureC = 100 }, "cpu"));
        Assert.Empty(WorkshopViewModel.Health(new BenchmarkSensorSummary { MaxTemperatureC = 100, LimitTemperatureC = 100 }, "cpu"));
    }

    [Fact]
    public void A_score_that_was_not_measured_gives_no_row()
    {
        var rows = WorkshopViewModel.CombinedRows(new BenchmarkReport { CpuScore = 40000, Cpu = "Processeur d'essai" });

        var row = Assert.Single(rows);
        Assert.Equal("Processeur d'essai", Assert.Single(row.Values).Value);
        Assert.Equal(1, row.Fill);
    }

    [Fact]
    public void An_empty_report_never_divides_by_zero() =>
        Assert.Empty(WorkshopViewModel.TestRows(new BenchmarkReport(), "gpu"));

    [Theory]
    [InlineData("11 805")]
    [InlineData("11,805")]
    [InlineData("72")]
    [InlineData("Score : 1 234 points")]
    public void A_rolling_number_ends_on_the_exact_text(string text) => UiThread.Run(async () =>
    {
        // Une animation n'avance que si une fenêtre est ouverte : celle-ci reste hors de l'écran.
        var block = new System.Windows.Controls.TextBlock();
        var window = new System.Windows.Window { Content = block, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual, Left = -4000, Width = 200, Height = 100 };
        window.Show();

        Maus.App.Appearance.Motion.SetRollingText(block, text);
        await Task.Delay(1100);
        window.Close();

        Assert.Equal(text, block.Text);
    });

    private static readonly double[] LineValues = [1, 2, 3];
    private static readonly int[] LineMarks = [-1, 0, 2, 99];

    private static BenchmarkReport Pass(int day, string? driver, string? build, bool completed = true, string resolution = "1920×1080") => new()
    {
        Date = new DateTimeOffset(2026, 10, day, 12, 0, 0, TimeSpan.Zero),
        Version = "0.8.6",
        Api = "Direct3D 12",
        RenderResolution = resolution,
        Gpu = "Carte A",
        OverallScore = 9000 + day,
        Completed = completed,
        GpuDriver = driver,
        WindowsBuild = build,
    };

    [Fact]
    public void The_marker_sits_under_the_newer_run_and_the_trend_gets_its_mark() => UiThread.Run(() =>
    {
        using var bench = new TestBench();
        var workshop = bench.ViewModel.Workshop;

        workshop.UseBenchmarkHistory([Pass(1, "1", "a"), Pass(2, "1", "a"), Pass(3, "2", "a")]);

        Assert.Equal(["Pilote graphique changé entre ces deux mesures : 1 → 2.", "", ""], workshop.BenchmarkRuns.Select(r => r.Marker));
        Assert.Equal([2], workshop.BenchmarkTrendMarks);
        Assert.Contains("1 → 2", workshop.BenchmarkTrendMarksText, StringComparison.Ordinal);
        Assert.True(workshop.HasBenchmarkMarkers);
        Assert.Equal([9001, 9002, 9003], workshop.BenchmarkTrend);

        return Task.CompletedTask;
    });

    [Fact]
    public void An_old_history_shows_no_marker_at_all() => UiThread.Run(() =>
    {
        using var bench = new TestBench();
        var workshop = bench.ViewModel.Workshop;

        workshop.UseBenchmarkHistory([Pass(1, null, null), Pass(2, null, null), Pass(3, "2", "a")]);

        Assert.All(workshop.BenchmarkRuns, r => Assert.False(r.HasMarker));
        Assert.Empty(workshop.BenchmarkTrendMarks);
        Assert.Null(workshop.BenchmarkTrendMarksText);
        Assert.False(workshop.HasBenchmarkMarkers);

        return Task.CompletedTask;
    });

    [Fact]
    public void An_empty_history_shows_no_marker() => UiThread.Run(() =>
    {
        using var bench = new TestBench();
        var workshop = bench.ViewModel.Workshop;

        workshop.UseBenchmarkHistory([]);

        Assert.Empty(workshop.BenchmarkRuns);
        Assert.Empty(workshop.BenchmarkTrend);
        Assert.Empty(workshop.BenchmarkTrendMarks);
        Assert.False(workshop.HasBenchmarkMarkers);

        return Task.CompletedTask;
    });

    [Fact]
    public void The_trend_compares_its_own_points_and_the_list_compares_its_own_lines() => UiThread.Run(() =>
    {
        using var bench = new TestBench();
        var workshop = bench.ViewModel.Workshop;

        // La passe interrompue n'est pas sur la courbe : le changement y tombe entre les deux points qui restent.
        workshop.UseBenchmarkHistory([Pass(1, "1", "a"), Pass(2, "2", "a", completed: false), Pass(3, "2", "a")]);

        Assert.Equal([1], workshop.BenchmarkTrendMarks);
        Assert.Equal(["", "Pilote graphique changé entre ces deux mesures : 1 → 2.", ""], workshop.BenchmarkRuns.Select(r => r.Marker));

        return Task.CompletedTask;
    });

    [Fact]
    public void The_oldest_line_of_the_list_has_no_marker_because_the_other_run_is_not_shown()
    {
        var runs = Enumerable.Range(1, 13).Select(day => Pass(day, day.ToString(System.Globalization.CultureInfo.InvariantCulture), "a")).ToList();

        var rows = WorkshopViewModel.RunRows(runs);

        Assert.Equal(12, rows.Count);
        Assert.All(rows.Take(11), r => Assert.True(r.HasMarker));
        Assert.False(rows[^1].HasMarker);
    }

    [Fact]
    public void The_list_separates_the_runs_of_another_edition()
    {
        var older = Pass(1, "1", "a") with { Version = "0.8.5" };
        var current = Pass(2, "1", "a") with { Edition = BenchmarkScoring.CurrentEdition };

        var mixed = WorkshopViewModel.RunRows([older, older with { Date = older.Date.AddHours(1) }, current]);

        Assert.Equal([false, true, false], mixed.Select(r => r.HasEditionHeading));
        Assert.True(WorkshopViewModel.RunRows([older])[0].HasEditionHeading);
        Assert.False(WorkshopViewModel.RunRows([current])[0].HasEditionHeading);
    }

    [Fact]
    public void Marks_draw_off_screen_without_error_even_when_out_of_range() => UiThread.Run(() =>
    {
        var line = new Maus.App.Controls.Sparkline { Values = LineValues, Marks = LineMarks, Maximum = 0 };
        line.Measure(new System.Windows.Size(120, 40));
        line.Arrange(new System.Windows.Rect(0, 0, 120, 40));
        var image = new System.Windows.Media.Imaging.RenderTargetBitmap(120, 40, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);

        image.Render(line);

        Assert.Equal(120, image.PixelWidth);

        return Task.CompletedTask;
    });

    [Fact]
    public void Heavy_moments_are_explained_only_when_they_are_measured()
    {
        var heavy = new BenchmarkTestResult("a", "gpu", 39.4, "u", 9941, 5.5);
        var smooth = new BenchmarkTestResult("b", "gpu", 34.1, "u", 9628, 31);

        Assert.NotEmpty(WorkshopViewModel.HeavyMoments(new BenchmarkReport { Tests = [smooth, heavy] }, "gpu"));
        Assert.Empty(WorkshopViewModel.HeavyMoments(new BenchmarkReport { Tests = [smooth] }, "gpu"));
        Assert.Empty(WorkshopViewModel.HeavyMoments(new BenchmarkReport { Tests = [heavy] }, "cpu"));
    }
}
