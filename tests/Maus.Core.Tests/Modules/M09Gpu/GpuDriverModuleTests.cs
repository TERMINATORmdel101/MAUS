using Maus.Core.Hardware;
using Maus.Core.Modules.M09Gpu;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Tests.Modules.M09Gpu;

public class GpuDriverModuleTests
{
    private const string VideoControllerQuery = "SELECT Name, PNPDeviceID, DriverVersion, DriverDate, InfFilename FROM Win32_VideoController";
    private const string NvidiaQuery = "nvidia-smi --query-gpu=pci.bus_id,driver_version,name --format=csv,noheader";
    private const string NvidiaMemory = "nvidia-smi -q -d MEMORY";
    private const string Rtx2080Ti = @"PCI\VEN_10DE&DEV_1E04&SUBSYS_86751043&REV_A1\4&F71F481&0&0008";
    private const string Rtx4070 = @"PCI\VEN_10DE&DEV_2786&SUBSYS_00000000&REV_A1\4&1&0&0008";
    private const string Rx6800 = @"PCI\VEN_1002&DEV_73BF&SUBSYS_00000000&REV_C1\6&1&0&00000019";
    private const string Uhd630 = @"PCI\VEN_8086&DEV_3E92&SUBSYS_00000000&REV_00\3&11583659&0&10";
    private const string AmdDriverKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0002";

    private static readonly DateTime Recent = new(2026, 9, 17);

    [Fact]
    public async Task Up_to_date_nvidia_card_on_full_link_is_compliant()
    {
        var cim = new FakeCim().Answer(VideoControllerQuery, Controller("NVIDIA GeForce RTX 2080 Ti", Rtx2080Ti, "32.0.16.1714", Recent, "oem157.inf"));
        AnswerProperties(cim, Rtx2080Ti, width: 16, maxWidth: 16, bus: 1);
        var commands = new FakeCommands()
            .Answer(NvidiaQuery, "00000000:01:00.0, 617.14, NVIDIA GeForce RTX 2080 Ti\r\n")
            .Answer(NvidiaMemory, MemoryOutput("00000000:01:00.0", 256));
        var scheduling = new FakeScheduling(new GpuSchedulingInfo(0x10DE, 0x1E04, Supported: true, Enabled: true, EnabledByDefault: false));

        var findings = await Detect(TestContext.Create(cim: cim, commands: commands), scheduling);

        Assert.Equal(FindingStatus.Ok, Get(findings, "M09.basic-display-driver").Status);
        var driver = Get(findings, "M09.driver.nvidia");
        Assert.Equal(FindingStatus.Ok, driver.Status);
        Assert.StartsWith("617.14 (version Windows 32.0.16.1714)", driver.Current, StringComparison.Ordinal);
        Assert.Equal(FindingStatus.Ok, Get(findings, "M09.pcie-link.nvidia").Status);
        Assert.Equal(FindingStatus.Ok, Get(findings, "M09.hags").Status);
        Assert.Equal(FindingStatus.Info, Get(findings, "M09.resizable-bar.nvidia").Status);
        Assert.Equal(FindingStatus.Info, Get(findings, "M09.windows-update-drivers").Status);
        Assert.DoesNotContain(findings, f => f.Status is FindingStatus.Problem or FindingStatus.Warning or FindingStatus.Improvable);
    }

