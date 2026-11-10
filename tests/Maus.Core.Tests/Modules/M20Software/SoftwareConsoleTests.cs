using Maus.Core.Modules.M20Software;

namespace Maus.Core.Tests.Modules.M20Software;

public class SoftwareConsoleTests
{
    [Fact]
    public void Winget_never_starts_with_administrator_rights()
    {
        var started = false;

        var outcome = SoftwareConsole.Open(elevated: true, @"C:\winget.exe", _ => "x", _ => started = true);

        Assert.False(started);
        Assert.False(outcome.Ran);
        Assert.Contains("administrateur", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_rights_the_window_opens_with_the_located_winget()
    {
        string? line = null;

        var outcome = SoftwareConsole.Open(elevated: false, @"C:\winget.exe", path => path + " upgrade", arguments => { line = arguments; return true; });

        Assert.True(outcome.Ran);
        Assert.Equal(@"C:\winget.exe upgrade", line);
    }

    [Fact]
    public void A_missing_winget_is_said() =>
        Assert.Contains("winget est absent", SoftwareConsole.Open(false, null, _ => "x", _ => true).Message, StringComparison.Ordinal);
}
