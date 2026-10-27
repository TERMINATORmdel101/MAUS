using Maus.Core.Modules.M02Repair;
using Maus.Core.Platform;

namespace Maus.Core.Tests.Modules.M02Repair;

/// <summary>Paquet du Microsoft Store : l'état de Windows se lit par dism.exe /CheckHealth (lecture seule).</summary>
public sealed class DismCommandTests
{
    [Theory]
    [InlineData("No component store corruption detected.\r\nThe operation completed successfully.", ImageHealth.Healthy)]
    [InlineData("The component store is repairable.\r\nThe operation completed successfully.", ImageHealth.Repairable)]
    [InlineData("The component store cannot be repaired.", ImageHealth.NotRepairable)]
    public void Reads_the_three_english_answers(string output, ImageHealth expected) =>
        Assert.Equal(expected, DismCommandImageHealthChecker.Parse(output));

    [Fact]
    public void Unknown_answer_is_never_taken_for_a_state() =>
        Assert.Null(DismCommandImageHealthChecker.Parse("Error: 87"));

    [Fact]
    public void Only_checkhealth_is_allowed()
    {
        Assert.True(ReadOnlyCommandRunner.IsAllowed("dism.exe", ["/English", "/Online", "/Cleanup-Image", "/CheckHealth"]));
        Assert.False(ReadOnlyCommandRunner.IsAllowed("dism.exe", ["/English", "/Online", "/Cleanup-Image", "/RestoreHealth"]));
        Assert.False(ReadOnlyCommandRunner.IsAllowed("dism.exe", ["/English", "/Online", "/Cleanup-Image", "/CheckHealth", "/Source:x"]));
        Assert.False(ReadOnlyCommandRunner.IsAllowed("dism.exe", ["/Online", "/Cleanup-Image", "/CheckHealth"]));
    }

    [Fact]
    public void Checker_uses_the_read_only_command()
    {
        var dism = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe");
        var commands = new FakeCommands().Answer(dism + " /English /Online /Cleanup-Image /CheckHealth", "No component store corruption detected.");
        Assert.Equal(ImageHealth.Healthy, new DismCommandImageHealthChecker(commands).Check(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Battery_report_is_only_written_where_maus_expects_it()
    {
        Assert.True(ReadOnlyCommandRunner.IsAllowed("powercfg.exe", ["/batteryreport", "/output", ReadOnlyCommandRunner.BatteryReportPath]));
        Assert.False(ReadOnlyCommandRunner.IsAllowed("powercfg.exe", ["/batteryreport", "/output", @"C:\Windows\System32\x.html"]));
        Assert.False(ReadOnlyCommandRunner.IsAllowed("powercfg.exe", ["/batteryreport"]));
        Assert.True(ReadOnlyCommandRunner.IsAllowed("powercfg.exe", ["/getactivescheme"]));
    }
}
