using Maus.Core.Reporting;

namespace Maus.Core.Tests.Reporting;

public class LinkTextTests
{
    private const string Advice = "Ne restaurez pas les clés. Page officielle : https://www.msi.com/search/MPG%20Z390%20GAMING%20PRO%20CARBON.";

    [Fact]
    public void First_link_is_found_without_the_final_full_stop()
    {
        Assert.Equal("https://www.msi.com/search/MPG%20Z390%20GAMING%20PRO%20CARBON", LinkText.FirstLink(Advice)?.OriginalString);
    }

    [Fact]
    public void Link_is_shortened_to_the_site_name()
    {
        Assert.Equal("Ne restaurez pas les clés. Page officielle : www.msi.com.", LinkText.Shorten(Advice));
    }

    [Fact]
    public void Text_without_link_is_unchanged()
    {
        Assert.Null(LinkText.FirstLink("Activer Secure Boot dans le BIOS."));
        Assert.Equal("Activer Secure Boot dans le BIOS.", LinkText.Shorten("Activer Secure Boot dans le BIOS."));
        Assert.Null(LinkText.Shorten(null));
    }
}
