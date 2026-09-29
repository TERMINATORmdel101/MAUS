using System.Buffers.Binary;
using Maus.Core.Hardware;
using Maus.Core.Modules.M05Power;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Tests.Modules.M05Power;

public class PowerModuleTests
{
    private const string UltimateCopy = "c2b44dfa-c4f8-45f2-9568-379082a805d4";

    private const string FrenchList =
        "Modes de gestion de l’alimentation existants (* Actif)\r\n" +
        "-----------------------------------\r\n" +
        "GUID du mode de gestion de l’alimentation : 381b4222-f694-41f0-9685-ff5bb260df2e  (Utilisation normale)\r\n" +
        "GUID du mode de gestion de l’alimentation : 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (Haute performance)\r\n" +
        "GUID du mode de gestion de l’alimentation : a1841308-3541-4fab-bc81-f71556f20b4a  (Économie d'énergie)\r\n" +
        "GUID du mode de gestion de l’alimentation : c2b44dfa-c4f8-45f2-9568-379082a805d4  (Performances optimales) *\r\n";

    private const string EnglishBalancedOnlyList =
        "Existing Power Schemes (* Active)\n" +
        "-----------------------------------\n" +
        "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced) *\n";

    private static readonly HardwareProfile Desktop = new()
    {
        FormFactor = FormFactor.Desktop,
        Cpu = new CpuInfo("Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz", HardwareVendor.Intel, 6, 12, 3700),
    };

    private static readonly HardwareProfile X3DDesktop = new()
    {
        FormFactor = FormFactor.Desktop,
        Cpu = new CpuInfo("AMD Ryzen 9 7950X3D 16-Core Processor", HardwareVendor.Amd, 16, 32, 4201),
    };

    private static readonly HardwareProfile HybridDesktop = new()
    {
        FormFactor = FormFactor.Desktop,
        Cpu = new CpuInfo("13th Gen Intel(R) Core(TM) i7-13700K", HardwareVendor.Intel, 16, 24, 3400),
    };

    private static readonly HardwareProfile Laptop = new()
    {
        FormFactor = FormFactor.Laptop,
        HasBattery = true,
        Cpu = new CpuInfo("AMD Ryzen 7 5800H with Radeon Graphics", HardwareVendor.Amd, 8, 16, 3200),
    };

    [Fact]
    public async Task Desktop_on_high_performance_is_compliant()
    {
        var platform = new FakePowerPlatform { ActiveScheme = PowerSchemes.HighPerformance };

        var findings = await Run(platform);

        var plan = Get(findings, "M05.power-plan");
        Assert.Equal(FindingStatus.Ok, plan.Status);
        Assert.Equal("Haute performance", plan.Current);
        Assert.False(plan.Fixable);
        Assert.DoesNotContain(findings, f => f.Id is "M05.power-mode-ac" or "M05.power-mode-dc" or "M05.x3d-optimizer" or "M05.hybrid-cpu");
        Assert.DoesNotContain(findings, f => f.Status is FindingStatus.Problem or FindingStatus.Warning);
        Assert.All(findings, f => Assert.StartsWith("M05.", f.Id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Desktop_on_balanced_is_improvable_and_shows_the_power_mode()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, PowerModule.SchemesKey, "ActiveOverlayAcPowerScheme", "ded574b5-45a0-4f42-8737-46345c09c238");
        var platform = new FakePowerPlatform { ActiveScheme = PowerSchemes.Balanced };

        var findings = await Run(platform, registry: registry);

        var plan = Get(findings, "M05.power-plan");
        Assert.Equal(FindingStatus.Improvable, plan.Status);
        Assert.Equal(Severity.Low, plan.Severity);
        Assert.True(plan.Fixable);
        Assert.Contains("Meilleures performances", plan.Current, StringComparison.Ordinal);
        Assert.NotNull(plan.Advice);
        Assert.Null(plan.SettingsPage); // Le mode de gestion se choisit dans le Panneau de configuration, sans adresse ms-settings.
        Assert.DoesNotContain(findings, f => f.Id == "M05.power-mode-ac");
    }

    [Fact]
    public async Task Ultimate_performance_copy_is_recognised_from_its_registry_name()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, $@"{PowerModule.SchemesKey}\{UltimateCopy}", "FriendlyName", @"@%SystemRoot%\system32\powrprof.dll,-19,Ultimate Performance");
        var platform = new FakePowerPlatform { ActiveScheme = new Guid(UltimateCopy) };

