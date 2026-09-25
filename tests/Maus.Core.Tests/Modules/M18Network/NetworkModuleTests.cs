using Maus.Core.Modules.M18Network;
using Maus.Core.Platform;

namespace Maus.Core.Tests.Modules.M18Network;

public class NetworkModuleTests
{
    private sealed class FakeWifi(WifiLink? link) : IWifiSource
    {
        public WifiLink? Current() => link;
    }

    private static Dictionary<string, object?> Adapter(string name, long medium, long speed, bool connected = true, bool hardware = true) => new()
    {
        ["Name"] = name,
        ["InterfaceDescription"] = name + " Controller",
        ["MediaConnectState"] = connected ? 1u : 2u,
        ["ReceiveLinkSpeed"] = (ulong)speed,
        ["NdisPhysicalMedium"] = (uint)medium,
        ["HardwareInterface"] = hardware,
    };

    private static Task<IReadOnlyList<Finding>> Detect(FakeCim cim, WifiLink? wifi = null) =>
        new NetworkModule(new FakeWifi(wifi)).DetectAsync(TestContext.Create(cim: cim), CancellationToken.None);

    private static FakeCim Adapters(params Dictionary<string, object?>[] rows) =>
        new FakeCim().Answer(NetworkModule.AdaptersQuery, CimScopes.StandardCimv2, rows);

    [Fact]
    public async Task Gigabit_ethernet_is_compliant()
    {
        var finding = Assert.Single(await Detect(Adapters(Adapter("Ethernet", 14, 1_000_000_000))));

        Assert.Equal(FindingStatus.Ok, finding.Status);
        Assert.Equal("1 Gb/s", finding.Current);
    }

    [Fact]
    public async Task A_cable_stuck_at_100_megabits_is_reported()
    {
        var finding = Assert.Single(await Detect(Adapters(Adapter("Ethernet", 14, 100_000_000))));

        Assert.Equal(FindingStatus.Improvable, finding.Status);
        Assert.Contains("câble", finding.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Weak_wifi_on_2_4_ghz_gets_advice()
    {
        var wifi = new WifiLink("Maison", 35, 72, 72, 7, 6);

        var finding = Assert.Single(await Detect(Adapters(Adapter("Wi-Fi", 9, 72_000_000)), wifi));

        Assert.Equal(FindingStatus.Improvable, finding.Status);
        Assert.Contains("35 %", finding.Current, StringComparison.Ordinal);
        Assert.Contains("2,4 GHz", finding.Current, StringComparison.Ordinal);
        Assert.Contains("5 GHz", finding.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Good_wifi_6_is_compliant()
    {
        var wifi = new WifiLink("Maison", 88, 1201, 1201, 10, 36);

        var finding = Assert.Single(await Detect(Adapters(Adapter("Wi-Fi", 9, 1_201_000_000)), wifi));

        Assert.Equal(FindingStatus.Ok, finding.Status);
        Assert.Contains("Wi-Fi 6", finding.Current, StringComparison.Ordinal);
        Assert.Contains("5 GHz", finding.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Virtual_and_disconnected_adapters_are_ignored()
    {
        var finding = Assert.Single(await Detect(Adapters(Adapter("VPN", 14, 100_000_000, hardware: false), Adapter("Ethernet 2", 14, 100_000_000, connected: false))));

        Assert.Equal("M18.connection", finding.Id);
        Assert.Equal(FindingStatus.Info, finding.Status);
    }

    [Theory]
    [InlineData(10, 1, null)]
    [InlineData(7, 11, "2,4 GHz")]
    [InlineData(8, 44, "5 GHz")]
    [InlineData(11, 200, "5 GHz")]
    public void The_band_is_only_given_when_certain(int phy, int channel, string? band)
    {
        Assert.Equal(band, new WifiLink("x", 50, 1, 1, phy, channel).Band);
    }
}
