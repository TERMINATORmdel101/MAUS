using Maus.Core.Hardware;
using Maus.Core.Modules.M14Display;
using Microsoft.Win32;

namespace Maus.Core.Tests.Modules.M14Display;

public class DisplayModuleTests
{
    private const string NvidiaPnp = @"PCI\VEN_10DE&DEV_1E04&SUBSYS_86751043&REV_A1\4&F71F481&0&0008";
    private const string IntelPnp = @"PCI\VEN_8086&DEV_3E92&SUBSYS_86941043&REV_00\3&11583659&0&10";
    private const string NvidiaPath = @"\\?\PCI#VEN_10DE&DEV_1E04&SUBSYS_86751043&REV_A1#4&f71f481&0&0008#{5b45201d-f2f2-4f3b-85bb-30ff1f953599}";
    private const string IntelPath = @"\\?\PCI#VEN_8086&DEV_3E92&SUBSYS_86941043&REV_00#3&11583659&0&10#{5b45201d-f2f2-4f3b-85bb-30ff1f953599}";

    private static readonly HardwareProfile DesktopWithDedicatedGpu = new()
    {
        FormFactor = FormFactor.Desktop,
        Gpus =
        [
            new GpuInfo("NVIDIA GeForce RTX 2080 Ti", HardwareVendor.Nvidia, "32.0.16.1714", null, NvidiaPnp, IsIntegrated: false),
            new GpuInfo("Intel(R) UHD Graphics 630", HardwareVendor.Intel, "31.0.101.2145", null, IntelPnp, IsIntegrated: true),
        ],
    };

    [Fact]
    public async Task Displayport_monitor_at_max_refresh_on_dedicated_gpu_is_compliant()
    {
        var monitor = Monitor(refresh: 164.94, color: new AdvancedColorInfo(true, false, ColorEncoding.Rgb, 8, AdvancedColorMode.Sdr));

        var findings = await Detect(DesktopWithDedicatedGpu, monitor);

        Assert.Equal(FindingStatus.Ok, Get(findings, "M14.adapter.display-1").Status);
        var refresh = Get(findings, "M14.refresh.display-1");
        Assert.Equal(FindingStatus.Ok, refresh.Status);
        Assert.Equal("164,94 Hz", refresh.Current);
        Assert.Equal(FindingStatus.Ok, Get(findings, "M14.color-encoding.display-1").Status);
        Assert.Equal(FindingStatus.Ok, Get(findings, "M14.connector.display-1").Status);
        Assert.Equal(FindingStatus.Ok, Get(findings, "M14.resolution.display-1").Status);
        Assert.Equal(FindingStatus.Info, Get(findings, "M14.hdr.display-1").Status);
        Assert.Equal(FindingStatus.Info, Get(findings, "M14.vrr").Status);
        Assert.Null(refresh.SettingsPage);
        Assert.Null(Get(findings, "M14.resolution.display-1").SettingsPage);
        Assert.Null(Get(findings, "M14.vrr").SettingsPage);
        Assert.DoesNotContain(findings, f => f.Id == "M14.bit-depth.display-1");
        Assert.DoesNotContain(findings, f => f.Status is FindingStatus.Problem or FindingStatus.Warning or FindingStatus.Improvable or FindingStatus.Unknown);
        Assert.Equal("Écran 1 : AW3423DWF", refresh.Category);
    }

    [Fact]
    public async Task Monitor_below_its_maximum_refresh_is_a_warning()
    {
        var monitor = Monitor(refresh: 60, color: new AdvancedColorInfo(false, false, ColorEncoding.Rgb, 8));

        var findings = await Detect(DesktopWithDedicatedGpu, monitor);

        var refresh = Get(findings, "M14.refresh.display-1");
        Assert.Equal(FindingStatus.Warning, refresh.Status);
        Assert.Equal(Severity.Medium, refresh.Severity);
        Assert.Equal("60 Hz", refresh.Current);
        Assert.StartsWith("165 Hz", refresh.Expected, StringComparison.Ordinal);
        Assert.Contains("accepte 165 Hz mais tourne à 60 Hz", refresh.Explanation, StringComparison.Ordinal);
        Assert.True(refresh.Fixable);
        Assert.Equal("ms-settings:display-advanced", refresh.SettingsPage);
        Assert.Null(Get(findings, "M14.hdr.display-1").SettingsPage);
    }

