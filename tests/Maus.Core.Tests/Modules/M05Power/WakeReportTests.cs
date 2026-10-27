using Maus.Core.Modules.M05Power;
using Maus.Core.Platform;
using Xunit;

namespace Maus.Core.Tests.Modules.M05Power;

public sealed class WakeReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Wakes_devices_and_tasks_are_listed_as_windows_writes_them_without_interpreting_the_numeric_type()
    {
        var logs = new FakeEventLogs()
            .Add("System", WakeReport.Provider, 1, new DateTime(2026, 10, 26, 3, 12, 0),
                new Dictionary<string, string> { ["WakeTime"] = "2026-10-26T03:11:58.0000000Z", ["WakeSourceType"] = "5", ["WakeSourceText"] = "", ["WakeTimerOwner"] = @"\Device\HarddiskVolume3\Windows\System32\svchost.exe" })
            .Add("System", WakeReport.Provider, 1, new DateTime(2026, 10, 25, 22, 0, 0),
                new Dictionary<string, string> { ["WakeTime"] = "2026-10-25T21:01:40Z", ["WakeSourceType"] = "1", ["WakeSourceText"] = "Souris HID" })
            .Add("System", WakeReport.Provider, 1, new DateTime(2026, 10, 24, 9, 0, 0),
                new Dictionary<string, string> { ["WakeSourceType"] = "1", ["WakeSourceText"] = "" });
        var settingsWake = new CimRow(new Dictionary<string, object?> { ["WakeToRun"] = true });
        var settingsNoWake = new CimRow(new Dictionary<string, object?> { ["WakeToRun"] = false });
        var cim = new FakeCim()
            .Answer(WakeReport.DeviceWakeQuery, CimScopes.Wmi,
                new Dictionary<string, object?> { ["InstanceName"] = @"HID\VID_046D&PID_C548&MI_00\7&C97164A&0&0000_0", ["Enable"] = true },
                new Dictionary<string, object?> { ["InstanceName"] = @"PCI\VEN_8086&DEV_15BC\3&11583659&0&FE_0", ["Enable"] = true },
                new Dictionary<string, object?> { ["InstanceName"] = @"USB\VID_1234\5&1_0", ["Enable"] = false })
            .Answer(WakeReport.DevicesQuery, CimScopes.Default,
                new Dictionary<string, object?> { ["DeviceID"] = @"HID\VID_046D&PID_C548&MI_00\7&c97164a&0&0000", ["Name"] = "Souris HID" })
            .Answer(WakeReport.TasksQuery, CimScopes.TaskScheduler,
                new Dictionary<string, object?> { ["TaskName"] = "Reboot", ["TaskPath"] = @"\Microsoft\Windows\UpdateOrchestrator\", ["State"] = 3L, ["Settings"] = settingsWake },
                new Dictionary<string, object?> { ["TaskName"] = "Coupé", ["TaskPath"] = @"\", ["State"] = 1L, ["Settings"] = settingsWake },
                new Dictionary<string, object?> { ["TaskName"] = "Normale", ["TaskPath"] = @"\", ["State"] = 3L, ["Settings"] = settingsNoWake });

        var finding = WakeReport.Detect(TestContext.Create(cim: cim, eventLogs: logs, now: Now), "Démarrage et veille");

        Assert.Equal(FindingStatus.Info, finding.Status);
        Assert.Equal("3 réveil(s) ces 30 derniers jours", finding.Current);
        Assert.Contains(@"(minuteur de \Device\HarddiskVolume3\Windows\System32\svchost.exe)", finding.Explanation, StringComparison.Ordinal);
        Assert.Contains("(Souris HID)", finding.Explanation, StringComparison.Ordinal);
        Assert.Contains("(cause non précisée par Windows)", finding.Explanation, StringComparison.Ordinal);
        Assert.Contains(@"Appareils autorisés à réveiller le PC : PCI\VEN_8086&DEV_15BC\3&11583659&0&FE, Souris HID.", finding.Explanation, StringComparison.Ordinal);
        Assert.Contains(@"réveiller le PC : Microsoft\Windows\UpdateOrchestrator\Reboot.", finding.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("USB", finding.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Unreadable_sources_are_said_so_and_a_protected_log_asks_for_administrator()
    {
        var cim = new FakeCim()
            .Throw(WakeReport.DeviceWakeQuery, new MausAccessDeniedException("refusé"), CimScopes.Wmi)
            .Throw(WakeReport.TasksQuery, new DataSourceUnavailableException("absent"), CimScopes.TaskScheduler);

        var limited = WakeReport.Detect(TestContext.Create(cim: cim, now: Now), "c");
        var denied = WakeReport.Detect(TestContext.Create(eventLogs: new FakeEventLogs().Deny("System"), now: Now), "c");

        Assert.Equal("0 réveil(s) ces 30 derniers jours", limited.Current);
        Assert.Contains("illisible (droits administrateur souvent nécessaires)", limited.Explanation, StringComparison.Ordinal);
        Assert.Contains("Liste des tâches planifiées : illisible.", limited.Explanation, StringComparison.Ordinal);
        Assert.Equal(FindingStatus.Unknown, denied.Status);
    }
}
