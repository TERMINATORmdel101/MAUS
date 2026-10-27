using Maus.Core.Modules.M12Startup;
using Microsoft.Win32;

namespace Maus.Core.Tests.Modules.M12Startup;

public class StartupFixTests
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const string SteamExe = @"C:\Program Files (x86)\Steam\steam.exe";
    private const string DiscordExe = @"C:\Users\x\AppData\Local\Discord\app-1.0\Discord.exe";

    [Fact]
    public async Task Entries_are_disabled_like_task_manager_and_restored()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.CurrentUser, Run, "Steam", $"\"{SteamExe}\" -silent")
            .Set(RegistryHive.CurrentUser, Run, "Discord", $"\"{DiscordExe}\" --start-minimized");
        var files = new FakeFiles()
            .AddFile(SteamExe).SetVersionInfo(SteamExe, "Valve Corporation", "Steam")
            .AddFile(DiscordExe).SetVersionInfo(DiscordExe, "Discord Inc.", "Discord");
        var audit = TestContext.Create(registry, files: files);

        var (plan, applied) = await Fixes.RoundTrip.AssertAsync(new StartupAppsModule(new FakeStartupEnvironment()), audit, registry);

        var steam = plan.Single(c => c.Id == "M12.hkcu-run-steam");
        Assert.True(steam.Recommended);
        Assert.False(plan.Single(c => c.Id == "M12.hkcu-run-discord").Recommended);
        var write = steam.Writes.Single();
        Assert.Equal($@"HKCU\{Approved}\Run\Steam", write.Key.Describe());
        var bytes = (byte[])write.Value!.ToRegistryObject();
        Assert.Equal(12, bytes.Length);
        Assert.Equal(0x03, bytes[0]);

        // La valeur Run n'est jamais touchée.
        Assert.DoesNotContain(registry.Writes, w => w.Contains(@"\Run\\Steam", StringComparison.Ordinal) && !w.Contains("StartupApproved", StringComparison.Ordinal));
        Assert.Equal($"\"{SteamExe}\" -silent", registry.GetValue(RegistryHive.CurrentUser, Run, "Steam"));
        Assert.Null(registry.GetValue(RegistryHive.CurrentUser, $@"{Approved}\Run", "Steam"));
        Assert.Equal(2, applied.AppliedCount);
    }

    [Fact]
    public async Task Third_party_logon_tasks_are_disabled_one_by_one_never_windows_ones_and_reenabled_on_revert()
    {
        static Dictionary<string, object?> Task(string path, string name, string exe) => new()
        {
            ["TaskPath"] = path,
            ["TaskName"] = name,
            ["State"] = 3u,
            ["Triggers"] = new[] { new Maus.Core.Platform.CimRow(new Dictionary<string, object?> { ["Enabled"] = true, ["UserId"] = "Bob" }) },
            ["Actions"] = new[] { new Maus.Core.Platform.CimRow(new Dictionary<string, object?> { ["Execute"] = exe, ["Arguments"] = null }) },
        };

        var afterburner = Task(@"\", "MSIAfterburner", @"C:\Program Files (x86)\MSI Afterburner\MSIAfterburner.exe");
        var vendor = Task(@"\Vendor\", "Tray Helper", @"C:\Vendor\tray.exe");
        var windows = Task(@"\Microsoft\Windows\Shell\", "CreateObjectTask", "explorer.exe");
        var cim = new FakeCim().Answer(StartupAppsModule.TasksQuery, StartupAppsModule.TaskSchedulerScope, afterburner, vendor, windows);
        var tasks = new FakeScheduledTasks(afterburner, vendor, windows);
        var registry = new FakeRegistry();
        var audit = TestContext.Create(registry, cim: cim);

        var (plan, _) = await Fixes.RoundTrip.AssertAsync(new StartupAppsModule(new FakeStartupEnvironment()), audit, registry, tasks: tasks);

        Assert.Equal(["M12.logon-task-msiafterburner", "M12.logon-task-vendor-tray-helper"], plan.Select(c => c.Id).Order(StringComparer.Ordinal));
        Assert.All(plan, c => Assert.False(c.Recommended));
        Assert.All(plan, c => Assert.Equal("M12.logon-tasks", c.FindingId));
        Assert.DoesNotContain(tasks.Calls, c => c.Path.StartsWith(@"\Microsoft\", StringComparison.Ordinal));
        Assert.Contains((@"\Vendor\", "Tray Helper", false), tasks.Calls);
        Assert.Contains((@"\Vendor\", "Tray Helper", true), tasks.Calls);
        Assert.Equal(@"Tâche planifiée \Vendor\Tray Helper", plan.Single(c => c.Id.EndsWith("tray-helper", StringComparison.Ordinal)).Writes.Single().Key.Describe());
    }

    [Fact]
    public void Task_names_are_escaped_for_wql()
    {
        Assert.Equal(@"\\Vendor\\", Maus.Core.Fixes.WindowsScheduledTaskAccessor.Escape(@"\Vendor\"));
        Assert.Equal(@"l\'outil", Maus.Core.Fixes.WindowsScheduledTaskAccessor.Escape("l'outil"));
        Assert.Equal(@"\Vendor\Tray", Maus.Core.Fixes.WindowsScheduledTaskAccessor.FullName(@"\Vendor\", "Tray"));
        Assert.Equal(@"\Tray", Maus.Core.Fixes.WindowsScheduledTaskAccessor.FullName(@"\", "Tray"));
    }
}