    [Fact]
    public async Task Lower_refresh_in_hdr_with_full_colors_is_only_an_optimisation()
    {
        var monitor = Monitor(refresh: 120, color: new AdvancedColorInfo(true, true, ColorEncoding.Rgb, 10, AdvancedColorMode.Hdr));

        var findings = await Detect(DesktopWithDedicatedGpu, monitor);

        var refresh = Get(findings, "M14.refresh.display-1");
        Assert.Equal(FindingStatus.Improvable, refresh.Status);
        Assert.Contains("RGB", refresh.Advice, StringComparison.Ordinal);
        Assert.Equal(FindingStatus.Ok, Get(findings, "M14.bit-depth.display-1").Status);
        Assert.Equal("activé", Get(findings, "M14.hdr.display-1").Current);
        Assert.Equal("ms-settings:display", Get(findings, "M14.hdr.display-1").SettingsPage);
    }

    [Fact]
    public async Task Hdr_with_chroma_subsampling_on_hdmi_is_a_warning_and_suggests_displayport()
    {
        var monitor = Monitor(refresh: 144.0, output: OutputTechnology.Hdmi, color: new AdvancedColorInfo(true, true, ColorEncoding.YCbCr422, 10, AdvancedColorMode.Hdr)) with
        {
            Width = 3840,
            Height = 2160,
            NativeWidth = 3840,
            NativeHeight = 2160,
            Modes = [new DisplayMode(3840, 2160, 60), new DisplayMode(3840, 2160, 120), new DisplayMode(3840, 2160, 144)],
        };

        var findings = await Detect(DesktopWithDedicatedGpu, monitor);

        var encoding = Get(findings, "M14.color-encoding.display-1");
        Assert.Equal(FindingStatus.Warning, encoding.Status);
        Assert.Contains("texte bave", encoding.Explanation, StringComparison.Ordinal);
        Assert.Contains("DisplayPort", encoding.Advice, StringComparison.Ordinal);
        Assert.True(encoding.Fixable);
        var connector = Get(findings, "M14.connector.display-1");
        Assert.Equal(FindingStatus.Info, connector.Status);
        Assert.Equal("HDMI", connector.Current);
        Assert.Contains("DisplayPort", connector.Advice, StringComparison.Ordinal);
        Assert.Equal(FindingStatus.Ok, Get(findings, "M14.refresh.display-1").Status);
    }

