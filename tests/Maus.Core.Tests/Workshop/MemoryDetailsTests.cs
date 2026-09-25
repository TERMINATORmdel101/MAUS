using System.Buffers.Binary;
using System.Text;
using Maus.Core.Workshop.Memory;

namespace Maus.Core.Tests.Workshop;

public class MemoryDetailsTests
{
    /// <summary>Barrette DDR4 8 Go (x8, 1 rang, puces 8 Gbit) : JEDEC 3200 CL22, XMP 3600 16-19-19-39 à 1,35 V.</summary>
    internal static byte[] Ddr4Kit()
    {
        var b = new byte[512];
        b[1] = 0x11;
        b[2] = 0x0C;
        b[3] = 0x02;
        b[4] = 0x85;
        b[5] = 0x29;
        b[12] = 0x01;
        b[13] = 0x03;
        b[18] = 5;
        b[20] = 0x00;
        b[21] = 0x80;
        b[24] = 110;
        b[25] = 110;
        b[26] = 110;
        b[27] = 0x11;
        b[28] = 0;
        b[29] = 110;
        b[30] = 0x30;
        b[31] = 0x11;
        b[37] = 168;
        b[38] = 20;
        b[39] = 40;
        b[40] = 40;
        b[42] = 120;
        b[44] = 20;
        b[45] = 60;
        Crc(b, 126, 126);
        Encoding.ASCII.GetBytes("F4-3600C16-8GVKC    ").CopyTo(b, 329);
        b[323] = 0x21;
        b[324] = 0x14;

        b[384] = 0x0C;
        b[385] = 0x4A;
        b[386] = 0x01;
        b[387] = 0x20;
        b[393] = 0xA3;
        b[396] = 5;
        b[431] = unchecked((byte)-70);
        b[401] = 71;
        b[430] = 13;
        b[402] = 84;
        b[429] = 56;
        b[403] = 84;
        b[428] = 56;
        b[405] = 173;
        return b;
    }

    /// <summary>Barrette DDR5 16 Go (x8, 1 rang, 2 sous-canaux) : JEDEC 4800 40-39-39, XMP 6000 36-38-38 « Gaming », EXPO 6000 30-36-36.</summary>
    internal static byte[] Ddr5Kit()
    {
        var b = new byte[1024];
        b[1] = 0x10;
        b[2] = 0x12;
        b[3] = 0x02;
        b[4] = 0x04;
        b[6] = 0x20;
        b[7] = 0x62;
        b[234] = 0x00;
        b[235] = 0x22;
        b[200] = 0x82;
        U16(b, 20, 416);
        b[24] = 0xFF;
        U16(b, 30, 16000);
        U16(b, 32, 16000);
        U16(b, 34, 16000);
        U16(b, 36, 32000);
        U16(b, 38, 48000);
        U16(b, 40, 30000);
        U16(b, 42, 295);
        U16(b, 44, 160);
        U16(b, 46, 130);
        U16(b, 70, 5000);
        b[72] = 8;
        U16(b, 82, 13333);
        b[84] = 32;
        Crc(b, 510, 510);
        Encoding.ASCII.GetBytes("CMK32GX5M2B6000C36").CopyTo(b, 521);

        b[640] = 0x0C;
        b[641] = 0x4A;
        b[642] = 0x30;
        b[643] = 0x01;
        Encoding.ASCII.GetBytes("Gaming").CopyTo(b, 654);
        var p = 704;
        b[p] = 0x30;
        b[p + 1] = 0x27;
        b[p + 2] = 0x27;
        U16(b, p + 5, 333);
        U16(b, p + 13, 12000);
        U16(b, p + 15, 12667);
        U16(b, p + 17, 12667);
        U16(b, p + 19, 32000);
        U16(b, p + 21, 44667);
        U16(b, p + 25, 295);
        U16(b, p + 31, 5000);
        b[p + 33] = 8;
        U16(b, p + 52, 13333);
        b[p + 54] = 32;
        Crc(b.AsSpan(p, 62), b, p + 62);

        "EXPO"u8.ToArray().CopyTo(b, 832);
        b[836] = 0x10;
        b[837] = 0x01;
        var e = 842;
        b[e] = 0x28;
        b[e + 1] = 0x28;
        b[e + 2] = 0x30;
        U16(b, e + 4, 333);
        U16(b, e + 6, 10000);
        U16(b, e + 8, 12000);
        U16(b, e + 10, 12000);
        U16(b, e + 12, 25000);
        U16(b, e + 14, 37000);
        U16(b, e + 24, 4000);
        U16(b, e + 32, 13333);
        Crc(b.AsSpan(832, 126), b, 958);
        return b;
    }

