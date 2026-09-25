using Maus.Core.Modules.M02Repair;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Tests.Modules.M02Repair;

public class WindowsHealthModuleTests
{
    private const string SystemLog = "System";
    private const long GiB = 1024L * 1024 * 1024;

    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.FromHours(2));

    private static DateTime DaysAgo(double days) => Now.LocalDateTime.AddDays(-days);

    /// <summary>PC sain : WMI répond, volume propre, 100 Go libres, indice de fiabilité 9,2.</summary>
    private static FakeCim HealthyCim() => new FakeCim()
        .Answer(WindowsHealthModule.WmiProbeQuery, new Dictionary<string, object?> { ["Caption"] = "Microsoft Windows 11 Pro" })
        .Answer(WindowsHealthModule.VolumeQuery("C:"), new Dictionary<string, object?> { ["DriveLetter"] = "C:", ["DirtyBitSet"] = false })
        .Answer(
            WindowsHealthModule.ReliabilityQuery(Now),
            new Dictionary<string, object?> { ["SystemStabilityIndex"] = 4.1, ["TimeGenerated"] = DaysAgo(3) },
            new Dictionary<string, object?> { ["SystemStabilityIndex"] = 9.2, ["TimeGenerated"] = DaysAgo(0.1) });

    private static FakeFiles HealthyFiles() => new FakeFiles().SetDrive(@"C:\", 100 * GiB, 500 * GiB);

    private static Task<IReadOnlyList<Finding>> Detect(
        FakeEventLogs? logs = null,
        FakeRegistry? registry = null,
        FakeCim? cim = null,
        FakeFiles? files = null,
        bool elevated = true) =>
        new WindowsHealthModule(@"C:\Windows").DetectAsync(
            TestContext.Create(registry, cim ?? HealthyCim(), elevated: elevated, now: Now, eventLogs: logs, files: files ?? HealthyFiles()),
            CancellationToken.None);

    private static Finding Get(IReadOnlyList<Finding> findings, string id) => findings.Single(f => f.Id == id);

    /// <summary>Contexte construit à la main, pour des lecteurs qui ne sont pas des faux partagés.</summary>
    private static Task<IReadOnlyList<Finding>> DetectWith(IEventLogReader logs, IFileSystemReader files) =>
        new WindowsHealthModule(@"C:\Windows").DetectAsync(
            new AuditContext
            {
                Registry = new FakeRegistry(),
                Cim = HealthyCim(),
                Commands = new FakeCommands(),
                EventLogs = logs,
                Files = files,
                Windows = new WindowsInfo("Windows 11 Pro", "Professional", "25H2", 26200, 1000),
                Hardware = new Maus.Core.Hardware.HardwareProfile(),
                IsElevated = true,
                Now = Now,
            },
            CancellationToken.None);

    [Fact]
    public async Task Healthy_pc_has_no_warning()
    {
        var findings = await Detect();

        Assert.DoesNotContain(findings, f => f.Status is FindingStatus.Problem or FindingStatus.Warning or FindingStatus.Unknown);
        Assert.All(findings, f => Assert.StartsWith("M02.", f.Id, StringComparison.Ordinal));
        Assert.Equal(findings.Count, findings.Select(f => f.Id).Distinct().Count());
        Assert.Equal(FindingStatus.Ok, Get(findings, "M02.whea-fatal").Status);
        Assert.Equal(FindingStatus.Ok, Get(findings, "M02.bluescreens").Status);
        Assert.Equal(FindingStatus.Info, Get(findings, "M02.memory-test").Status);
        Assert.Null(Get(findings, "M02.memory-test").Advice);
        Assert.Equal(FindingStatus.Info, Get(findings, "M02.minidumps").Status);
        Assert.Equal("aucun", Get(findings, "M02.minidumps").Current);

        var reliability = Get(findings, "M02.reliability-index");
        Assert.Equal(FindingStatus.Ok, reliability.Status);
        Assert.StartsWith("9,2 / 10", reliability.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fatal_and_corrected_whea_errors_point_to_hardware_modules()
    {
        var logs = new FakeEventLogs()
            .Add(SystemLog, WindowsHealthModule.WheaProvider, 18, DaysAgo(2))
            .Add(SystemLog, WindowsHealthModule.WheaProvider, 19, DaysAgo(1))
            .Add(SystemLog, WindowsHealthModule.WheaProvider, 19, DaysAgo(3))
            .Add(SystemLog, WindowsHealthModule.WheaProvider, 47, DaysAgo(5))
            .Add(SystemLog, WindowsHealthModule.WheaProvider, 19, DaysAgo(45));

        var findings = await Detect(logs);

        var fatal = Get(findings, "M02.whea-fatal");
        Assert.Equal(FindingStatus.Problem, fatal.Status);
        Assert.StartsWith("1 erreur en 30 jours", fatal.Current, StringComparison.Ordinal);
        Assert.Contains("Modules 10", fatal.Advice, StringComparison.Ordinal);
        Assert.Contains("15", fatal.Advice, StringComparison.Ordinal);

        var corrected = Get(findings, "M02.whea-corrected");
        Assert.Equal(FindingStatus.Warning, corrected.Status);
        Assert.Contains("3 erreurs", corrected.Current, StringComparison.Ordinal);
        Assert.Contains("ID 19 : 2", corrected.Current, StringComparison.Ordinal);
        Assert.Contains("ID 47 : 1", corrected.Current, StringComparison.Ordinal);
        Assert.StartsWith("3 erreurs matérielles corrigées en 30 jours", corrected.Advice, StringComparison.Ordinal);

        // Des erreurs matérielles suffisent à recommander le test de la mémoire.
        Assert.NotNull(Get(findings, "M02.memory-test").Advice);
    }

    [Fact]
    public async Task Blue_screen_is_reported_once_even_with_both_traces()
    {
        var logs = new FakeEventLogs()
            .Add(SystemLog, WindowsHealthModule.KernelPowerProvider, 41, DaysAgo(4), new() { ["BugcheckCode"] = "59", ["PowerButtonTimestamp"] = "0" })
            .Add(SystemLog, WindowsHealthModule.WerProvider, 1001, DaysAgo(4), new()
            {
                ["param1"] = "0x0000003b (0x00000000c0000005, 0xfffff801bdc0684b, 0xfffff98e385f6be0, 0x0000000000000000)",
                ["param2"] = @"C:\WINDOWS\Minidump\091926-7531-01.dmp",
            });

        var findings = await Detect(logs);

        var blueScreens = Get(findings, "M02.bluescreens");
        Assert.Equal(FindingStatus.Warning, blueScreens.Status);
        Assert.Contains("1 écran bleu en 30 jours", blueScreens.Current, StringComparison.Ordinal);
        Assert.Contains("0x0000003B (SYSTEM_SERVICE_EXCEPTION)", blueScreens.Current, StringComparison.Ordinal);
        Assert.Contains("pilote", blueScreens.Advice, StringComparison.Ordinal);
        Assert.Contains("Module 9", blueScreens.Advice, StringComparison.Ordinal);

        // Kernel-Power 41 avec un code d'arrêt n'est pas compté comme une coupure.
        Assert.Equal(FindingStatus.Ok, Get(findings, "M02.unexpected-shutdowns").Status);
    }

    [Fact]
    public async Task Repeated_blue_screens_are_a_problem_and_dump_failures_are_explained()
    {
        var logs = new FakeEventLogs()
            .Add(SystemLog, WindowsHealthModule.KernelPowerProvider, 41, DaysAgo(1), new() { ["BugcheckCode"] = "292" })
            .Add(SystemLog, WindowsHealthModule.KernelPowerProvider, 41, DaysAgo(6), new() { ["BugcheckCode"] = "292" })
            .Add(SystemLog, WindowsHealthModule.KernelPowerProvider, 41, DaysAgo(9), new() { ["BugcheckCode"] = "80" })
            .Add(SystemLog, WindowsHealthModule.VolmgrProvider, 46, DaysAgo(1));

        var findings = await Detect(logs);

        var blueScreens = Get(findings, "M02.bluescreens");
        Assert.Equal(FindingStatus.Problem, blueScreens.Status);
        Assert.Equal(Severity.High, blueScreens.Severity);
        Assert.Contains("3 écrans bleus", blueScreens.Current, StringComparison.Ordinal);
        Assert.Contains("WHEA_UNCORRECTABLE_ERROR", blueScreens.Current, StringComparison.Ordinal);
        Assert.Contains("volmgr 46", blueScreens.Current, StringComparison.Ordinal);
        Assert.Contains("fichier d'échange", blueScreens.Advice, StringComparison.Ordinal);
        Assert.Contains("Module 15", blueScreens.Advice, StringComparison.Ordinal);
        Assert.NotNull(Get(findings, "M02.memory-test").Advice);
    }

    [Fact]
    public async Task Unexpected_shutdowns_distinguish_power_button_from_power_loss()
    {
        var logs = new FakeEventLogs()
            .Add(SystemLog, WindowsHealthModule.KernelPowerProvider, 41, DaysAgo(2), new() { ["BugcheckCode"] = "0", ["PowerButtonTimestamp"] = "134335132494732934" })
            .Add(SystemLog, WindowsHealthModule.KernelPowerProvider, 41, DaysAgo(3), new() { ["BugcheckCode"] = "0", ["PowerButtonTimestamp"] = "0" })
            .Add(SystemLog, WindowsHealthModule.KernelPowerProvider, 41, DaysAgo(4));

        var findings = await Detect(logs);

        var shutdowns = Get(findings, "M02.unexpected-shutdowns");
        Assert.Equal(FindingStatus.Warning, shutdowns.Status);
        Assert.Contains("3 arrêts brutaux", shutdowns.Current, StringComparison.Ordinal);
        Assert.Contains("1 arrêt forcé", shutdowns.Current, StringComparison.Ordinal);
        Assert.Contains("2 coupures", shutdowns.Current, StringComparison.Ordinal);
        Assert.Equal(FindingStatus.Ok, Get(findings, "M02.bluescreens").Status);
    }

    [Fact]
    public async Task Memory_test_result_uses_the_latest_run()
    {
        var failing = new FakeEventLogs()
            .Add(SystemLog, WindowsHealthModule.MemoryDiagnosticsProvider, 1201, DaysAgo(60))
            .Add(SystemLog, WindowsHealthModule.MemoryDiagnosticsProvider, 1202, DaysAgo(10));
        var passing = new FakeEventLogs()
            .Add(SystemLog, WindowsHealthModule.MemoryDiagnosticsProvider, 1102, DaysAgo(60))
            .Add(SystemLog, WindowsHealthModule.MemoryDiagnosticsProvider, 1101, DaysAgo(10));

        var failed = Get(await Detect(failing), "M02.memory-test");
        var passed = Get(await Detect(passing), "M02.memory-test");

        Assert.Equal(FindingStatus.Problem, failed.Status);
        Assert.Contains("Module 10", failed.Advice, StringComparison.Ordinal);
        Assert.Equal(FindingStatus.Ok, passed.Status);
        Assert.StartsWith("aucune erreur", passed.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disk_and_ntfs_errors_point_to_module_11()
    {
        var logs = new FakeEventLogs()
            .Add(SystemLog, WindowsHealthModule.DiskProvider, 51, DaysAgo(6), new() { ["Data0"] = @"\Device\Harddisk1\DR6" })
            .Add(SystemLog, WindowsHealthModule.DiskProvider, 51, DaysAgo(6), new() { ["Data0"] = @"\Device\Harddisk1\DR6" })
            .Add(SystemLog, WindowsHealthModule.DiskProvider, 7, DaysAgo(2), new() { ["Data0"] = @"\Device\Harddisk0\DR0" })
            .Add(SystemLog, WindowsHealthModule.NtfsProvider, 55, DaysAgo(1));

        var findings = await Detect(logs);

        var disk = Get(findings, "M02.disk-errors");
        Assert.Equal(FindingStatus.Warning, disk.Status);
        Assert.Contains("4 événements", disk.Current, StringComparison.Ordinal);
        Assert.Contains("secteur défectueux (disk 7) ×1", disk.Current, StringComparison.Ordinal);
        Assert.Contains("(disk 51) ×2", disk.Current, StringComparison.Ordinal);
        Assert.Contains("Ntfs 55", disk.Current, StringComparison.Ordinal);
        Assert.Contains(@"\Device\Harddisk1\DR6", disk.Current, StringComparison.Ordinal);
        Assert.Contains("Module 11", disk.Advice, StringComparison.Ordinal);
        Assert.True(disk.Fixable);
    }

    [Fact]
    public async Task Event_log_access_denied_gives_admin_required_never_a_problem()
    {
        var findings = await Detect(new FakeEventLogs().Deny(SystemLog), elevated: false);

        foreach (var id in new[] { "M02.whea-fatal", "M02.whea-corrected", "M02.bluescreens", "M02.unexpected-shutdowns", "M02.memory-test", "M02.disk-errors" })
        {
            var finding = Get(findings, id);
            Assert.Equal(FindingStatus.Unknown, finding.Status);
            Assert.Contains("administrateur", finding.Explanation, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(findings, f => f.Status == FindingStatus.Problem);
    }

    [Fact]
    public async Task Missing_event_log_is_unknown()
    {
        var findings = await DetectWith(new ThrowingEventLogs(), HealthyFiles());

        var whea = Get(findings, "M02.whea-fatal");
        Assert.Equal(FindingStatus.Unknown, whea.Status);
        Assert.Contains("Journal absent", whea.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pending_reboot_is_detected_from_each_source()
    {
        var cbs = new FakeRegistry().Set(RegistryHive.LocalMachine, WindowsHealthModule.CbsRebootPendingKey, "Owners", 1);
        var update = new FakeRegistry().Set(RegistryHive.LocalMachine, WindowsHealthModule.WindowsUpdateRebootKey, "{guid}", 1);
        var renames = new FakeRegistry().Set(
            RegistryHive.LocalMachine, WindowsHealthModule.SessionManagerKey, "PendingFileRenameOperations", new[] { @"\??\C:\temp\a.dll", string.Empty });
        var emptyRenames = new FakeRegistry().Set(
            RegistryHive.LocalMachine, WindowsHealthModule.SessionManagerKey, "PendingFileRenameOperations", new[] { string.Empty });

        var byCbs = Get(await Detect(registry: cbs), "M02.pending-reboot");
        var byUpdate = Get(await Detect(registry: update), "M02.pending-reboot");
        var byRenames = Get(await Detect(registry: renames), "M02.pending-reboot");
        var none = Get(await Detect(registry: emptyRenames), "M02.pending-reboot");

        Assert.Equal(FindingStatus.Warning, byCbs.Status);
        Assert.Contains("composants Windows", byCbs.Current, StringComparison.Ordinal);
        Assert.Contains("Windows Update", byUpdate.Current, StringComparison.Ordinal);
        Assert.Contains("fichiers à remplacer", byRenames.Current, StringComparison.Ordinal);
        Assert.NotNull(byRenames.Advice);
        Assert.Equal(FindingStatus.Ok, none.Status);
    }

    [Fact]
    public async Task Pending_reboot_access_denied_is_admin_required()
    {
        var registry = new FakeRegistry()
            .Deny(RegistryHive.LocalMachine, WindowsHealthModule.CbsRebootPendingKey)
            .Deny(RegistryHive.LocalMachine, WindowsHealthModule.SessionManagerKey);

        var finding = Get(await Detect(registry: registry), "M02.pending-reboot");

        Assert.Equal(FindingStatus.Unknown, finding.Status);
    }

    [Fact]
    public async Task Low_free_space_is_a_warning_and_missing_drive_is_unknown()
    {
        var low = Get(await Detect(files: new FakeFiles().SetDrive(@"C:\", 12 * GiB, 256 * GiB)), "M02.disk-space");
        var missing = Get(await Detect(files: new FakeFiles()), "M02.disk-space");

        Assert.Equal(FindingStatus.Warning, low.Status);
        Assert.Equal("12,0 Go libres sur 256,0 Go (C:)", low.Current);
        Assert.Equal("au moins 20,0 Go", low.Expected);
        Assert.NotNull(low.Advice);
        Assert.Equal(FindingStatus.Unknown, missing.Status);
    }

    [Fact]
    public async Task Low_reliability_index_is_a_warning()
    {
        var cim = HealthyCim().Answer(
            WindowsHealthModule.ReliabilityQuery(Now),
            new Dictionary<string, object?> { ["SystemStabilityIndex"] = 3.2f, ["TimeGenerated"] = DaysAgo(0.2) });

        var finding = Get(await Detect(cim: cim), "M02.reliability-index");

        Assert.Equal(FindingStatus.Warning, finding.Status);
        Assert.StartsWith("3,2 / 10", finding.Current, StringComparison.Ordinal);
        Assert.Contains("Observateur de fiabilité", finding.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reliability_index_missing_or_unavailable_is_unknown()
    {
        var empty = HealthyCim().Answer(WindowsHealthModule.ReliabilityQuery(Now));
        var unavailable = HealthyCim().Throw(WindowsHealthModule.ReliabilityQuery(Now), new DataSourceUnavailableException("absent"));
        var denied = HealthyCim().Throw(WindowsHealthModule.ReliabilityQuery(Now), new MausAccessDeniedException("refusé"));

        Assert.Equal(FindingStatus.Unknown, Get(await Detect(cim: empty), "M02.reliability-index").Status);
        Assert.Equal(FindingStatus.Unknown, Get(await Detect(cim: unavailable), "M02.reliability-index").Status);
        Assert.Equal(FindingStatus.Unknown, Get(await Detect(cim: denied), "M02.reliability-index").Status);
    }

    [Fact]
    public void Reliability_query_is_limited_to_the_last_week_in_utc()
    {
        Assert.Equal(
            "SELECT SystemStabilityIndex, TimeGenerated FROM Win32_ReliabilityStabilityMetrics WHERE TimeGenerated >= '20260917100000.000000+000'",
            WindowsHealthModule.ReliabilityQuery(Now));
    }

    [Fact]
    public async Task Broken_wmi_is_a_fixable_warning()
    {
        var failing = HealthyCim().Throw(WindowsHealthModule.WmiProbeQuery, new DataSourceUnavailableException("absent"));
        var silent = HealthyCim().Answer(WindowsHealthModule.WmiProbeQuery);

        var broken = Get(await Detect(cim: failing), "M02.wmi");
        var empty = Get(await Detect(cim: silent), "M02.wmi");

        Assert.Equal(FindingStatus.Warning, broken.Status);
        Assert.True(broken.Fixable);
        Assert.Contains("V0.2", broken.Advice, StringComparison.Ordinal);
        Assert.Equal(FindingStatus.Warning, empty.Status);
    }

    [Fact]
    public async Task Dirty_volume_is_a_fixable_warning()
    {
        var cim = HealthyCim().Answer(
            WindowsHealthModule.VolumeQuery("C:"),
            new Dictionary<string, object?> { ["DriveLetter"] = "C:", ["DirtyBitSet"] = true });

        var finding = Get(await Detect(cim: cim), "M02.volume-dirty");

        Assert.Equal(FindingStatus.Warning, finding.Status);
        Assert.True(finding.Fixable);
        Assert.Contains("chkdsk", finding.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dirty_bit_hidden_without_rights_is_admin_required()
    {
        var cim = HealthyCim().Answer(
            WindowsHealthModule.VolumeQuery("C:"),
            new Dictionary<string, object?> { ["DriveLetter"] = "C:", ["DirtyBitSet"] = null });

        var notElevated = Get(await Detect(cim: cim, elevated: false), "M02.volume-dirty");
        var elevated = Get(await Detect(cim: cim), "M02.volume-dirty");

        Assert.Equal(FindingStatus.Unknown, notElevated.Status);
        Assert.Contains("administrateur", notElevated.Explanation, StringComparison.Ordinal);
        Assert.Equal(FindingStatus.Unknown, elevated.Status);
        Assert.DoesNotContain("administrateur", elevated.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Minidumps_are_counted_with_the_newest_date()
    {
        var files = HealthyFiles()
            .AddFile(@"C:\Windows\Minidump\090826-7531-01.dmp")
            .AddFile(@"C:\Windows\Minidump\012526-5000-01.dmp")
            .AddFile(@"C:\Windows\Minidump\notes.txt");

        var finding = Get(await Detect(files: files), "M02.minidumps");

        Assert.Equal(FindingStatus.Info, finding.Status);
        Assert.Equal("2 fichiers (le plus récent du 08/09/2026)", finding.Current);
        Assert.Contains("WinDbg", finding.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Minidump_folder_access_denied_is_admin_required()
    {
        var finding = Get(await DetectWith(new FakeEventLogs(), new DeniedFiles()), "M02.minidumps");

        Assert.Equal(FindingStatus.Unknown, finding.Status);
    }

    [Fact]
    public void Module_metadata_follows_the_spec()
    {
        var module = new WindowsHealthModule();

        Assert.Equal("M02", module.Id);
        Assert.Equal("Réparation de Windows", module.Title);
        Assert.Equal(20, module.Order);
        Assert.True(((IAuditModule)module).Timeout >= TimeSpan.FromSeconds(60));
    }

    [Theory]
    [InlineData("59", 0x3B)]
    [InlineData("0", 0)]
    [InlineData("0x0000003b (0x00000000c0000005, 0xfffff801bdc0684b)", 0x3B)]
    [InlineData("0x00000124", 0x124)]
    [InlineData("  292  ", 0x124)]
    public void Bugcheck_codes_are_parsed_in_decimal_and_hexadecimal(string text, long expected)
    {
        Assert.Equal(expected, HealthParsers.ParseBugcheckCode(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0xZZ")]
    public void Invalid_bugcheck_codes_are_null(string? text)
    {
        Assert.Null(HealthParsers.ParseBugcheckCode(text));
    }

    [Fact]
    public void Bugcheck_descriptions_and_hints()
    {
        Assert.Equal("0x0000003B (SYSTEM_SERVICE_EXCEPTION)", HealthParsers.DescribeBugcheck(0x3B));
        Assert.Equal("0x0000BEEF", HealthParsers.DescribeBugcheck(0xBEEF));
        Assert.Contains("Module 15", HealthParsers.BugcheckHint(0x124), StringComparison.Ordinal);
        Assert.Null(HealthParsers.BugcheckHint(0xBEEF));
        Assert.True(HealthParsers.IsNonZero("134335132494732934"));
        Assert.False(HealthParsers.IsNonZero("0"));
        Assert.False(HealthParsers.IsNonZero(null));
    }

    [Theory]
    [InlineData(@"C:\Windows\Minidump\090826-7531-01.dmp", 2026, 9, 8)]
    [InlineData("123125-15625-01.DMP", 2025, 12, 31)]
    public void Minidump_names_give_the_crash_date(string path, int year, int month, int day)
    {
        Assert.Equal(new DateOnly(year, month, day), HealthParsers.ParseMinidumpDate(path));
    }

    [Theory]
    [InlineData("MEMORY.DMP")]
    [InlineData("133126-1-01.dmp")]
    [InlineData("090826-7531-01.txt")]
    public void Unusual_minidump_names_have_no_date(string path)
    {
        Assert.Null(HealthParsers.ParseMinidumpDate(path));
    }

    [Fact]
    public void Cim_numbers_are_converted_to_double()
    {
        Assert.Equal(5.5, HealthParsers.ToDouble(5.5));
        Assert.Equal(7.0, HealthParsers.ToDouble(7));
        Assert.Equal(3.25, HealthParsers.ToDouble(3.25f));
        Assert.Equal(6.5, HealthParsers.ToDouble("6.5"));
        Assert.Null(HealthParsers.ToDouble("n/a"));
        Assert.Null(HealthParsers.ToDouble(null));
    }

    private sealed class ThrowingEventLogs : IEventLogReader
    {
        public IReadOnlyList<EventRecordInfo> Query(string logName, string? provider, IReadOnlyCollection<int> eventIds, DateTime since, int maxEvents = 200, bool includeMessage = false) =>
            throw new DataSourceUnavailableException($"Journal absent : {logName}");
    }

    private sealed class DeniedFiles : IFileSystemReader
    {
        public bool FileExists(string path) => false;

        public bool DirectoryExists(string path) => true;

        public string ReadAllText(string path) => throw new MausAccessDeniedException("refusé");

        public IReadOnlyList<string> EnumerateFiles(string directory, string searchPattern = "*") => throw new MausAccessDeniedException("refusé");

        public (string? Company, string? Product, string? Version) GetVersionInfo(string path) => (null, null, null);

        public (long FreeBytes, long TotalBytes)? GetDriveSpace(string root) => (100 * GiB, 500 * GiB);
    }
}
