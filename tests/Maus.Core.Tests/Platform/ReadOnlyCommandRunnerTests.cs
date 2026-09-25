using Maus.Core.Platform;

namespace Maus.Core.Tests.Platform;

public class ReadOnlyCommandRunnerTests
{
    [Theory]
    [InlineData("powercfg", new[] { "/getactivescheme" })]
    [InlineData("powercfg.exe", new[] { "/a" })]
    [InlineData("bcdedit", new[] { "/enum", "{current}" })]
    [InlineData("fsutil", new[] { "behavior", "query", "DisableDeleteNotify" })]
    [InlineData("netsh", new[] { "winhttp", "show", "proxy" })]
    [InlineData("nvidia-smi", new[] { "--query-gpu=pcie.link.gen.current", "--format=csv" })]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.24_x64__8wekyb3d8bbwe\winget.exe", new[] { "upgrade", "--source", "winget", "--disable-interactivity" })]
    public void Allows_read_only_commands(string exe, string[] args) =>
        Assert.True(ReadOnlyCommandRunner.IsAllowed(exe, args));

    [Theory]
    [InlineData("powercfg", new[] { "/setactive", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" })]
    [InlineData("powercfg", new[] { "/attributes", "SUB_PROCESSOR", "x", "-ATTRIB_HIDE" })]
    [InlineData("powercfg", new[] { "/duplicatescheme", "e9a42b02-d5df-448d-aa00-03f14749eb61" })]
    [InlineData("bcdedit", new[] { "/set", "{current}", "nx", "OptIn" })]
    [InlineData("fsutil", new[] { "behavior", "set", "disabledeletenotify", "0" })]
    [InlineData("netsh", new[] { "winhttp", "reset", "proxy" })]
    [InlineData("reg", new[] { "add", "HKLM\\x" })]
    [InlineData("cmd", new[] { "/c", "del" })]
    [InlineData("powercfg", new string[0])]
    [InlineData("winget", new[] { "upgrade", "--all" })]
    [InlineData("winget", new[] { "upgrade", "--id", "Mozilla.Firefox" })]
    [InlineData("winget", new[] { "upgrade", "Mozilla.Firefox" })]
    [InlineData("winget", new[] { "install", "--id", "x" })]
    [InlineData("winget", new[] { "upgrade", "--source", "winget", "--accept-source-agreements" })]
    public void Refuses_anything_that_could_write(string exe, string[] args) =>
        Assert.False(ReadOnlyCommandRunner.IsAllowed(exe, args));

    [Fact]
    public async Task RunAsync_throws_before_starting_a_refused_command()
    {
        var runner = new ReadOnlyCommandRunner();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync("powercfg", ["/setactive", "x"], TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.24_x64__8wekyb3d8bbwe\winget.exe", true)]
    [InlineData(@"c:\program files\windowsapps\x\winget.exe", true)]
    [InlineData(@"C:\Users\Alex\AppData\Local\Microsoft\WindowsApps\winget.exe", false)]
    [InlineData(@"C:\Program Files\WindowsApps\..\Evil\winget.exe", false)]
    [InlineData(@"C:\Program Files\WindowsAppsX\winget.exe", false)]
    public void Winget_runs_only_from_the_protected_apps_folder(string path, bool expected) =>
        Assert.Equal(expected, ReadOnlyCommandRunner.IsInProtectedAppsFolder(path, @"C:\Program Files"));

    [Fact]
    public async Task Winget_from_the_user_profile_is_refused_before_starting()
    {
        var runner = new ReadOnlyCommandRunner();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(@"C:\Users\Alex\AppData\Local\Microsoft\WindowsApps\winget.exe", ["upgrade"], TimeSpan.FromSeconds(5), CancellationToken.None));
    }
}
