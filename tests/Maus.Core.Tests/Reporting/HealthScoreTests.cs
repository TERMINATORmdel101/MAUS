using Maus.Core.Reporting;

namespace Maus.Core.Tests.Reporting;

public class HealthScoreTests
{
    private static ModuleResult Module(string id, params FindingStatus[] statuses) =>
        new(id, id, statuses.Select((s, i) => new Finding { Id = $"{id}.{i}", Title = "t", Status = s, Explanation = "e" }).ToList(), TimeSpan.Zero);

    private static Finding Costly(string id, FindingStatus status, Severity severity) =>
        new() { Id = id, Title = id, Status = status, Severity = severity, Explanation = "e" };

    [Fact]
    public void Points_depend_on_the_gravity_and_the_score_drops_ever_more_slowly()
    {
        Assert.Equal(100, HealthScore.Compute([Module("M06", FindingStatus.Ok, FindingStatus.Info, FindingStatus.Unknown)]));

        // Un point à surveiller de gravité moyenne : 4 points, 100 × e^-0,04 = 96.
        Assert.Equal(96, HealthScore.Compute([Module("M13", FindingStatus.Warning)]));

        // Cinquante optimisations ne retirent que 10 points en tout : 100 × e^-0,1 = 90.
        Assert.Equal(90, HealthScore.Compute([Module("M06", Enumerable.Repeat(FindingStatus.Improvable, 50).ToArray())]));

        // Dix problèmes importants : 100 points, 100 × e^-1 = 37 (l'ancien barème tombait à 0).
        Assert.Equal(37, HealthScore.Compute([Module("M01", Enumerable.Repeat(FindingStatus.Problem, 10).ToArray())]));
    }

    [Fact]
    public void Worst_gravity_caps_the_score_so_a_serious_problem_is_never_called_good()
    {
        // Un seul problème important : 100 × e^-0,1 = 90, plafonné à 74 (« à améliorer »).
        var high = HealthScore.Explain([new ModuleResult("M13", "M13", [Costly("M13.a", FindingStatus.Problem, Severity.High)], TimeSpan.Zero)]);
        Assert.Equal(74, high.Score);
        Assert.Equal(HealthScore.HighCap, high.Cap);
        Assert.Equal(90, HealthScore.Uncapped(high.Points));

        // Un constat critique : plafonné à 49 (« à corriger en priorité »).
        var critical = HealthScore.Explain([new ModuleResult("M11", "M11", [Costly("M11.a", FindingStatus.Problem, Severity.Critical)], TimeSpan.Zero)]);
        Assert.Equal(49, critical.Score);
        Assert.Equal("à corriger en priorité", HealthScore.Describe(critical.Score));

        // Plafond non atteint : pas de mention de plafond.
        Assert.Null(HealthScore.Explain([Module("M01", Enumerable.Repeat(FindingStatus.Problem, 10).ToArray())]).Cap);
    }

    [Fact]
    public void Declared_gravity_wins_over_the_colour_and_ok_findings_cost_nothing()
    {
        Assert.Equal(Severity.Critical, HealthScore.GravityOf(Costly("a", FindingStatus.Problem, Severity.Critical)));
        Assert.Equal(Severity.High, HealthScore.GravityOf(Costly("b", FindingStatus.Problem, Severity.Info)));
        Assert.Equal(Severity.Medium, HealthScore.GravityOf(Costly("c", FindingStatus.Warning, Severity.Info)));
        Assert.Equal(Severity.Low, HealthScore.GravityOf(Costly("d", FindingStatus.Improvable, Severity.Info)));
        Assert.Equal(Severity.Info, HealthScore.GravityOf(Costly("e", FindingStatus.Ok, Severity.High)));
        Assert.Equal(Severity.Info, HealthScore.GravityOf(Costly("f", FindingStatus.Unknown, Severity.Critical)));
    }

    [Fact]
    public void Owner_pc_of_29_09_gets_a_fair_score_and_secure_boot_counts_once()
    {
        // Audit du PC du porteur le 29/09/2026 : 3 problèmes importants, 8 points à surveiller (dont Secure Boot vu par
        // les modules 1 et 8), 17 optimisations. Ancien barème : 0/100.
        var findings = new List<Finding>
        {
            Costly("M13.cpu-mitigations", FindingStatus.Problem, Severity.High),
            Costly("M13.driver-blocklist", FindingStatus.Problem, Severity.High),
            Costly("M01.defender-tamper", FindingStatus.Problem, Severity.High),
            Costly("M01.secure-boot", FindingStatus.Warning, Severity.Medium),
            Costly("M08.secure-boot", FindingStatus.Warning, Severity.Medium),
        };
        findings.AddRange(Enumerable.Range(0, 6).Select(i => Costly($"M02.w{i}", FindingStatus.Warning, Severity.Medium)));
        findings.AddRange(Enumerable.Range(0, 17).Select(i => Costly($"M04.o{i}", FindingStatus.Improvable, Severity.Low)));

        var score = HealthScore.Explain([new ModuleResult("M00", "tous", findings, TimeSpan.Zero)]);

        // 3 × 10 + 7 × 4 + 10 (optimisations plafonnées) = 68 points ; 100 × e^-0,68 = 51.
        Assert.Equal(68, score.Points);
        Assert.Equal(51, score.Score);
        Assert.Equal(1, score.Lines.Count(l => l.FindingId.EndsWith(".secure-boot", StringComparison.Ordinal)));
        Assert.Equal(17, score.OptimisationCount);
        Assert.Equal(10, score.OptimisationPoints);
        Assert.Equal("M01.defender-tamper", score.Lines[0].FindingId);
    }

    [Fact]
    public void Families_follow_the_letters_of_maus()
    {
        var summaries = HealthScore.Summaries([Module("M01", FindingStatus.Problem), Module("M06", FindingStatus.Improvable), Module("M13", FindingStatus.Warning), Module("M03", FindingStatus.Ok)]);

        Assert.Equal(["M", "A", "U", "S"], summaries.Select(s => s.Letter));
        Assert.Equal(1, summaries.Single(s => s.Letter == "A").Problems);
        Assert.Equal(1, summaries.Single(s => s.Letter == "M").Improvements);
        Assert.Equal(1, summaries.Single(s => s.Letter == "S").Warnings);
        Assert.Equal(0, summaries.Single(s => s.Letter == "U").Problems);
    }

    [Theory]
    [InlineData(95, "excellent")]
    [InlineData(78, "bon")]
    [InlineData(60, "à améliorer")]
    [InlineData(20, "à corriger en priorité")]
    public void Score_is_described_in_words(int score, string expected)
    {
        Assert.Equal(expected, HealthScore.Describe(score));
    }
}