    [Fact]
    public async Task Old_nvidia_driver_is_improvable_with_official_link()
    {
        var cim = new FakeCim().Answer(VideoControllerQuery, Controller("NVIDIA GeForce RTX 2080 Ti", Rtx2080Ti, "31.0.15.3598", new DateTime(2023, 6, 1), "oem12.inf"));

        var findings = await Detect(TestContext.Create(cim: cim), new FakeScheduling());

        var driver = Get(findings, "M09.driver.nvidia");
        Assert.Equal(FindingStatus.Improvable, driver.Status);
        Assert.StartsWith("535.98", driver.Current, StringComparison.Ordinal);
        Assert.Contains("nvidia.com", driver.Advice, StringComparison.Ordinal);
        Assert.Contains("Restaurer le pilote", driver.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Slightly_older_driver_is_only_informative()
    {
        var cim = new FakeCim().Answer(VideoControllerQuery, Controller("NVIDIA GeForce RTX 2080 Ti", Rtx2080Ti, "32.0.16.1690", new DateTime(2026, 8, 20), "oem12.inf"));

        var findings = await Detect(TestContext.Create(cim: cim), new FakeScheduling());

        Assert.Equal(FindingStatus.Info, Get(findings, "M09.driver.nvidia").Status);
    }

    [Fact]
    public async Task Laptop_advice_mentions_the_pc_manufacturer()
    {
        var cim = new FakeCim().Answer(VideoControllerQuery, Controller("NVIDIA GeForce RTX 2080 Ti", Rtx2080Ti, "31.0.15.3598", new DateTime(2023, 6, 1), "oem12.inf"));
        var laptop = new HardwareProfile { FormFactor = FormFactor.Laptop };

        var findings = await Detect(TestContext.Create(cim: cim, hardware: laptop), new FakeScheduling());

        Assert.Contains("fabricant du PC", Get(findings, "M09.driver.nvidia").Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Microsoft_basic_display_adapter_is_a_problem()
    {
        var cim = new FakeCim().Answer(VideoControllerQuery, Controller("Carte graphique de base Microsoft", Rtx2080Ti, "10.0.26100.1", new DateTime(2006, 6, 21), "display.inf"));

        var findings = await Detect(TestContext.Create(cim: cim), new FakeScheduling());

        var basic = Get(findings, "M09.basic-display-driver");
        Assert.Equal(FindingStatus.Problem, basic.Status);
        Assert.Equal(Severity.High, basic.Severity);
        Assert.Contains("NVIDIA", basic.Current, StringComparison.Ordinal);
        Assert.Equal("M09.basic-display-driver", findings[0].Id);
        Assert.DoesNotContain(findings, f => f.Id.StartsWith("M09.driver.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_video_controllers_are_unknown()
    {
        var findings = await Detect(TestContext.Create(), new FakeScheduling());

        Assert.Equal(FindingStatus.Unknown, Get(findings, "M09.gpu").Status);
        Assert.DoesNotContain(findings, f => f.Status == FindingStatus.Problem);
    }

    [Fact]
    public async Task Access_denied_on_wmi_is_unknown_not_a_problem()
    {
        var cim = new FakeCim().Throw(VideoControllerQuery, new MausAccessDeniedException("refusé"));

        var findings = await Detect(TestContext.Create(cim: cim), new FakeScheduling());

        Assert.Equal(FindingStatus.Unknown, Get(findings, "M09.gpu").Status);
    }

    [Fact]
    public async Task Narrow_pcie_link_on_nvidia_is_a_warning()
    {
        var cim = new FakeCim().Answer(VideoControllerQuery, Controller("NVIDIA GeForce RTX 2080 Ti", Rtx2080Ti, "32.0.16.1714", Recent, "oem157.inf"));
        AnswerProperties(cim, Rtx2080Ti, width: 8, maxWidth: 16, bus: 1);

        var findings = await Detect(TestContext.Create(cim: cim), new FakeScheduling());

        var link = Get(findings, "M09.pcie-link.nvidia");
        Assert.Equal(FindingStatus.Warning, link.Status);
        Assert.StartsWith("x8, Gen1", link.Current, StringComparison.Ordinal);
        Assert.StartsWith("x16", link.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreadable_pcie_link_is_unknown()
    {
        var cim = new FakeCim().Answer(VideoControllerQuery, Controller("NVIDIA GeForce RTX 2080 Ti", Rtx2080Ti, "32.0.16.1714", Recent, "oem157.inf"));

        var findings = await Detect(TestContext.Create(cim: cim), new FakeScheduling());

        Assert.Equal(FindingStatus.Unknown, Get(findings, "M09.pcie-link.nvidia").Status);
    }

    [Fact]
    public async Task Rtx_40_without_hags_and_resizable_bar_is_improvable()
    {
        var cim = new FakeCim().Answer(VideoControllerQuery, Controller("NVIDIA GeForce RTX 4070", Rtx4070, "32.0.16.1714", Recent, "oem157.inf"));
        AnswerProperties(cim, Rtx4070, width: 16, maxWidth: 16, bus: 1);
        var commands = new FakeCommands()
            .Answer(NvidiaQuery, "00000000:01:00.0, 617.14, NVIDIA GeForce RTX 4070")
            .Answer(NvidiaMemory, MemoryOutput("00000000:01:00.0", 256));
        var scheduling = new FakeScheduling(new GpuSchedulingInfo(0x10DE, 0x2786, Supported: true, Enabled: false, EnabledByDefault: true));

        var findings = await Detect(TestContext.Create(cim: cim, commands: commands), scheduling);

        var hags = Get(findings, "M09.hags");
        Assert.Equal(FindingStatus.Improvable, hags.Status);
        Assert.True(hags.Fixable);
        var bar = Get(findings, "M09.resizable-bar.nvidia");
        Assert.Equal(FindingStatus.Improvable, bar.Status);
        Assert.Contains("256 Mio", bar.Current, StringComparison.Ordinal);
        Assert.Contains("Above 4G Decoding", bar.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rtx_40_with_large_bar1_has_resizable_bar_active()
    {
        var cim = new FakeCim().Answer(VideoControllerQuery, Controller("NVIDIA GeForce RTX 4070", Rtx4070, "32.0.16.1714", Recent, "oem157.inf"));
        AnswerProperties(cim, Rtx4070, width: 16, maxWidth: 16, bus: 1);
        var commands = new FakeCommands()
            .Answer(NvidiaQuery, "00000000:01:00.0, 617.14, NVIDIA GeForce RTX 4070")
            .Answer(NvidiaMemory, MemoryOutput("00000000:01:00.0", 16384));

        var findings = await Detect(TestContext.Create(cim: cim, commands: commands), new FakeScheduling());

        Assert.Equal(FindingStatus.Ok, Get(findings, "M09.resizable-bar.nvidia").Status);
    }

    [Fact]
    public async Task Resizable_bar_without_nvidia_smi_or_memory_ranges_is_unknown()
    {
        var cim = new FakeCim().Answer(VideoControllerQuery, Controller("NVIDIA GeForce RTX 4070", Rtx4070, "32.0.16.1714", Recent, "oem157.inf"));

        var findings = await Detect(TestContext.Create(cim: cim), new FakeScheduling());

        Assert.Equal(FindingStatus.Unknown, Get(findings, "M09.resizable-bar.nvidia").Status);
        Assert.StartsWith("617.14", Get(findings, "M09.driver.nvidia").Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resizable_bar_falls_back_to_memory_ranges()
    {
        var cim = new FakeCim()
            .Answer(VideoControllerQuery, Controller("NVIDIA GeForce RTX 4070", Rtx4070, "32.0.16.1714", Recent, "oem157.inf"))
            .Answer(
                GpuDriverModule.MemoryRangesQuery(Rtx4070),
                new Dictionary<string, object?> { ["StartingAddress"] = 0xF6000000UL, ["EndingAddress"] = 0xF6FFFFFFUL },
                new Dictionary<string, object?> { ["StartingAddress"] = 0x4000000000UL, ["EndingAddress"] = 0x43FFFFFFFFUL });

        var findings = await Detect(TestContext.Create(cim: cim), new FakeScheduling());

        var bar = Get(findings, "M09.resizable-bar.nvidia");
        Assert.Equal(FindingStatus.Ok, bar.Status);
        Assert.Contains("16384 Mio", bar.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hags_falls_back_to_registry_value()
    {
        var cim = new FakeCim().Answer(VideoControllerQuery, Controller("NVIDIA GeForce RTX 4070", Rtx4070, "32.0.16.1714", Recent, "oem157.inf"));
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 1);

        var findings = await Detect(TestContext.Create(registry, cim), new FakeScheduling(null));

        var hags = Get(findings, "M09.hags");
        Assert.Equal(FindingStatus.Improvable, hags.Status);
        Assert.Contains("HwSchMode = 1", hags.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hags_unreadable_is_unknown()
    {
        var cim = new FakeCim().Answer(VideoControllerQuery, Controller("NVIDIA GeForce RTX 4070", Rtx4070, "32.0.16.1714", Recent, "oem157.inf"));
        var registry = new FakeRegistry().Deny(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");

        var findings = await Detect(TestContext.Create(registry, cim), new FakeScheduling(null));

        Assert.Equal(FindingStatus.Unknown, Get(findings, "M09.hags").Status);
    }

    [Fact]
    public async Task Hags_off_on_older_card_is_informative()
    {
        var cim = new FakeCim().Answer(VideoControllerQuery, Controller("NVIDIA GeForce RTX 2080 Ti", Rtx2080Ti, "32.0.16.1714", Recent, "oem157.inf"));
        var scheduling = new FakeScheduling(new GpuSchedulingInfo(0x10DE, 0x1E04, Supported: true, Enabled: false, EnabledByDefault: false));

        var findings = await Detect(TestContext.Create(cim: cim), scheduling);

        var hags = Get(findings, "M09.hags");
        Assert.Equal(FindingStatus.Info, hags.Status);
        Assert.False(hags.Fixable);
    }

    [Fact]
    public async Task Hags_not_supported_is_informative()
    {
        var cim = new FakeCim().Answer(VideoControllerQuery, Controller("NVIDIA GeForce RTX 2080 Ti", Rtx2080Ti, "32.0.16.1714", Recent, "oem157.inf"));
        var scheduling = new FakeScheduling(new GpuSchedulingInfo(0x10DE, 0x1E04, Supported: false, Enabled: false, EnabledByDefault: false));

        var findings = await Detect(TestContext.Create(cim: cim), scheduling);

        Assert.Equal(FindingStatus.Info, Get(findings, "M09.hags").Status);
    }

    [Fact]
    public async Task Amd_version_comes_from_radeon_software_registry_value()
    {
        var cim = new FakeCim().Answer(VideoControllerQuery, Controller("AMD Radeon RX 6800 XT", Rx6800, "32.0.21001.9024", new DateTime(2025, 11, 1), "oem45.inf"));
        AnswerProperties(cim, Rx6800, width: 8, maxWidth: 16, bus: 3, driver: @"{4d36e968-e325-11ce-bfc1-08002be10318}\0002");
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, AmdDriverKey, "RadeonSoftwareVersion", "25.11.1")
            .Set(RegistryHive.LocalMachine, AmdDriverKey, "HardwareInformation.qwMemorySize", 17179869184L);

        var findings = await Detect(TestContext.Create(registry, cim), new FakeScheduling());

        var driver = Get(findings, "M09.driver.amd");
        Assert.Equal(FindingStatus.Improvable, driver.Status);
        Assert.StartsWith("25.11.1 (version Windows 32.0.21001.9024)", driver.Current, StringComparison.Ordinal);
        Assert.StartsWith("26.9.1", driver.Expected, StringComparison.Ordinal);
        Assert.Contains("amd.com", driver.Advice, StringComparison.Ordinal);

        // Sur une Radeon, la largeur lue peut être celle du commutateur interne : simple information.
        Assert.Equal(FindingStatus.Info, Get(findings, "M09.pcie-link.amd").Status);
    }

    [Fact]
    public async Task Integrated_intel_gpu_is_judged_on_version_only()
    {
        var cim = new FakeCim().Answer(VideoControllerQuery, Controller("Intel(R) UHD Graphics 630", Uhd630, "31.0.101.2145", new DateTime(2026, 9, 1), "oem3.inf"));

        var findings = await Detect(TestContext.Create(cim: cim), new FakeScheduling());

        Assert.Equal(FindingStatus.Ok, Get(findings, "M09.driver.intel").Status);
        Assert.DoesNotContain(findings, f => f.Id.StartsWith("M09.pcie-link", StringComparison.Ordinal));
        Assert.DoesNotContain(findings, f => f.Id.StartsWith("M09.resizable-bar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_gpu_without_catalog_branch_is_judged_on_age()
    {
        var cim = new FakeCim().Answer(VideoControllerQuery, Controller("Matrox G200eW", @"PCI\VEN_102B&DEV_0532&SUBSYS_00000000&REV_0A\4&1", "9.15.1.224", new DateTime(2020, 1, 1), "oem9.inf"));

        var findings = await Detect(TestContext.Create(cim: cim), new FakeScheduling());

        var driver = Get(findings, "M09.driver.gpu");
        Assert.Equal(FindingStatus.Improvable, driver.Status);
        Assert.Contains("moins de 6 mois", driver.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_nvidia_cards_get_distinct_identifiers()
    {
        var cim = new FakeCim().Answer(
            VideoControllerQuery,
            Controller("NVIDIA GeForce RTX 2080 Ti", Rtx2080Ti, "32.0.16.1714", Recent, "oem157.inf"),
            Controller("NVIDIA GeForce RTX 4070", Rtx4070, "32.0.16.1714", Recent, "oem157.inf"));

        var findings = await Detect(TestContext.Create(cim: cim), new FakeScheduling());

        Assert.Single(findings, f => f.Id == "M09.driver.nvidia");
        Assert.Single(findings, f => f.Id == "M09.driver.nvidia-2");
    }

    [Fact]
    public async Task Windows_update_driver_exclusion_is_reported_as_the_users_choice()
    {
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", 1);

        var findings = await Detect(TestContext.Create(registry), new FakeScheduling());

        var wu = Get(findings, "M09.windows-update-drivers");
        Assert.Equal(FindingStatus.Info, wu.Status);
        Assert.StartsWith("exclus", wu.Current, StringComparison.Ordinal);
        Assert.True(wu.Fixable);
    }

    [Fact]
    public async Task Windows_update_policy_has_no_effect_on_home_edition()
    {
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", 1);
        var home = new WindowsInfo("Windows 11 Famille", "Core", "25H2", 26200, 1000);

        var findings = await Detect(TestContext.Create(registry, windows: home), new FakeScheduling());

        var wu = Get(findings, "M09.windows-update-drivers");
        Assert.Contains("Famille", wu.Current, StringComparison.Ordinal);
        Assert.False(wu.Fixable);
        Assert.True(Get(findings, "M09.device-installation-settings").Fixable);
    }

    [Fact]
    public async Task Denied_policy_keys_are_unknown()
    {
        var registry = new FakeRegistry()
            .Deny(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate")
            .Deny(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching");

        var findings = await Detect(TestContext.Create(registry), new FakeScheduling());

        Assert.Equal(FindingStatus.Unknown, Get(findings, "M09.windows-update-drivers").Status);
        Assert.Equal(FindingStatus.Unknown, Get(findings, "M09.device-installation-settings").Status);
    }

    [Fact]
    public void Public_constructor_loads_the_embedded_catalog()
    {
        var module = new GpuDriverModule();

        Assert.Equal("M09", module.Id);
        Assert.Equal(90, module.Order);
        Assert.NotEmpty(GpuDriverCatalog.Default.Branches);
    }

    private static Task<IReadOnlyList<Finding>> Detect(AuditContext context, IGpuSchedulingReader scheduling) =>
        new GpuDriverModule(scheduling).DetectAsync(context, CancellationToken.None);

    private static Finding Get(IReadOnlyList<Finding> findings, string id) => findings.Single(f => f.Id == id);

    private static Dictionary<string, object?> Controller(string name, string pnp, string version, DateTime date, string inf) => new()
    {
        ["Name"] = name,
        ["PNPDeviceID"] = pnp,
        ["DriverVersion"] = version,
        ["DriverDate"] = date,
        ["InfFilename"] = inf,
    };

    private static void AnswerProperties(FakeCim cim, string pnp, uint width, uint maxWidth, uint bus, string? driver = null)
    {
        var rows = new List<CimRow>
        {
            Property("DEVPKEY_PciDevice_CurrentLinkWidth", width),
            Property("DEVPKEY_PciDevice_MaxLinkWidth", maxWidth),
            Property("DEVPKEY_PciDevice_CurrentLinkSpeed", 1u),
            Property("DEVPKEY_PciDevice_MaxLinkSpeed", 4u),
            Property("DEVPKEY_Device_BusNumber", bus),
        };
        if (driver is not null)
        {
            rows.Add(Property("DEVPKEY_Device_Driver", driver));
        }

        cim.AnswerMethod(GpuDriverModule.DeviceQuery(pnp), "GetDeviceProperties", new Dictionary<string, object?> { ["deviceProperties"] = rows.ToArray() });
    }

    private static CimRow Property(string key, object data) =>
        new(new Dictionary<string, object?> { ["KeyName"] = key, ["Data"] = data });

    private static string MemoryOutput(string busId, int bar1MiB) => $"""
        ==============NVSMI LOG==============

        Attached GPUs                                          : 1
        GPU {busId}
            FB Memory Usage
                Total                                          : 11264 MiB
                Reserved                                       : 237 MiB
                Used                                           : 1849 MiB
                Free                                           : 9179 MiB
            BAR1 Memory Usage
                Total                                          : {bar1MiB} MiB
                Used                                           : 2 MiB
                Free                                           : 254 MiB
        """;

    private sealed class FakeScheduling(params GpuSchedulingInfo[]? adapters) : IGpuSchedulingReader
    {
        public IReadOnlyList<GpuSchedulingInfo>? Read() => adapters;
    }
}
