namespace Maus.Core.Tests.Engine;

public class ModuleResultTests
{
    private static Finding Found(FindingStatus status) => new() { Id = "M02.x", Title = "t", Status = status, Explanation = "e" };

    [Fact]
    public void Information_alone_is_shown_as_in_order()
    {
        var result = new ModuleResult("M02", "t", [Found(FindingStatus.Ok), Found(FindingStatus.Info)], TimeSpan.Zero);

        Assert.Equal(FindingStatus.Info, result.WorstStatus);
        Assert.Equal(FindingStatus.Ok, result.Verdict);
    }

    [Theory]
    [InlineData(FindingStatus.Unknown)]
    [InlineData(FindingStatus.Improvable)]
    [InlineData(FindingStatus.Problem)]
    public void Any_other_status_is_shown_as_is(FindingStatus status)
    {
        var result = new ModuleResult("M02", "t", [Found(FindingStatus.Info), Found(status)], TimeSpan.Zero);

        Assert.Equal(status, result.Verdict);
    }

    [Fact]
    public void Module_in_error_stays_undetermined()
    {
        Assert.Equal(FindingStatus.Unknown, new ModuleResult("M02", "t", [], TimeSpan.Zero, "délai dépassé").Verdict);
    }
}
