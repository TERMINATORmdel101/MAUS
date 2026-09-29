using Maus.Core.Platform;
using Maus.Core.Workshop;

namespace Maus.Core.Tests.Workshop;

public class DriverTests
{
    private const string NvidiaId = @"PCI\VEN_10DE&DEV_1E07&SUBSYS_37111462&REV_A1\4&2C8A8F4E&0&0008";

    private sealed class FakeDates(Dictionary<string, DateTime> dates) : IDriverDateSource
    {
        public DateTime? DriverDate(string instanceId) => dates.TryGetValue(instanceId, out var date) ? date : null;
    }

    private static DriverEntry Gpu(string? inf = "oem42.inf", string id = NvidiaId) =>
        new("NVIDIA GeForce RTX 2080 Ti", "DISPLAY", DriverGroup.Graphics, id, "32.0.16.1714", new DateTime(2026, 9, 17), "NVIDIA", "NVIDIA", inf, true, "Microsoft Windows Hardware Compatibility Publisher");

    [Fact]
    public void Drivers_are_read_grouped_and_sorted_without_duplicates()
    {
        var cim = new FakeCim().Answer(DriverInventoryReader.DriverQuery,
            new Dictionary<string, object?> { ["DeviceName"] = "Realtek High Definition Audio", ["DeviceClass"] = "MEDIA", ["DeviceID"] = @"HDAUDIO\FUNC_01&VEN_10EC\4&1", ["DriverVersion"] = "6.0.9", ["InfName"] = "oem7.inf", ["IsSigned"] = true },
            new Dictionary<string, object?> { ["DeviceName"] = "NVIDIA GeForce RTX 2080 Ti", ["DeviceClass"] = "DISPLAY", ["DeviceID"] = NvidiaId, ["DriverVersion"] = "32.0.16.1714", ["DriverProviderName"] = "NVIDIA", ["InfName"] = "oem42.inf", ["IsSigned"] = true },
            new Dictionary<string, object?> { ["DeviceName"] = "NVIDIA GeForce RTX 2080 Ti", ["DeviceClass"] = "DISPLAY", ["DeviceID"] = NvidiaId.ToLowerInvariant() },
            new Dictionary<string, object?> { ["DeviceName"] = "Pont PCI standard", ["DeviceClass"] = "SYSTEM", ["DeviceID"] = @"PCI\VEN_8086&DEV_A343\3&11583659&0&E0", ["InfName"] = "machine.inf" },
            new Dictionary<string, object?> { ["DeviceName"] = null, ["DeviceClass"] = "SYSTEM", ["DeviceID"] = @"ROOT\X\0000" });

        var drivers = DriverInventoryReader.Read(cim, new FakeDates(new Dictionary<string, DateTime> { [NvidiaId] = new DateTime(2026, 9, 17) }));

        Assert.Equal(["NVIDIA GeForce RTX 2080 Ti", "Realtek High Definition Audio", "Pont PCI standard"], drivers.Select(d => d.Device));
        Assert.Equal([DriverGroup.Graphics, DriverGroup.Audio, DriverGroup.Chipset], drivers.Select(d => d.Group));
        Assert.True(drivers[0].IsThirdPartyPackage);
        Assert.False(drivers[2].IsThirdPartyPackage);
        Assert.Equal(new DateTime(2026, 9, 17), drivers[0].Date);
        Assert.Null(drivers[1].Date);
    }

    [Fact]
    public void Unreadable_driver_list_is_empty_not_an_error()
    {
        var cim = new FakeCim().Throw(DriverInventoryReader.DriverQuery, new DataSourceUnavailableException("WMI"));

        Assert.Empty(DriverInventoryReader.Read(cim));
    }

