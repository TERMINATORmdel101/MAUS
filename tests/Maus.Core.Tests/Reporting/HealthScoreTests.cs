using Maus.Core.Reporting;

namespace Maus.Core.Tests.Reporting;

public class HealthScoreTests
{
    private static ModuleResult Module(string id, params FindingStatus[] statuses) =>
        new(id, id, statuses.Select((s, i) => new Finding { Id = $"{id}.{i}", Title = "t", Status = s, Explanation = "e" }).ToList(), TimeSpan.Zero);

    [Fact]
    public void Score_drops_with_problems_and_warnings_and_caps_small_optimisations()
    {
        Assert.Equal(100, HealthScore.Compute([Module("M06", FindingStatus.Ok, FindingStatus.Info, FindingStatus.Unknown)]));
        Assert.Equal(83, HealthScore.Compute([Module("M01", FindingStatus.Problem), Module("M13", FindingStatus.Warning)]));
        Assert.Equal(80, HealthScore.Compute([Module("M06", Enumerable.Repeat(FindingStatus.Improvable, 50).ToArray())]));
        Assert.Equal(0, HealthScore.Compute([Module("M01", Enumerable.Repeat(FindingStatus.Problem, 10).ToArray())]));
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