        var findings = await Run(platform, registry: registry);

        var plan = Get(findings, "M05.power-plan");
        Assert.Equal(FindingStatus.Ok, plan.Status);
        Assert.Equal("Performances optimales", plan.Current);
        Assert.Contains("option experte", plan.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Active_scheme_falls_back_to_the_powercfg_list()
    {
        var commands = new FakeCommands().Answer("powercfg /list", FrenchList);

        var findings = await Run(new FakePowerPlatform(), commands: commands);

        var plan = Get(findings, "M05.power-plan");
        Assert.Equal(FindingStatus.Ok, plan.Status);
        Assert.Equal("Performances optimales", plan.Current);
    }

    [Fact]
    public async Task Missing_power_api_falls_back_to_the_registry_without_crashing()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, PowerModule.SchemesKey, "ActivePowerScheme", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        var platform = new FakePowerPlatform { Failure = new DllNotFoundException("powrprof.dll") };

        var findings = await Run(platform, registry: registry);

        Assert.Equal(FindingStatus.Ok, Get(findings, "M05.power-plan").Status);
        Assert.Equal(FindingStatus.Unknown, Get(findings, "M05.effective-mode").Status);
        Assert.Equal(FindingStatus.Unknown, Get(findings, "M05.modern-standby").Status);
    }

    [Fact]
    public async Task Unreadable_plan_is_unknown_not_a_problem()
    {
        var registry = new FakeRegistry().Deny(RegistryHive.LocalMachine, PowerModule.SchemesKey);

        var findings = await Run(new FakePowerPlatform(), registry: registry);

        Assert.Equal(FindingStatus.Unknown, Get(findings, "M05.power-plan").Status);
        Assert.DoesNotContain(findings, f => f.Status is FindingStatus.Problem or FindingStatus.Warning);
    }

