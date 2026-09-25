using Maus.Core.Hardware;
using Maus.Core.Modules.M15Overclocking;
using Microsoft.Win32;

namespace Maus.Core.Tests.Modules.M15Overclocking;

public class OverclockingModuleTests
{
    private const string Uninstall = OverclockingModule.UninstallPath;
    private const string Uninstall32 = OverclockingModule.Wow64UninstallPath;

    private static readonly GpuInfo Rtx2080Ti = new("NVIDIA GeForce RTX 2080 Ti", HardwareVendor.Nvidia, "32.0.15.7314", null, @"PCI\VEN_10DE&DEV_1E07", false);
    private static readonly GpuInfo Rx7800Xt = new("AMD Radeon RX 7800 XT", HardwareVendor.Amd, "32.0.21001.9024", null, @"PCI\VEN_1002&DEV_747E", false);
    private static readonly GpuInfo Uhd630 = new("Intel(R) UHD Graphics 630", HardwareVendor.Intel, "31.0.101.2111", null, @"PCI\VEN_8086&DEV_3E92", true);

    [Fact]
    public void Module_metadata_follows_the_spec()
    {
        var module = new OverclockingModule();

        Assert.Equal("M15", module.Id);
        Assert.Equal("Overclocking et undervolting", module.Title);
        Assert.Equal(150, module.Order);
    }

