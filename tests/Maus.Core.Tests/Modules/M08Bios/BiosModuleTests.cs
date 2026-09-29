using System.Text;
using Maus.Core.Hardware;
using Maus.Core.Modules.M08Bios;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Tests.Modules.M08Bios;

public class BiosModuleTests
{
    private const string MsiBoard = "Micro-Star International Co., Ltd.";

    private static readonly HardwareProfile MsiDesktop = new()
    {
        FormFactor = FormFactor.Desktop,
        Manufacturer = MsiBoard,
        Model = "MS-7B17",
        BoardManufacturer = MsiBoard,
        BoardProduct = "MPG Z390 GAMING PRO CARBON (MS-7B17)",
        Cpu = new CpuInfo("Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz", HardwareVendor.Intel, 6, 12, 3700),
    };

    private static readonly HardwareProfile RaptorLake = MsiDesktop with
    {
        Cpu = new CpuInfo("13th Gen Intel(R) Core(TM) i9-13900K", HardwareVendor.Intel, 24, 32, 3000),
    };

    [Fact]
    public async Task Compliant_pc_reports_ok_for_certificates_and_microcode()
    {
        var context = Context(RaptorLake, biosDate: new DateTime(2026, 5, 2), microcode: [0x2F, 0x01, 0x00, 0x00]);

        var findings = await new BiosModule(new FakeFirmware(Encoding.ASCII.GetBytes("xx Windows UEFI CA 2023 xx"))).DetectAsync(context, CancellationToken.None);

        Assert.Equal(FindingStatus.Ok, Single(findings, "M08.bios-age").Status);
        Assert.Equal(FindingStatus.Ok, Single(findings, "M08.intel-microcode").Status);
        Assert.Equal(FindingStatus.Ok, Single(findings, "M08.secure-boot").Status);
        Assert.Equal(FindingStatus.Ok, Single(findings, "M08.ca2023-status").Status);
        Assert.Equal(FindingStatus.Ok, Single(findings, "M08.ca2023-error").Status);
        Assert.Equal(FindingStatus.Ok, Single(findings, "M08.secure-boot-events").Status);
        Assert.Equal(FindingStatus.Ok, Single(findings, "M08.dbdefault").Status);
        Assert.Equal(FindingStatus.Ok, Single(findings, "M08.boot-manager-2023").Status);
        Assert.Null(Single(findings, "M08.ca2023-status").SettingsPage);
        Assert.DoesNotContain(findings, f => f.Status is FindingStatus.Problem or FindingStatus.Warning or FindingStatus.Improvable);
    }

    [Fact]
    public async Task Old_bios_is_an_improvement_with_vendor_page_and_warning()
    {
        var findings = await Detect(Context(MsiDesktop, biosDate: new DateTime(2024, 6, 7)));

        var age = Single(findings, "M08.bios-age");
        Assert.Equal(FindingStatus.Improvable, age.Status);
        Assert.Contains("1.D0", age.Current);
        Assert.Contains("07/06/2024", age.Current);
        Assert.Contains("2 ans et 3 mois", age.Current);
        Assert.Contains("https://www.msi.com/search/MPG%20Z390%20GAMING%20PRO%20CARBON", age.Advice);
        Assert.Contains("ne coupez jamais l'alimentation", age.Advice);
        Assert.DoesNotContain("décline", age.Advice);
    }

    [Fact]
    public async Task Missing_release_date_is_unknown()
    {
        var cim = BaseCim(biosDate: null);

        var findings = await Detect(Context(MsiDesktop, cim: cim));

        Assert.Equal(FindingStatus.Unknown, Single(findings, "M08.bios-age").Status);
    }

