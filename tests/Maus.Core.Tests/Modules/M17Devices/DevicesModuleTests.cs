using Maus.Core.Modules.M17Devices;
using Maus.Core.Platform;

namespace Maus.Core.Tests.Modules.M17Devices;

public class DevicesModuleTests
{
    private static Dictionary<string, object?> Device(string name, string pnpClass, long code, bool present = true) => new()
    {
        ["Name"] = name,
        ["PNPDeviceID"] = $@"PCI\VEN_{name.Length:X4}\{name}",
        ["PNPClass"] = pnpClass,
        ["ConfigManagerErrorCode"] = (uint)code,
        ["Present"] = present,
    };

    private static Task<IReadOnlyList<Finding>> Detect(FakeCim cim) =>
        new DevicesModule().DetectAsync(TestContext.Create(cim: cim), CancellationToken.None);

    [Fact]
    public async Task No_device_in_error_is_compliant()
    {
        var findings = await Detect(new FakeCim().Answer(DevicesModule.ProblemQuery));

        var summary = Assert.Single(findings);
        Assert.Equal(FindingStatus.Ok, summary.Status);
    }

    [Fact]
    public async Task A_device_without_driver_is_explained()
    {
        var findings = await Detect(new FakeCim().Answer(DevicesModule.ProblemQuery, Device("Lecteur de cartes", "SDHost", 28)));

        Assert.Equal(FindingStatus.Info, findings.Single(f => f.Id == "M17.devices").Status);
        var device = findings.Single(f => f.Id.StartsWith("M17.device.", StringComparison.Ordinal));
        Assert.Contains("code 28", device.Current, StringComparison.Ordinal);
        Assert.Contains("Windows Update", device.Advice, StringComparison.Ordinal);
        Assert.Equal("ms-settings:windowsupdate-optionalupdates", device.SettingsPage);
    }

    /// <summary>
    /// 0.8.7.1 : la fin de l'identifiant de Windows est le numéro de série d'un appareil USB, ou l'adresse d'un appareil Bluetooth ;
    /// l'identifiant du constat, repris dans « Demander de l'aide », le CSV et le JSON, ne la contient plus.
    /// </summary>
    [Theory]
    [InlineData(@"USB\VID_0781&PID_5581\4C530001234567890123", "4c530001234567890123", "M17.device.usb.")]
    [InlineData(@"BTHENUM\DEV_A1B2C3D4E5F6\7&1A2B3C4D&0&BLUETOOTHDEVICE_A1B2C3D4E5F6", "a1b2c3d4e5f6", "M17.device.bthenum.")]
    public async Task A_device_finding_id_never_carries_the_serial_number_or_address(string pnpId, string secret, string prefix)
    {
        var device = Device("Clé USB", "USB", 10);
        device["PNPDeviceID"] = pnpId;

        var findings = await Detect(new FakeCim().Answer(DevicesModule.ProblemQuery, device));

        var id = findings.Single(f => f.Id.StartsWith("M17.device.", StringComparison.Ordinal)).Id;
        Assert.StartsWith(prefix, id, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, id, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pnpId.Split('\\')[^1], id, StringComparison.OrdinalIgnoreCase);

        // Stable d'un audit à l'autre, et l'identifiant d'avant la 0.8.7.1 (gardé par une marque « voulu ») mène au même.
        Assert.Equal(id, DeviceFindingId.Of(pnpId));
        Assert.Equal(id, DeviceFindingId.Upgrade("M17.device." + FindingSlug.Of(pnpId, 60)));
        Assert.Equal(id, DeviceFindingId.Upgrade(id));
        Assert.Equal("M17.devices", DeviceFindingId.Upgrade("M17.devices"));
        Assert.Equal("M05.fast-startup", DeviceFindingId.Upgrade("M05.fast-startup"));
    }

    [Fact]
    public void Two_devices_with_the_same_fingerprint_keep_two_findings() =>
        Assert.Equal([DeviceFindingId.Of("a"), DeviceFindingId.Of("a") + ".2", DeviceFindingId.Of("b")], DeviceFindingId.ForEach(["a", "a", "b"]));

    [Fact]
    public async Task The_name_of_a_device_is_marked_as_read_on_the_pc()
    {
        var findings = await Detect(new FakeCim().Answer(DevicesModule.ProblemQuery, Device("Lecteur de cartes", "SDHost", 28)));

        Assert.True(findings.Single(f => f.Id.StartsWith("M17.device.", StringComparison.Ordinal)).ExplanationHasPcData);
    }

