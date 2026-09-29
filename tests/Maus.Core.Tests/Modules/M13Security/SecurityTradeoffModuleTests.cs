using Maus.Core.Modules.M13Security;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Tests.Modules.M13Security;

public class SecurityTradeoffModuleTests
{
    [Fact]
    public async Task Default_secure_windows_is_compliant_and_never_suggests_disabling_protections()
    {
        var cim = DeviceGuard(vbsStatus: 2, running: [2], configured: [2], available: [1, 2, 3, 5, 7]);

        var findings = await Run(cim: cim);

        Assert.Equal(FindingStatus.Ok, Get(findings, "M13.cpu-mitigations").Status);
        Assert.Equal("actives (réglage Windows par défaut)", Get(findings, "M13.cpu-mitigations").Current);
        Assert.Equal(FindingStatus.Ok, Get(findings, "M13.driver-blocklist").Status);
        Assert.Equal("active (réglage Windows par défaut)", Get(findings, "M13.driver-blocklist").Current);
        var hvci = Get(findings, "M13.memory-integrity");
        Assert.Equal(FindingStatus.Ok, hvci.Status);
        Assert.StartsWith("Recommandé : la laisser active.", hvci.Advice, StringComparison.Ordinal);
        Assert.Contains("Valorant et FACEIT", hvci.Explanation, StringComparison.Ordinal);
        Assert.Equal(FindingStatus.Ok, Get(findings, "M13.mbec").Status);
        Assert.Equal(FindingStatus.Ok, Get(findings, "M13.vbs").Status);
        Assert.Equal("en cours d'exécution (intégrité de la mémoire)", Get(findings, "M13.vbs").Current);
        Assert.DoesNotContain(findings, f => f.Status is FindingStatus.Problem or FindingStatus.Warning or FindingStatus.Improvable or FindingStatus.Unknown);
        Assert.DoesNotContain(findings, f => f.Fixable);
        Assert.All(findings, f => Assert.StartsWith("M13.", f.Id, StringComparison.Ordinal));

        // Intégrité de la mémoire active : aucun bouton qui mènerait vers l'endroit où on la coupe.
        Assert.All(findings, f => Assert.Null(f.SettingsPage));
    }