    [Fact]
    public async Task Plan_imposed_by_policy_is_not_offered_as_a_fix()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, PowerModule.PowerPolicyKey, "ActivePowerScheme", "381b4222-f694-41f0-9685-ff5bb260df2e");
        var platform = new FakePowerPlatform { ActiveScheme = PowerSchemes.Balanced };

        var findings = await Run(platform, registry: registry);

        var plan = Get(findings, "M05.power-plan");
        Assert.Equal(FindingStatus.Improvable, plan.Status);
        Assert.False(plan.Fixable);
        Assert.Contains("stratégie", plan.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_form_factor_gives_no_recommendation()
    {
        var platform = new FakePowerPlatform { ActiveScheme = PowerSchemes.Balanced };

        var findings = await Run(platform, new HardwareProfile { FormFactor = FormFactor.Unknown });

        var plan = Get(findings, "M05.power-plan");
        Assert.Equal(FindingStatus.Info, plan.Status);
        Assert.Equal("Indéterminé", Get(findings, "M05.profile").Current);
    }

    [Fact]
    public async Task X3D_desktop_keeps_balanced_with_the_amd_service_running()
    {
        var cim = new FakeCim().Answer(PowerModule.X3DServiceQuery, new Dictionary<string, object?> { ["Name"] = "amd3dvcacheSvc", ["State"] = "Running" });
        var platform = new FakePowerPlatform { ActiveScheme = PowerSchemes.Balanced };

        var findings = await Run(platform, X3DDesktop, cim: cim);

        Assert.Equal(FindingStatus.Ok, Get(findings, "M05.power-plan").Status);
        Assert.Equal("Utilisation normale", Get(findings, "M05.power-plan").Expected);
        Assert.Equal(FindingStatus.Ok, Get(findings, "M05.x3d-optimizer").Status);
        Assert.Equal(FindingStatus.Info, Get(findings, "M05.power-mode-ac").Status);
        Assert.DoesNotContain(findings, f => f.Id == "M05.power-mode-dc");
        Assert.Equal("PC fixe, Ryzen X3D à deux CCD", Get(findings, "M05.profile").Current);
    }

    [Fact]
    public async Task X3D_desktop_on_high_performance_without_the_amd_service_is_improvable()
    {
        var platform = new FakePowerPlatform { ActiveScheme = PowerSchemes.HighPerformance };

        var findings = await Run(platform, X3DDesktop);

        var plan = Get(findings, "M05.power-plan");
        Assert.Equal(FindingStatus.Improvable, plan.Status);
        Assert.Contains("Vous pouvez garder", plan.Advice, StringComparison.Ordinal);
        var service = Get(findings, "M05.x3d-optimizer");
        Assert.Equal(FindingStatus.Improvable, service.Status);
        Assert.Equal("absent", service.Current);
    }

    [Theory]
    [InlineData("Stopped", FindingStatus.Improvable, "installé mais arrêté")]
    [InlineData("running", FindingStatus.Ok, "en cours d'exécution")]
    public async Task X3D_service_state_is_reported(string state, FindingStatus expected, string current)
    {
        var cim = new FakeCim().Answer(PowerModule.X3DServiceQuery, new Dictionary<string, object?> { ["Name"] = "amd3dvcacheSvc", ["State"] = state });

        var findings = await Run(new FakePowerPlatform { ActiveScheme = PowerSchemes.Balanced }, X3DDesktop, cim: cim);

        Assert.Equal(expected, Get(findings, "M05.x3d-optimizer").Status);
        Assert.Equal(current, Get(findings, "M05.x3d-optimizer").Current);
    }

    [Fact]
    public async Task X3D_service_query_denied_is_admin_required()
    {
        var cim = new FakeCim().Throw(PowerModule.X3DServiceQuery, new MausAccessDeniedException("refusé"));

        var findings = await Run(new FakePowerPlatform { ActiveScheme = PowerSchemes.Balanced }, X3DDesktop, cim: cim);

        Assert.Equal(FindingStatus.Unknown, Get(findings, "M05.x3d-optimizer").Status);
    }

    [Fact]
    public async Task Laptop_with_recommended_modes_is_compliant()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, PowerModule.SchemesKey, "ActiveOverlayAcPowerScheme", "ded574b5-45a0-4f42-8737-46345c09c238");
        var platform = new FakePowerPlatform { ActiveScheme = PowerSchemes.Balanced, Capabilities = FakePowerPlatform.Caps(aoAc: true, s3: false, batteries: true) };

        var findings = await Run(platform, Laptop, registry);

        Assert.Equal(FindingStatus.Ok, Get(findings, "M05.power-plan").Status);
        Assert.Equal(FindingStatus.Ok, Get(findings, "M05.power-mode-ac").Status);
        Assert.Null(Get(findings, "M05.power-mode-ac").SettingsPage);
        var dc = Get(findings, "M05.power-mode-dc");
        Assert.Equal(FindingStatus.Ok, dc.Status);
        Assert.Null(dc.SettingsPage);
        Assert.Equal("Équilibré (réglage par défaut)", dc.Current);
        Assert.Equal("prise en charge", Get(findings, "M05.modern-standby").Current);
        Assert.Equal("Portable", Get(findings, "M05.profile").Current);
        Assert.NotNull(Get(findings, "M05.profile").Advice);
    }

    [Fact]
    public async Task Laptop_default_and_battery_draining_modes_are_improvable()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, PowerModule.SchemesKey, "ActiveOverlayDcPowerScheme", "ded574b5-45a0-4f42-8737-46345c09c238");
        var platform = new FakePowerPlatform { ActiveScheme = PowerSchemes.Balanced };

        var findings = await Run(platform, Laptop, registry);

        var ac = Get(findings, "M05.power-mode-ac");
        Assert.Equal(FindingStatus.Improvable, ac.Status);
        Assert.True(ac.Fixable);
        Assert.Equal("Équilibré (réglage par défaut)", ac.Current);
        Assert.Equal("ms-settings:powersleep", ac.SettingsPage);
        var dc = Get(findings, "M05.power-mode-dc");
        Assert.Equal(FindingStatus.Improvable, dc.Status);
        Assert.Equal("Meilleures performances", dc.Current);
        Assert.Equal("ms-settings:powersleep", dc.SettingsPage);
    }

    [Fact]
    public async Task Laptop_battery_saving_mode_is_accepted()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, PowerModule.SchemesKey, "ActiveOverlayAcPowerScheme", "ded574b5-45a0-4f42-8737-46345c09c238")
            .Set(RegistryHive.LocalMachine, PowerModule.SchemesKey, "ActiveOverlayDcPowerScheme", "961cc777-2547-4f9d-8174-7d86181b8a7a");

        var findings = await Run(new FakePowerPlatform { ActiveScheme = PowerSchemes.Balanced }, Laptop, registry);

        Assert.Equal(FindingStatus.Ok, Get(findings, "M05.power-mode-dc").Status);
        Assert.Equal("Meilleure efficacité énergétique", Get(findings, "M05.power-mode-dc").Current);
    }

    [Fact]
    public async Task Laptop_on_high_performance_is_improvable_and_modes_are_not_evaluated()
    {
        var findings = await Run(new FakePowerPlatform { ActiveScheme = PowerSchemes.HighPerformance }, Laptop);

        Assert.Equal(FindingStatus.Improvable, Get(findings, "M05.power-plan").Status);
        Assert.DoesNotContain(findings, f => f.Id is "M05.power-mode-ac" or "M05.power-mode-dc");
    }

    [Fact]
    public async Task Unreadable_overlays_are_admin_required()
    {
        var registry = new FakeRegistry().Deny(RegistryHive.LocalMachine, PowerModule.SchemesKey);

        var findings = await Run(new FakePowerPlatform { ActiveScheme = PowerSchemes.Balanced }, Laptop, registry);

        Assert.Equal(FindingStatus.Unknown, Get(findings, "M05.power-mode-ac").Status);
        Assert.Equal(FindingStatus.Unknown, Get(findings, "M05.power-mode-dc").Status);
    }

    [Fact]
    public async Task Garbled_overlay_value_is_unknown()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, PowerModule.SchemesKey, "ActiveOverlayAcPowerScheme", "pas un GUID");

        var findings = await Run(new FakePowerPlatform { ActiveScheme = PowerSchemes.Balanced }, Laptop, registry);

        Assert.Equal(FindingStatus.Unknown, Get(findings, "M05.power-mode-ac").Status);
        Assert.Equal(FindingStatus.Ok, Get(findings, "M05.power-mode-dc").Status);
    }

    [Fact]
    public async Task Modern_standby_desktop_without_high_performance_accepts_balanced()
    {
        var commands = new FakeCommands().Answer("powercfg /list", EnglishBalancedOnlyList);
        var platform = new FakePowerPlatform { Capabilities = FakePowerPlatform.Caps(aoAc: true, s3: false) };

        var findings = await Run(platform, commands: commands);

        Assert.Equal(FindingStatus.Ok, Get(findings, "M05.power-plan").Status);
        Assert.Equal("Balanced", Get(findings, "M05.power-plan").Current);
        Assert.Equal(FindingStatus.Improvable, Get(findings, "M05.power-mode-ac").Status);
        Assert.Equal("PC fixe en veille moderne", Get(findings, "M05.profile").Current);
    }

    [Fact]
    public async Task Intel_hybrid_desktop_is_described_and_balanced_is_accepted()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, PowerModule.SchemesKey, "ActiveOverlayAcPowerScheme", "ded574b5-45a0-4f42-8737-46345c09c238");
        var platform = new FakePowerPlatform { ActiveScheme = PowerSchemes.Balanced, EfficiencyClasses = 2 };

        var findings = await Run(platform, HybridDesktop, registry);

        Assert.Equal(FindingStatus.Ok, Get(findings, "M05.power-plan").Status);
        Assert.Equal(FindingStatus.Ok, Get(findings, "M05.power-mode-ac").Status);
        Assert.Equal(FindingStatus.Info, Get(findings, "M05.hybrid-cpu").Status);
        Assert.Equal("PC fixe, processeur Intel hybride", Get(findings, "M05.profile").Current);
    }

    [Fact]
    public async Task Fast_startup_with_hibernation_is_improvable()
    {
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, PowerModule.SessionPowerKey, "HiberbootEnabled", 1);
        var platform = new FakePowerPlatform { ActiveScheme = PowerSchemes.HighPerformance, Capabilities = FakePowerPlatform.Caps(hiberFile: true) };

        var findings = await Run(platform, registry: registry);

        var fastStartup = Get(findings, "M05.fast-startup");
        Assert.Equal(FindingStatus.Improvable, fastStartup.Status);
        Assert.True(fastStartup.Fixable);
        Assert.Equal("activé", fastStartup.Current);
        Assert.Contains("plus lent", fastStartup.Advice, StringComparison.Ordinal);
        Assert.Null(fastStartup.SettingsPage);
    }

    [Fact]
    public async Task Fast_startup_without_hibernation_has_no_effect()
    {
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, PowerModule.SessionPowerKey, "HiberbootEnabled", 1);

        var findings = await Run(new FakePowerPlatform { ActiveScheme = PowerSchemes.HighPerformance }, registry: registry);

        var fastStartup = Get(findings, "M05.fast-startup");
        Assert.Equal(FindingStatus.Ok, fastStartup.Status);
        Assert.Contains("sans effet", fastStartup.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fast_startup_uses_the_registry_when_capabilities_are_missing()
    {
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, PowerModule.PowerKey, "HibernateEnabled", 1);
        var platform = new FakePowerPlatform { ActiveScheme = PowerSchemes.HighPerformance, Capabilities = null };

        var findings = await Run(platform, registry: registry);

        var fastStartup = Get(findings, "M05.fast-startup");
        Assert.Equal(FindingStatus.Improvable, fastStartup.Status);
        Assert.Equal("activé (réglage Windows par défaut)", fastStartup.Current);
    }

    [Fact]
    public async Task Fast_startup_disabled_is_compliant_and_denied_is_unknown()
    {
        var disabled = new FakeRegistry().Set(RegistryHive.LocalMachine, PowerModule.SessionPowerKey, "HiberbootEnabled", 0);
        var denied = new FakeRegistry().Deny(RegistryHive.LocalMachine, PowerModule.SessionPowerKey);
        var platform = new FakePowerPlatform { ActiveScheme = PowerSchemes.HighPerformance, Capabilities = FakePowerPlatform.Caps(hiberFile: true) };

        Assert.Equal(FindingStatus.Ok, Get(await Run(platform, registry: disabled), "M05.fast-startup").Status);
        Assert.Equal(FindingStatus.Unknown, Get(await Run(platform, registry: denied), "M05.fast-startup").Status);
    }

    [Fact]
    public async Task Oem_utilities_are_found_by_package_service_or_installed_program()
    {
        var packages = new FakePackages().Add("B9ECED6F.ArmouryCrate");
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, $@"{OemPowerUtilities.ServicesKey}\AWCCService", "Start", 2)
            .Set(RegistryHive.LocalMachine, $@"{OemPowerUtilities.UninstallKey}\{{0F4F0C8A-0000-4E1B-A1F5-000000000000}}", "DisplayName", "OMEN Gaming Hub");

        var findings = await Run(new FakePowerPlatform { ActiveScheme = PowerSchemes.HighPerformance }, registry: registry, packages: packages);

        var oem = Get(findings, "M05.oem-utility");
        Assert.Equal(FindingStatus.Info, oem.Status);
        Assert.Equal("Armoury Crate (ASUS), Alienware Command Center (Dell), OMEN Gaming Hub (HP)", oem.Current);
        Assert.NotNull(oem.Advice);
    }

    [Fact]
    public async Task No_oem_utility_is_reported_as_such()
    {
        var registry = new FakeRegistry().Deny(RegistryHive.LocalMachine, OemPowerUtilities.UninstallKey);

        var findings = await Run(new FakePowerPlatform { ActiveScheme = PowerSchemes.HighPerformance }, registry: registry);

        Assert.Equal("aucun détecté", Get(findings, "M05.oem-utility").Current);
        Assert.Null(Get(findings, "M05.oem-utility").Advice);
    }

    [Theory]
    [InlineData((int)EffectivePowerMode.GameMode, "Mode Jeu (un jeu est au premier plan)")]
    [InlineData((int)EffectivePowerMode.MaxPerformance, "Performances maximales")]
    [InlineData((int)EffectivePowerMode.BetterBattery, "Meilleure efficacité énergétique")]
    public async Task Effective_mode_is_described(int mode, string expected)
    {
        var findings = await Run(new FakePowerPlatform { ActiveScheme = PowerSchemes.HighPerformance, EffectiveMode = (EffectivePowerMode)mode });

        Assert.Equal(expected, Get(findings, "M05.effective-mode").Current);
        Assert.Equal(FindingStatus.Info, Get(findings, "M05.effective-mode").Status);
    }

    [Fact]
    public async Task Ups_battery_on_a_desktop_is_explained()
    {
        var hardware = Desktop with { HasBattery = true };
        var platform = new FakePowerPlatform { ActiveScheme = PowerSchemes.HighPerformance, Capabilities = FakePowerPlatform.Caps(ups: true) };

        var findings = await Run(platform, hardware);

        Assert.Contains("onduleur", Get(findings, "M05.profile").Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Scheme_list_is_parsed_in_french_and_english()
    {
        var french = PowerSchemes.ParseList(FrenchList);
        Assert.Equal(4, french.Count);
        Assert.Equal("Économie d'énergie", french[2].Name);
        Assert.Equal(new Guid(UltimateCopy), french.Single(s => s.IsActive).Guid);
        Assert.Equal("Performances optimales", french[3].Name);

        var english = PowerSchemes.ParseList(EnglishBalancedOnlyList);
        Assert.Equal(new ListedScheme(PowerSchemes.Balanced, "Balanced", true), Assert.Single(english));

        Assert.Empty(PowerSchemes.ParseList("Erreur : accès refusé"));
    }

    [Theory]
    [InlineData("381b4222-f694-41f0-9685-ff5bb260df2e", null, null, "Balanced")]
    [InlineData("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", null, null, "HighPerformance")]
    [InlineData("a1841308-3541-4fab-bc81-f71556f20b4a", null, null, "PowerSaver")]
    [InlineData("e9a42b02-d5df-448d-aa00-03f14749eb61", null, null, "UltimatePerformance")]
    [InlineData(UltimateCopy, @"@%SystemRoot%\system32\powrprof.dll,-19,Ultimate Performance", "Mon mode", "UltimatePerformance")]
    [InlineData(UltimateCopy, @"@C:\WINDOWS\system32\powrprof.dll,-13,High performance", null, "HighPerformance")]
    [InlineData(UltimateCopy, @"@%SystemRoot%\system32\powrprof.dll,-15,Balanced", null, "Balanced")]
    [InlineData(UltimateCopy, null, "Haute performance", "HighPerformance")]
    [InlineData(UltimateCopy, null, "Performances optimales", "UltimatePerformance")]
    [InlineData(UltimateCopy, "Mon mode jeu", null, "Other")]
    [InlineData(UltimateCopy, null, null, "Other")]
    public void Schemes_are_classified(string scheme, string? registryName, string? displayName, string expected) =>
        Assert.Equal(Enum.Parse<SchemeKind>(expected), PowerSchemes.Classify(new Guid(scheme), registryName, displayName));

    [Fact]
    public void Overlay_labels_match_windows_settings()
    {
        Assert.Equal("Équilibré", PowerSchemes.OverlayLabel(Guid.Empty));
        Assert.Equal("Meilleures performances", PowerSchemes.OverlayLabel(PowerSchemes.OverlayBestPerformance));
        Assert.Equal("Meilleure efficacité énergétique", PowerSchemes.OverlayLabel(PowerSchemes.OverlayBestEfficiency));
        Assert.StartsWith("mode inconnu", PowerSchemes.OverlayLabel(PowerSchemes.HighPerformance), StringComparison.Ordinal);
    }

    [Fact]
    public void Power_capabilities_are_read_at_their_native_offsets()
    {
        var buffer = new byte[PowerCapabilities.NativeSize];
        buffer[5] = 1;
        buffer[8] = 1;
        buffer[20] = 1;
        buffer[30] = 1;

        var capabilities = PowerCapabilities.Parse(buffer);

        Assert.Equal(new PowerCapabilities(false, true, false, true, false, true, true, false), capabilities);
        Assert.Null(PowerCapabilities.Parse(new byte[16]));
    }

    [Fact]
    public void Efficiency_classes_are_counted_per_core()
    {
        Assert.Equal(1, ProcessorTopology.CountEfficiencyClasses(Topology((0, 0), (0, 0), (0, 0))));
        Assert.Equal(2, ProcessorTopology.CountEfficiencyClasses(Topology((0, 1), (0, 1), (0, 0))));

        // Une autre relation (cache, par exemple) n'est pas un cœur et n'est pas comptée.
        Assert.Equal(1, ProcessorTopology.CountEfficiencyClasses(Topology((0, 0), (2, 5))));
        Assert.Equal(0, ProcessorTopology.CountEfficiencyClasses([]));
    }

    [Fact]
    public void Profile_follows_form_factor_and_cpu()
    {
        var listed = PowerSchemes.ParseList(FrenchList);
        var modernStandby = FakePowerPlatform.Caps(aoAc: true);

        Assert.Equal(PowerModule.PowerProfile.Laptop, PowerModule.DetermineProfile(Laptop, false, null, null));
        Assert.Equal(PowerModule.PowerProfile.DesktopX3D, PowerModule.DetermineProfile(X3DDesktop, false, null, null));
        Assert.Equal(PowerModule.PowerProfile.DesktopHybrid, PowerModule.DetermineProfile(HybridDesktop, true, null, null));
        Assert.Equal(PowerModule.PowerProfile.Desktop, PowerModule.DetermineProfile(Desktop, false, modernStandby, listed));
        Assert.Equal(PowerModule.PowerProfile.Desktop, PowerModule.DetermineProfile(Desktop, false, modernStandby, null));
        Assert.Equal(PowerModule.PowerProfile.Unknown, PowerModule.DetermineProfile(new HardwareProfile(), false, null, null));
    }

    [Fact]
    public void Module_identity_matches_the_specification()
    {
        var module = new PowerModule(new FakePowerPlatform());

        Assert.Equal("M05", module.Id);
        Assert.Equal("Alimentation", module.Title);
        Assert.Equal(50, module.Order);
    }

    private static Task<IReadOnlyList<Finding>> Run(
        FakePowerPlatform platform,
        HardwareProfile? hardware = null,
        FakeRegistry? registry = null,
        FakeCommands? commands = null,
        FakeCim? cim = null,
        FakePackages? packages = null) =>
        new PowerModule(platform).DetectAsync(
            TestContext.Create(registry, cim, commands, hardware: hardware ?? Desktop, packages: packages),
            CancellationToken.None);

    private static Finding Get(IReadOnlyList<Finding> findings, string id) => findings.Single(f => f.Id == id);

    /// <summary>Tampon SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX : entrées de 48 octets (relation, classe d'efficacité).</summary>
    private static byte[] Topology(params (int Relationship, byte EfficiencyClass)[] entries)
    {
        const int size = 48;
        var buffer = new byte[entries.Length * size];
        for (var i = 0; i < entries.Length; i++)
        {
            var offset = i * size;
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), entries[i].Relationship);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 4), size);
            buffer[offset + 9] = entries[i].EfficiencyClass;
        }

        return buffer;
    }
}