    [Fact]
    public async Task A_stopped_graphics_card_is_a_problem()
    {
        var findings = await Detect(new FakeCim().Answer(DevicesModule.ProblemQuery, Device("GeForce", "Display", 43)));

        var device = findings.Single(f => f.Id.StartsWith("M17.device.", StringComparison.Ordinal));
        Assert.Equal(FindingStatus.Problem, device.Status);
        Assert.Null(device.SettingsPage);
    }

    [Fact]
    public async Task Unplugged_and_disabled_devices_are_not_failures()
    {
        var cim = new FakeCim().Answer(DevicesModule.ProblemQuery,
            Device("Clé USB", "USB", 45),
            Device("Ancienne souris", "Mouse", 10, present: false),
            Device("Bluetooth", "Bluetooth", 22),
            Device("Carte réseau", "Net", 14));

        var findings = await Detect(cim);

        Assert.Equal(FindingStatus.Ok, findings.Single(f => f.Id == "M17.devices").Status);
        Assert.Equal(FindingStatus.Info, findings.Single(f => f.Id == "M17.disabled").Status);
        Assert.DoesNotContain(findings, f => f.Id.StartsWith("M17.device.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unreadable_list_is_unknown_never_a_problem()
    {
        var findings = await Detect(new FakeCim().Throw(DevicesModule.ProblemQuery, new DataSourceUnavailableException("absent")));

        Assert.Equal(FindingStatus.Unknown, Assert.Single(findings).Status);
    }

    [Theory]
    [InlineData(28)]
    [InlineData(43)]
    [InlineData(52)]
    [InlineData(999)]
    public void Every_code_has_a_meaning_and_an_advice(long code)
    {
        var (meaning, advice, _) = DevicesModule.Explain(code);

        Assert.False(string.IsNullOrWhiteSpace(meaning));
        Assert.False(string.IsNullOrWhiteSpace(advice));
    }

    [Fact]
    public async Task Unsigned_drivers_are_reported_as_information_with_the_device_names()
    {
        var cim = new FakeCim()
            .Answer(DevicesModule.ProblemQuery, Device("Lecteur de cartes", "SDHost", 28))
            .Answer(Maus.Core.Workshop.DriverInventoryReader.DriverQuery,
                new Dictionary<string, object?> { ["DeviceName"] = "Manette maison", ["DeviceClass"] = "HIDClass", ["DeviceID"] = @"USB\VID_1234&PID_0001\1", ["IsSigned"] = false },
                new Dictionary<string, object?> { ["DeviceName"] = "Realtek(R) Audio", ["DeviceClass"] = "MEDIA", ["DeviceID"] = @"HDAUDIO\FUNC_01\1", ["IsSigned"] = true });

        var findings = await Detect(cim);

        var unsigned = findings.Single(f => f.Id == "M17.unsigned-drivers");
        Assert.Equal(FindingStatus.Info, unsigned.Status);
        Assert.Equal("1 sur 2 : Manette maison", unsigned.Current);
        Assert.Contains("Voir les pilotes non signés", unsigned.Advice, StringComparison.Ordinal);
        Assert.Equal(["Manette maison", "Realtek(R) Audio"], unsigned.Followed!.Select(d => d.Name));

        // Le résumé des périphériques en erreur ouvre la page des mises à jour facultatives de Windows.
        Assert.Equal("ms-settings:windowsupdate-optionalupdates", findings.Single(f => f.Id == "M17.devices").SettingsPage);
    }

    [Fact]
    public async Task All_signed_drivers_are_compliant_and_an_unreadable_list_adds_nothing()
    {
        var signed = new FakeCim()
            .Answer(DevicesModule.ProblemQuery)
            .Answer(Maus.Core.Workshop.DriverInventoryReader.DriverQuery,
                new Dictionary<string, object?> { ["DeviceName"] = "Realtek(R) Audio", ["DeviceClass"] = "MEDIA", ["DeviceID"] = @"HDAUDIO\FUNC_01\1", ["IsSigned"] = true });

        Assert.Equal(FindingStatus.Ok, (await Detect(signed)).Single(f => f.Id == "M17.unsigned-drivers").Status);
        Assert.DoesNotContain(await Detect(new FakeCim().Answer(DevicesModule.ProblemQuery)), f => f.Id == "M17.unsigned-drivers");
    }
}
