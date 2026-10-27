using Maus.Core.Modules.M18Network;
using Maus.Core.Platform;

namespace Maus.Core.Tests.Modules.M18Network;

public class NetworkModuleTests
{
    private sealed class FakeWifi(WifiReading reading) : IWifiSource
    {
        public WifiReading Read() => reading;
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
        Detect(cim, new WifiReading(wifi, [], false));

    private static Task<IReadOnlyList<Finding>> Detect(FakeCim cim, WifiReading wifi) =>
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

    [Fact]
    public async Task Location_refusal_is_explained_with_the_settings_page_instead_of_a_stopped_service()
    {
        var finding = Assert.Single(await Detect(Adapters(Adapter("Wi-Fi", 9, 72_000_000)), new WifiReading(null, [], LocationDenied: true)));

        Assert.Equal(FindingStatus.Info, finding.Status);
        Assert.Contains("position précise", finding.Explanation, StringComparison.Ordinal);
        Assert.Equal("ms-settings:privacy-location", finding.SettingsPage);
    }

    [Fact]
    public async Task Nearby_access_points_are_counted_by_channel_without_their_names_and_the_frequency_gives_the_band()
    {
        // Wi-Fi 6 sur le canal 5 : sans la fréquence, 2,4 GHz et 6 GHz sont indiscernables ; 5975 MHz = 6 GHz.
        var link = new WifiLink("Maison", 80, 1200, 1200, 10, 5) { FrequencyMhz = 5975 };
        var nearby = new List<WifiAccessPoint>
        {
            new("AA0000000001", 40, 5975),
            new("AA0000000002", 30, 2437),
            new("AA0000000003", 20, 2437),
            new("AA0000000004", 60, 5180),
        };

        var finding = Assert.Single(await Detect(Adapters(Adapter("Wi-Fi", 9, 1_201_000_000)), new WifiReading(link, nearby, false)));

        Assert.Contains("6 GHz", finding.Current, StringComparison.Ordinal);
        Assert.Contains("Autres points d'accès visibles : 4.", finding.Explanation, StringComparison.Ordinal);
        Assert.Contains("Sur votre canal (5, 6 GHz) : 1.", finding.Explanation, StringComparison.Ordinal);
        Assert.Contains("2,4 GHz : canal 6 (2) ; 5 GHz : canal 36 (1) ; 6 GHz : canal 5 (1)", finding.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("AA00", finding.Explanation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2412, 1)]
    [InlineData(2472, 13)]
    [InlineData(2484, 14)]
    [InlineData(5180, 36)]
    [InlineData(5825, 165)]
    [InlineData(5955, 1)]
    [InlineData(7115, 233)]
    public void Channels_follow_the_ieee_frequency_formulas(int mhz, int channel) => Assert.Equal(channel, WifiChannels.Channel(mhz));

    [Fact]
    public void Bss_list_is_read_with_the_wlanapi_layout_and_bounded_by_its_size()
    {
        var buffer = new byte[8 + (2 * WifiBssList.EntrySize)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)buffer.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), 3); // annonce 3 entrées, n'en contient que 2
        for (var i = 0; i < 2; i++)
        {
            var entry = buffer.AsSpan(8 + (i * WifiBssList.EntrySize));
            entry[40] = 0xA0;
            entry[45] = (byte)i;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(entry[60..], 70 - (i * 10));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(entry[92..], i == 0 ? 2_437_000u : 5_180_000u);
        }

        var points = WifiBssList.Parse(buffer);

        Assert.Equal(2, points.Count);
        Assert.Equal(new WifiAccessPoint("A00000000000", 70, 2437), points[0]);
        Assert.Equal(36, points[1].Channel);
        Assert.Equal("5 GHz", points[1].Band);
    }
}
