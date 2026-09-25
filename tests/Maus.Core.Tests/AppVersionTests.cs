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
    public void Current_version_is_the_alpha() => Assert.Equal("0.3.2-alpha", AppVersion.Display);
}
