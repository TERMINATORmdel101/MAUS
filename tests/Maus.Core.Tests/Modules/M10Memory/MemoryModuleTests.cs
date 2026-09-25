using Maus.Core.Hardware;
using Maus.Core.Modules.M10Memory;
using Maus.Core.Platform;

namespace Maus.Core.Tests.Modules.M10Memory;

public class MemoryModuleTests
{
    private const long EightGb = 8L * 1024 * 1024 * 1024;
    private const long SixteenGb = 16L * 1024 * 1024 * 1024;

    private static readonly HardwareProfile IntelDesktop = new()
    {
        FormFactor = FormFactor.Desktop,
        BoardManufacturer = "Micro-Star International Co., Ltd.",
        Cpu = new CpuInfo("Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz", HardwareVendor.Intel, 6, 12, 3700),
        Gpus = [new GpuInfo("NVIDIA GeForce RTX 2080 Ti", HardwareVendor.Nvidia, null, null, @"PCI\VEN_10DE", false)],
    };

    private static readonly HardwareProfile Am5Desktop = IntelDesktop with
    {
        BoardManufacturer = "ASUSTeK COMPUTER INC.",
        Cpu = new CpuInfo("AMD Ryzen 7 7800X3D 8-Core Processor", HardwareVendor.Amd, 8, 16, 4200),
    };

    [Fact]
    public async Task Test_pc_with_manual_overclock_and_dual_channel()
    {
        var findings = await Detect(IntelDesktop,
            Dimm("ChannelA-DIMM1", "BANK 1", "3200 Series", 3467, 3467, manufacturer: "8502"),
            Dimm("ChannelB-DIMM1", "BANK 3", "3200 Series", 3467, 3467, manufacturer: "8502"));

        var speed = Single(findings, "M10.xmp");
        Assert.Equal(FindingStatus.Info, speed.Status);
        Assert.Equal("3467 MT/s", speed.Current);
        Assert.Contains("3200 MT/s", speed.Expected);
        Assert.Equal(FindingStatus.Ok, Single(findings, "M10.dual-channel").Status);
        Assert.Equal(FindingStatus.Ok, Single(findings, "M10.mixed-kit").Status);
        Assert.Equal("16 Go au total (2 × 8 Go), DDR4", Single(findings, "M10.capacity").Current);
        var dimm = Single(findings, "M10.dimm-1");
        Assert.Equal("Barrette ChannelA-DIMM1", dimm.Title);
        Assert.Contains("Patriot, 3200 MT/s annoncés", dimm.Current);
        Assert.Contains("code JEDEC 8502", dimm.Explanation);
        Assert.DoesNotContain(findings, f => f.Status is FindingStatus.Problem or FindingStatus.Warning or FindingStatus.Improvable);
    }

    [Fact]
    public async Task Ddr5_kit_at_jedec_speed_on_am5_suggests_expo()
    {
        var findings = await Detect(Am5Desktop,
            Dimm("DIMM 1", "P0 CHANNEL A", "F5-6000J3038F16G", 4800, 4800, type: 34),
            Dimm("DIMM 1", "P0 CHANNEL B", "F5-6000J3038F16G", 4800, 4800, type: 34));

        var speed = Single(findings, "M10.xmp");
        Assert.Equal(FindingStatus.Improvable, speed.Status);
        Assert.Equal("XMP / EXPO probablement désactivé", speed.Title);
        Assert.Contains("vendue pour 6000 MT/s mais fonctionne à 4800 MT/s", speed.Explanation);
        Assert.Contains("EXPO", speed.Advice);
        Assert.Contains("écran noir", speed.Advice);
        Assert.Contains("garantie", speed.Advice);
        Assert.False(speed.Fixable);
        Assert.Equal(FindingStatus.Ok, Single(findings, "M10.dual-channel").Status);
    }

    [Fact]
    public async Task Ddr4_kit_at_jedec_speed_on_intel_suggests_xmp_without_am5_training_note()
    {
        var findings = await Detect(IntelDesktop,
            Dimm("ChannelA-DIMM1", "BANK 1", "CMK16GX4M2B3200C16", 2133, 2133),
            Dimm("ChannelB-DIMM1", "BANK 3", "CMK16GX4M2B3200C16", 2133, 2133));

        var speed = Single(findings, "M10.xmp");
        Assert.Equal(FindingStatus.Improvable, speed.Status);
        Assert.Contains("profil XMP", speed.Advice);
        Assert.DoesNotContain("écran noir", speed.Advice);
    }

