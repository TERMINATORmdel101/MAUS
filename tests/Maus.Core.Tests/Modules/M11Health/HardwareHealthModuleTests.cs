using Maus.Core.Hardware;
using Maus.Core.Modules.M11Health;
using Maus.Core.Platform;

namespace Maus.Core.Tests.Modules.M11Health;

public class HardwareHealthModuleTests
{
    private const string FrenchTrimOn =
        "NTFS DisableDeleteNotify = 0   (Autorise l’envoi d'opérations TRIM au dispositif de stockage)\r\n"
        + "ReFS DisableDeleteNotify = 0   (Autorise l’envoi d'opérations TRIM au dispositif de stockage)\r\n";

    private const string EnglishTrimOff =
        "NTFS DisableDeleteNotify = 1  (Disabled)\r\nReFS DisableDeleteNotify = 0  (Enabled)\r\n";

    private const long Gigabyte = 1024L * 1024 * 1024;

    private static readonly DateTime Recent = new(2026, 9, 20, 10, 0, 0);

    [Fact]
    public async Task Healthy_nvme_pc_is_compliant()
    {
        var context = Context(NvmeDisk(), trimOutput: FrenchTrimOn);

        var findings = await Detect(context);

        Assert.Equal(FindingStatus.Ok, Status(findings, "M11.system-disk"));
        Assert.Equal(FindingStatus.Ok, Status(findings, "M11.disk-0-health"));
        Assert.Equal(FindingStatus.Ok, Status(findings, "M11.disk-0-reliability"));
        Assert.Equal(FindingStatus.Ok, Status(findings, "M11.trim"));
        Assert.Equal(FindingStatus.Ok, Status(findings, "M11.free-space"));
        Assert.Null(findings.Single(f => f.Id == "M11.free-space").SettingsPage);
        Assert.Equal(FindingStatus.Ok, Status(findings, "M11.firmware-throttling"));
        Assert.Equal(FindingStatus.Info, Status(findings, "M11.benchmark"));
        Assert.Contains("Atelier", findings.Single(f => f.Id == "M11.benchmark").Title, StringComparison.Ordinal);
        Assert.Equal("sain · SSD NVMe · 465,8 Go", findings.Single(f => f.Id == "M11.disk-0-health").Current);
        Assert.DoesNotContain(findings, f => f.Status is FindingStatus.Problem or FindingStatus.Warning or FindingStatus.Unknown);
    }