    private static void U16(byte[] b, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(offset), (ushort)value);

    private static void Crc(byte[] b, int length, int at) => Crc(b.AsSpan(0, length), b, at);

    private static void Crc(ReadOnlySpan<byte> covered, byte[] b, int at)
    {
        var crc = SpdDecoder.Crc16(covered);
        b[at] = (byte)crc;
        b[at + 1] = (byte)(crc >> 8);
    }

    private static int? Clocks(SpdProfile profile, string key) => profile.Timings.FirstOrDefault(t => t.Key == key)?.Clocks;

    [Fact]
    public void Ddr4_module_jedec_and_xmp_are_decoded()
    {
        var module = SpdDecoder.Decode(new SpdImage(0, Ddr4Kit(), "G.Skill", "Samsung"))!;

        Assert.Equal("DDR4", module.MemoryType);
        Assert.Equal("UDIMM", module.ModuleType);
        Assert.Equal(8L << 30, module.CapacityBytes);
        Assert.Equal((1, 8, 8), (module.Ranks, module.DeviceWidth, module.DieDensityGbit));
        Assert.Equal((4, 4), (module.BankGroups, module.BanksPerGroup));
        Assert.True(module.ChecksumOk);
        Assert.Equal("F4-3600C16-8GVKC", module.PartNumber);
        Assert.Equal((2021, 14), (module.ManufacturedYear, module.ManufacturedWeek));

        var jedec = module.Profiles[0];
        Assert.Equal(ProfileKind.Jedec, jedec.Kind);
        Assert.Equal(3200, jedec.SpeedMts);
        Assert.Equal("22-22-22-52", jedec.Summary);
        Assert.Equal(74, Clocks(jedec, "tRC"));
        Assert.Equal(880, Clocks(jedec, "tRFC"));
        Assert.Contains(22, jedec.SupportedCl);

        var xmp = module.Profiles[1];
        Assert.Equal(ProfileKind.Xmp, xmp.Kind);
        Assert.Equal("2.0", xmp.Version);
        Assert.Equal(3600, xmp.SpeedMts);
        Assert.Equal("16-19-19-39", xmp.Summary);
        Assert.Equal(1.35, xmp.Vdd);
        Assert.Equal(8.9, xmp.TrueLatencyNs!.Value, 1);
    }

    [Fact]
    public void Ddr5_module_jedec_xmp_and_expo_are_decoded()
    {
        var module = SpdDecoder.Decode(new SpdImage(1, Ddr5Kit()))!;

        Assert.Equal("DDR5", module.MemoryType);
        Assert.Equal(16L << 30, module.CapacityBytes);
        Assert.Equal((8, 4, 2), (module.BankGroups, module.BanksPerGroup, module.SubChannels));
        Assert.Equal("PMIC5100", module.Pmic);
        Assert.True(module.ChecksumOk);

        var jedec = module.Profiles.Single(p => p.Kind == ProfileKind.Jedec);
        Assert.Equal(4800, jedec.SpeedMts);
        Assert.Equal("40-39-39-77", jedec.Summary);
        Assert.Equal(12, Clocks(jedec, "tRRDL"));
        Assert.Equal(32, Clocks(jedec, "tFAW"));

        var xmp = module.Profiles.Single(p => p.Kind == ProfileKind.Xmp);
        Assert.Equal(6000, xmp.SpeedMts);
        Assert.Equal("Gaming", xmp.Name);
        Assert.Equal("36-38-38-96", xmp.Summary);
        Assert.Equal((1.35, 1.35, 1.8), (xmp.Vdd!.Value, xmp.Vddq!.Value, xmp.Vpp!.Value));
        Assert.True(xmp.ChecksumOk);
        Assert.Equal(15, Clocks(xmp, "tRRDL"));
        Assert.Equal(40, Clocks(xmp, "tFAW"));

        var expo = module.Profiles.Single(p => p.Kind == ProfileKind.Expo);
        Assert.Equal("30-36-36-75", expo.Summary);
        Assert.Equal(1.4, expo.Vdd);
        Assert.Equal(12, Clocks(expo, "tRRDL"));
        Assert.Equal(40, Clocks(expo, "tFAW"));
        Assert.True(expo.ChecksumOk);
    }

