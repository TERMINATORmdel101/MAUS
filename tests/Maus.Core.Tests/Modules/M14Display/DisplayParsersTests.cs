using Maus.Core.Modules.M14Display;

namespace Maus.Core.Tests.Modules.M14Display;

public class DisplayParsersTests
{
    [Fact]
    public void Advanced_color_info_2_reads_hdr_support_and_active_mode()
    {
        // advancedColorSupported (bit 0), advancedColorActive (bit 1), highDynamicRangeSupported (bit 4), highDynamicRangeUserEnabled (bit 5).
        var info = DisplayParsers.FromAdvancedColorInfo2(0b0011_0011, encoding: 0, bitsPerChannel: 10, activeColorMode: 2);

        Assert.True(info.HdrSupported);
        Assert.True(info.HdrActive);
        Assert.Equal(ColorEncoding.Rgb, info.Encoding);
        Assert.Equal(10, info.BitsPerColorChannel);
        Assert.Equal(AdvancedColorMode.Hdr, info.ActiveMode);
    }

    [Fact]
    public void Advanced_color_info_2_in_wide_color_mode_is_not_hdr()
    {
        var info = DisplayParsers.FromAdvancedColorInfo2(0b1100_0011, encoding: 2, bitsPerChannel: 8, activeColorMode: 1);

        Assert.False(info.HdrSupported);
        Assert.False(info.HdrActive);
        Assert.Equal(ColorEncoding.YCbCr422, info.Encoding);
        Assert.Equal(AdvancedColorMode.Wcg, info.ActiveMode);
    }

    [Fact]
    public void Advanced_color_info_2_with_unknown_mode_is_not_hdr()
    {
        var info = DisplayParsers.FromAdvancedColorInfo2(0x13, encoding: 3, bitsPerChannel: 10, activeColorMode: 7);

        Assert.True(info.HdrSupported);
        Assert.False(info.HdrActive);
        Assert.Null(info.ActiveMode);
        Assert.Equal(ColorEncoding.YCbCr420, info.Encoding);
    }

    [Theory]
    [InlineData(0x3u, true, true)]
    [InlineData(0x1u, true, false)]
    [InlineData(0x7u, false, false)]
    [InlineData(0x0u, false, false)]
    public void Advanced_color_info_1_ignores_wide_color_only_screens(uint value, bool supported, bool active)
    {
        var info = DisplayParsers.FromAdvancedColorInfo(value, encoding: 1, bitsPerChannel: 10);

        Assert.Equal(supported, info.HdrSupported);
        Assert.Equal(active, info.HdrActive);
        Assert.Equal(ColorEncoding.YCbCr444, info.Encoding);
        Assert.Null(info.ActiveMode);
    }

    [Theory]
    [InlineData(@"\\?\PCI#VEN_10DE&DEV_1E04&SUBSYS_86751043&REV_A1#4&f71f481&0&0008#{5b45201d-f2f2-4f3b-85bb-30ff1f953599}", @"PCI\VEN_10DE&DEV_1E04&SUBSYS_86751043&REV_A1\4&f71f481&0&0008")]
    [InlineData(@"PCI#VEN_8086&DEV_3E92#3&1&0&10", @"PCI\VEN_8086&DEV_3E92\3&1&0&10")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("DISPLAY1", null)]
    public void Adapter_path_is_converted_to_a_pnp_id(string? path, string? expected) =>
        Assert.Equal(expected, DisplayParsers.PnpIdFromAdapterPath(path));

    [Fact]
    public void Max_refresh_accepts_both_orientations()
    {
        DisplayMode[] modes = [new(2560, 1440, 60), new(2560, 1440, 144), new(1440, 2560, 165), new(1920, 1080, 240)];

        Assert.Equal(165, DisplayParsers.MaxRefreshAt(modes, 2560, 1440));
        Assert.Equal(240, DisplayParsers.MaxRefreshAt(modes, 1920, 1080));
        Assert.Null(DisplayParsers.MaxRefreshAt(modes, 3840, 2160));
        Assert.Equal((2560, 1440), DisplayParsers.LargestResolution(modes));
        Assert.Null(DisplayParsers.LargestResolution([]));
    }

    [Fact]
    public void Directx_settings_are_split_into_pairs()
    {
        var settings = DisplayParsers.ParseDirectXSettings("SwapEffectUpgradeEnable=1; AutoHDREnable=0;;broken;VRROptimizeEnable=1;");

        Assert.Equal(3, settings.Count);
        Assert.Equal("0", settings["autohdrenable"]);
        Assert.Equal("1", settings["VRROptimizeEnable"]);
        Assert.Empty(DisplayParsers.ParseDirectXSettings(null));
    }

    [Theory]
    [InlineData(5u, "HDMI")]
    [InlineData(10u, "DisplayPort")]
    [InlineData(0x80000000u, "dalle intégrée")]
    [InlineData(11u, "dalle intégrée")]
    [InlineData(0u, "VGA (analogique)")]
    [InlineData(0xFFFFFFFFu, "autre connecteur")]
    public void Connector_labels_are_readable(uint output, string expected) =>
        Assert.Equal(expected, DisplayParsers.ConnectorLabel((OutputTechnology)output));

    [Theory]
    [InlineData(0, "RGB", false)]
    [InlineData(1, "YCbCr 4:4:4", false)]
    [InlineData(2, "YCbCr 4:2:2", true)]
    [InlineData(3, "YCbCr 4:2:0", true)]
    public void Encoding_labels_and_subsampling(int value, string label, bool subsampled)
    {
        var encoding = (ColorEncoding)value;
        Assert.Equal(label, DisplayParsers.EncodingLabel(encoding));
        Assert.Equal(subsampled, DisplayParsers.IsChromaSubsampled(encoding));
    }
}