    [Theory]
    [InlineData("ASUSTeK COMPUTER INC.", "DOCP")]
    [InlineData("Micro-Star International Co., Ltd.", "A-XMP")]
    [InlineData("Gigabyte Technology Co., Ltd.", "XMP (appelé DOCP chez ASUS et A-XMP chez MSI)")]
    public void Am4_profile_name_depends_on_board(string board, string expected) =>
        Assert.Equal(expected, MemoryModule.ProfileName(Am5Desktop with { BoardManufacturer = board }, 4));

    [Fact]
    public void Profile_name_for_intel_and_am5()
    {
        Assert.Equal("XMP", MemoryModule.ProfileName(IntelDesktop, 5));
        Assert.StartsWith("EXPO", MemoryModule.ProfileName(Am5Desktop, 5), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Profile_active_is_compliant()
    {
        var findings = await Detect(IntelDesktop,
            Dimm("ChannelA-DIMM1", "BANK 1", "F4-3600C16-16GTZNC", 3600, 3600),
            Dimm("ChannelB-DIMM1", "BANK 3", "F4-3600C16-16GTZNC", 3600, 3600));

        Assert.Equal(FindingStatus.Ok, Single(findings, "M10.xmp").Status);
    }

    [Fact]
    public async Task Unknown_part_number_at_jedec_speed_is_undetermined_never_disabled()
    {
        var findings = await Detect(IntelDesktop,
            Dimm("ChannelA-DIMM1", "BANK 1", "99U5471-020.A00LF", 2666, 2666),
            Dimm("ChannelB-DIMM1", "BANK 3", "99U5471-020.A00LF", 2666, 2666));

        var speed = Single(findings, "M10.xmp");
        Assert.Equal(FindingStatus.Unknown, speed.Status);
        Assert.Contains("99U5471-020.A00LF", speed.Explanation);
        Assert.DoesNotContain("désactivé", speed.Title);
    }

    [Fact]
    public async Task Unknown_part_number_above_jedec_speed_means_profile_active()
    {
        var findings = await Detect(IntelDesktop,
            Dimm("ChannelA-DIMM1", "BANK 1", "MYSTERY-RAM", 3600, 3600),
            Dimm("ChannelB-DIMM1", "BANK 3", null, 3600, 3600));

        Assert.Equal(FindingStatus.Ok, Single(findings, "M10.xmp").Status);
    }

    [Fact]
    public async Task Jedec_module_capped_by_platform_is_informative()
    {
        var findings = await Detect(IntelDesktop,
            Dimm("ChannelA-DIMM1", "BANK 1", "CT16G4DFRA32A.C8FE", 2666, 3200),
            Dimm("ChannelB-DIMM1", "BANK 3", "CT16G4DFRA32A.C8FE", 2666, 3200));

        var speed = Single(findings, "M10.xmp");
        Assert.Equal(FindingStatus.Info, speed.Status);
        Assert.Contains("limite probablement", speed.Explanation);
    }

    [Fact]
    public async Task Non_standard_lower_speed_is_informative()
    {
        var findings = await Detect(IntelDesktop,
            Dimm("ChannelA-DIMM1", "BANK 1", "F4-3600C16-16GTZNC", 3466, 3600),
            Dimm("ChannelB-DIMM1", "BANK 3", "F4-3600C16-16GTZNC", 3466, 3600));

        Assert.Equal(FindingStatus.Info, Single(findings, "M10.xmp").Status);
    }

    [Fact]
    public async Task Laptop_gets_no_bios_advice()
    {
        var laptop = IntelDesktop with { FormFactor = FormFactor.Laptop, Gpus = [] };

        var findings = await Detect(laptop, Dimm("ChannelA-DIMM0", "BANK 0", "F4-3200C18S-16GRS", 2666, 2666));

        var speed = Single(findings, "M10.xmp");
        Assert.Equal(FindingStatus.Info, speed.Status);
        Assert.Null(speed.Advice);
        var channel = Single(findings, "M10.dual-channel");
        Assert.Equal(FindingStatus.Warning, channel.Status);
        Assert.Contains("emplacement libre", channel.Advice);
        Assert.Contains("puce graphique intégrée", channel.Explanation);
        Assert.DoesNotContain(findings, f => f.Id == "M10.mixed-kit");
    }

    [Fact]
    public async Task Single_module_is_a_single_channel_warning()
    {
        var findings = await Detect(IntelDesktop, Dimm("ChannelA-DIMM1", "BANK 1", "CMK16GX4M1B3200C16", 3200, 3200, capacity: SixteenGb));

        var channel = Single(findings, "M10.dual-channel");
        Assert.Equal(FindingStatus.Warning, channel.Status);
        Assert.Equal(Severity.Medium, channel.Severity);
        Assert.Contains("A2 et B2", channel.Advice);
    }

    [Fact]
    public async Task Two_modules_on_the_same_channel_is_a_warning()
    {
        var findings = await Detect(IntelDesktop,
            Dimm("ChannelA-DIMM0", "BANK 0", "CMK16GX4M2B3200C16", 3200, 3200),
            Dimm("ChannelA-DIMM1", "BANK 1", "CMK16GX4M2B3200C16", 3200, 3200));

        var channel = Single(findings, "M10.dual-channel");
        Assert.Equal(FindingStatus.Warning, channel.Status);
        Assert.Contains("même canal", channel.Current);
    }

    [Fact]
    public async Task Four_modules_on_two_channels_are_compliant()
    {
        var findings = await Detect(IntelDesktop,
            Dimm("ChannelA-DIMM0", "BANK 0", "CMK16GX4M2B3200C16", 3200, 3200),
            Dimm("ChannelA-DIMM1", "BANK 1", "CMK16GX4M2B3200C16", 3200, 3200),
            Dimm("ChannelB-DIMM0", "BANK 2", "CMK16GX4M2B3200C16", 3200, 3200),
            Dimm("ChannelB-DIMM1", "BANK 3", "CMK16GX4M2B3200C16", 3200, 3200));

        Assert.Equal(FindingStatus.Ok, Single(findings, "M10.dual-channel").Status);
        Assert.Equal(4, findings.Count(f => f.Id.StartsWith("M10.dimm-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Odd_number_of_modules_is_informative()
    {
        var findings = await Detect(IntelDesktop,
            Dimm("ChannelA-DIMM0", "BANK 0", "CMK16GX4M2B3200C16", 3200, 3200),
            Dimm("ChannelA-DIMM1", "BANK 1", "CMK16GX4M2B3200C16", 3200, 3200),
            Dimm("ChannelB-DIMM0", "BANK 2", "CMK16GX4M2B3200C16", 3200, 3200));

        Assert.Equal(FindingStatus.Info, Single(findings, "M10.dual-channel").Status);
    }

    [Fact]
    public async Task Unreadable_slot_names_are_unknown()
    {
        var findings = await Detect(IntelDesktop,
            Dimm("DIMM 0", "BANK 0", "CMK16GX4M2B3200C16", 3200, 3200),
            Dimm("DIMM 1", "BANK 1", "CMK16GX4M2B3200C16", 3200, 3200));

        Assert.Equal(FindingStatus.Unknown, Single(findings, "M10.dual-channel").Status);
    }

    [Fact]
    public async Task Mixed_kit_is_an_informative_risk()
    {
        var findings = await Detect(IntelDesktop,
            Dimm("ChannelA-DIMM1", "BANK 1", "CMK16GX4M2B3200C16", 3200, 3200, capacity: EightGb),
            Dimm("ChannelB-DIMM1", "BANK 3", "F4-3200C16-16GVKB", 3200, 3200, capacity: SixteenGb));

        var mixed = Single(findings, "M10.mixed-kit");
        Assert.Equal(FindingStatus.Info, mixed.Status);
        Assert.Contains("CMK16GX4M2B3200C16", mixed.Current);
        Assert.Contains("8 Go, 16 Go", mixed.Current);
        Assert.Equal("24 Go au total (1 × 8 Go + 1 × 16 Go), DDR4", Single(findings, "M10.capacity").Current);
    }

    [Fact]
    public async Task Mixed_kit_uses_slowest_rated_speed()
    {
        var findings = await Detect(IntelDesktop,
            Dimm("ChannelA-DIMM1", "BANK 1", "F4-3600C16-8GTZNC", 3200, 3200),
            Dimm("ChannelB-DIMM1", "BANK 3", "CMK8GX4M1B3200C16", 3200, 3200));

        var speed = Single(findings, "M10.xmp");
        Assert.Equal(FindingStatus.Ok, speed.Status);
        Assert.Contains("CMK8GX4M1B3200C16", speed.Expected);
    }

    [Fact]
    public async Task Old_bios_reporting_mhz_is_converted()
    {
        var findings = await Detect(IntelDesktop,
            Dimm("ChannelA-DIMM1", "BANK 1", "CMK16GX4M2B3200C16", 1600, 1600),
            Dimm("ChannelB-DIMM1", "BANK 3", "CMK16GX4M2B3200C16", 1600, 1600));

        var speed = Single(findings, "M10.xmp");
        Assert.Equal(FindingStatus.Ok, speed.Status);
        Assert.Equal("3200 MT/s", speed.Current);
    }

    [Fact]
    public async Task Soldered_lpddr_is_informative()
    {
        var laptop = IntelDesktop with { FormFactor = FormFactor.Laptop };

        var findings = await Detect(laptop,
            Dimm("Controller0-ChannelA", "BANK 0", "MT62F1G32D2DS-026", 6400, 6400, type: 35),
            Dimm("Controller1-ChannelA", "BANK 0", "MT62F1G32D2DS-026", 6400, 6400, type: 35));

        Assert.Equal(FindingStatus.Info, Single(findings, "M10.xmp").Status);
        Assert.Equal(FindingStatus.Info, Single(findings, "M10.dual-channel").Status);
        Assert.Contains("LPDDR5", Single(findings, "M10.capacity").Current);
        Assert.DoesNotContain(findings, f => f.Id == "M10.mixed-kit");
    }

    [Fact]
    public async Task Missing_configured_speed_is_unknown()
    {
        var findings = await Detect(IntelDesktop,
            Dimm("ChannelA-DIMM1", "BANK 1", "CMK16GX4M2B3200C16", 0, 0),
            Dimm("ChannelB-DIMM1", "BANK 3", "CMK16GX4M2B3200C16", null, null));

        Assert.Equal(FindingStatus.Unknown, Single(findings, "M10.xmp").Status);
    }

    [Fact]
    public async Task Access_denied_gives_admin_required()
    {
        var cim = new FakeCim().Throw(MemoryModule.MemoryQuery, new MausAccessDeniedException("refusé"));

        var findings = await new MemoryModule().DetectAsync(TestContext.Create(cim: cim, hardware: IntelDesktop), CancellationToken.None);

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Equal(FindingStatus.Unknown, f.Status));
    }

    [Fact]
    public async Task Empty_or_unavailable_inventory_is_unknown()
    {
        var empty = await Detect(IntelDesktop);
        Assert.All(empty, f => Assert.Equal(FindingStatus.Unknown, f.Status));

        var cim = new FakeCim().Throw(MemoryModule.MemoryQuery, new DataSourceUnavailableException("absent"));
        var unavailable = await new MemoryModule().DetectAsync(TestContext.Create(cim: cim, hardware: IntelDesktop), CancellationToken.None);
        Assert.All(unavailable, f => Assert.Equal(FindingStatus.Unknown, f.Status));
    }

    [Theory]
    [InlineData(8L * 1024 * 1024 * 1024, "8 Go")]
    [InlineData(512L * 1024 * 1024, "0,5 Go")]
    [InlineData(48L * 1024 * 1024 * 1024, "48 Go")]
    public void Capacity_is_written_in_french(long bytes, string expected) => Assert.Equal(expected, MemoryModule.FormatCapacity(bytes));

    private static Task<IReadOnlyList<Finding>> Detect(HardwareProfile hardware, params Dictionary<string, object?>[] dimms)
    {
        var cim = new FakeCim().Answer(MemoryModule.MemoryQuery, dimms);
        return new MemoryModule().DetectAsync(TestContext.Create(cim: cim, hardware: hardware), CancellationToken.None);
    }

    private static Finding Single(IReadOnlyList<Finding> findings, string id) => findings.Single(f => f.Id == id);

    private static Dictionary<string, object?> Dimm(
        string locator,
        string bank,
        string? partNumber,
        int? configured,
        int? speed,
        long capacity = EightGb,
        int type = 26,
        string manufacturer = "Corsair") => new()
    {
        ["Capacity"] = (ulong)capacity,
        ["Speed"] = speed is null ? null : (uint)speed.Value,
        ["ConfiguredClockSpeed"] = configured is null ? null : (uint)configured.Value,
        ["PartNumber"] = partNumber is null ? null : partNumber + "      ",
        ["Manufacturer"] = manufacturer,
        ["DeviceLocator"] = locator,
        ["BankLabel"] = bank,
        ["SMBIOSMemoryType"] = (uint)type,
    };
}