    [Theory]
    [InlineData(0x01, 1)]
    [InlineData(0x03, 1)]
    [InlineData(0x11, 2)]
    public void Expo_profile_two_is_enabled_by_bit_four(byte enableBits, int expected)
    {
        var bytes = Ddr5Kit();
        bytes[837] = enableBits;
        U16(bytes, 882 + 4, 312);
        U16(bytes, 882 + 6, 10000);

        Assert.Equal(expected, SpdDecoder.Decode(new SpdImage(0, bytes))!.Profiles.Count(p => p.Kind == ProfileKind.Expo));
    }

    [Fact]
    public void Corrupted_spd_is_flagged_and_unknown_types_are_ignored()
    {
        var bytes = Ddr4Kit();
        bytes[24] ^= 0x01;

        Assert.False(SpdDecoder.Decode(new SpdImage(0, bytes))!.ChecksumOk);
        Assert.Null(SpdDecoder.Decode(new SpdImage(0, new byte[512])));
    }

    [Theory]
    [InlineData(0.625, 3200)]
    [InlineData(0.555, 3600)]
    [InlineData(0.6, 3333)]
    [InlineData(0.535, 3733)]
    public void Ddr4_speeds_round_to_usual_steps(double tck, int expected) => Assert.Equal(expected, SpdDecoder.Ddr4Speed(tck));

    /// <summary>Contrôleur AMD simulé : deux canaux DDR5-6000, 30-36-36-96.</summary>
    private sealed class FakeSmn : ISmnReader
    {
        public Dictionary<uint, uint> Registers { get; } = new()
        {
            [0x50000] = 1,
            [0x150000] = 1,
            [0x50200] = 3000,
            [0x50204] = 30 | (96 << 8) | (36 << 16) | (36 << 24),
            [0x50208] = 132 | (36 << 16),
            [0x5020C] = 8 | (12 << 8) | (18 << 24),
            [0x50210] = 32,
            [0x50214] = 28 | (4 << 8) | (16 << 16),
            [0x50218] = 48,
            [0x50230] = 65535,
            [0x50260] = 0x00C00138,
            [0x50264] = 884 | (480 << 16),
            [0x502A4] = 0,
            [0x5012C] = 1 << 28,
            [0x50050] = 0x87654321,
            [0x50058] = 0x87654321,
            [0x150204] = 30 | (96 << 8) | (36 << 16) | (36 << 24),
            [0x150208] = 132 | (36 << 16),
        };

        public uint Read(uint address) => Registers.GetValueOrDefault(address);
    }

    private sealed class FakePm(uint version, Dictionary<int, float> values) : IPmTableReader
    {
        public uint? Version() => version;

        public byte[] Read(int size)
        {
            var table = new byte[size];
            foreach (var (offset, value) in values)
            {
                BinaryPrimitives.WriteSingleLittleEndian(table.AsSpan(offset), value);
            }

            return table;
        }
    }