    [Fact]
    public async Task Eight_bits_in_hdr_is_informative()
    {
        var monitor = Monitor(refresh: 165, color: new AdvancedColorInfo(true, true, ColorEncoding.Rgb, 8, AdvancedColorMode.Hdr));

        var findings = await Detect(DesktopWithDedicatedGpu, monitor);

        var depth = Get(findings, "M14.bit-depth.display-1");
        Assert.Equal(FindingStatus.Info, depth.Status);
        Assert.Contains("banding", depth.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Monitor_on_the_motherboard_of_a_desktop_is_a_problem()
    {
        var monitor = Monitor(refresh: 60) with { AdapterDevicePath = IntelPath, AdapterName = "Intel(R) UHD Graphics 630" };

        var findings = await Detect(DesktopWithDedicatedGpu, monitor);

        var adapter = Get(findings, "M14.adapter.display-1");
        Assert.Equal(FindingStatus.Problem, adapter.Status);
        Assert.Equal(Severity.High, adapter.Severity);
        Assert.Contains("carte mère", adapter.Explanation, StringComparison.Ordinal);
        Assert.Contains("NVIDIA GeForce RTX 2080 Ti", adapter.Expected, StringComparison.Ordinal);
        Assert.Equal("M14.adapter.display-1", findings[0].Id);
    }

    [Fact]
    public async Task Adapter_is_matched_by_name_when_the_device_path_is_missing()
    {
        var monitor = Monitor(refresh: 60) with { AdapterDevicePath = null, AdapterName = "Intel(R) UHD Graphics 630" };

        var findings = await Detect(DesktopWithDedicatedGpu, monitor);

        Assert.Equal(FindingStatus.Problem, Get(findings, "M14.adapter.display-1").Status);
    }

    [Fact]
    public async Task Unmatched_adapter_is_unknown()
    {
        var monitor = Monitor(refresh: 60) with { AdapterDevicePath = null, AdapterName = "Carte inconnue" };

        var findings = await Detect(DesktopWithDedicatedGpu, monitor);

        Assert.Equal(FindingStatus.Unknown, Get(findings, "M14.adapter.display-1").Status);
    }

    [Fact]
    public async Task Laptop_internal_panel_on_integrated_gpu_is_normal()
    {
        var laptop = DesktopWithDedicatedGpu with { FormFactor = FormFactor.Laptop, HasBattery = true };
        var panel = Monitor(refresh: 60, output: OutputTechnology.Internal) with
        {
            MonitorName = string.Empty,
            AdapterDevicePath = IntelPath,
            Modes = [new DisplayMode(3440, 1440, 60), new DisplayMode(3440, 1440, 144)],
        };

        var findings = await Detect(laptop, panel);

        Assert.DoesNotContain(findings, f => f.Id.StartsWith("M14.adapter", StringComparison.Ordinal));
        var refresh = Get(findings, "M14.refresh.display-1");
        Assert.Contains("secteur", refresh.Advice, StringComparison.Ordinal);
        Assert.Equal("Écran 1 : écran intégré", refresh.Category);
        Assert.Equal("dalle intégrée", Get(findings, "M14.connector.display-1").Current);
    }

    [Fact]
    public async Task Unreadable_display_configuration_is_unknown()
    {
        var findings = await new DisplayModule(new FakeDisplays(null)).DetectAsync(TestContext.Create(), CancellationToken.None);

        Assert.Equal(FindingStatus.Unknown, Get(findings, "M14.displays").Status);
        Assert.DoesNotContain(findings, f => f.Status is FindingStatus.Problem or FindingStatus.Warning);
    }

    [Fact]
    public async Task No_active_display_is_unknown()
    {
        var findings = await Detect(DesktopWithDedicatedGpu);

        Assert.Equal(FindingStatus.Unknown, Get(findings, "M14.displays").Status);
    }

    [Fact]
    public async Task Missing_refresh_or_modes_are_unknown()
    {
        var noRefresh = Monitor(refresh: null);
        var noModes = Monitor(refresh: 60) with { Modes = [], NativeWidth = null, NativeHeight = null };

        var findings = await Detect(DesktopWithDedicatedGpu, noRefresh, noModes);

        Assert.Equal(FindingStatus.Unknown, Get(findings, "M14.refresh.display-1").Status);
        Assert.Equal(FindingStatus.Unknown, Get(findings, "M14.refresh.display-2").Status);
        Assert.Equal(FindingStatus.Unknown, Get(findings, "M14.hdr.display-1").Status);
        Assert.DoesNotContain(findings, f => f.Id == "M14.color-encoding.display-1");
    }

    [Fact]
    public async Task Inconsistent_mode_list_is_unknown_instead_of_a_false_alarm()
    {
        // Constaté le 29/09/2026 : liste générique jusqu'à 2560×1600 à 60 Hz et mode préféré 1024×768 pour un écran en 3440×1440 à 165 Hz.
        var monitor = Monitor(refresh: 164.9) with
        {
            NativeWidth = 1024,
            NativeHeight = 768,
            Modes = [new DisplayMode(1024, 768, 60), new DisplayMode(1920, 1080, 60), new DisplayMode(2560, 1600, 60)],
        };

        var findings = await Detect(DesktopWithDedicatedGpu, monitor);

        var refresh = Get(findings, "M14.refresh.display-1");
        Assert.Equal(FindingStatus.Unknown, refresh.Status);
        Assert.Contains("3440×1440", refresh.Explanation, StringComparison.Ordinal);
        Assert.Contains("redémarrez le PC", refresh.Advice, StringComparison.Ordinal);
        Assert.Equal("ms-settings:display", refresh.SettingsPage);
        Assert.Equal(FindingStatus.Unknown, Get(findings, "M14.resolution.display-1").Status);
    }

    [Fact]
    public async Task Resolution_above_the_preferred_mode_is_not_called_stretched()
    {
        // Résolution virtuelle (DSR) : le mode affiché dépasse le mode préféré, l'image n'est pas « étirée ».
        var monitor = Monitor(refresh: 165) with { Width = 5160, Height = 2160, Modes = [new DisplayMode(3440, 1440, 165), new DisplayMode(5160, 2160, 165)] };

        var findings = await Detect(DesktopWithDedicatedGpu, monitor);

        var resolution = Get(findings, "M14.resolution.display-1");
        Assert.Equal(FindingStatus.Unknown, resolution.Status);
        Assert.Contains("DSR", resolution.Explanation, StringComparison.Ordinal);
        Assert.Equal(FindingStatus.Ok, Get(findings, "M14.refresh.display-1").Status);
    }

    [Fact]
    public void Active_hdr_counts_as_supported_even_without_the_capability_bit()
    {
        var color = DisplayParsers.FromAdvancedColorInfo2(0, 0, 8, (int)AdvancedColorMode.Hdr);

        Assert.True(color.HdrSupported);
        Assert.True(color.HdrActive);
        Assert.False(DisplayParsers.FromAdvancedColorInfo2(0, 0, 8, (int)AdvancedColorMode.Sdr).HdrSupported);
    }

    [Fact]
    public async Task Non_native_resolution_is_informative()
    {
        var monitor = Monitor(refresh: 165) with { Width = 2560, Height = 1080, Modes = [new DisplayMode(2560, 1080, 165), new DisplayMode(3440, 1440, 165)] };

        var findings = await Detect(DesktopWithDedicatedGpu, monitor);

        var resolution = Get(findings, "M14.resolution.display-1");
        Assert.Equal(FindingStatus.Info, resolution.Status);
        Assert.Equal("2560×1080", resolution.Current);
        Assert.Equal("3440×1440", resolution.Expected);
        Assert.Equal("ms-settings:display", resolution.SettingsPage);
    }

    [Fact]
    public async Task Sixty_hertz_office_monitor_gets_no_variable_refresh_reminder()
    {
        var monitor = Monitor(refresh: 60) with { Modes = [new DisplayMode(1920, 1080, 60)], Width = 1920, Height = 1080, NativeWidth = 1920, NativeHeight = 1080 };

        var findings = await Detect(DesktopWithDedicatedGpu, monitor);

        Assert.DoesNotContain(findings, f => f.Id == "M14.vrr");
        Assert.Equal(FindingStatus.Ok, Get(findings, "M14.refresh.display-1").Status);
    }

    [Fact]
    public async Task Vga_connector_is_informative()
    {
        var monitor = Monitor(refresh: 60, output: OutputTechnology.Hd15) with { Modes = [new DisplayMode(3440, 1440, 60)] };

        var findings = await Detect(DesktopWithDedicatedGpu, monitor);

        var connector = Get(findings, "M14.connector.display-1");
        Assert.Equal(FindingStatus.Info, connector.Status);
        Assert.Contains("DisplayPort", connector.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Windows_graphics_settings_are_listed()
    {
        var registry = new FakeRegistry().Set(
            RegistryHive.CurrentUser,
            @"Software\Microsoft\DirectX\UserGpuPreferences",
            "DirectXUserGlobalSettings",
            "SwapEffectUpgradeEnable=1;AutoHDREnable=0;VRROptimizeEnable=1;");

        var findings = await new DisplayModule(new FakeDisplays([Monitor(refresh: 165)])).DetectAsync(TestContext.Create(registry, hardware: DesktopWithDedicatedGpu), CancellationToken.None);

        var settings = Get(findings, "M14.windows-graphics-settings");
        Assert.Equal(FindingStatus.Info, settings.Status);
        Assert.Equal("Auto HDR désactivé, optimisations des jeux en fenêtre activé, fréquence variable Windows activé", settings.Current);
        Assert.Equal("ms-settings:display-advancedgraphics", settings.SettingsPage);
    }

    [Fact]
    public async Task Denied_graphics_settings_are_unknown()
    {
        var registry = new FakeRegistry().Deny(RegistryHive.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences");

        var findings = await new DisplayModule(new FakeDisplays([Monitor(refresh: 165)])).DetectAsync(TestContext.Create(registry, hardware: DesktopWithDedicatedGpu), CancellationToken.None);

        Assert.Equal(FindingStatus.Unknown, Get(findings, "M14.windows-graphics-settings").Status);
    }

    [Fact]
    public void Public_constructor_exposes_module_identity()
    {
        var module = new DisplayModule();

        Assert.Equal("M14", module.Id);
        Assert.Equal("Écran : fréquence et HDR", module.Title);
        Assert.Equal(140, module.Order);
    }

    private static Task<IReadOnlyList<Finding>> Detect(HardwareProfile hardware, params DisplayPath[] paths) =>
        new DisplayModule(new FakeDisplays(paths)).DetectAsync(TestContext.Create(hardware: hardware), CancellationToken.None);

    private static Finding Get(IReadOnlyList<Finding> findings, string id) => findings.Single(f => f.Id == id);

    private static DisplayPath Monitor(double? refresh, OutputTechnology output = OutputTechnology.DisplayPortExternal, AdvancedColorInfo? color = null) => new()
    {
        MonitorName = "AW3423DWF",
        SourceName = @"\\.\DISPLAY1",
        AdapterDevicePath = NvidiaPath,
        AdapterName = "NVIDIA GeForce RTX 2080 Ti",
        Output = output,
        Width = 3440,
        Height = 1440,
        RefreshHz = refresh,
        NativeWidth = 3440,
        NativeHeight = 1440,
        Modes =
        [
            new DisplayMode(1920, 1080, 60),
            new DisplayMode(1920, 1080, 240),
            new DisplayMode(3440, 1440, 60),
            new DisplayMode(3440, 1440, 100),
            new DisplayMode(3440, 1440, 120),
            new DisplayMode(3440, 1440, 144),
            new DisplayMode(3440, 1440, 165),
        ],
        Color = color,
    };

    private sealed class FakeDisplays(IReadOnlyList<DisplayPath>? paths) : IDisplayConfigReader
    {
        public IReadOnlyList<DisplayPath>? ReadActivePaths() => paths;
    }
}