    [Fact]
    public void Restart_uses_pnputil_on_the_exact_device_in_a_visible_console()
    {
        var arguments = GraphicsDriverActions.ConsoleArguments(GraphicsDriverAction.Restart, Gpu(), @"C:\ProgramData\MAUS\pilotes\x", @"C:\Windows\System32\pnputil.exe")!;

        Assert.StartsWith("/s /k \"title MAUS & echo ", arguments, StringComparison.Ordinal);
        Assert.Contains($"\"C:\\Windows\\System32\\pnputil.exe\" /restart-device \"{NvidiaId}\"", arguments, StringComparison.Ordinal);
        Assert.EndsWith("\"", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("/delete-driver", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Removal_only_happens_after_a_successful_backup()
    {
        var arguments = GraphicsDriverActions.ConsoleArguments(GraphicsDriverAction.Remove, Gpu(), @"C:\ProgramData\MAUS\pilotes\20260929-101500-oem42", "pnputil.exe")!;

        Assert.Contains("\"pnputil.exe\" /export-driver oem42.inf \"C:\\ProgramData\\MAUS\\pilotes\\20260929-101500-oem42\" && \"pnputil.exe\" /delete-driver oem42.inf /uninstall", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("/force", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Reinstall_removes_then_rescans()
    {
        var arguments = GraphicsDriverActions.ConsoleArguments(GraphicsDriverAction.Reinstall, Gpu(), @"C:\x", "pnputil.exe")!;

        Assert.True(arguments.IndexOf("/remove-device", StringComparison.Ordinal) < arguments.IndexOf("/scan-devices", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("PCI\\VEN_10DE\" & del C:\\x")]
    [InlineData("PCI\\VEN_10DE%PATH%")]
    [InlineData("PCI\\VEN_10DE!x!")]
    [InlineData("PCI\\VEN 10DE")]
    [InlineData("")]
    public void Suspicious_device_ids_are_refused(string id)
    {
        Assert.False(GraphicsDriverActions.IsSafeInstanceId(id));
        Assert.Null(GraphicsDriverActions.ConsoleArguments(GraphicsDriverAction.Restart, Gpu(id: id), @"C:\x"));
    }

    [Fact]
    public void Windows_drivers_and_other_devices_are_never_backed_up_or_removed()
    {
        Assert.Null(GraphicsDriverActions.ConsoleArguments(GraphicsDriverAction.Remove, Gpu(inf: "display.inf"), @"C:\x"));
        Assert.Null(GraphicsDriverActions.ConsoleArguments(GraphicsDriverAction.Backup, Gpu(inf: null), @"C:\x"));
        Assert.Null(GraphicsDriverActions.ConsoleArguments(GraphicsDriverAction.Remove, Gpu(inf: "oem42.inf & calc"), @"C:\x"));
        Assert.NotNull(GraphicsDriverActions.ConsoleArguments(GraphicsDriverAction.Restart, Gpu(inf: "display.inf"), @"C:\x"));

        var audio = Gpu() with { Group = DriverGroup.Audio };
        Assert.Null(GraphicsDriverActions.ConsoleArguments(GraphicsDriverAction.Restart, audio, @"C:\x"));
        Assert.Null(GraphicsDriverActions.ConsoleArguments(GraphicsDriverAction.Backup, Gpu(), "C:\\x\" & calc"));
    }

    [Fact]
    public void Backup_folder_is_dated_and_restore_installs_every_inf_inside()
    {
        var folder = GraphicsDriverActions.BackupFolder("OEM42.INF", new DateTimeOffset(2026, 9, 29, 10, 15, 0, TimeSpan.FromHours(2)));
        Assert.EndsWith(Path.Combine("MAUS", "pilotes", "20260929-101500-oem42"), folder, StringComparison.Ordinal);

        var restore = GraphicsDriverActions.RestoreArguments(@"C:\ProgramData\MAUS\pilotes\20260929-101500-oem42", "pnputil.exe")!;
        Assert.Contains("\"pnputil.exe\" /add-driver \"C:\\ProgramData\\MAUS\\pilotes\\20260929-101500-oem42", restore, StringComparison.Ordinal);
        Assert.Contains("*.inf\" /subdirs /install", restore, StringComparison.Ordinal);
        Assert.Null(GraphicsDriverActions.RestoreArguments("C:\\x%TEMP%"));
    }
}