    [Fact]
    public void Zen_controller_gives_primary_secondary_tertiary_timings_and_settings()
    {
        var live = ZenMemoryController.ReadTimings(new FakeSmn(), ddr5: true)!;

        Assert.Equal([0, 1], live.Channels);
        Assert.Equal(6000, live.SpeedMts);
        int? T(string key) => live.Timings.Single(t => t.Key == key).Clocks;
        Assert.Equal((30, 36, 36, 36, 96, 132), (T("tCL"), T("tRCDRD"), T("tRCDWR"), T("tRP"), T("tRAS"), T("tRC")));
        Assert.Equal((8, 12, 32, 4, 16, 48, 18, 28), (T("tRRDS"), T("tRRDL"), T("tFAW"), T("tWTRS"), T("tWTRL"), T("tWR"), T("tRTP"), T("tCWL")));
        var rfc = live.Timings.Single(t => t.Key == "tRFC");
        Assert.Equal(884, rfc.Clocks);
        Assert.Equal(294.7, rfc.Nanoseconds!.Value, 1);
        Assert.Equal(1, T("tWRPRE"));
        Assert.Contains(live.Timings, t => t.Group == TimingGroup.Tertiary && t.Key == "tRDRDSCL");
        Assert.False(live.Settings.GearDownMode);
        Assert.False(live.Settings.Command2T);
        Assert.True(live.Settings.PowerDown);
        Assert.False(live.Settings.BankGroupSwap);
        Assert.False(live.ChannelsDiffer);
    }

    [Fact]
    public void Without_driver_no_channel_is_found()
    {
        Assert.Null(ZenMemoryController.ReadTimings(new FakePmLessSmn(), ddr5: true));
    }

    private sealed class FakePmLessSmn : ISmnReader
    {
        public uint Read(uint address) => 0;
    }

    [Fact]
    public void Pm_table_gives_fclk_uclk_mclk_and_voltages()
    {
        var clocks = ZenMemoryController.ReadClocks(new FakePm(0x540104, new() { [0x118] = 2000, [0x128] = 3000, [0x138] = 3000, [0xD0] = 1.25f, [0x430] = 1.05f }))!;

        Assert.Equal((2000.0, 3000.0, 3000.0), (clocks.FclkMhz!.Value, clocks.UclkMhz!.Value, clocks.MclkMhz!.Value));
        Assert.Equal("1:1", clocks.UclkMode);
        Assert.Equal(1.25, clocks.SocVolts);
        Assert.False(clocks.GenericLayout);

        var generic = ZenMemoryController.ReadClocks(new FakePm(0x540999, new() { [0x118] = 2100, [0x128] = 1600, [0x138] = 3200 }))!;
        Assert.True(generic.GenericLayout);
        Assert.Equal("1:2", generic.UclkMode);

        Assert.Null(ZenMemoryController.ReadClocks(new FakePm(0x999999, [])));
    }

    private sealed class FakeSpd(params SpdImage[] images) : ISpdSource
    {
        public IReadOnlyList<SpdImage> ReadAll() => images;
    }

    [Fact]
    public void Full_report_combines_everything_and_says_what_is_missing()
    {
        var report = MemoryDetails.Read(new FakeSpd(new SpdImage(0, Ddr5Kit(), "Corsair", "SK Hynix")), new FakeSmn(),
            new FakePm(0x540104, new() { [0x118] = 2000, [0x128] = 3000, [0x138] = 3000 }), ddr5Hint: null);

        Assert.Single(report.Modules);
        Assert.NotNull(report.Live);
        Assert.NotNull(report.Clocks);
        var text = MemoryDetails.ToText(report);
        Assert.Contains("FCLK 2000 MHz", text, StringComparison.Ordinal);
        Assert.Contains("tRCDWR 36", text, StringComparison.Ordinal);
        Assert.Contains("CMK32GX5M2B6000C36", text, StringComparison.Ordinal);
        Assert.Contains("EXPO", text, StringComparison.Ordinal);

        var intel = MemoryDetails.Read(new FakeSpd(), null, null, ddr5Hint: true);
        Assert.Null(intel.Live);
        Assert.Equal(2, intel.Notes.Count);
        Assert.NotNull(MemoryDetails.Describe("tCL"));
    }
}