    [Fact]
    public async Task Unlocked_intel_desktop_with_geforce_gets_xtu_and_afterburner()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, $@"{Uninstall32}\Afterburner", "DisplayName", "MSI Afterburner 4.6.7 Beta 2")
            .Set(RegistryHive.LocalMachine, $@"{Uninstall32}\Afterburner", "DisplayVersion", "4.6.7 Beta 2")
            .Set(RegistryHive.LocalMachine, $@"{Uninstall32}\Afterburner", "Publisher", "MSI Co., LTD")
            .Set(RegistryHive.LocalMachine, $@"{Uninstall}\{{B2FE}}_Display.NvApp", "DisplayName", "NVIDIA App 11.0.9.251")
            .Set(RegistryHive.LocalMachine, $@"{Uninstall}\{{B2FE}}_Display.NvApp", "DisplayVersion", "11.0.9.251")
            .Set(RegistryHive.LocalMachine, $@"{Uninstall}\{{B2FE}}_Display.NvApp.NvCPL", "DisplayName", "NVIDIA App driver settings")
            .Set(RegistryHive.LocalMachine, $@"{Uninstall}\HWiNFO® 64_is1", "DisplayName", "HWiNFO® 64")
            .Set(RegistryHive.LocalMachine, $@"{Uninstall}\HWiNFO® 64_is1", "DisplayVersion", "8.52");
        var hardware = Pc("Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz", HardwareVendor.Intel, gpus: [Rtx2080Ti, Uhd630]);

        var findings = await Detect(hardware, registry);

        Assert.Equal(5, findings.Count);
        Assert.All(findings, f => Assert.Equal(FindingStatus.Info, f.Status));
        Assert.All(findings, f => Assert.False(f.Fixable));
        Assert.Equal("M15.before-you-start", findings[0].Id);

        var cpu = findings.Single(f => f.Id == "M15.cpu");
        Assert.Equal("Processeur i7-8700K : Intel Extreme Tuning Utility (XTU)", cpu.Title);
        Assert.Equal("i7-8700K, coefficient débloqué", cpu.Current);
        Assert.Contains(OverclockingModule.XtuUrl, cpu.Advice, StringComparison.Ordinal);
        Assert.Contains("https://www.youtube.com/results?search_query=undervolt%20i7-8700K%20XTU", cpu.Advice, StringComparison.Ordinal);
        Assert.Contains("undervolting", cpu.Explanation, StringComparison.Ordinal);

        var gpu = findings.Single(f => f.Id == "M15.gpu-0");
        Assert.Equal("RTX 2080 Ti · installé : MSI Afterburner 4.6.7 Beta 2, NVIDIA App 11.0.9.251", gpu.Current);
        Assert.Contains("BleepingComputer, 23/11/2022", gpu.Explanation, StringComparison.Ordinal);
        Assert.Contains(OverclockingModule.AfterburnerUrl, gpu.Advice, StringComparison.Ordinal);
        Assert.Contains("https://www.youtube.com/results?search_query=undervolt%20RTX%202080%20Ti%20Afterburner", gpu.Advice, StringComparison.Ordinal);
        Assert.DoesNotContain(findings, f => f.Id == "M15.gpu-1");

        Assert.Equal("MSI Afterburner 4.6.7 Beta 2 (MSI Co., LTD), NVIDIA App 11.0.9.251", findings.Single(f => f.Id == "M15.tuning-tools").Current);
        Assert.Equal("installés : HWiNFO 8.52", findings.Single(f => f.Id == "M15.stability-tools").Current);
    }

    [Fact]
    public async Task Raptor_lake_desktop_is_warned_against_overclocking()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, $@"{Uninstall}\XTU", "DisplayName", "Intel(R) Extreme Tuning Utility")
            .Set(RegistryHive.LocalMachine, $@"{Uninstall}\XTU", "DisplayVersion", "7.14.2.93");
        var hardware = Pc("13th Gen Intel(R) Core(TM) i9-13900K", HardwareVendor.Intel, gpus: [Rtx2080Ti]);

        var findings = await Detect(hardware, registry);

        var cpu = findings[0];
        Assert.Equal("M15.cpu", cpu.Id);
        Assert.Equal(FindingStatus.Warning, cpu.Status);
        Assert.Equal(Severity.Medium, cpu.Severity);
        Assert.Equal("Processeur i9-13900K : pas d'overclocking", cpu.Title);
        Assert.Contains("0x12F", cpu.Expected, StringComparison.Ordinal);
        Assert.Contains("Module 8", cpu.Advice, StringComparison.Ordinal);
        Assert.Contains("Intel XTU 7.14.2.93 est installé", cpu.Advice, StringComparison.Ordinal);
        Assert.DoesNotContain(findings, f => f.Status == FindingStatus.Problem);
    }

    [Theory]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", "Ryzen 7 7800X3D", "7000X3D")]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", "Ryzen 7 5800X3D", "5000X3D")]
    [InlineData("AMD Ryzen 9 9950X3D 16-Core Processor", "Ryzen 9 9950X3D", "9000X3D")]
    [InlineData("AMD Ryzen 5 7600X 6-Core Processor", "Ryzen 5 7600X", null)]
    public async Task Ryzen_gets_ryzen_master_and_curve_optimizer(string name, string model, string? x3dNote)
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, $@"{Uninstall}\AMD Ryzen Master", "DisplayName", "AMD Ryzen Master")
            .Set(RegistryHive.LocalMachine, $@"{Uninstall}\AMD Ryzen Master", "DisplayVersion", "2.14.2.3341");
        var hardware = Pc(name, HardwareVendor.Amd, gpus: [Rx7800Xt]);

        var findings = await Detect(hardware, registry);

        var cpu = findings.Single(f => f.Id == "M15.cpu");
        Assert.Equal(FindingStatus.Info, cpu.Status);
        Assert.Equal($"{model} · AMD Ryzen Master 2.14.2.3341 installé", cpu.Current);
        Assert.Contains(OverclockingModule.RyzenMasterUrl, cpu.Advice, StringComparison.Ordinal);
        Assert.Contains(OverclockingParsers.TutorialUrl($"Curve Optimizer {model}"), cpu.Advice, StringComparison.Ordinal);
        if (x3dNote is null)
        {
            Assert.DoesNotContain("X3D", cpu.Explanation, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(x3dNote, cpu.Explanation, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Radeon_is_tuned_in_adrenalin()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, $@"{Uninstall}\AMD Catalyst Install Manager", "DisplayName", "AMD Software")
            .Set(RegistryHive.LocalMachine, $@"{Uninstall}\AMD Catalyst Install Manager", "DisplayVersion", "25.9.1");
        var hardware = Pc("AMD Ryzen 5 7600X 6-Core Processor", HardwareVendor.Amd, gpus: [Rx7800Xt]);

        var findings = await Detect(hardware, registry);

        var gpu = findings.Single(f => f.Id == "M15.gpu-0");
        Assert.Equal("Carte graphique RX 7800 XT : AMD Software Adrenalin", gpu.Title);
        Assert.Equal("RX 7800 XT · AMD Software: Adrenalin Edition 25.9.1 installé", gpu.Current);
        Assert.Contains("Réinitialiser", gpu.Explanation, StringComparison.Ordinal);
        Assert.Contains("search_query=undervolt%20RX%207800%20XT%20Adrenalin", gpu.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Intel_arc_is_tuned_in_intel_graphics_software()
    {
        var arc = new GpuInfo("Intel(R) Arc(TM) B580 Graphics", HardwareVendor.Intel, null, null, @"PCI\VEN_8086&DEV_E20B", false);
        var hardware = Pc("Intel(R) Core(TM) i5-12400F", HardwareVendor.Intel, gpus: [arc]);

        var findings = await Detect(hardware);

        var gpu = findings.Single(f => f.Id == "M15.gpu-0");
        Assert.Equal("Carte graphique Arc B580 : Intel Graphics Software", gpu.Title);
        Assert.Contains(OverclockingModule.IntelArcUrl, gpu.Advice, StringComparison.Ordinal);
        var cpu = findings.Single(f => f.Id == "M15.cpu");
        Assert.Equal("Processeur i5-12400F : coefficient verrouillé", cpu.Title);
        Assert.Null(cpu.Expected);
    }

    [Fact]
    public async Task Laptop_gets_the_manufacturer_utility_and_a_thermal_note()
    {
        var gpu = new GpuInfo("NVIDIA GeForce RTX 3060 Laptop GPU", HardwareVendor.Nvidia, "32.0.15.7314", null, @"PCI\VEN_10DE&DEV_2520", false);
        var hardware = Pc("12th Gen Intel(R) Core(TM) i7-12700H", HardwareVendor.Intel, FormFactor.Laptop, "ASUSTeK COMPUTER INC.", "ROG Strix G513QY", gpu);

        var findings = await Detect(hardware);

        var cpu = findings.Single(f => f.Id == "M15.cpu");
        Assert.Equal("Processeur i7-12700H (portable) : utilitaire du constructeur", cpu.Title);
        Assert.Contains("Armoury Crate", cpu.Advice, StringComparison.Ordinal);
        Assert.Contains("asus.com", cpu.Advice, StringComparison.Ordinal);
        Assert.Contains("search_query=ASUSTeK%20ROG%20Strix%20G513QY%20undervolt", cpu.Advice, StringComparison.Ordinal);
        Assert.Contains("marge thermique", cpu.Explanation, StringComparison.Ordinal);
        Assert.Contains("Sur un portable", findings.Single(f => f.Id == "M15.gpu-0").Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_laptop_brand_gets_generic_wording()
    {
        var hardware = Pc("AMD Ryzen 7 7840U w/ Radeon 780M Graphics", HardwareVendor.Amd, FormFactor.Laptop, "Contoso", "Book 14");

        var findings = await Detect(hardware);

        var cpu = findings.Single(f => f.Id == "M15.cpu");
        Assert.Contains("utilitaire fourni par le constructeur", cpu.Advice, StringComparison.Ordinal);
        Assert.Contains("search_query=Contoso%20Book%2014%20undervolt", cpu.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unidentified_hardware_is_unknown_not_a_problem()
    {
        var findings = await Detect(new HardwareProfile { FormFactor = FormFactor.Desktop });

        Assert.Equal(FindingStatus.Unknown, findings.Single(f => f.Id == "M15.cpu").Status);
        Assert.Equal(FindingStatus.Unknown, findings.Single(f => f.Id == "M15.gpu-0").Status);
        Assert.Equal("aucun", findings.Single(f => f.Id == "M15.tuning-tools").Current);
        Assert.Equal("aucun installé", findings.Single(f => f.Id == "M15.stability-tools").Current);
        Assert.DoesNotContain(findings, f => f.Status is FindingStatus.Problem or FindingStatus.Warning);
    }

    [Fact]
    public async Task Integrated_graphics_only_has_nothing_to_tune()
    {
        var hardware = Pc("Intel(R) Core(TM) i5-10400 CPU @ 2.90GHz", HardwareVendor.Intel, gpus: [Uhd630]);

        var findings = await Detect(hardware);

        var gpu = findings.Single(f => f.Id == "M15.gpu-0");
        Assert.Equal(FindingStatus.Info, gpu.Status);
        Assert.Equal("UHD Graphics 630", gpu.Current);
        Assert.StartsWith("Graphiques intégrés", gpu.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_cpu_vendor_has_no_tool()
    {
        var hardware = Pc("Snapdragon(R) X Elite - X1E78100 - Qualcomm(R) Oryon(TM) CPU", HardwareVendor.Other, FormFactor.Desktop);

        var findings = await Detect(hardware);

        Assert.Contains("pas d'outil de réglage connu", findings.Single(f => f.Id == "M15.cpu").Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Denied_uninstall_keys_make_tool_lists_unknown()
    {
        var registry = new FakeRegistry()
            .Deny(RegistryHive.LocalMachine, Uninstall)
            .Deny(RegistryHive.LocalMachine, Uninstall32)
            .Deny(RegistryHive.CurrentUser, Uninstall);
        var hardware = Pc("Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz", HardwareVendor.Intel, gpus: [Rtx2080Ti]);

        var findings = await Detect(hardware, registry);

        Assert.Equal(FindingStatus.Unknown, findings.Single(f => f.Id == "M15.tuning-tools").Status);
        Assert.Equal(FindingStatus.Unknown, findings.Single(f => f.Id == "M15.stability-tools").Status);
        Assert.Equal(FindingStatus.Info, findings.Single(f => f.Id == "M15.cpu").Status);
    }

    [Fact]
    public async Task Unreadable_program_key_is_skipped()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.CurrentUser, $@"{Uninstall}\Bad", "DisplayName", "MSI Afterburner")
            .Deny(RegistryHive.CurrentUser, $@"{Uninstall}\Bad")
            .Set(RegistryHive.CurrentUser, $@"{Uninstall}\OCCT", "DisplayName", "OCCT")
            .Set(RegistryHive.CurrentUser, $@"{Uninstall}\OCCT", "DisplayVersion", "14.1.4");
        var hardware = Pc("Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz", HardwareVendor.Intel, gpus: [Rtx2080Ti]);

        var findings = await Detect(hardware, registry);

        Assert.Equal("aucun", findings.Single(f => f.Id == "M15.tuning-tools").Current);
        Assert.Equal("installés : OCCT 14.1.4", findings.Single(f => f.Id == "M15.stability-tools").Current);
    }

    // --- Analyseurs ---

    [Theory]
    [InlineData("Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz", "i7-8700K")]
    [InlineData("13th Gen Intel(R) Core(TM) i9-13900KS", "i9-13900KS")]
    [InlineData("Intel(R) Core(TM) i9-10980XE CPU @ 3.00GHz", "i9-10980XE")]
    [InlineData("Intel(R) Core(TM) Ultra 9 285K", "Core Ultra 9 285K")]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", "Ryzen 7 7800X3D")]
    [InlineData("AMD Ryzen 7 PRO 5850U with Radeon Graphics", "Ryzen 7 PRO 5850U")]
    [InlineData("AMD Ryzen Threadripper 3970X 32-Core Processor", "Ryzen Threadripper 3970X")]
    [InlineData("AMD Athlon 3000G with Radeon Vega Graphics", "AMD Athlon 3000G")]
    [InlineData(null, "")]
    public void Cpu_model_is_shortened_for_tutorials(string? name, string expected)
    {
        Assert.Equal(expected, OverclockingParsers.CpuModel(name));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 2080 Ti", "RTX 2080 Ti")]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", "GTX 1660 SUPER")]
    [InlineData("AMD Radeon RX 7800 XT", "RX 7800 XT")]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", "Arc B580")]
    [InlineData("NVIDIA", "NVIDIA")]
    public void Gpu_model_is_shortened_for_tutorials(string name, string expected)
    {
        Assert.Equal(expected, OverclockingParsers.GpuModel(name));
    }

    [Theory]
    [InlineData("i7-8700K", true)]
    [InlineData("i5-13600KF", true)]
    [InlineData("i9-14900KS", true)]
    [InlineData("i9-10980XE", true)]
    [InlineData("Core Ultra 7 265K", true)]
    [InlineData("i5-12400F", false)]
    [InlineData("i7-12700H", false)]
    [InlineData("i5-10400", false)]
    public void Unlocked_intel_models_are_recognised(string model, bool expected)
    {
        Assert.Equal(expected, OverclockingParsers.IsUnlockedIntel(model));
    }

    [Theory]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", "Ryzen5000")]
    [InlineData("AMD Ryzen 5 5600X3D 6-Core Processor", "Ryzen5000")]
    [InlineData("AMD Ryzen 9 7950X3D 16-Core Processor", "Ryzen7000")]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor", "Ryzen9000")]
    [InlineData("AMD Ryzen 9 9950X3D2 16-Core Processor", "Ryzen9000")]
    [InlineData("AMD Ryzen 7 7700X 8-Core Processor", "None")]
    [InlineData(null, "None")]
    public void X3D_generation_is_read_from_the_model(string? name, string expected)
    {
        Assert.Equal(expected, OverclockingParsers.X3DGenerationOf(name).ToString());
    }

    [Theory]
    [InlineData("MSI Afterburner 4.6.7 Beta 2", "afterburner")]
    [InlineData("RivaTuner Statistics Server 7.3.6", "rtss")]
    [InlineData("AMD Ryzen Master", "ryzen-master")]
    [InlineData("Intel(R) Extreme Tuning Utility", "xtu")]
    [InlineData("NVIDIA App 11.0.9.251", "nvidia-app")]
    [InlineData("NVIDIA App", "nvidia-app")]
    [InlineData("AMD Software", "adrenalin")]
    [InlineData("Intel® Graphics Software", "intel-graphics")]
    [InlineData("Intel® Arc™ Control", "intel-graphics")]
    [InlineData("HWiNFO® 64", "hwinfo")]
    [InlineData("3DMark", "3dmark")]
    [InlineData("Maxon Cinebench 2024", "cinebench")]
    [InlineData("PassMark MemTest86", "memtest86")]
    [InlineData("NVIDIA App driver settings", null)]
    [InlineData("Intel® Driver & Support Assistant", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Installed_tools_are_recognised_by_display_name(string? displayName, string? expectedKey)
    {
        Assert.Equal(expectedKey, OverclockingParsers.MatchTool(displayName)?.Key);
    }

    [Fact]
    public void Tutorial_url_encodes_only_the_query()
    {
        Assert.Equal(
            "https://www.youtube.com/results?search_query=Curve%20Optimizer%20Ryzen%207%207800X3D",
            OverclockingParsers.TutorialUrl("Curve  Optimizer Ryzen 7 7800X3D "));
        Assert.Equal(
            "https://www.youtube.com/results?search_query=undervolt%20A%26B%3F%20x",
            OverclockingParsers.TutorialUrl("undervolt A&B? x"));
    }

    [Theory]
    [InlineData("ASUSTeK COMPUTER INC.", "Armoury Crate ou MyASUS")]
    [InlineData("LENOVO", "Lenovo Vantage")]
    [InlineData("HP", "OMEN Gaming Hub ou HP Command Center")]
    [InlineData("Alienware", "Alienware Command Center")]
    [InlineData("Micro-Star International Co., Ltd.", "MSI Center")]
    [InlineData("Acer", "NitroSense ou PredatorSense")]
    [InlineData("SHPX Industries", null)]
    [InlineData(null, null)]
    public void Laptop_utility_depends_on_the_manufacturer(string? manufacturer, string? expected)
    {
        Assert.Equal(expected, OverclockingParsers.LaptopUtility(manufacturer)?.Utility);
    }

    [Theory]
    [InlineData("ASUSTeK COMPUTER INC.", "ROG Strix G513QY", "ASUSTeK ROG Strix G513QY")]
    [InlineData("Dell Inc.", "Dell G15 5530", "Dell G15 5530")]
    [InlineData("", "Book 14", "Book 14")]
    [InlineData(null, null, "")]
    public void Pc_model_combines_brand_and_model(string? manufacturer, string? model, string expected)
    {
        Assert.Equal(expected, OverclockingParsers.PcModel(manufacturer, model));
    }

    // --- Outils de test ---

    private static async Task<IReadOnlyList<Finding>> Detect(HardwareProfile hardware, FakeRegistry? registry = null)
    {
        var context = TestContext.Create(registry: registry, hardware: hardware, elevated: false);
        return await new OverclockingModule().DetectAsync(context, CancellationToken.None);
    }

    private static HardwareProfile Pc(
        string cpuName,
        HardwareVendor vendor,
        FormFactor formFactor = FormFactor.Desktop,
        string manufacturer = "",
        string model = "",
        params GpuInfo[] gpus) => new()
    {
        FormFactor = formFactor,
        Manufacturer = manufacturer,
        Model = model,
        Cpu = new CpuInfo(cpuName, vendor, 8, 16, 3700),
        Gpus = gpus,
    };
}