    [Fact]
    public async Task Memory_bandwidth_uses_channels_times_speed_times_eight()
    {
        var findings = await Detect(Context(NvmeDisk(), trimOutput: FrenchTrimOn));

        var memory = findings.Single(f => f.Id == "M11.ram-bandwidth");
        Assert.Equal(FindingStatus.Info, memory.Status);
        Assert.Equal("55,5 Go/s", memory.Current);
        Assert.Contains("2 canal(aux) × 3467 MT/s", memory.Explanation, StringComparison.Ordinal);
        Assert.Contains("DDR4", memory.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Windows_on_hard_drive_is_a_warning()
    {
        var disk = Row(("DeviceId", "0"), ("FriendlyName", "WDC WD10EZEX"), ("MediaType", (ushort)3), ("BusType", (ushort)11), ("HealthStatus", (ushort)0), ("Size", 1_000_204_886_016UL), ("SpindleSpeed", 7200u));
        var findings = await Detect(Context(disk, trimOutput: FrenchTrimOn));

        var system = findings.Single(f => f.Id == "M11.system-disk");
        Assert.Equal(FindingStatus.Warning, system.Status);
        Assert.StartsWith("disque dur (HDD)", system.Current, StringComparison.Ordinal);
        Assert.NotNull(system.Advice);
    }

    [Fact]
    public async Task Trim_disabled_with_an_ssd_is_a_fixable_warning()
    {
        var findings = await Detect(Context(NvmeDisk(), trimOutput: EnglishTrimOff));

        var trim = findings.Single(f => f.Id == "M11.trim");
        Assert.Equal(FindingStatus.Warning, trim.Status);
        Assert.True(trim.Fixable);
    }

    [Fact]
    public async Task Trim_disabled_without_ssd_is_only_information()
    {
        var disk = Row(("DeviceId", "0"), ("FriendlyName", "HDD"), ("MediaType", (ushort)3), ("BusType", (ushort)11), ("HealthStatus", (ushort)0));
        var findings = await Detect(Context(disk, trimOutput: EnglishTrimOff));

        Assert.Equal(FindingStatus.Info, Status(findings, "M11.trim"));
    }

    [Fact]
    public async Task Low_free_space_is_a_warning()
    {
        var files = new FakeFiles().SetDrive(@"C:\", 10 * Gigabyte, 200 * Gigabyte);
        var findings = await Detect(Context(NvmeDisk(), trimOutput: FrenchTrimOn, files: files));

        Assert.Equal(FindingStatus.Warning, Status(findings, "M11.free-space"));
        Assert.Equal("ms-settings:storagerecommendations", findings.Single(f => f.Id == "M11.free-space").SettingsPage);
    }

    [Fact]
    public async Task Failing_disk_is_a_problem_and_listed_first()
    {
        var disk = Row(("DeviceId", "1"), ("FriendlyName", "Old SSD"), ("MediaType", (ushort)4), ("BusType", (ushort)11), ("HealthStatus", (ushort)2));
        var context = Context(disk, trimOutput: FrenchTrimOn);

        var findings = await Detect(context);

        Assert.Equal(FindingStatus.Problem, Status(findings, "M11.disk-1-health"));
        Assert.Equal(FindingStatus.Problem, findings[0].Status);
    }

    [Fact]
    public async Task Warning_health_status_is_a_warning()
    {
        var disk = Row(("DeviceId", "0"), ("FriendlyName", "SSD"), ("MediaType", (ushort)4), ("BusType", (ushort)11), ("HealthStatus", (ushort)1));
        var findings = await Detect(Context(disk, trimOutput: FrenchTrimOn));

        Assert.Equal(FindingStatus.Warning, Status(findings, "M11.disk-0-health"));
    }

    [Fact]
    public async Task Worn_ssd_is_a_warning_and_read_errors_are_a_problem()
    {
        var worn = Context(NvmeDisk(), trimOutput: FrenchTrimOn, reliability: Row(("DeviceId", "0"), ("Wear", (byte)85), ("Temperature", (byte)40), ("TemperatureMax", (byte)70), ("ReadErrorsUncorrected", 0UL)));
        var broken = Context(NvmeDisk(), trimOutput: FrenchTrimOn, reliability: Row(("DeviceId", "0"), ("Wear", (byte)10), ("ReadErrorsUncorrected", 3UL)));
        var hot = Context(NvmeDisk(), trimOutput: FrenchTrimOn, reliability: Row(("DeviceId", "0"), ("Temperature", (byte)75), ("TemperatureMax", (byte)70)));

        Assert.Equal(FindingStatus.Warning, Status(await Detect(worn), "M11.disk-0-reliability"));
        Assert.Equal(FindingStatus.Problem, Status(await Detect(broken), "M11.disk-0-reliability"));
        Assert.Equal(FindingStatus.Problem, Status(await Detect(hot), "M11.disk-0-reliability"));
    }

    [Fact]
    public async Task Reliability_counters_without_admin_rights_are_unknown()
    {
        var cim = BaseCim(NvmeDisk()).Throw(HardwareHealthModule.ReliabilityQuery, new MausAccessDeniedException("refusé"), CimScopes.Storage);
        var findings = await Detect(TestContext.Create(cim: cim, commands: Trim(FrenchTrimOn), files: Drive(), elevated: false));

        var reliability = findings.Single(f => f.Id == "M11.disk-reliability");
        Assert.Equal(FindingStatus.Unknown, reliability.Status);
        Assert.Contains("administrateur", reliability.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain(findings, f => f.Status is FindingStatus.Problem or FindingStatus.Warning);
    }

    [Fact]
    public async Task Disk_without_counters_is_unknown()
    {
        var findings = await Detect(Context(NvmeDisk(), trimOutput: FrenchTrimOn, reliability: Row(("DeviceId", "7"))));

        Assert.Equal(FindingStatus.Unknown, Status(findings, "M11.disk-0-reliability"));
    }

    [Fact]
    public async Task Nvme_log_replaces_missing_windows_counters()
    {
        // Constaté le 10/10/2026 sur le PC du porteur : pas de compteurs Windows, mais le journal NVMe du disque est lisible.
        var findings = await Detect(Context(NvmeDisk(), trimOutput: FrenchTrimOn, reliability: Row(("DeviceId", "7"))), Healthy);

        Assert.DoesNotContain(findings, f => f.Id == "M11.disk-0-reliability");
        Assert.Equal(FindingStatus.Ok, Status(findings, "M11.disk-0-nvme"));
    }

    [Fact]
    public async Task Missing_sources_give_unknown_never_problem()
    {
        var cim = new FakeCim()
            .Throw(HardwareHealthModule.DiskQuery, new DataSourceUnavailableException("absent"), CimScopes.Storage)
            .Throw(HardwareHealthModule.MemoryQuery, new MausAccessDeniedException("refusé"));
        var events = new FakeEventLogs().Deny("System");

        var findings = await Detect(TestContext.Create(cim: cim, eventLogs: events, elevated: false));

        Assert.Equal(FindingStatus.Unknown, Status(findings, "M11.disks"));
        Assert.Equal(FindingStatus.Unknown, Status(findings, "M11.trim"));
        Assert.Equal(FindingStatus.Unknown, Status(findings, "M11.free-space"));
        Assert.Equal(FindingStatus.Unknown, Status(findings, "M11.ram-bandwidth"));
        Assert.Equal(FindingStatus.Unknown, Status(findings, "M11.firmware-throttling"));
        Assert.DoesNotContain(findings, f => f.Status is FindingStatus.Problem or FindingStatus.Warning);
    }

    [Fact]
    public async Task Empty_disk_list_is_unknown()
    {
        var findings = await Detect(TestContext.Create(commands: Trim(FrenchTrimOn), files: Drive()));

        Assert.Equal(FindingStatus.Unknown, Status(findings, "M11.disks"));
        Assert.Equal(FindingStatus.Ok, Status(findings, "M11.trim"));
    }

    [Fact]
    public async Task Unreadable_fsutil_output_is_unknown()
    {
        var findings = await Detect(Context(NvmeDisk(), trimOutput: "Erreur : accès refusé."));

        Assert.Equal(FindingStatus.Unknown, Status(findings, "M11.trim"));
    }

    [Fact]
    public async Task Firmware_throttling_is_a_warning_on_desktop_and_information_on_laptop()
    {
        var events = new FakeEventLogs().Add("System", HardwareHealthModule.ProcessorPowerProvider, 37, Recent);

        var desktop = await Detect(Context(NvmeDisk(), trimOutput: FrenchTrimOn, events: events));
        var laptop = await Detect(Context(NvmeDisk(), trimOutput: FrenchTrimOn, events: events, hardware: new HardwareProfile { FormFactor = FormFactor.Laptop }));

        Assert.Equal(FindingStatus.Warning, Status(desktop, "M11.firmware-throttling"));
        Assert.Equal(FindingStatus.Info, Status(laptop, "M11.firmware-throttling"));
    }

    [Theory]
    [InlineData(FrenchTrimOn, 0)]
    [InlineData(EnglishTrimOff, 1)]
    [InlineData("DisableDeleteNotify = 1\r\n", 1)]
    [InlineData("ReFS DisableDeleteNotify = 1\r\nNTFS DisableDeleteNotify = 0\r\n", 0)]
    [InlineData("ReFS DisableDeleteNotify = 1\r\n", null)]
    [InlineData("", null)]
    [InlineData("n'importe quoi", null)]
    public void Fsutil_output_is_parsed(string output, int? expected)
    {
        Assert.Equal(expected, HealthParsers.ParseNtfsDisableDeleteNotify(output));
    }

    [Fact]
    public void Memory_channels_are_counted_from_locators()
    {
        Assert.Equal((2, false), HealthParsers.CountMemoryChannels([("ChannelA-DIMM1", "BANK 1"), ("ChannelB-DIMM1", "BANK 3")]));
        Assert.Equal((1, false), HealthParsers.CountMemoryChannels([("ChannelA-DIMM0", null), ("ChannelA-DIMM1", null)]));
        Assert.Equal((2, false), HealthParsers.CountMemoryChannels([("DIMM_A2", null), ("DIMM_B2", null)]));
        Assert.Equal((2, false), HealthParsers.CountMemoryChannels([("Controller0-ChannelA-DIMM0", null), ("Controller1-ChannelB-DIMM0", null)]));
        Assert.Equal((2, false), HealthParsers.CountMemoryChannels([(null, "P0 CHANNEL A"), (null, "P0 CHANNEL B")]));
        Assert.Equal((2, true), HealthParsers.CountMemoryChannels([("DIMM 1", "Slot1"), ("DIMM 2", "Slot2"), ("DIMM 3", "Slot3")]));
        Assert.Equal((1, true), HealthParsers.CountMemoryChannels([("XMM1", null)]));
        Assert.Equal((0, true), HealthParsers.CountMemoryChannels([]));
    }

    [Fact]
    public void Drive_letters_and_memory_types_are_decoded()
    {
        Assert.Equal('C', HealthParsers.DriveLetterOf('c'));
        Assert.Equal('D', HealthParsers.DriveLetterOf("D"));
        Assert.Equal('E', HealthParsers.DriveLetterOf((ushort)'E'));
        Assert.Equal('C', HealthParsers.DriveLetterOf(@"C:\Windows"));
        Assert.Null(HealthParsers.DriveLetterOf('\0'));
        Assert.Null(HealthParsers.DriveLetterOf(null));
        Assert.Equal("DDR4", HealthParsers.MemoryTypeLabel(26));
        Assert.Equal("DDR5", HealthParsers.MemoryTypeLabel(34));
        Assert.Null(HealthParsers.MemoryTypeLabel(0));
        Assert.Equal("465,8 Go", HealthParsers.FormatGigabytes(500_107_862_016));
    }

    [Fact]
    public void Disk_type_is_derived_from_media_bus_and_spindle()
    {
        Assert.True(new PhysicalDiskInfo("0", "a", 0, 17, 0, null, null).IsSsd);
        Assert.True(new PhysicalDiskInfo("0", "a", 0, 11, 0, null, 0).IsSsd);
        Assert.True(new PhysicalDiskInfo("0", "a", 0, 11, 0, null, 5400).IsHdd);
        Assert.False(new PhysicalDiskInfo("0", "a", 0, 7, 0, null, uint.MaxValue).IsHdd);
        Assert.Equal("SSD SATA", new PhysicalDiskInfo("0", "a", 4, 11, 0, null, null).MediaLabel);
        Assert.Equal("type de disque inconnu", new PhysicalDiskInfo("0", "a", 0, 7, 0, null, null).MediaLabel);
    }

    private static Task<IReadOnlyList<Finding>> Detect(AuditContext context, NvmeHealthLog? nvme = null) =>
        new HardwareHealthModule(new FakeNvme(nvme)).DetectAsync(context, CancellationToken.None);

    /// <summary>Journal NVMe simulé (jamais le vrai disque de la machine de test).</summary>
    private sealed class FakeNvme(NvmeHealthLog? log) : INvmeHealthReader
    {
        public NvmeHealthLog? Read(int diskNumber) => log;
    }

    private static NvmeHealthLog Healthy => new(0, 31, 100, 10, 2, 20_000_000, 37, 27, 0);

    [Fact]
    public async Task Healthy_nvme_log_is_compliant_and_shows_the_drive_values()
    {
        var finding = (await Detect(Context(NvmeDisk(), "NTFS DisableDeleteNotify = 0"), Healthy)).Single(f => f.Id == "M11.disk-0-nvme");

        Assert.Equal(FindingStatus.Ok, finding.Status);
        Assert.Contains("réserve 100 % (seuil du fabricant 10 %)", finding.Current, StringComparison.Ordinal);
        Assert.Contains("10,24 To écrits", finding.Current, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x08, "passé en lecture seule")]
    [InlineData(0x04, "fiabilité dégradée")]
    [InlineData(0x01, "réserve sous le seuil du fabricant")]
    public async Task Critical_warning_declared_by_the_drive_is_a_problem(byte warning, string expected)
    {
        var finding = (await Detect(Context(NvmeDisk(), "NTFS DisableDeleteNotify = 0"), Healthy with { CriticalWarning = warning })).Single(f => f.Id == "M11.disk-0-nvme");

        Assert.Equal(FindingStatus.Problem, finding.Status);
        Assert.Contains(expected, finding.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Spare_under_the_drive_threshold_is_a_problem_even_without_the_bit()
    {
        var finding = (await Detect(Context(NvmeDisk(), "NTFS DisableDeleteNotify = 0"), Healthy with { AvailableSpare = 5 })).Single(f => f.Id == "M11.disk-0-nvme");

        Assert.Equal(FindingStatus.Problem, finding.Status);
    }

    [Fact]
    public async Task Temperature_alert_alone_is_a_warning_and_wear_over_100_is_information()
    {
        var hot = (await Detect(Context(NvmeDisk(), "NTFS DisableDeleteNotify = 0"), Healthy with { CriticalWarning = 0x02 })).Single(f => f.Id == "M11.disk-0-nvme");
        var worn = (await Detect(Context(NvmeDisk(), "NTFS DisableDeleteNotify = 0"), Healthy with { PercentageUsed = 104 })).Single(f => f.Id == "M11.disk-0-nvme");

        Assert.Equal(FindingStatus.Warning, hot.Status);
        Assert.Equal(FindingStatus.Info, worn.Status);
        Assert.Contains("pas une panne", worn.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreadable_nvme_log_adds_no_finding()
    {
        Assert.DoesNotContain(await Detect(Context(NvmeDisk(), "NTFS DisableDeleteNotify = 0")), f => f.Id.EndsWith("-nvme", StringComparison.Ordinal));
    }

    private static FindingStatus Status(IReadOnlyList<Finding> findings, string id) => findings.Single(f => f.Id == id).Status;

    private static AuditContext Context(
        Dictionary<string, object?> disk,
        string trimOutput,
        Dictionary<string, object?>? reliability = null,
        FakeFiles? files = null,
        FakeEventLogs? events = null,
        HardwareProfile? hardware = null)
    {
        var cim = BaseCim(disk).Answer(
            HardwareHealthModule.ReliabilityQuery,
            CimScopes.Storage,
            reliability ?? Row(("DeviceId", disk["DeviceId"]), ("Wear", (byte)3), ("Temperature", (byte)38), ("TemperatureMax", (byte)70), ("ReadErrorsUncorrected", 0UL), ("PowerOnHours", 12000u)));
        return TestContext.Create(cim: cim, commands: Trim(trimOutput), files: files ?? Drive(), eventLogs: events, hardware: hardware);
    }

    private static FakeCim BaseCim(Dictionary<string, object?> disk) => new FakeCim()
        .Answer(HardwareHealthModule.DiskQuery, CimScopes.Storage, disk)
        .Answer(HardwareHealthModule.PartitionQuery, CimScopes.Storage,
            Row(("DiskNumber", 0u), ("DriveLetter", '\0')),
            Row(("DiskNumber", 0u), ("DriveLetter", 'C')))
        .Answer(HardwareHealthModule.MemoryQuery,
            Row(("Capacity", 8_589_934_592UL), ("ConfiguredClockSpeed", 3467u), ("Speed", 3467u), ("DeviceLocator", "ChannelA-DIMM1"), ("BankLabel", "BANK 1"), ("SMBIOSMemoryType", 26u)),
            Row(("Capacity", 8_589_934_592UL), ("ConfiguredClockSpeed", 3467u), ("Speed", 3467u), ("DeviceLocator", "ChannelB-DIMM1"), ("BankLabel", "BANK 3"), ("SMBIOSMemoryType", 26u)));

    private static Dictionary<string, object?> NvmeDisk() =>
        Row(("DeviceId", "0"), ("FriendlyName", "CT500P2SSD8"), ("MediaType", (ushort)4), ("BusType", (ushort)17), ("HealthStatus", (ushort)0), ("Size", 500_107_862_016UL), ("SpindleSpeed", 0u));

    private static FakeCommands Trim(string output) => new FakeCommands().Answer("fsutil.exe behavior query DisableDeleteNotify", output);

    private static FakeFiles Drive() => new FakeFiles().SetDrive(@"C:\", 125 * Gigabyte, 465 * Gigabyte);

    private static Dictionary<string, object?> Row(params (string Name, object? Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);
}
