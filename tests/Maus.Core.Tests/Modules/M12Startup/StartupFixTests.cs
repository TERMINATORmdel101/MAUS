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
}