    [Fact]
    public async Task Denied_wmi_gives_admin_required_not_problem()
    {
        var cim = new FakeCim()
            .Throw(BiosModule.BiosQuery, new MausAccessDeniedException("refusé"))
            .Throw(BiosModule.BoardQuery, new MausAccessDeniedException("refusé"))
            .Throw(BiosModule.BitLockerQuery, new MausAccessDeniedException("refusé"), CimScopes.BitLocker);
        var registry = new FakeRegistry()
            .Deny(RegistryHive.LocalMachine, BiosModule.SecureBootStateKey)
            .Deny(RegistryHive.LocalMachine, BiosModule.ServicingKey)
            .Deny(RegistryHive.LocalMachine, BiosModule.CpuKey);
        var events = new FakeEventLogs().Deny("System");
        var context = TestContext.Create(registry, cim, hardware: new HardwareProfile(), eventLogs: events, elevated: false);

        var findings = await Detect(context);

        Assert.Equal(FindingStatus.Unknown, Single(findings, "M08.bios-age").Status);
        Assert.Equal(FindingStatus.Unknown, Single(findings, "M08.secure-boot").Status);
        Assert.Equal(FindingStatus.Unknown, Single(findings, "M08.ca2023-status").Status);
        Assert.Equal(FindingStatus.Unknown, Single(findings, "M08.secure-boot-events").Status);
        Assert.Equal(FindingStatus.Unknown, Single(findings, "M08.bitlocker").Status);
        Assert.Equal(FindingStatus.Unknown, Single(findings, "M08.board").Status);
        Assert.DoesNotContain(findings, f => f.Status is FindingStatus.Problem or FindingStatus.Warning);
    }

    [Fact]
    public async Task Raptor_lake_with_old_microcode_is_a_problem()
    {
        var findings = await Detect(Context(RaptorLake, microcode: [0x1D, 0x01, 0x00, 0x00]));

        var microcode = Single(findings, "M08.intel-microcode");
        Assert.Equal(FindingStatus.Problem, microcode.Status);
        Assert.Equal(Severity.High, microcode.Severity);
        Assert.Equal("0x11D", microcode.Current);
        Assert.Contains("000102331", microcode.Advice);
    }

    [Fact]
    public async Task Raptor_lake_with_windows_loaded_microcode_but_old_bios_is_improvable()
    {
        var context = Context(RaptorLake, microcode: [0x2F, 0x01, 0x00, 0x00], previousMicrocode: [0x04, 0x01, 0x00, 0x00]);

        var findings = await Detect(context);

        var microcode = Single(findings, "M08.intel-microcode");
        Assert.Equal(FindingStatus.Improvable, microcode.Status);
        Assert.Contains("0x104", microcode.Current);
    }

    [Fact]
    public async Task Raptor_lake_microcode_missing_or_denied_is_unknown()
    {
        var missing = await Detect(Context(RaptorLake, microcode: null));
        Assert.Equal(FindingStatus.Unknown, Single(missing, "M08.intel-microcode").Status);

        var registry = Registry().Deny(RegistryHive.LocalMachine, BiosModule.CpuKey);
        var denied = await Detect(Context(RaptorLake, registry: registry));
        Assert.Equal(FindingStatus.Unknown, Single(denied, "M08.intel-microcode").Status);
    }

    [Fact]
    public async Task Other_cpu_only_shows_microcode_revision()
    {
        var findings = await Detect(Context(MsiDesktop, microcode: [0xF0, 0x00, 0x00, 0x00]));

        Assert.DoesNotContain(findings, f => f.Id == "M08.intel-microcode");
        var info = Single(findings, "M08.microcode");
        Assert.Equal(FindingStatus.Info, info.Status);
        Assert.Equal("0xF0", info.Current);
    }

