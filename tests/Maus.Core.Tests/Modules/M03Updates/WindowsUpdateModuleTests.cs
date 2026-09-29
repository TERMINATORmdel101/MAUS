using System.Runtime.InteropServices;
using Maus.Core.Hardware;
using Maus.Core.Modules.M03Updates;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Tests.Modules.M03Updates;

public class WindowsUpdateModuleTests
{
    private const string SecurityCategory = "0FA1201D-4330-4FA8-8AE9-B877473B6441";
    private const string CriticalCategory = "E6CF1350-C01B-414D-A61F-263D14D133B4";
    private const string DefinitionCategory = "E0789628-CE08-4437-BE74-2495B842F43B";
    private const string DriverCategory = "EBFC1FC5-71A4-4F7B-9ACA-3B9A503104A0";

    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.FromHours(2));

    private static readonly WindowsInfo Pro25H2 = new("Windows 11 Pro", "Professional", "25H2", 26200, 9550);

    /// <summary>PC à jour : correctif installé il y a 10 jours, Defender actif avec des définitions du jour.</summary>
    private static FakeCim HealthyCim() => new FakeCim()
        .Answer(
            WindowsUpdateModule.QfeQuery,
            new Dictionary<string, object?> { ["HotFixID"] = "KB5124008", ["InstalledOn"] = "9/14/2026" },
            new Dictionary<string, object?> { ["HotFixID"] = "KB5000001", ["InstalledOn"] = "8/12/2026" },
            new Dictionary<string, object?> { ["HotFixID"] = "KB5000002", ["InstalledOn"] = null })
        .Answer(
            WindowsUpdateModule.DefenderQuery,
            CimScopes.Defender,
            new Dictionary<string, object?>
            {
                ["AMRunningMode"] = "Normal",
                ["AntivirusEnabled"] = true,
                ["AntivirusSignatureAge"] = 0u,
                ["AntivirusSignatureVersion"] = "1.459.388.0",
                ["AntivirusSignatureLastUpdated"] = new DateTime(2026, 9, 24, 8, 0, 0),
            })
        .Answer(
            WindowsUpdateModule.AntivirusQuery,
            CimScopes.SecurityCenter2,
            new Dictionary<string, object?> { ["displayName"] = "Windows Defender", ["productState"] = 397568u });

    private static FakeRegistry HealthyRegistry()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, WindowsUpdateModule.UxSettingsKey, "ActiveHoursStart", 8);
        foreach (var (name, _) in WindowsUpdateModule.UpdateServices)
        {
            registry.Set(RegistryHive.LocalMachine, $@"{WindowsUpdateModule.ServicesKey}\{name}", "Start", 3);
        }

        return registry;
    }

    private static FakeUpdateAgent HealthyAgent() => new()
    {
        Results = new AutomaticUpdatesResults(new DateTime(2026, 9, 24, 6, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 23, 20, 0, 0, DateTimeKind.Utc)),
    };

    private static Task<IReadOnlyList<Finding>> Detect(
        FakeUpdateAgent? agent = null,
        FakeRegistry? registry = null,
        FakeCim? cim = null,
        WindowsInfo? windows = null,
        DateTimeOffset? now = null,
        HardwareProfile? hardware = null) =>
        new WindowsUpdateModule(agent ?? HealthyAgent(), TimeSpan.FromSeconds(5)).DetectAsync(
            TestContext.Create(registry ?? HealthyRegistry(), cim ?? HealthyCim(), windows: windows ?? Pro25H2, now: now ?? Now, hardware: hardware, elevated: false),
            CancellationToken.None);

    private static Finding Get(IReadOnlyList<Finding> findings, string id) => findings.Single(f => f.Id == id);

    private static PendingUpdate Update(string title, string category, bool browseOnly = false, string? msrc = null, bool isDriver = false) =>
        new(Guid.NewGuid().ToString(), title, [], ["{" + category + "}"], msrc, browseOnly, isDriver);

    [Fact]
    public async Task Up_to_date_pc_has_no_deviation()
    {
        var agent = HealthyAgent();
        var findings = await Detect(agent);

        Assert.All(findings, f => Assert.StartsWith("M03.", f.Id, StringComparison.Ordinal));
        Assert.Equal(findings.Count, findings.Select(f => f.Id).Distinct().Count());
        Assert.DoesNotContain(findings, f => f.Status is FindingStatus.Problem or FindingStatus.Warning or FindingStatus.Improvable or FindingStatus.Unknown);
        Assert.Equal("M03.windows-support", findings[0].Id);
        Assert.Contains("25H2", Get(findings, "M03.windows-support").Current, StringComparison.Ordinal);
        Assert.Contains("12/10/2027", Get(findings, "M03.windows-support").Current, StringComparison.Ordinal);
        Assert.Equal("aucune", Get(findings, "M03.pending-updates").Current);
        Assert.Equal("aucune", Get(findings, "M03.driver-updates").Current);
        Assert.Null(Get(findings, "M03.driver-updates").SettingsPage);
        Assert.Contains("KB5124008", Get(findings, "M03.last-install").Current, StringComparison.Ordinal);
        Assert.Contains("il y a 10 jours", Get(findings, "M03.last-install").Current, StringComparison.Ordinal);
        Assert.Equal(FindingStatus.Ok, Get(findings, "M03.defender-signatures").Status);
        Assert.Equal("Windows Update (serveurs Microsoft)", Get(findings, "M03.update-source").Current);
        Assert.Equal("désactivée", Get(findings, "M03.latest-updates-toggle").Current);
    }

    [Fact]
    public async Task Search_includes_optional_updates_and_drivers_in_one_query()
    {
        var agent = HealthyAgent();
        await Detect(agent);

        Assert.Equal(WindowsUpdateModule.SearchCriteria, agent.ReceivedCriteria);
        Assert.Contains("Type='Software'", agent.ReceivedCriteria, StringComparison.Ordinal);
        Assert.Contains("BrowseOnly=1", agent.ReceivedCriteria, StringComparison.Ordinal);
        Assert.Contains(" or IsInstalled=0 and IsHidden=0 and Type='Driver'", agent.ReceivedCriteria, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(180), new WindowsUpdateModule(agent, TimeSpan.FromSeconds(1)).Timeout);
    }

    [Fact]
    public void Module_identity_and_public_constructor()
    {
        var module = new WindowsUpdateModule();

        Assert.Equal("M03", module.Id);
        Assert.Equal("Mises à jour Windows", module.Title);
        Assert.Equal(30, module.Order);
    }

    [Fact]
    public async Task Home_24H2_close_to_end_of_service_suggests_enablement_package()
    {
        var windows = new WindowsInfo("Windows 11 Famille", "Core", "24H2", 26100, 6000);

        var finding = Get(await Detect(windows: windows), "M03.windows-support");

        Assert.Equal(FindingStatus.Warning, finding.Status);
        Assert.Equal(Severity.Medium, finding.Severity);
        Assert.Contains("13/10/2026", finding.Current, StringComparison.Ordinal);
        Assert.Contains("19 jours", finding.Current, StringComparison.Ordinal);
        Assert.Contains("13 octobre 2026", finding.Advice, StringComparison.Ordinal);
        Assert.Contains("KB5054156", finding.Advice, StringComparison.Ordinal);
        Assert.DoesNotContain("révision", finding.Advice, StringComparison.Ordinal);
        Assert.True(finding.Fixable);
    }

    [Fact]
    public async Task Enablement_package_advice_asks_for_prerequisite_revision()
    {
        var windows = new WindowsInfo("Windows 11 Pro", "Professional", "24H2", 26100, 4000);

        var finding = Get(await Detect(windows: windows), "M03.windows-support");

        Assert.Contains("26100.5074", finding.Advice, StringComparison.Ordinal);
        Assert.Contains("26100.4000", finding.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Home_24H2_after_end_of_service_is_a_problem()
    {
        var windows = new WindowsInfo("Windows 11 Pro", "Professional", "24H2", 26100, 6000);

        var finding = Get(await Detect(windows: windows, now: new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(2))), "M03.windows-support");

        Assert.Equal(FindingStatus.Problem, finding.Status);
        Assert.Contains("depuis le 13/10/2026", finding.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enterprise_24H2_keeps_its_longer_support()
    {
        var windows = new WindowsInfo("Windows 11 Entreprise", "Enterprise", "24H2", 26100, 6000);

        var finding = Get(await Detect(windows: windows), "M03.windows-support");

        Assert.Equal(FindingStatus.Ok, finding.Status);
        Assert.Contains("Entreprise et Éducation", finding.Current, StringComparison.Ordinal);
        Assert.Contains("12/10/2027", finding.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ltsc_uses_its_own_end_of_service()
    {
        var windows = new WindowsInfo("Windows 11 Entreprise LTSC", "EnterpriseS", "24H2", 26100, 6000);

        var finding = Get(await Detect(windows: windows), "M03.windows-support");

        Assert.Equal(FindingStatus.Ok, finding.Status);
        Assert.Contains("09/10/2029", finding.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsupported_23H2_points_to_25H2_not_to_new_device_release()
    {
        var windows = new WindowsInfo("Windows 11 Pro", "Professional", "23H2", 22631, 5000);

        var finding = Get(await Detect(windows: windows), "M03.windows-support");

        Assert.Equal(FindingStatus.Problem, finding.Status);
        Assert.Contains("25H2", finding.Advice, StringComparison.Ordinal);
        Assert.DoesNotContain("26H1", finding.Advice, StringComparison.Ordinal);
        Assert.False(finding.Fixable);
    }

    [Fact]
    public async Task Windows_10_is_out_of_scope_problem()
    {
        var windows = new WindowsInfo("Windows 10 Pro", "Professional", "22H2", 19045, 6000);

        var finding = Get(await Detect(windows: windows), "M03.windows-support");

        Assert.Equal(FindingStatus.Problem, finding.Status);
        Assert.Equal(Severity.High, finding.Severity);
        Assert.Contains("14 octobre 2025", finding.Current, StringComparison.Ordinal);
        Assert.Contains("hors périmètre", finding.Explanation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(29500, "plus récente")]
    [InlineData(22635, "ne figure pas")]
    public async Task Build_missing_from_catalog_is_neutral(int build, string expected)
    {
        var windows = new WindowsInfo("Windows 11 Pro", "Professional", string.Empty, build, 1);

        var finding = Get(await Detect(windows: windows), "M03.windows-support");

        Assert.Equal(FindingStatus.Info, finding.Status);
        Assert.Contains(expected, finding.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreadable_build_is_unknown()
    {
        var finding = Get(await Detect(windows: new WindowsInfo("Windows", string.Empty, string.Empty, 0, 0)), "M03.windows-support");

        Assert.Equal(FindingStatus.Unknown, finding.Status);
    }

    [Fact]
    public async Task Disabled_update_service_is_a_problem()
    {
        var registry = HealthyRegistry()
            .Set(RegistryHive.LocalMachine, $@"{WindowsUpdateModule.ServicesKey}\wuauserv", "Start", 4);

        var finding = Get(await Detect(registry: registry), "M03.update-services");

        Assert.Equal(FindingStatus.Problem, finding.Status);
        Assert.Contains("désactivés : Windows Update", finding.Current, StringComparison.Ordinal);
        Assert.True(finding.Fixable);
    }

    [Fact]
    public async Task Missing_update_service_is_a_warning()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, $@"{WindowsUpdateModule.ServicesKey}\wuauserv", "Start", 3)
            .Set(RegistryHive.LocalMachine, $@"{WindowsUpdateModule.ServicesKey}\UsoSvc", "Start", 2)
            .Set(RegistryHive.LocalMachine, $@"{WindowsUpdateModule.ServicesKey}\BITS", "Start", 3);

        var finding = Get(await Detect(registry: registry), "M03.update-services");

        Assert.Equal(FindingStatus.Warning, finding.Status);
        Assert.Contains("absents : Windows Update Medic", finding.Current, StringComparison.Ordinal);
        Assert.False(finding.Fixable);
    }

    [Fact]
    public async Task Unreadable_services_need_admin()
    {
        var registry = new FakeRegistry();
        foreach (var (name, _) in WindowsUpdateModule.UpdateServices)
        {
            registry.Deny(RegistryHive.LocalMachine, $@"{WindowsUpdateModule.ServicesKey}\{name}");
        }

        var finding = Get(await Detect(registry: registry), "M03.update-services");

        Assert.Equal(FindingStatus.Unknown, finding.Status);
    }

    [Fact]
    public async Task Paused_updates_are_a_warning()
    {
        var registry = HealthyRegistry()
            .Set(RegistryHive.LocalMachine, WindowsUpdateModule.UxSettingsKey, "PauseUpdatesExpiryTime", "2026-10-08T09:30:00Z")
            .Set(RegistryHive.LocalMachine, WindowsUpdateModule.UxSettingsKey, "PauseQualityUpdatesEndTime", "2026-09-01T00:00:00Z");

        var finding = Get(await Detect(registry: registry), "M03.automatic-updates");

        Assert.Equal(FindingStatus.Warning, finding.Status);
        Assert.Contains("en pause jusqu'au 08/10/2026", finding.Current, StringComparison.Ordinal);
        Assert.Contains("Reprendre les mises à jour", finding.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expired_pause_is_compliant()
    {
        var registry = HealthyRegistry()
            .Set(RegistryHive.LocalMachine, WindowsUpdateModule.UxSettingsKey, "PauseUpdatesExpiryTime", "2026-09-01T00:00:00Z");

        var finding = Get(await Detect(registry: registry), "M03.automatic-updates");

        Assert.Equal(FindingStatus.Ok, finding.Status);
        Assert.Equal("active", finding.Current);
    }

    [Theory]
    [InlineData(false, "gpedit.msc")]
    [InlineData(true, "service informatique")]
    public async Task Policy_blocking_automatic_updates_is_a_warning(bool managed, string advice)
    {
        var registry = HealthyRegistry()
            .Set(RegistryHive.LocalMachine, WindowsUpdateModule.AuPolicyKey, "NoAutoUpdate", 1)
            .Set(RegistryHive.LocalMachine, WindowsUpdateModule.PolicyKey, "DisableWindowsUpdateAccess", 1);

        var finding = Get(
            await Detect(registry: registry, hardware: new HardwareProfile { FormFactor = FormFactor.Desktop, IsManaged = managed }),
            "M03.automatic-updates");

        Assert.Equal(FindingStatus.Warning, finding.Status);
        Assert.Contains("NoAutoUpdate", finding.Current, StringComparison.Ordinal);
        Assert.Contains("DisableWindowsUpdateAccess", finding.Current, StringComparison.Ordinal);
        Assert.Contains(advice, finding.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Notify_only_policy_is_a_warning()
    {
        var registry = HealthyRegistry().Set(RegistryHive.LocalMachine, WindowsUpdateModule.AuPolicyKey, "AUOptions", 2);

        var finding = Get(await Detect(registry: registry), "M03.automatic-updates");

        Assert.Equal(FindingStatus.Warning, finding.Status);
        Assert.Contains("AUOptions = 2", finding.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreadable_update_settings_need_admin()
    {
        var registry = HealthyRegistry()
            .Deny(RegistryHive.LocalMachine, WindowsUpdateModule.AuPolicyKey)
            .Deny(RegistryHive.LocalMachine, WindowsUpdateModule.UxSettingsKey);

        var findings = await Detect(registry: registry);

        Assert.Equal(FindingStatus.Unknown, Get(findings, "M03.automatic-updates").Status);
        Assert.Equal(FindingStatus.Unknown, Get(findings, "M03.latest-updates-toggle").Status);
        Assert.Equal(FindingStatus.Unknown, Get(findings, "M03.update-source").Status);
    }

    [Fact]
    public async Task Pending_updates_are_counted_by_kind_without_drivers()
    {
        var agent = HealthyAgent();
        agent.Updates.AddRange(
        [
            Update("Mise à jour cumulative 2026-09 (KB5124008)", SecurityCategory),
            Update("Mise à jour cumulative 2026-09 (KB5124008)", SecurityCategory) with { UpdateId = "dup" },
            Update("Mise à jour cumulative 2026-09 (KB5124008)", SecurityCategory) with { UpdateId = "DUP" },
            Update("Outil de suppression de logiciels malveillants", "28BC880E-0592-4CBF-8F95-C79B17911D5F"),
            Update("Mise à jour critique", CriticalCategory, msrc: "Important"),
            Update("Définitions Defender (KB2267602)", DefinitionCategory),
            Update("Aperçu cumulatif 2026-09 (KB5124010)", SecurityCategory, browseOnly: true),
            Update("NVIDIA - Display", DriverCategory),
            Update("Realtek - Audio", "00000000-0000-0000-0000-000000000000", isDriver: true),
        ]);

        var findings = await Detect(agent);
        var pending = Get(findings, "M03.pending-updates");
        var optional = Get(findings, "M03.optional-updates");

        Assert.Equal(FindingStatus.Warning, pending.Status);
        Assert.StartsWith("3 de sécurité et 1 autre : ", pending.Current, StringComparison.Ordinal);
        Assert.Contains("(et 1 de plus)", pending.Current, StringComparison.Ordinal);
        Assert.Contains("1 mise à jour de définitions Defender", pending.Current, StringComparison.Ordinal);
        Assert.DoesNotContain("NVIDIA", pending.Current, StringComparison.Ordinal);
        Assert.DoesNotContain("Realtek", pending.Current, StringComparison.Ordinal);
        Assert.DoesNotContain("Aperçu", pending.Current, StringComparison.Ordinal);
        Assert.True(pending.Fixable);
        Assert.Contains("Aucun pilote", pending.Advice, StringComparison.Ordinal);

        Assert.Equal(FindingStatus.Info, optional.Status);
        Assert.Equal("1 facultative : Aperçu cumulatif 2026-09 (KB5124010)", optional.Current);
        Assert.Contains("seulement si un correctif précis vous concerne", optional.Advice, StringComparison.Ordinal);
        Assert.True(optional.Fixable);

        // Les pilotes ont leur propre constat, informatif, avec la page des mises à jour facultatives de Windows.
        var drivers = Get(findings, "M03.driver-updates");
        Assert.Equal(FindingStatus.Info, drivers.Status);
        Assert.StartsWith("2 pilotes : ", drivers.Current, StringComparison.Ordinal);
        Assert.Contains("NVIDIA - Display", drivers.Current, StringComparison.Ordinal);
        Assert.Contains("Realtek - Audio", drivers.Current, StringComparison.Ordinal);
        Assert.Equal(WindowsUpdateModule.OptionalUpdatesPage, drivers.SettingsPage);
        Assert.False(drivers.Fixable);
    }

    [Fact]
    public async Task Non_security_pending_updates_are_improvable()
    {
        var agent = HealthyAgent();
        agent.Updates.Add(Update("Outil de suppression de logiciels malveillants", "28BC880E-0592-4CBF-8F95-C79B17911D5F"));

        var finding = Get(await Detect(agent), "M03.pending-updates");

        Assert.Equal(FindingStatus.Improvable, finding.Status);
        Assert.Equal(Severity.Low, finding.Severity);
        Assert.Equal("1 autre : Outil de suppression de logiciels malveillants", finding.Current);
    }

    [Fact]
    public async Task Pending_defender_definitions_alone_are_compliant()
    {
        var agent = HealthyAgent();
        agent.Updates.Add(Update("Définitions Defender (KB2267602)", DefinitionCategory));

        var finding = Get(await Detect(agent), "M03.pending-updates");

        Assert.Equal(FindingStatus.Ok, finding.Status);
        Assert.Contains("définitions Defender", finding.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreachable_update_server_is_unknown()
    {
        var agent = HealthyAgent();
        agent.SearchError = Marshal.GetExceptionForHR(unchecked((int)0x80072EE7));
        Assert.IsType<COMException>(agent.SearchError);

        var findings = await Detect(agent);

        var pending = Get(findings, "M03.pending-updates");
        Assert.Equal(FindingStatus.Unknown, pending.Status);
        Assert.Contains("injoignable", pending.Explanation, StringComparison.Ordinal);
        Assert.Contains("0x80072EE7", pending.Explanation, StringComparison.Ordinal);
        Assert.Equal(FindingStatus.Unknown, Get(findings, "M03.optional-updates").Status);
        Assert.Equal(FindingStatus.Ok, Get(findings, "M03.last-install").Status);
    }

    [Fact]
    public async Task Search_timeout_is_unknown()
    {
        var agent = HealthyAgent();
        agent.SearchError = new TimeoutException();

        var finding = Get(await Detect(agent), "M03.pending-updates");

        Assert.Equal(FindingStatus.Unknown, finding.Status);
        Assert.Contains("aucune réponse en 5 s", finding.Explanation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unauthorized", "accès refusé")]
    [InlineData("access-denied", "accès refusé")]
    [InlineData("failed", "La recherche a échoué")]
    [InlineData("unavailable", "Agent absent")]
    public async Task Other_search_failures_are_unknown(string kind, string expected)
    {
        var agent = HealthyAgent();
        agent.SearchError = kind switch
        {
            "unauthorized" => new UnauthorizedAccessException(),
            "access-denied" => new MausAccessDeniedException("refusé"),
            "failed" => new InvalidOperationException("La recherche a échoué."),
            _ => new DataSourceUnavailableException("Agent absent."),
        };

        var finding = Get(await Detect(agent), "M03.pending-updates");

        Assert.Equal(FindingStatus.Unknown, finding.Status);
        Assert.Contains(expected, finding.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Old_last_install_is_a_warning()
    {
        var cim = HealthyCim().Answer(
            WindowsUpdateModule.QfeQuery,
            new Dictionary<string, object?> { ["HotFixID"] = "KB5060000", ["InstalledOn"] = "7/1/2026" });

        var finding = Get(await Detect(cim: cim), "M03.last-install");

        Assert.Equal(FindingStatus.Warning, finding.Status);
        Assert.Contains("KB5060000 installé le 01/07/2026 (il y a 85 jours)", finding.Current, StringComparison.Ordinal);
        Assert.Contains("Module 2", finding.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_install_dates_are_unknown()
    {
        var cim = HealthyCim().Answer(
            WindowsUpdateModule.QfeQuery,
            new Dictionary<string, object?> { ["HotFixID"] = "KB5060000", ["InstalledOn"] = string.Empty });

        Assert.Equal(FindingStatus.Unknown, Get(await Detect(cim: cim), "M03.last-install").Status);
    }

    [Fact]
    public async Task Unreadable_hotfix_list_is_unknown()
    {
        var denied = HealthyCim().Throw(WindowsUpdateModule.QfeQuery, new MausAccessDeniedException("refusé"));
        var broken = HealthyCim().Throw(WindowsUpdateModule.QfeQuery, new DataSourceUnavailableException("WMI"));

        Assert.Equal(FindingStatus.Unknown, Get(await Detect(cim: denied), "M03.last-install").Status);
        Assert.Equal(FindingStatus.Unknown, Get(await Detect(cim: broken), "M03.last-install").Status);
    }

    [Fact]
    public async Task Old_automatic_search_is_a_warning()
    {
        var agent = new FakeUpdateAgent { Results = new AutomaticUpdatesResults(new DateTime(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc), null) };

        var finding = Get(await Detect(agent), "M03.last-search");

        Assert.Equal(FindingStatus.Warning, finding.Status);
        Assert.Contains("il y a 23 jours", finding.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_automatic_search_date_is_unknown()
    {
        Assert.Equal(FindingStatus.Unknown, Get(await Detect(new FakeUpdateAgent()), "M03.last-search").Status);
        Assert.Equal(
            FindingStatus.Unknown,
            Get(await Detect(new FakeUpdateAgent { ResultsError = Marshal.GetExceptionForHR(unchecked((int)0x80240024)) }), "M03.last-search").Status);
    }

    [Fact]
    public async Task Old_defender_signatures_are_a_warning()
    {
        var cim = HealthyCim().Answer(
            WindowsUpdateModule.DefenderQuery,
            CimScopes.Defender,
            new Dictionary<string, object?> { ["AMRunningMode"] = "Normal", ["AntivirusEnabled"] = true, ["AntivirusSignatureAge"] = 5u });

        var finding = Get(await Detect(cim: cim), "M03.defender-signatures");

        Assert.Equal(FindingStatus.Warning, finding.Status);
        Assert.Equal("âge : 5 jours", finding.Current);
        Assert.True(finding.Fixable);
    }

    [Fact]
    public async Task Third_party_antivirus_skips_defender_signatures()
    {
        var cim = HealthyCim().Answer(
            WindowsUpdateModule.AntivirusQuery,
            CimScopes.SecurityCenter2,
            new Dictionary<string, object?> { ["displayName"] = "Malwarebytes", ["productState"] = 393232u },
            new Dictionary<string, object?> { ["displayName"] = "Bitdefender Antivirus", ["productState"] = 266240u },
            new Dictionary<string, object?> { ["displayName"] = "Windows Defender", ["productState"] = 393472u });

        var finding = Get(await Detect(cim: cim), "M03.defender-signatures");

        Assert.Equal(FindingStatus.Info, finding.Status);
        Assert.Equal("antivirus actif : Bitdefender Antivirus", finding.Current);
    }

    [Fact]
    public async Task Inactive_third_party_antivirus_does_not_hide_defender()
    {
        var cim = HealthyCim().Answer(
            WindowsUpdateModule.AntivirusQuery,
            CimScopes.SecurityCenter2,
            new Dictionary<string, object?> { ["displayName"] = "Malwarebytes", ["productState"] = 393232u },
            new Dictionary<string, object?> { ["displayName"] = "Windows Defender", ["productState"] = 397568u });

        Assert.Equal(FindingStatus.Ok, Get(await Detect(cim: cim), "M03.defender-signatures").Status);
    }

    [Fact]
    public async Task Passive_defender_is_neutral()
    {
        var cim = HealthyCim().Answer(
            WindowsUpdateModule.DefenderQuery,
            CimScopes.Defender,
            new Dictionary<string, object?> { ["AMRunningMode"] = "Passive Mode", ["AntivirusEnabled"] = true, ["AntivirusSignatureAge"] = 30u });

        var finding = Get(await Detect(cim: cim), "M03.defender-signatures");

        Assert.Equal(FindingStatus.Info, finding.Status);
        Assert.Contains("Passive Mode", finding.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_defender_data_is_unknown()
    {
        var empty = HealthyCim().Answer(WindowsUpdateModule.DefenderQuery, CimScopes.Defender);
        var denied = HealthyCim().Throw(WindowsUpdateModule.DefenderQuery, new MausAccessDeniedException("refusé"), CimScopes.Defender);
        var noAge = HealthyCim().Answer(
            WindowsUpdateModule.DefenderQuery,
            CimScopes.Defender,
            new Dictionary<string, object?> { ["AMRunningMode"] = "Normal", ["AntivirusEnabled"] = true });

        Assert.Equal(FindingStatus.Unknown, Get(await Detect(cim: empty), "M03.defender-signatures").Status);
        Assert.Equal(FindingStatus.Unknown, Get(await Detect(cim: denied), "M03.defender-signatures").Status);
        Assert.Equal(FindingStatus.Unknown, Get(await Detect(cim: noAge), "M03.defender-signatures").Status);
    }

    [Fact]
    public async Task Latest_updates_toggle_and_policy_are_reported()
    {
        var registry = HealthyRegistry()
            .Set(RegistryHive.LocalMachine, WindowsUpdateModule.UxSettingsKey, "IsContinuousInnovationOptedIn", 1)
            .Set(RegistryHive.LocalMachine, WindowsUpdateModule.PolicyKey, "AllowOptionalContent", 1);

        var finding = Get(await Detect(registry: registry), "M03.latest-updates-toggle");

        Assert.Equal(FindingStatus.Info, finding.Status);
        Assert.Equal("activée (stratégie AllowOptionalContent = 1)", finding.Current);
        Assert.Contains("ms-settings:windowsupdate", finding.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wsus_server_is_reported()
    {
        var registry = HealthyRegistry()
            .Set(RegistryHive.LocalMachine, WindowsUpdateModule.AuPolicyKey, "UseWUServer", 1)
            .Set(RegistryHive.LocalMachine, WindowsUpdateModule.PolicyKey, "WUServer", "http://wsus.contoso.local:8530");

        var finding = Get(await Detect(registry: registry), "M03.update-source");

        Assert.Equal("serveur de l'organisation (WSUS) : http://wsus.contoso.local:8530", finding.Current);
    }

    [Theory]
    [InlineData("9/24/2026", 2026, 9, 24)]
    [InlineData("09/04/2026", 2026, 9, 4)]
    [InlineData("20260924", 2026, 9, 24)]
    [InlineData("2026-09-24", 2026, 9, 24)]
    [InlineData("01DD4C1C39976000", 2026, 9, 24)]
    public void Qfe_dates_are_parsed(string text, int year, int month, int day)
    {
        Assert.Equal(new DateOnly(year, month, day), UpdateParsers.ParseQfeDate(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pas une date")]
    [InlineData("0000000000000000")]
    public void Invalid_qfe_dates_are_null(string? text)
    {
        Assert.Null(UpdateParsers.ParseQfeDate(text));
    }

    [Fact]
    public void Pause_dates_are_read_as_universal_time()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 9, 30, 0, TimeSpan.Zero), UpdateParsers.ParseIsoDate("2026-10-08T09:30:00Z"));
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 9, 30, 0, TimeSpan.Zero), UpdateParsers.ParseIsoDate("2026-10-08T09:30:00"));
        Assert.Null(UpdateParsers.ParseIsoDate(null));
        Assert.Null(UpdateParsers.ParseIsoDate("demain"));
    }

    [Theory]
    [InlineData(397568L, true)]
    [InlineData(266240L, true)]
    [InlineData(393232L, false)]
    [InlineData(393472L, false)]
    public void Antivirus_state_is_decoded(long productState, bool enabled)
    {
        Assert.Equal(enabled, UpdateParsers.IsAntivirusEnabled(productState));
    }

    [Theory]
    [InlineData("Windows Defender", true)]
    [InlineData("Microsoft Defender Antivirus", true)]
    [InlineData("Malwarebytes", false)]
    [InlineData("Bitdefender Antivirus Free", false)]
    [InlineData(null, false)]
    public void Defender_is_recognised(string? name, bool expected)
    {
        Assert.Equal(expected, UpdateParsers.IsMicrosoftDefender(name));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070422), "service Windows Update est désactivé (code 0x80070422)")]
    [InlineData(unchecked((int)0x8024402C), "injoignable")]
    [InlineData(unchecked((int)0x8024500C), "stratégie")]
    [InlineData(unchecked((int)0x80240FFF), "code d'erreur 0x80240FFF")]
    public void Search_errors_are_explained(int hresult, string expected)
    {
        Assert.Contains(expected, UpdateParsers.DescribeSearchError(hresult), StringComparison.Ordinal);
    }

    [Fact]
    public void Updates_are_classified()
    {
        Assert.Equal(PendingUpdateKind.Security, UpdateParsers.Classify(Update("a", SecurityCategory.ToLowerInvariant())));
        Assert.Equal(PendingUpdateKind.Security, UpdateParsers.Classify(Update("b", CriticalCategory, msrc: "Critical")));
        Assert.Equal(PendingUpdateKind.Other, UpdateParsers.Classify(Update("c", CriticalCategory)));
        Assert.Equal(PendingUpdateKind.Definitions, UpdateParsers.Classify(Update("d", DefinitionCategory)));
        Assert.Equal(PendingUpdateKind.Optional, UpdateParsers.Classify(Update("e", SecurityCategory, browseOnly: true)));
        Assert.Equal(PendingUpdateKind.Driver, UpdateParsers.Classify(Update("f", DriverCategory, browseOnly: true)));
        Assert.Equal(PendingUpdateKind.Driver, UpdateParsers.Classify(Update("g", SecurityCategory, isDriver: true)));
    }

    [Theory]
    [InlineData("Core", "HomePro")]
    [InlineData("Professional", "HomePro")]
    [InlineData("ProfessionalEducation", "HomePro")]
    [InlineData("Enterprise", "EnterpriseEducation")]
    [InlineData("Education", "EnterpriseEducation")]
    [InlineData("IoTEnterprise", "EnterpriseEducation")]
    [InlineData("EnterpriseS", "Ltsc")]
    [InlineData("IoTEnterpriseS", "IotLtsc")]
    [InlineData("", "HomePro")]
    public void Editions_map_to_servicing_channels(string editionId, string expected)
    {
        Assert.Equal(Enum.Parse<ServicingChannel>(expected), WindowsLifecycleCatalog.Classify(editionId));
    }

    [Fact]
    public void Lifecycle_catalog_covers_supported_releases()
    {
        var catalog = WindowsLifecycleCatalog.LoadEmbedded();

        Assert.Equal(22631, catalog.MinimumSupportedBuild);
        Assert.Equal(new DateOnly(2025, 10, 14), catalog.Windows10EndOfSupport);
        Assert.Equal(new DateOnly(2027, 10, 12), catalog.Find(26200)!.HomePro);
        Assert.Equal(new DateOnly(2028, 10, 10), catalog.Find(26200)!.EnterpriseEducation);
        Assert.Equal(new DateOnly(2026, 10, 13), catalog.Find(26100)!.HomePro);
        Assert.Equal(new DateOnly(2034, 10, 10), catalog.Find(26100)!.EndOfService(ServicingChannel.IotLtsc));
        Assert.Equal(new DateOnly(2026, 11, 10), catalog.Find(22631)!.EndOfService(ServicingChannel.EnterpriseEducation));
        Assert.True(catalog.Find(28000)!.NewDevicesOnly);
        Assert.Equal(28000, catalog.NewestBuild);
        Assert.All(catalog.Releases, r => Assert.True(r.EnterpriseEducation > r.HomePro));
    }

    /// <summary>Agent Windows Update simulé : aucune recherche réseau.</summary>
    private sealed class FakeUpdateAgent : IWindowsUpdateAgent
    {
        public List<PendingUpdate> Updates { get; } = [];

        public AutomaticUpdatesResults? Results { get; init; }

        public Exception? ResultsError { get; init; }

        public Exception? SearchError { get; set; }

        public string? ReceivedCriteria { get; private set; }

        public AutomaticUpdatesResults? GetAutomaticUpdatesResults() => ResultsError is null ? Results : throw ResultsError;

        public Task<IReadOnlyList<PendingUpdate>> SearchAsync(string criteria, TimeSpan timeout, CancellationToken cancellationToken)
        {
            ReceivedCriteria = criteria;
            return SearchError is null
                ? Task.FromResult<IReadOnlyList<PendingUpdate>>(Updates)
                : Task.FromException<IReadOnlyList<PendingUpdate>>(SearchError);
        }
    }
}
