using Maus.Core.Workshop;

namespace Maus.Core.Tests.Workshop;

public class ScoreTrendsTests
{
    private static readonly DateTimeOffset Day = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Health_entry_counts_the_findings()
    {
        IReadOnlyCollection<ModuleResult> results =
        [
            new ModuleResult("M06", "t",
            [
                new Finding { Id = "a", Title = "a", Status = FindingStatus.Problem, Severity = Severity.High, Explanation = "e" },
                new Finding { Id = "b", Title = "b", Status = FindingStatus.Improvable, Severity = Severity.Low, Explanation = "e" },
                new Finding { Id = "c", Title = "c", Status = FindingStatus.Ok, Explanation = "e" },
            ], TimeSpan.Zero),
        ];

        var entry = ScoreTrends.HealthEntry(results, Day);

        Assert.NotNull(entry);
        Assert.Equal(ScoreTrends.HealthKind, entry.Kind);
        // Un problème important plafonne le score à 74 (HealthScore.HighCap).
        Assert.Equal(74, entry.Score);
        Assert.Equal("1/0/1", entry.Detail);
    }

    [Fact]
    public void Partial_health_score_is_not_recorded()
    {
        IReadOnlyCollection<ModuleResult> results =
        [
            new ModuleResult("M06", "t", [new Finding { Id = "a", Title = "a", Status = FindingStatus.Ok, Explanation = "e" }], TimeSpan.Zero),
            new ModuleResult("M03", "Mises à jour Windows", [], TimeSpan.FromSeconds(180), "Délai dépassé (180 s)."),
        ];

        // 100/100 sans les constats de M03 : l'enregistrer montrerait une fausse amélioration sur la courbe.
        Assert.Null(ScoreTrends.HealthEntry(results, Day));
    }

    [Fact]
    public void Series_keeps_stable_results_of_one_kind_in_order_and_describes_the_change()
    {
        BenchmarkEntry[] entries =
        [
            new("ram", 40, true, Day.AddDays(2)),
            new("ram", 30, true, Day),
            new("ram", 99, false, Day.AddDays(1)),
            new("vram", 500, true, Day),
        ];

        var series = ScoreTrends.Series(entries, "ram");

        Assert.Equal([30.0, 40.0], series.Select(e => e.Score));
        var text = ScoreTrends.Describe(series);
        Assert.Contains("30 → 40", text, StringComparison.Ordinal);
        Assert.Contains("+33 %", text, StringComparison.Ordinal);
        Assert.Equal("Pas encore de résultat.", ScoreTrends.Describe([]));
    }

    [Fact]
    public void Health_history_is_kept_apart_from_tests()
    {
        var folder = Path.Combine(Path.GetTempPath(), "maus-trends-" + Guid.NewGuid().ToString("N"));
        try
        {
            var health = new BenchmarkHistory(Path.Combine(folder, "health-history.json"));
            health.Add(new BenchmarkEntry(ScoreTrends.HealthKind, 80, true, Day));
            health.Add(new BenchmarkEntry(ScoreTrends.HealthKind, 85, true, Day.AddDays(1)));

            Assert.Equal(2, ScoreTrends.Series(health.Load(), ScoreTrends.HealthKind).Count);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void Health_trend_is_told_in_points()
    {
        BenchmarkEntry At(double score, int day) => new(ScoreTrends.HealthKind, score, true, Day.AddDays(day));
        var date = Day.LocalDateTime.ToString("d", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));

        Assert.Equal($"Premier score enregistré le {date}.", ScoreTrends.DescribeHealth([At(80, 0)]));
        Assert.Equal($"Score stable depuis le {date} (2 audits).", ScoreTrends.DescribeHealth([At(80, 0), At(80, 1)]));
        Assert.Equal($"En hausse de 6 points depuis le {date}.", ScoreTrends.DescribeHealth([At(80, 0), At(84, 1), At(86, 2)]));
        Assert.Equal($"En baisse d'1 point depuis le {date}.", ScoreTrends.DescribeHealth([At(80, 0), At(79, 1)]));
    }

    /// <summary>Le barème a changé en 0.8.6 : le même PC ne doit pas paraître « en hausse » à cause d'une version de MAUS.</summary>
    [Fact]
    public void A_change_of_scale_is_not_told_as_a_progress_of_the_pc()
    {
        var date = Day.LocalDateTime.ToString("d", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));
        BenchmarkEntry Before(double score, int day) => new(ScoreTrends.PreviousHealthKind, score, true, Day.AddDays(day));
        BenchmarkEntry After(double score, int day) => new(ScoreTrends.HealthKind, score, true, Day.AddDays(day));

        Assert.Empty(ScoreTrends.Series([Before(85, 0)], ScoreTrends.HealthKind));
        Assert.Equal([92], ScoreTrends.Series([Before(85, 0), After(92, 1)], ScoreTrends.HealthKind).Select(e => e.Score));
        Assert.DoesNotContain("hausse", ScoreTrends.DescribeHealthHistory([Before(85, 0), After(92, 1)]), StringComparison.Ordinal);
        Assert.Contains("barème a changé", ScoreTrends.DescribeHealthHistory([Before(85, 0), After(92, 1)]), StringComparison.Ordinal);
        Assert.Contains("barème a changé", ScoreTrends.DescribeHealthHistory([Before(85, 0)]), StringComparison.Ordinal);
        Assert.Equal($"En hausse de 3 points depuis le {date}.", ScoreTrends.DescribeHealthHistory([Before(85, 0), After(89, 0), After(92, 1)]));
        Assert.Equal("Score de santé (barème précédent)", ScoreTrends.Label(ScoreTrends.PreviousHealthKind));
        Assert.Equal("/100", ScoreTrends.Unit(ScoreTrends.PreviousHealthKind));
        Assert.False(ScoreTrends.IsPossible(ScoreTrends.PreviousHealthKind, 150));
    }

    [Fact]
    public void Each_disk_and_graphics_card_has_its_own_curve()
    {
        BenchmarkEntry[] entries =
        [
            new(ScoreTrends.DiskKind(@"C:\"), 500, true, Day),
            new(ScoreTrends.DiskKind(@"D:\"), 100, true, Day),
            new("disk-read", 50, true, Day),
        ];

        Assert.Equal([500.0], ScoreTrends.Series(entries, ScoreTrends.DiskKind(@"C:\")).Select(e => e.Score));
        Assert.Equal(ScoreTrends.Unit("disk-read"), ScoreTrends.Unit(ScoreTrends.DiskKind(@"C:\")));
        Assert.Equal(ScoreTrends.Unit("vram"), ScoreTrends.Unit(ScoreTrends.VramKind("Carte")));
        Assert.EndsWith("Carte", ScoreTrends.Label(ScoreTrends.VramKind("Carte")), StringComparison.Ordinal);
    }
}