    [Fact]
    public async Task Certificates_not_deployed_is_a_warning()
    {
        var registry = Registry(status: "InProgress", capable: 1, error: 0x80070015);
        var events = new FakeEventLogs()
            .Add("System", BiosModule.TpmWmiProvider, 1808, new DateTime(2026, 6, 1))
            .Add("System", BiosModule.TpmWmiProvider, 1801, new DateTime(2026, 9, 20));

        var findings = await Detect(Context(MsiDesktop, registry: registry, events: events));

        Assert.Equal(FindingStatus.Warning, Single(findings, "M08.ca2023-status").Status);
        Assert.Equal("en cours (InProgress)", Single(findings, "M08.ca2023-status").Current);
        Assert.Equal("ms-settings:windowsupdate", Single(findings, "M08.ca2023-status").SettingsPage);
        Assert.Null(Single(findings, "M08.ca2023-error").SettingsPage);
        Assert.Equal(FindingStatus.Warning, Single(findings, "M08.ca2023-error").Status);
        Assert.Equal("code 0x80070015", Single(findings, "M08.ca2023-error").Current);
        var journal = Single(findings, "M08.secure-boot-events");
        Assert.Equal(FindingStatus.Warning, journal.Status);
        Assert.Contains("1801", journal.Current);
        Assert.Equal(FindingStatus.Info, Single(findings, "M08.boot-manager-2023").Status);
        Assert.DoesNotContain(findings, f => f.Id == "M08.dbdefault");
    }

    [Fact]
    public async Task Missing_certificate_status_is_unknown_when_secure_boot_is_on()
    {
        var findings = await Detect(Context(MsiDesktop, registry: Registry(status: null)));

        Assert.Equal(FindingStatus.Unknown, Single(findings, "M08.ca2023-status").Status);
    }

    [Fact]
    public async Task Secure_boot_off_is_a_warning_and_certificates_become_informative()
    {
        var registry = Registry(secureBoot: 0, status: "NotStarted", capable: 0);
        var events = new FakeEventLogs().Add("System", BiosModule.TpmWmiProvider, 1795, new DateTime(2026, 9, 1));

        var findings = await Detect(Context(MsiDesktop, registry: registry, events: events));

        Assert.Equal(FindingStatus.Warning, Single(findings, "M08.secure-boot").Status);
        Assert.Equal(FindingStatus.Info, Single(findings, "M08.ca2023-status").Status);
        Assert.Equal(FindingStatus.Info, Single(findings, "M08.secure-boot-events").Status);
    }

    [Fact]
    public async Task Secure_boot_value_absent_is_unknown()
    {
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, BiosModule.CpuKey, "Update Revision", new byte[] { 0xF0, 0, 0, 0 });

        var findings = await Detect(Context(MsiDesktop, registry: registry));

