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

        Assert.Equal(FindingStatus.Warning, findings.Single(f => f.Id == "M17.devices").Status);
        var device = findings.Single(f => f.Id.StartsWith("M17.device.", StringComparison.Ordinal));
        Assert.Contains("code 28", device.Current, StringComparison.Ordinal);
        Assert.Contains("Windows Update", device.Advice, StringComparison.Ordinal);
        Assert.Equal("ms-settings:windowsupdate-optionalupdates", device.SettingsPage);
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
