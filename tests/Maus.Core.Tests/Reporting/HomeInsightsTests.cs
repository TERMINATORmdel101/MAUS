using Maus.Core;
using Maus.Core.Reporting;
using Maus.Core.Workshop.Benchmark;
using Xunit;

namespace Maus.Core.Tests.Reporting;

/// <summary>Lot B de la 0.8.6 : corrections qui rapportent le plus, pilotes, CSV, anonymisation visible, Markdown, classement.</summary>
public class HomeInsightsTests
{
    private static Finding Costly(string id, FindingStatus status, Severity severity) =>
        new() { Id = id, Title = id, Status = status, Severity = severity, Explanation = "x" };

    private static ModuleResult Module(string id, params Finding[] findings) => new(id, id, findings, TimeSpan.Zero);

    [Fact]
    public void BestGains_ranks_by_points_and_ignores_what_MAUS_cannot_fix()
    {
        var results = new[]
        {
            Module("M01", Costly("low", FindingStatus.Improvable, Severity.Low), Costly("medium", FindingStatus.Warning, Severity.Medium), Costly("locked", FindingStatus.Warning, Severity.Medium)),
        };

        var gains = HealthScore.BestGains(results, f => f.Id != "locked");

        Assert.Equal(["medium", "low"], gains.Select(g => g.Finding.Id));
        Assert.True(gains[0].Points > gains[1].Points);
    }

    [Fact]
    public void BestGains_gives_the_real_gain_of_a_fix()
    {
        var results = new[] { Module("M01", Costly("a", FindingStatus.Warning, Severity.Medium)) };

        var gain = Assert.Single(HealthScore.BestGains(results, _ => true));

        Assert.Equal(100 - HealthScore.Compute(results), gain.Points);
    }

    [Fact]
    public void BestGains_keeps_an_important_finding_held_back_by_the_cap()
    {
        // Deux constats importants : en corriger un seul laisse le plafond en place, mais il faut quand même le proposer.
        var results = new[] { Module("M01", Costly("a", FindingStatus.Problem, Severity.High), Costly("b", FindingStatus.Problem, Severity.High)) };

        var gains = HealthScore.BestGains(results, _ => true);

        Assert.Equal(2, gains.Count);
        Assert.All(gains, g => Assert.Equal(0, g.Points));
    }

    [Fact]
    public void BestGains_counts_a_subject_seen_by_two_modules_once()
    {
        var results = new[]
        {
            Module("M01", Costly("M01.secure-boot", FindingStatus.Problem, Severity.High)),
            Module("M08", Costly("M08.secure-boot", FindingStatus.Problem, Severity.High)),
        };

        var gain = Assert.Single(HealthScore.BestGains(results, _ => true));

        Assert.Equal(100 - HealthScore.Compute(results), gain.Points);
    }

    [Fact]
    public void DriverLine_sums_up_each_state_and_stays_silent_without_a_driver()
    {
        Assert.Null(HealthScore.DriverLine([Costly("M09.hags", FindingStatus.Ok, Severity.Info)]));

        var line = HealthScore.DriverLine(
        [
            Costly("M09.driver.a", FindingStatus.Ok, Severity.Info),
            Costly("M09.driver.b", FindingStatus.Info, Severity.Info),
            Costly("M09.driver.c", FindingStatus.Unknown, Severity.Info),
        ]);

        Assert.Contains("1 à jour", line, StringComparison.Ordinal);
        Assert.Contains("1 à vérifier", line, StringComparison.Ordinal);
        Assert.Contains("1 inconnu", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("=cmd|'/c calc'!A1")]
    [InlineData("+1+1")]
    [InlineData("-2+3")]
    [InlineData("@SOMME(A1)")]
    public void Csv_neutralises_what_a_spreadsheet_would_run_as_a_formula(string title)
    {
        var report = new AuditReport(DateTimeOffset.Now, "1", null!, null!, false, [Module("M01", Costly(title, FindingStatus.Warning, Severity.Medium))]);

        var csv = FindingsCsv.Build(report, new PrivacyFilter(null, null, null));

        Assert.Contains("\"'" + title + "\"", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("\"" + title + "\"", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_keeps_one_row_per_finding_and_masks_the_user()
    {
        var finding = Costly("a", FindingStatus.Warning, Severity.Medium) with { Current = "C:\\Users\\Alex\\x \"y\"\r\nz" };
        var report = new AuditReport(DateTimeOffset.Now, "1", null!, null!, false, [Module("M01", finding)]);

        var csv = FindingsCsv.Build(report, new PrivacyFilter("Alex", "PC-ALEX", "C:\\Users\\Alex"));

        Assert.Equal(2, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("Alex", csv, StringComparison.Ordinal);
        Assert.Contains("\"\"y\"\"", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Privacy_filter_says_what_it_removed_never_the_value()
    {
        var filter = new PrivacyFilter("Camille", "PC-BUREAU", null);
        Assert.Contains("aucun", HelpSummary.AnonymizationNote(filter), StringComparison.Ordinal);

        Assert.Equal("[utilisateur] sur [nom-du-pc]", filter.Mask("Camille sur PC-BUREAU"));

        var note = HelpSummary.AnonymizationNote(filter);
        Assert.Contains("nom d'utilisateur", note, StringComparison.Ordinal);
        Assert.Contains("nom du PC", note, StringComparison.Ordinal);
        Assert.DoesNotContain("adresse e-mail", note, StringComparison.Ordinal);
        Assert.DoesNotContain("Camille", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_keeps_lists_bolds_titles_and_neutralises_formatting()
    {
        var markdown = HelpSummary.ToMarkdown("Mon problème : jeu_lent\r\n\r\nPoints relevés :\r\n- [PROBLÈME] *pilote* <b>\r\n");

        Assert.Equal("Mon problème : jeu\\_lent\n\n**Points relevés :**\n- \\[PROBLÈME\\] \\*pilote\\* \\<b\\>\n", markdown);
    }

    [Fact]
    public void Ranking_keeps_complete_comparable_runs_best_first()
    {
        static BenchmarkReport Run(double score, bool completed = true, string resolution = "1920x1080") =>
            new() { OverallScore = score, Completed = completed, RenderResolution = resolution, Date = DateTimeOffset.Now };

        var ranking = BenchmarkComparison.Ranking([Run(5000), Run(9000, completed: false), Run(7000, resolution: "1280x720"), Run(8000), Run(6000)]);

        Assert.Equal([8000d, 6000d, 5000d], ranking.Select(r => r.OverallScore));
        Assert.Empty(BenchmarkComparison.Ranking([]));
    }
}