        Assert.Equal(FindingStatus.Unknown, Single(findings, "M08.secure-boot").Status);
        Assert.Equal(FindingStatus.Info, Single(findings, "M08.ca2023-status").Status);
    }

    [Fact]
    public async Task No_servicing_events_is_neutral()
    {
        var findings = await Detect(Context(MsiDesktop, events: new FakeEventLogs()));

        Assert.Equal(FindingStatus.Info, Single(findings, "M08.secure-boot-events").Status);
    }

    [Fact]
    public async Task Default_keys_without_2023_certificate_are_a_warning()
    {
        var context = Context(MsiDesktop);

        var findings = await new BiosModule(new FakeFirmware(Encoding.ASCII.GetBytes("Microsoft Corporation UEFI CA 2011"))).DetectAsync(context, CancellationToken.None);

        var keys = Single(findings, "M08.dbdefault");
        Assert.Equal(FindingStatus.Warning, keys.Status);
        Assert.Contains("Ne restaurez pas", keys.Advice);
    }

    [Fact]
    public async Task Default_keys_need_admin_or_published_variable()
    {
        var notElevated = await Detect(Context(MsiDesktop, elevated: false));
        Assert.Equal(FindingStatus.Unknown, Single(notElevated, "M08.dbdefault").Status);

        var absent = await new BiosModule(new FakeFirmware((byte[]?)null)).DetectAsync(Context(MsiDesktop), CancellationToken.None);
        Assert.Equal(FindingStatus.Unknown, Single(absent, "M08.dbdefault").Status);

        var denied = await new BiosModule(new FakeFirmware(new MausAccessDeniedException("refusé"))).DetectAsync(Context(MsiDesktop), CancellationToken.None);
        Assert.Equal(FindingStatus.Unknown, Single(denied, "M08.dbdefault").Status);

        var legacy = await new BiosModule(new FakeFirmware(new DataSourceUnavailableException("BIOS hérité"))).DetectAsync(Context(MsiDesktop), CancellationToken.None);
        Assert.Equal(FindingStatus.Unknown, Single(legacy, "M08.dbdefault").Status);
    }

    [Fact]
    public async Task Bitlocker_active_gives_recovery_key_advice()
    {
        var cim = BaseCim(new DateTime(2026, 5, 1))
            .Answer(BiosModule.BitLockerQuery, CimScopes.BitLocker,
                new Dictionary<string, object?> { ["DriveLetter"] = "D:", ["ProtectionStatus"] = 0u, ["VolumeType"] = 1u },
                new Dictionary<string, object?> { ["DriveLetter"] = "C:", ["ProtectionStatus"] = 1u, ["VolumeType"] = 0u });

        var findings = await Detect(Context(MsiDesktop, cim: cim));

        var bitlocker = Single(findings, "M08.bitlocker");
        Assert.Equal(FindingStatus.Info, bitlocker.Status);
        Assert.Equal("protection active", bitlocker.Current);
        Assert.Contains("https://aka.ms/myrecoverykey", bitlocker.Advice);
        Assert.True(bitlocker.Fixable);
    }

    [Fact]
    public async Task Bitlocker_unavailable_or_off_is_informative()
    {
        var cim = BaseCim(new DateTime(2026, 5, 1))
            .Throw(BiosModule.BitLockerQuery, new DataSourceUnavailableException("absent"), CimScopes.BitLocker);
        var unavailable = await Detect(Context(MsiDesktop, cim: cim));
        Assert.Equal("non disponible sur ce PC", Single(unavailable, "M08.bitlocker").Current);

        var off = BaseCim(new DateTime(2026, 5, 1))
            .Answer(BiosModule.BitLockerQuery, CimScopes.BitLocker, new Dictionary<string, object?> { ["DriveLetter"] = "C:", ["ProtectionStatus"] = 0u });
        var findings = await Detect(Context(MsiDesktop, cim: off));
        Assert.Equal("protection désactivée", Single(findings, "M08.bitlocker").Current);
        Assert.False(Single(findings, "M08.bitlocker").Fixable);
    }

    [Fact]
    public async Task Board_finding_names_model_revision_and_tools_without_serial()
    {
        var findings = await Detect(Context(MsiDesktop));

        var board = Single(findings, "M08.board");
        Assert.Equal(FindingStatus.Info, board.Status);
        Assert.Equal("MSI MPG Z390 GAMING PRO CARBON (MS-7B17), révision 1.0", board.Current);
        Assert.DoesNotContain("SERIAL-123", string.Join(' ', findings.Select(f => $"{f.Current} {f.Advice}")));
        var method = Single(findings, "M08.update-method");
        Assert.Contains("M-Flash", method.Current);
        Assert.Contains("Flash BIOS Button", method.Current);
        Assert.DoesNotContain("fTPM", method.Advice);
    }

    [Fact]
    public async Task Branded_amd_laptop_points_to_oem_tool_and_ftpm_warning()
    {
        var laptop = new HardwareProfile
        {
            FormFactor = FormFactor.Laptop,
            Manufacturer = "LENOVO",
            Model = "21K5",
            Cpu = new CpuInfo("AMD Ryzen 7 7840U", HardwareVendor.Amd, 8, 16, 3300),
        };
        var cim = BaseCim(new DateTime(2026, 1, 1), boardManufacturer: "LENOVO", boardProduct: "21K5CTO1WW");

        var findings = await Detect(Context(laptop, cim: cim));

        var method = Single(findings, "M08.update-method");
        Assert.Equal("Lenovo Vantage", method.Current);
        Assert.Contains("fTPM", method.Advice);
        Assert.Contains("https://pcsupport.lenovo.com/", Single(findings, "M08.board").Advice);
    }

    private static Task<IReadOnlyList<Finding>> Detect(AuditContext context) =>
        new BiosModule(new FakeFirmware(Encoding.ASCII.GetBytes("Windows UEFI CA 2023"))).DetectAsync(context, CancellationToken.None);

    private static Finding Single(IReadOnlyList<Finding> findings, string id) => findings.Single(f => f.Id == id);

    private static AuditContext Context(
        HardwareProfile hardware,
        DateTime? biosDate = null,
        byte[]? microcode = null,
        byte[]? previousMicrocode = null,
        FakeRegistry? registry = null,
        FakeCim? cim = null,
        FakeEventLogs? events = null,
        bool elevated = true)
    {
        registry ??= Registry();
        if (microcode is not null)
        {
            registry.Set(RegistryHive.LocalMachine, BiosModule.CpuKey, "Update Revision", microcode);
            registry.Set(RegistryHive.LocalMachine, BiosModule.CpuKey, "Previous Update Revision", previousMicrocode ?? microcode);
        }

        events ??= new FakeEventLogs().Add("System", BiosModule.TpmWmiProvider, 1808, new DateTime(2026, 9, 22));
        return TestContext.Create(registry, cim ?? BaseCim(biosDate ?? new DateTime(2024, 6, 7)), hardware: hardware, eventLogs: events, elevated: elevated);
    }

    private static FakeRegistry Registry(int? secureBoot = 1, string? status = "Updated", int? capable = 2, uint? error = null)
    {
        var registry = new FakeRegistry();
        if (secureBoot is not null)
        {
            registry.Set(RegistryHive.LocalMachine, BiosModule.SecureBootStateKey, "UEFISecureBootEnabled", secureBoot.Value);
        }

        if (status is not null)
        {
            registry.Set(RegistryHive.LocalMachine, BiosModule.ServicingKey, "UEFICA2023Status", status);
        }

        if (capable is not null)
        {
            registry.Set(RegistryHive.LocalMachine, BiosModule.ServicingKey, "WindowsUEFICA2023Capable", capable.Value);
        }

        if (error is not null)
        {
            registry.Set(RegistryHive.LocalMachine, BiosModule.ServicingKey, "UEFICA2023Error", unchecked((int)error.Value));
        }

        return registry;
    }

    private static FakeCim BaseCim(DateTime? biosDate, string boardManufacturer = MsiBoard, string boardProduct = "MPG Z390 GAMING PRO CARBON (MS-7B17)") =>
        new FakeCim()
            .Answer(BiosModule.BiosQuery, new Dictionary<string, object?>
            {
                ["Manufacturer"] = "American Megatrends Inc.",
                ["SMBIOSBIOSVersion"] = "1.D0",
                ["ReleaseDate"] = biosDate,
                ["SerialNumber"] = "SERIAL-123",
            })
            .Answer(BiosModule.BoardQuery, new Dictionary<string, object?>
            {
                ["Manufacturer"] = boardManufacturer,
                ["Product"] = boardProduct,
                ["Version"] = "1.0",
                ["SerialNumber"] = "SERIAL-123",
            });

    private sealed class FakeFirmware : IFirmwareVariableReader
    {
        private readonly byte[]? _value;
        private readonly Exception? _exception;

        public FakeFirmware(byte[]? value)
        {
            _value = value;
        }

        public FakeFirmware(Exception exception)
        {
            _exception = exception;
        }

        public byte[]? Read(string name, string vendorGuid)
        {
            Assert.Equal(FirmwareVariables.DbDefault, name);
            return _exception is null ? _value : throw _exception;
        }
    }
}
