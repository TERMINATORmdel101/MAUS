using Maus.Core.Hardware;
using Maus.Core.Modules.M09Gpu;

namespace Maus.Core.Tests.Modules.M09Gpu;

public class GpuParsersTests
{
    [Theory]
    [InlineData("32.0.16.1714", "617.14")]
    [InlineData("31.0.15.3598", "535.98")]
    [InlineData("32.0.15.8157", "581.57")]
    [InlineData("30.0.14.7141", "471.41")]
    [InlineData("32.0.16.0905", "609.05")]
    [InlineData("32.0.16.905", "609.05")]
    public void Nvidia_marketing_version_uses_the_last_five_digits(string windows, string expected) =>
        Assert.Equal(expected, GpuParsers.NvidiaMarketingVersion(windows));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("32.0.16")]
    [InlineData("a.b.c.d")]
    [InlineData("32.0.x.1714")]
    public void Nvidia_marketing_version_rejects_malformed_input(string? windows) =>
        Assert.Null(GpuParsers.NvidiaMarketingVersion(windows));

    [Theory]
    [InlineData("617.14", 617, 14)]
    [InlineData("Adrenalin 26.9.1", 26, 9)]
    [InlineData("32.0.101.9033", 32, 0)]
    public void Parse_version_finds_the_numeric_part(string text, int major, int minor)
    {
        var version = GpuParsers.ParseVersion(text);

        Assert.NotNull(version);
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
    }

    [Fact]
    public void Parse_version_returns_null_without_digits() => Assert.Null(GpuParsers.ParseVersion("N/A"));

    [Fact]
    public void Nvidia_smi_query_is_parsed_per_card()
    {
        var gpus = GpuParsers.ParseNvidiaSmiQuery("00000000:01:00.0, 617.14, NVIDIA GeForce RTX 2080 Ti\r\n00000000:0A:00.0, 617.14, NVIDIA GeForce RTX 4070\r\n\r\nNVIDIA-SMI has failed");

        Assert.Equal(2, gpus.Count);
        Assert.Equal(1, gpus[0].BusNumber);
        Assert.Equal("617.14", gpus[0].DriverVersion);
        Assert.Equal("NVIDIA GeForce RTX 2080 Ti", gpus[0].Name);
        Assert.Equal(10, gpus[1].BusNumber);
    }

    [Fact]
    public void Nvidia_bar1_total_is_read_for_each_card_and_ignores_framebuffer_total()
    {
        const string output = """
            ==============NVSMI LOG==============

            Timestamp                                              : Fri Sep 25 13:23:23 2026
            Driver Version                                         : 617.14
            Attached GPUs                                          : 2
            GPU 00000000:01:00.0
                FB Memory Usage
                    Total                                          : 11264 MiB
                    Reserved                                       : 237 MiB
                    Used                                           : 1849 MiB
                    Free                                           : 9179 MiB
                BAR1 Memory Usage
                    Total                                          : 256 MiB
                    Used                                           : 2 MiB
                    Free                                           : 254 MiB
                Conf Compute Protected Memory Usage
                    Total                                          : N/A
            GPU 00000000:02:00.0
                FB Memory Usage
                    Total                                          : 12282 MiB
                BAR1 Memory Usage
                    Total                                          : 16 GiB
            """;

        var bars = GpuParsers.ParseNvidiaBar1(output);

        Assert.Equal(2, bars.Count);
        Assert.Equal(new NvidiaBar1(1, 256), bars[0]);
        Assert.Equal(new NvidiaBar1(2, 16384), bars[1]);
    }

    [Fact]
    public void Nvidia_bar1_output_without_bar_section_is_empty() =>
        Assert.Empty(GpuParsers.ParseNvidiaBar1("NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver."));

    [Theory]
    [InlineData("00000000:01:00.0", 1)]
    [InlineData("0000:2B:00.0", 0x2B)]
    [InlineData("01:00.0", 1)]
    public void Bus_number_is_read_in_hexadecimal(string busId, int expected) =>
        Assert.Equal(expected, GpuParsers.ParseBusNumber(busId));

    [Theory]
    [InlineData(null)]
    [InlineData("garbage")]
    public void Bus_number_is_null_when_malformed(string? busId) => Assert.Null(GpuParsers.ParseBusNumber(busId));

    [Theory]
    [InlineData(1L, "Gen1 (2,5 GT/s)")]
    [InlineData(3L, "Gen3 (8 GT/s)")]
    [InlineData(4L, "Gen4 (16 GT/s)")]
    [InlineData(9L, "code de vitesse 9")]
    [InlineData(null, "vitesse inconnue")]
    public void Pcie_generation_is_named(long? speed, string expected) => Assert.Equal(expected, GpuParsers.PcieGeneration(speed));

    [Fact]
    public void Wql_literals_are_escaped() =>
        Assert.Equal(@"PCI\\VEN_10DE&DEV_1E04\\4&F71F481\'X", GpuParsers.EscapeWql(@"PCI\VEN_10DE&DEV_1E04\4&F71F481'X"));

    [Theory]
    [InlineData(HardwareVendor.Nvidia, "NVIDIA GeForce RTX 2080 Ti", "nvidia-game-ready")]
    [InlineData(HardwareVendor.Nvidia, "NVIDIA GeForce GTX 1660 SUPER", "nvidia-game-ready")]
    [InlineData(HardwareVendor.Nvidia, "NVIDIA GeForce GTX 1080 Ti", "nvidia-r580-legacy")]
    [InlineData(HardwareVendor.Nvidia, "NVIDIA GeForce GTX 970", "nvidia-r580-legacy")]
    [InlineData(HardwareVendor.Amd, "Radeon RX 580 Series", "amd-polaris-vega")]
    [InlineData(HardwareVendor.Amd, "AMD Radeon RX 5700 XT", "amd-adrenalin-rdna1-2")]
    [InlineData(HardwareVendor.Amd, "AMD Radeon RX 6800 XT", "amd-adrenalin-rdna1-2")]
    [InlineData(HardwareVendor.Amd, "AMD Radeon RX 7900 XTX", "amd-adrenalin")]
    [InlineData(HardwareVendor.Amd, "AMD Radeon RX 9070 XT", "amd-adrenalin")]
    [InlineData(HardwareVendor.Intel, "Intel(R) Arc(TM) A770 Graphics", "intel-arc")]
    [InlineData(HardwareVendor.Intel, "Intel(R) Iris(R) Xe Graphics", "intel-11-14")]
    [InlineData(HardwareVendor.Intel, "Intel(R) UHD Graphics 770", "intel-11-14")]
    [InlineData(HardwareVendor.Intel, "Intel(R) UHD Graphics 630", "intel-7-10")]
    public void Catalog_finds_the_driver_branch(HardwareVendor vendor, string name, string expected) =>
        Assert.Equal(expected, GpuDriverCatalog.Default.FindBranch(vendor, name)?.Id);

    [Fact]
    public void Catalog_flags_resizable_bar_and_frame_generation_capable_cards()
    {
        var catalog = GpuDriverCatalog.Default;

        Assert.True(catalog.IsResizableBarCapable(HardwareVendor.Nvidia, "NVIDIA GeForce RTX 3080"));
        Assert.False(catalog.IsResizableBarCapable(HardwareVendor.Nvidia, "NVIDIA GeForce RTX 2080 Ti"));
        Assert.True(catalog.IsResizableBarCapable(HardwareVendor.Intel, "Intel(R) Arc(TM) B580 Graphics"));
        Assert.True(catalog.NeedsHagsForFrameGeneration("NVIDIA GeForce RTX 5090"));
        Assert.False(catalog.NeedsHagsForFrameGeneration("NVIDIA GeForce RTX 3090"));
        Assert.StartsWith("https://", catalog.DownloadUrlFor(HardwareVendor.Amd), StringComparison.Ordinal);
        Assert.True(catalog.CheckedOn >= new DateOnly(2026, 9, 1));
    }
}
