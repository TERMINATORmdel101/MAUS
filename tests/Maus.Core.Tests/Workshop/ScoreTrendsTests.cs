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
        Assert.InRange(entry.Score, 0, 99);
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
}
