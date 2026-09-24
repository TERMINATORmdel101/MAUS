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
    public void Refuses_anything_that_could_write(string exe, string[] args) =>
        Assert.False(ReadOnlyCommandRunner.IsAllowed(exe, args));

    [Fact]
    public async Task RunAsync_throws_before_starting_a_refused_command()
    {
        var runner = new ReadOnlyCommandRunner();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync("powercfg", ["/setactive", "x"], TimeSpan.FromSeconds(5), CancellationToken.None));
    }
}
