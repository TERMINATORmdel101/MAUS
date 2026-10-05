using Maus.Core.Reporting;

namespace Maus.Core.Tests.Reporting;

public class OptimizationMythsTests
{
    [Fact]
    public void Every_myth_cites_a_microsoft_page()
    {
        Assert.NotEmpty(OptimizationMyths.All);
        Assert.All(OptimizationMyths.All, myth =>
        {
            Assert.Equal("https", myth.Source.Scheme);
            Assert.True(myth.Source.Host.EndsWith("microsoft.com", StringComparison.Ordinal), myth.Source.Host);
            Assert.False(string.IsNullOrWhiteSpace(myth.Truth));
        });
    }
}
