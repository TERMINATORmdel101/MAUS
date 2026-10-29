namespace Maus.Core.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("0.3.2-alpha+f028b73", "0.3.2-alpha")]
    [InlineData("0.3.2-alpha", "0.3.2-alpha")]
    [InlineData(null, "0.0.0")]
    public void Commit_hash_is_removed(string? informational, string expected) =>
        Assert.Equal(expected, AppVersion.Clean(informational));

    [Fact]
    public void Current_version_is_0_8_4() => Assert.Equal("0.8.4", AppVersion.Display);

    [Theory]
    [InlineData("0.7.6+00155f56a8e6a877341d6e6ca9f7cc51b0afb4fe", "00155f5")]
    [InlineData("0.7.6", null)]
    [InlineData("0.7.6+abc", null)]
    [InlineData(null, null)]
    public void Commit_is_the_short_hash_after_the_plus(string? informational, string? expected) =>
        Assert.Equal(expected, AppVersion.CommitOf(informational));
}