    [Fact]
    public async Task Disabled_cpu_mitigations_are_a_problem()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, SecurityTradeoffModule.MemoryManagementKey, "FeatureSettingsOverride", 3)
            .Set(RegistryHive.LocalMachine, SecurityTradeoffModule.MemoryManagementKey, "FeatureSettingsOverrideMask", 3);

        var findings = await Run(registry);

        var mitigations = Get(findings, "M13.cpu-mitigations");
        Assert.Equal(FindingStatus.Problem, mitigations.Status);
        Assert.Equal(Severity.High, mitigations.Severity);
        Assert.True(mitigations.Fixable);
        Assert.Contains("Spectre variante 2 et Meltdown", mitigations.Current, StringComparison.Ordinal);
        Assert.Contains("Supprimer", mitigations.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Strengthening_mitigation_values_are_compliant()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, SecurityTradeoffModule.MemoryManagementKey, "FeatureSettingsOverride", 72)
            .Set(RegistryHive.LocalMachine, SecurityTradeoffModule.MemoryManagementKey, "FeatureSettingsOverrideMask", 3);

        var findings = await Run(registry);

        Assert.Equal(FindingStatus.Ok, Get(findings, "M13.cpu-mitigations").Status);
        Assert.StartsWith("actives et renforcées", Get(findings, "M13.cpu-mitigations").Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disable_bits_without_mask_are_a_warning()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, SecurityTradeoffModule.MemoryManagementKey, "FeatureSettingsOverride", 1);

        var findings = await Run(registry);

        Assert.Equal(FindingStatus.Warning, Get(findings, "M13.cpu-mitigations").Status);
    }

    [Fact]
    public async Task Unreadable_registry_keys_are_admin_required_not_problems()
    {
        var registry = new FakeRegistry()
            .Deny(RegistryHive.LocalMachine, SecurityTradeoffModule.MemoryManagementKey)
            .Deny(RegistryHive.LocalMachine, SecurityTradeoffModule.CodeIntegrityConfigKey)
            .Deny(RegistryHive.LocalMachine, SecurityTradeoffModule.DeviceGuardKey)
            .Deny(RegistryHive.LocalMachine, $@"{SecurityTradeoffModule.ServicesKey}\vgc");

        var findings = await Run(registry, DeviceGuard(vbsStatus: 2, running: [2], configured: [2], available: [7]));

        Assert.Equal(FindingStatus.Unknown, Get(findings, "M13.cpu-mitigations").Status);
        Assert.Equal(FindingStatus.Unknown, Get(findings, "M13.driver-blocklist").Status);
        Assert.Equal(FindingStatus.Unknown, Get(findings, "M13.uefi-lock").Status);
        Assert.Equal(FindingStatus.Unknown, Get(findings, "M13.anti-cheat").Status);
        Assert.Equal(FindingStatus.Ok, Get(findings, "M13.memory-integrity").Status);
        Assert.DoesNotContain(findings, f => f.Status is FindingStatus.Problem or FindingStatus.Warning);
    }

    [Theory]
    [InlineData(0, FindingStatus.Problem)]
    [InlineData(1, FindingStatus.Ok)]
    public async Task Vulnerable_driver_blocklist_is_checked(int value, FindingStatus expected)
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, SecurityTradeoffModule.CodeIntegrityConfigKey, "VulnerableDriverBlocklistEnable", value);

        var findings = await Run(registry);

        var blocklist = Get(findings, "M13.driver-blocklist");
        Assert.Equal(expected, blocklist.Status);
        Assert.Equal(value == 0, blocklist.Fixable);
        Assert.Equal(value == 0, blocklist.Advice is not null);
        Assert.Equal(value == 0 ? "ms-settings:windowsdefender" : null, blocklist.SettingsPage);
    }

    [Fact]
    public async Task Device_guard_denied_is_admin_required()
    {
        var cim = new FakeCim()
            .Throw(SecurityTradeoffModule.DeviceGuardQuery, new MausAccessDeniedException("refusé"), CimScopes.DeviceGuard)
            .Throw(SecurityTradeoffModule.OptionalFeaturesQuery, new MausAccessDeniedException("refusé"));

        var findings = await Run(cim: cim);

        foreach (var id in new[] { "M13.memory-integrity", "M13.mbec", "M13.vbs", "M13.vbs-dependencies", "M13.hypervisor-features" })
        {
            var finding = Get(findings, id);
            Assert.Equal(FindingStatus.Unknown, finding.Status);
            Assert.Contains("administrateur", finding.Explanation, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Device_guard_unavailable_is_unknown()
    {
        var cim = new FakeCim()
            .Throw(SecurityTradeoffModule.DeviceGuardQuery, new DataSourceUnavailableException("absent"), CimScopes.DeviceGuard)
            .Throw(SecurityTradeoffModule.OptionalFeaturesQuery, new DataSourceUnavailableException("absent"))
            .Throw(SecurityTradeoffModule.HypervisorQuery, new DataSourceUnavailableException("absent"));

        var findings = await Run(cim: cim);

        Assert.Equal(FindingStatus.Unknown, Get(findings, "M13.memory-integrity").Status);
        Assert.DoesNotContain("administrateur", Get(findings, "M13.memory-integrity").Explanation, StringComparison.Ordinal);
        Assert.Equal(FindingStatus.Unknown, Get(findings, "M13.hypervisor-features").Status);
        Assert.Equal(FindingStatus.Unknown, Get(findings, "M13.vbs-dependencies").Status);
    }

    [Fact]
    public async Task Memory_integrity_requested_but_not_running_is_a_warning()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, SecurityTradeoffModule.MemoryIntegrityKey, "Enabled", 1);
        var cim = DeviceGuard(vbsStatus: 1, running: [0], configured: [0], available: [1, 2, 5, 7]);

        var findings = await Run(registry, cim);

        var hvci = Get(findings, "M13.memory-integrity");
        Assert.Equal(FindingStatus.Warning, hvci.Status);
        Assert.Equal(Severity.Medium, hvci.Severity);
        Assert.Contains("BIOS", hvci.Advice, StringComparison.Ordinal);
        Assert.Equal("ms-settings:windowsdefender", hvci.SettingsPage);
        Assert.Equal("activée, mais pas en cours d'exécution", Get(findings, "M13.vbs").Current);
        Assert.Equal(FindingStatus.Info, Get(findings, "M13.vbs").Status);
    }

    [Fact]
    public async Task Memory_integrity_off_is_information_only()
    {
        var cim = DeviceGuard(vbsStatus: 0, running: [0], configured: [0], available: [1, 2, 5, 7]);

        var findings = await Run(cim: cim);

        var hvci = Get(findings, "M13.memory-integrity");
        Assert.Equal(FindingStatus.Info, hvci.Status);
        Assert.Equal("désactivée", hvci.Current);
        Assert.Contains("activer", hvci.Advice, StringComparison.Ordinal);
        Assert.Equal("non activée", Get(findings, "M13.vbs").Current);
        Assert.False(hvci.Fixable);
        Assert.Equal("ms-settings:windowsdefender", hvci.SettingsPage);
    }

    [Fact]
    public async Task Anti_cheat_reinforces_the_advice_to_keep_memory_integrity()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, $@"{SecurityTradeoffModule.ServicesKey}\vgc", "Start", 3)
            .Set(RegistryHive.LocalMachine, $@"{SecurityTradeoffModule.ServicesKey}\vgk", "Start", 0)
            .Set(RegistryHive.LocalMachine, $@"{SecurityTradeoffModule.ServicesKey}\FACEIT", "Start", 3);
        var cim = DeviceGuard(vbsStatus: 2, running: [2], configured: [2], available: [7]);

        var findings = await Run(registry, cim);

        var antiCheat = Get(findings, "M13.anti-cheat");
        Assert.Equal(FindingStatus.Info, antiCheat.Status);
        Assert.Equal("Riot Vanguard, FACEIT", antiCheat.Current);
        Assert.Contains("Gardez l'intégrité de la mémoire active", antiCheat.Advice, StringComparison.Ordinal);
        Assert.Contains("Riot Vanguard et FACEIT sont installés", Get(findings, "M13.memory-integrity").Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anti_cheat_with_memory_integrity_off_suggests_turning_it_on()
    {
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, $@"{SecurityTradeoffModule.ServicesKey}\vgk", "Start", 0);
        var cim = DeviceGuard(vbsStatus: 0, running: [0], configured: [0], available: [1, 2, 5, 7]);

        var findings = await Run(registry, cim);

        Assert.Equal("Riot Vanguard", Get(findings, "M13.anti-cheat").Current);
        Assert.Contains("Riot Vanguard est installé : si un jeu protégé refuse de se lancer, activez-la.", Get(findings, "M13.memory-integrity").Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_anti_cheat_is_reported_as_such()
    {
        var findings = await Run();

        Assert.Equal("aucun détecté", Get(findings, "M13.anti-cheat").Current);
        Assert.Null(Get(findings, "M13.anti-cheat").Advice);
    }

    [Fact]
    public async Task Missing_mbec_with_memory_integrity_explains_the_higher_cost()
    {
        var cim = DeviceGuard(vbsStatus: 2, running: [2], configured: [2], available: [1, 2, 5]);

        var findings = await Run(cim: cim);

        var mbec = Get(findings, "M13.mbec");
        Assert.Equal(FindingStatus.Info, mbec.Status);
        Assert.Equal("absente (émulation logicielle)", mbec.Current);
        Assert.NotNull(mbec.Advice);
    }

    [Fact]
    public async Task Empty_security_properties_are_unknown_for_mbec()
    {
        var cim = DeviceGuard(vbsStatus: 0, running: [0], configured: [0], available: [0]);

        var findings = await Run(cim: cim);

        Assert.Equal(FindingStatus.Unknown, Get(findings, "M13.mbec").Status);
    }

    [Theory]
    [InlineData(SecurityTradeoffModule.MemoryIntegrityKey, "Locked")]
    [InlineData(SecurityTradeoffModule.DeviceGuardKey, "Locked")]
    [InlineData(SecurityTradeoffModule.DeviceGuardPolicyKey, "HypervisorEnforcedCodeIntegrity")]
    public async Task Uefi_lock_is_detected(string key, string value)
    {
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, key, value, 1);
        var cim = DeviceGuard(vbsStatus: 2, running: [2], configured: [2], available: [7]);

        var findings = await Run(registry, cim);

        var uefiLock = Get(findings, "M13.uefi-lock");
        Assert.Equal(FindingStatus.Info, uefiLock.Status);
        Assert.Equal("présent", uefiLock.Current);
        Assert.Contains("verrou UEFI", Get(findings, "M13.memory-integrity").Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Policy_imposed_memory_integrity_is_mentioned()
    {
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, SecurityTradeoffModule.DeviceGuardPolicyKey, "HypervisorEnforcedCodeIntegrity", 2);
        var cim = DeviceGuard(vbsStatus: 2, running: [2], configured: [2], available: [7]);

        var findings = await Run(registry, cim);

        Assert.Contains("stratégie", Get(findings, "M13.memory-integrity").Explanation, StringComparison.Ordinal);
        Assert.Equal("absent", Get(findings, "M13.uefi-lock").Current);
    }

    [Fact]
    public async Task Vbs_dependencies_and_hypervisor_features_are_listed()
    {
        var cim = DeviceGuard(vbsStatus: 2, running: [1, 2], configured: [1, 2], available: [7])
            .Answer(
                SecurityTradeoffModule.OptionalFeaturesQuery,
                Feature("Microsoft-Hyper-V-All", 1),
                Feature("VirtualMachinePlatform", 1),
                Feature("Containers-DisposableClientVM", 2),
                Feature("Recall", 1))
            .Answer(SecurityTradeoffModule.HypervisorQuery, new Dictionary<string, object?> { ["HypervisorPresent"] = true });

        var findings = await Run(cim: cim);

        Assert.Equal("Credential Guard (en cours), Recall (installé)", Get(findings, "M13.vbs-dependencies").Current);
        Assert.Equal("Hyper-V, Plateforme de machine virtuelle (WSL2) ; hyperviseur Windows chargé", Get(findings, "M13.hypervisor-features").Current);
        Assert.Equal("en cours d'exécution (Credential Guard, intégrité de la mémoire)", Get(findings, "M13.vbs").Current);
    }

    [Fact]
    public async Task No_hypervisor_feature_is_reported_as_such()
    {
        var cim = DeviceGuard(vbsStatus: 0, running: [0], configured: [0], available: [1, 2, 5, 7])
            .Answer(SecurityTradeoffModule.OptionalFeaturesQuery, Feature("Microsoft-Hyper-V-All", 2), Feature("VirtualMachinePlatform", 2))
            .Answer(SecurityTradeoffModule.HypervisorQuery, new Dictionary<string, object?> { ["HypervisorPresent"] = false });

        var findings = await Run(cim: cim);

        Assert.Equal("aucune ; hyperviseur Windows non chargé", Get(findings, "M13.hypervisor-features").Current);
        Assert.Equal("aucune détectée", Get(findings, "M13.vbs-dependencies").Current);
    }

    [Theory]
    [InlineData(null, null, "Default")]
    [InlineData(0L, 3L, "Default")]
    [InlineData(3L, 3L, "Disabled")]
    [InlineData(2L, 3L, "Disabled")]
    [InlineData(1L, 1L, "Disabled")]
    [InlineData(3L, null, "DisabledWithoutMask")]
    [InlineData(1L, 2L, "DisabledWithoutMask")]
    [InlineData(8L, 3L, "Strengthened")]
    [InlineData(72L, 3L, "Strengthened")]
    [InlineData(8264L, 3L, "Strengthened")]
    public void Mitigation_overrides_are_interpreted(long? overrideValue, long? mask, string expected) =>
        Assert.Equal(Enum.Parse<MitigationState>(expected), CpuMitigationOverrides.Evaluate(overrideValue, mask));

    [Fact]
    public void Registry_numbers_are_parsed_from_every_value_type()
    {
        Assert.Equal(3L, CpuMitigationOverrides.ToNumber(3));
        Assert.Equal(4294967295L, CpuMitigationOverrides.ToNumber(-1));
        Assert.Equal(8264L, CpuMitigationOverrides.ToNumber(8264L));
        Assert.Equal(72L, CpuMitigationOverrides.ToNumber(" 72 "));
        Assert.Equal(3L, CpuMitigationOverrides.ToNumber("0x3"));
        Assert.Null(CpuMitigationOverrides.ToNumber("trois"));
        Assert.Null(CpuMitigationOverrides.ToNumber(new byte[] { 3 }));
        Assert.Null(CpuMitigationOverrides.ToNumber(null));
        Assert.Equal("Meltdown", CpuMitigationOverrides.DescribeDisabled(2));
    }

    [Fact]
    public void Device_guard_row_is_parsed_from_wmi_arrays()
    {
        var row = new CimRow(new Dictionary<string, object?>
        {
            ["VirtualizationBasedSecurityStatus"] = 2u,
            ["SecurityServicesRunning"] = new uint[] { 1, 2 },
            ["SecurityServicesConfigured"] = new uint[] { 1, 2, 3 },
            ["AvailableSecurityProperties"] = new uint[] { 1, 2, 3, 5, 7 },
        });

        var state = DeviceGuardState.FromRow(row);

        Assert.True(state.IsVbsRunning);
        Assert.True(state.IsMemoryIntegrityRunning);
        Assert.True(state.IsCredentialGuardRunning);
        Assert.True(state.HasModeBasedExecutionControl);
        Assert.Null(DeviceGuardState.ServiceLabel(0));
        Assert.Equal("intégrité de la mémoire", DeviceGuardState.ServiceLabel(2));
    }

    [Fact]
    public void Module_identity_matches_the_specification()
    {
        var module = new SecurityTradeoffModule();

        Assert.Equal("M13", module.Id);
        Assert.Equal("Performance contre sécurité", module.Title);
        Assert.Equal(130, module.Order);
    }

    private static Task<IReadOnlyList<Finding>> Run(FakeRegistry? registry = null, FakeCim? cim = null) =>
        new SecurityTradeoffModule().DetectAsync(TestContext.Create(registry, cim), CancellationToken.None);

    private static Finding Get(IReadOnlyList<Finding> findings, string id) => findings.Single(f => f.Id == id);

    private static FakeCim DeviceGuard(long vbsStatus, uint[] running, uint[] configured, uint[] available) =>
        new FakeCim().Answer(
            SecurityTradeoffModule.DeviceGuardQuery,
            CimScopes.DeviceGuard,
            new Dictionary<string, object?>
            {
                ["VirtualizationBasedSecurityStatus"] = (uint)vbsStatus,
                ["SecurityServicesRunning"] = running,
                ["SecurityServicesConfigured"] = configured,
                ["AvailableSecurityProperties"] = available,
            });

    private static Dictionary<string, object?> Feature(string name, uint installState) => new()
    {
        ["Name"] = name,
        ["InstallState"] = installState,
    };
}
