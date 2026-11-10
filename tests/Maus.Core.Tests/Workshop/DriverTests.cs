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

    private static Func<IReadOnlyList<string>, Task<int>> Pnputil(List<string> calls, int export = 0, int other = 0) => arguments =>
    {
        calls.Add(string.Join(' ', arguments));
        return Task.FromResult(arguments[0] == "/export-driver" ? export : other);
    };

    [Fact]
    public async Task Restart_uses_pnputil_on_the_exact_device()
    {
        var calls = new List<string>();

        var message = await GraphicsDriverActions.RunAsync(GraphicsDriverAction.Restart, Gpu(), @"C:\ProgramData\MAUS\pilotes\x", Pnputil(calls), _ => { });

        Assert.Equal($"/restart-device {NvidiaId}", Assert.Single(calls));
        Assert.Equal("Pilote redémarré.", message);
    }

    [Fact]
    public async Task Removal_only_happens_after_a_successful_backup()
    {
        const string folder = @"C:\ProgramData\MAUS\pilotes\20260929-101500-oem42";
        var calls = new List<string>();

        await GraphicsDriverActions.RunAsync(GraphicsDriverAction.Remove, Gpu(), folder, Pnputil(calls), _ => { });

        Assert.Equal([$"/export-driver oem42.inf {folder}", "/delete-driver oem42.inf /uninstall"], calls);

        calls.Clear();
        var refused = await GraphicsDriverActions.RunAsync(GraphicsDriverAction.Remove, Gpu(), folder, Pnputil(calls, export: 1), _ => { });

        Assert.Single(calls);
        Assert.Contains("n'a pas été supprimé", refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reinstall_removes_then_rescans()
    {
        var calls = new List<string>();

        await GraphicsDriverActions.RunAsync(GraphicsDriverAction.Reinstall, Gpu(), @"C:\x", Pnputil(calls), _ => { });

        Assert.Equal([$"/remove-device {NvidiaId}", "/scan-devices"], calls);

        // Un adaptateur virtuel (ROOT\, SWD\) ne serait pas recréé par /scan-devices : pas de retrait.
        var parsec = Gpu(id: @"ROOT\DISPLAY\0000");
        Assert.False(GraphicsDriverActions.CanReinstall(parsec));
        Assert.False(GraphicsDriverActions.CanRun(GraphicsDriverAction.Reinstall, parsec, @"C:\x"));
        Assert.True(GraphicsDriverActions.CanRun(GraphicsDriverAction.Restart, parsec, @"C:\x"));
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
        Assert.False(GraphicsDriverActions.CanRun(GraphicsDriverAction.Restart, Gpu(id: id), @"C:\x"));
    }

    [Fact]
    public void Windows_drivers_and_other_devices_are_never_backed_up_or_removed()
    {
        Assert.False(GraphicsDriverActions.CanRun(GraphicsDriverAction.Remove, Gpu(inf: "display.inf"), @"C:\x"));
        Assert.False(GraphicsDriverActions.CanRun(GraphicsDriverAction.Backup, Gpu(inf: null), @"C:\x"));
        Assert.False(GraphicsDriverActions.CanRun(GraphicsDriverAction.Remove, Gpu(inf: "oem42.inf & calc"), @"C:\x"));
        Assert.True(GraphicsDriverActions.CanRun(GraphicsDriverAction.Restart, Gpu(inf: "display.inf"), @"C:\x"));

        var audio = Gpu() with { Group = DriverGroup.Audio };
        Assert.False(GraphicsDriverActions.CanRun(GraphicsDriverAction.Restart, audio, @"C:\x"));
        Assert.False(GraphicsDriverActions.CanRun(GraphicsDriverAction.Backup, Gpu(), "C:\\x\" & calc"));
    }

    [Fact]
    public async Task Backup_folder_is_dated_and_restore_installs_every_inf_inside()
    {
        var folder = GraphicsDriverActions.BackupFolder("OEM42.INF", new DateTimeOffset(2026, 9, 29, 10, 15, 0, TimeSpan.FromHours(2)));
        Assert.EndsWith(Path.Combine("MAUS", "pilotes", "20260929-101500-oem42"), folder, StringComparison.Ordinal);

        var calls = new List<string>();
        await GraphicsDriverActions.RestoreAsync(@"C:\ProgramData\MAUS\pilotes\20260929-101500-oem42", Pnputil(calls), _ => { });
        Assert.Equal(@"/add-driver C:\ProgramData\MAUS\pilotes\20260929-101500-oem42\*.inf /subdirs /install", Assert.Single(calls));
        Assert.False(GraphicsDriverActions.CanRestore("C:\\x%TEMP%"));
    }

    [Fact]
    public void Age_is_shown_without_threshold_and_the_windows_2006_date_is_not_called_old()
    {
        var today = new DateTime(2026, 9, 29);
        var inbox = Gpu() with { Provider = "Microsoft", Date = new DateTime(2006, 6, 21), IsSigned = true };
        var vendor = Gpu() with { Provider = "Realtek Semiconductor Corp.", Date = new DateTime(2023, 3, 14) };
        var recent = Gpu() with { Date = new DateTime(2026, 9, 17) };

        Assert.Equal("date de convention de Windows", DriverAge.Describe(inbox, today));
        Assert.Equal("3 ans", DriverAge.Describe(vendor, today));
        Assert.Equal("moins d'un mois", DriverAge.Describe(recent, today));
        Assert.Equal("8 mois", DriverAge.Describe(Gpu() with { Date = new DateTime(2026, 1, 29) }, today));
        Assert.Equal("1 mois", DriverAge.Describe(Gpu() with { Date = new DateTime(2026, 8, 20) }, today));

        // Date de fondation d'Intel donnée à son « Chipset Device Software » : convention, pas un pilote de 58 ans.
        var chipset = Gpu() with { Provider = "INTEL", Manufacturer = "INTEL", Date = new DateTime(1968, 7, 18) };
        Assert.Equal("date de convention d'Intel", DriverAge.Describe(chipset, today));
        var undated = Gpu() with { Date = DriverAge.NoRealDate };
        Assert.Equal("pas de vraie date", DriverAge.Describe(undated, today));
        Assert.Same(vendor, DriverAge.OldestVendorDriver([undated, vendor]));
        Assert.Same(vendor, DriverAge.OldestVendorDriver([chipset, vendor, recent]));
        Assert.Equal("—", DriverAge.Describe(Gpu() with { Date = null }, today));

        // Le plus ancien pilote de fabricant ignore les pilotes de Microsoft (datés par convention).
        Assert.Same(vendor, DriverAge.OldestVendorDriver([inbox, vendor, recent]));
        Assert.Equal(1, DriverAge.UnsignedCount([inbox, vendor with { IsSigned = false }, recent]));
    }

    /// <summary>
    /// Exécute vraiment la commande dans cmd.exe, avec un faux pnputil (fichier .cmd) qui renvoie le code choisi :
    /// vérifie les branches « réussi », « redémarrage nécessaire », « échec », « sauvegarde ratée » et « pilote plus récent ».
    /// </summary>
    [Theory]
    [InlineData("Restart", 0, 0, "Pilote redémarré.")]
    [InlineData("Restart", 0, 3010, "redémarrage du PC")]
    [InlineData("Restart", 0, 5, "a échoué")]
    [InlineData("Remove", 0, 0, "Pilote sauvegardé puis supprimé.")]
    [InlineData("Remove", 0, 3010, "redémarrez le PC")]
    [InlineData("Remove", 0, 259, "la suppression a échoué")]
    [InlineData("Remove", 1, 0, "n'a pas été supprimé")]
    public async Task Pnputil_exit_codes_give_the_result(string action, int export, int code, string expected)
    {
        var message = await GraphicsDriverActions.RunAsync(Enum.Parse<GraphicsDriverAction>(action), Gpu(), @"C:\x", Pnputil([], export, code), _ => { });

        Assert.Contains(expected, message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "Pilote réinstallé.")]
    [InlineData(3010, "redémarrez le PC")]
    [InlineData(259, "Rien n'a été remplacé")]
    [InlineData(5, "a échoué")]
    public async Task Restore_exit_codes_give_the_result(int code, string expected) =>
        Assert.Contains(expected, await GraphicsDriverActions.RestoreAsync(@"C:\x", Pnputil([], other: code), _ => { }), StringComparison.Ordinal);

    [Fact]
    public async Task An_action_outside_the_list_runs_nothing()
    {
        const GraphicsDriverAction unknown = (GraphicsDriverAction)99;
        var calls = 0;

        Assert.False(GraphicsDriverActions.CanRun(unknown, Gpu(), @"C:\x"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => GraphicsDriverActions.RunAsync(unknown, Gpu(), @"C:\x", _ =>
        {
            calls++;
            return Task.FromResult(0);
        }, _ => { }));

        Assert.Equal(0, calls);
    }

    [Fact]
    public void An_instance_id_with_a_final_line_break_is_refused() =>
        Assert.False(GraphicsDriverActions.IsSafeInstanceId("PCI\\VEN_10DE\n"));
}
