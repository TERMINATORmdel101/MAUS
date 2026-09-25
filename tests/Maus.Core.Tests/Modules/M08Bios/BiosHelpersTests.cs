using Maus.Core.Modules.M08Bios;

namespace Maus.Core.Tests.Modules.M08Bios;

public class BiosHelpersTests
{
    [Theory]
    [InlineData(new byte[] { 0xF0, 0x00, 0x00, 0x00 }, 0xF0u)]
    [InlineData(new byte[] { 0x2F, 0x01, 0x00, 0x00 }, 0x12Fu)]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x1E, 0x00, 0x00, 0x00 }, 0x1Eu)]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x2F, 0x01, 0x00, 0x00 }, 0x12Fu)]
    [InlineData(new byte[] { 0x54, 0x10, 0xA2, 0x08, 0x00, 0x00, 0x00, 0x00 }, 0x08A21054u)]
    public void Microcode_revision_is_read_little_endian(byte[] value, uint expected) =>
        Assert.Equal(expected, MicrocodeRevision.Parse(value));

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[] { 0x01, 0x02 })]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 })]
    public void Unreadable_microcode_revision_is_null(byte[]? value) => Assert.Null(MicrocodeRevision.Parse(value));

    [Fact]
    public void Microcode_revision_is_formatted_in_hexadecimal() => Assert.Equal("0x12F", MicrocodeRevision.Format(0x12F));

    [Theory]
    [InlineData(0, "moins d'un mois")]
    [InlineData(5, "5 mois")]
    [InlineData(12, "1 an")]
    [InlineData(14, "1 an et 2 mois")]
    [InlineData(36, "3 ans")]
    public void Age_is_written_in_plain_french(int months, string expected) => Assert.Equal(expected, BiosModule.FormatAge(months));

    [Fact]
    public void Catalog_loads_known_vendors_with_https_pages()
    {
        var vendors = BiosVendorDirectory.Vendors;

        foreach (var name in new[] { "ASUS", "MSI", "Gigabyte", "ASRock", "Biostar", "Dell", "HP", "Lenovo", "Acer" })
        {
            Assert.Contains(vendors, v => v.Name == name);
        }

        Assert.All(vendors, v => Assert.StartsWith("https://", v.SupportUrl, StringComparison.Ordinal));
        Assert.All(vendors.Where(v => v.SearchUrl is not null), v => Assert.Contains("{model}", v.SearchUrl, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ASUSTeK COMPUTER INC.", "ASUS")]
    [InlineData("Micro-Star International Co., Ltd.", "MSI")]
    [InlineData("Gigabyte Technology Co., Ltd.", "Gigabyte")]
    [InlineData("ASRock", "ASRock")]
    [InlineData("Dell Inc.", "Dell")]
    [InlineData("HP", "HP")]
    [InlineData("Hewlett-Packard", "HP")]
    [InlineData("LENOVO", "Lenovo")]
    [InlineData("Acer", "Acer")]
    public void Vendor_is_found_by_whole_word(string manufacturer, string expected) =>
        Assert.Equal(expected, BiosVendorDirectory.Find(manufacturer, BiosVendorDirectory.Vendors)?.Name);

    [Theory]
    [InlineData(null)]
    [InlineData("System manufacturer")]
    [InlineData("To Be Filled By O.E.M.")]
    [InlineData("Default string")]
    [InlineData("Shenzhen Unknown Board Co.")]
    public void Placeholder_or_unknown_vendor_is_not_matched(string? manufacturer) =>
        Assert.Null(BiosVendorDirectory.Find(manufacturer, BiosVendorDirectory.Vendors));

    [Fact]
    public void Self_built_pc_uses_board_model_and_search_page()
    {
        var target = BiosVendorDirectory.Resolve("ASUSTeK COMPUTER INC.", "System Product Name", false, "ASUSTeK COMPUTER INC.", "ROG STRIX B650E-F GAMING WIFI");

        Assert.False(target.IsBrandedPc);
        Assert.Equal("ASUS ROG STRIX B650E-F GAMING WIFI", target.DisplayName);
        Assert.Equal("https://www.asus.com/searchresult?searchType=support&searchKey=ROG%20STRIX%20B650E-F%20GAMING%20WIFI", target.Url);
        Assert.Contains("EZ Flash", target.Tool);
        Assert.Contains("USB BIOS FlashBack", target.Tool);
    }

    [Fact]
    public void Branded_pc_uses_system_model_and_oem_tool()
    {
        var target = BiosVendorDirectory.Resolve("Dell Inc.", "XPS 8960", false, "Dell Inc.", "0KV1J8");

        Assert.True(target.IsBrandedPc);
        Assert.Equal("Dell XPS 8960", target.DisplayName);
        Assert.Equal("https://www.dell.com/support/home/", target.Url);
        Assert.Equal("Dell Command Update ou SupportAssist", target.Tool);
    }

    [Fact]
    public void Unknown_board_vendor_has_no_url_and_generic_tool()
    {
        var target = BiosVendorDirectory.Resolve("To Be Filled By O.E.M.", "To Be Filled By O.E.M.", false, "Maxsun", "MS-Terminator B760M");

        Assert.Null(target.Url);
        Assert.Equal("Maxsun MS-Terminator B760M", target.DisplayName);
        Assert.Contains("outil de mise à jour intégré au BIOS", target.Tool);
    }

    [Fact]
    public void Unknown_brand_laptop_is_treated_as_branded()
    {
        var target = BiosVendorDirectory.Resolve("Framework", "Laptop 13 (AMD Ryzen 7040Series)", true, "Framework", "FRANMDCP07");

        Assert.True(target.IsBrandedPc);
        Assert.Null(target.Url);
        Assert.Contains("fabricant du PC", target.Tool);
    }

    [Theory]
    [InlineData("MPG Z390 GAMING PRO CARBON (MS-7B17)", "MPG Z390 GAMING PRO CARBON")]
    [InlineData("B550 AORUS ELITE", "B550 AORUS ELITE")]
    [InlineData("(MS-7C56)", "(MS-7C56)")]
    public void Model_loses_internal_code_in_parentheses(string product, string expected) =>
        Assert.Equal(expected, BiosVendorDirectory.CleanModel(product));
}
