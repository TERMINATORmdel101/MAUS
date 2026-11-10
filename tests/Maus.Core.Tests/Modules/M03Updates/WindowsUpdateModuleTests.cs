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
        Results = new AutomaticUpdatesResults(new DateTime(2026, 9, 24, 6, 0, 0, DateTimeKind.Utc)),
    };

    internal static Task<IReadOnlyList<Finding>> Detect(
        FakeUpdateAgent? agent = null,
        FakeRegistry? registry = null,
        FakeCim? cim = null,
        WindowsInfo? windows = null,
        DateTimeOffset? now = null,
        HardwareProfile? hardware = null) =>
        new WindowsUpdateModule(agent ?? HealthyAgent(), TimeSpan.FromSeconds(5)).DetectAsync(
            TestContext.Create(registry ?? HealthyRegistry(), cim ?? HealthyCim(), windows: windows ?? Pro25H2, now: now ?? Now, hardware: hardware, elevated: false),
            CancellationToken.None);

    internal static Finding Get(IReadOnlyList<Finding> findings, string id) => findings.Single(f => f.Id == id);

    private static PendingUpdate Update(string title, string category, bool browseOnly = false, string? msrc = null, bool isDriver = false) =>
        new(Guid.NewGuid().ToString(), title, ["{" + category + "}"], msrc, browseOnly, isDriver);

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

        // Rien à faire : pas de bouton « Ouvrir dans Windows », sauf pour l'option qui ne se règle que dans les Paramètres.
        Assert.Null(Get(findings, "M03.windows-support").SettingsPage);
        Assert.Null(Get(findings, "M03.automatic-updates").SettingsPage);
        Assert.Null(Get(findings, "M03.pending-updates").SettingsPage);
        Assert.Null(Get(findings, "M03.optional-updates").SettingsPage);
        Assert.Null(Get(findings, "M03.last-install").SettingsPage);
        Assert.Null(Get(findings, "M03.defender-signatures").SettingsPage);
        Assert.Equal("ms-settings:windowsupdate", Get(findings, "M03.latest-updates-toggle").SettingsPage);
    }

    [Fact]
    public async Task Without_network_the_agent_answers_from_its_last_search()
    {
        var agent = HealthyAgent();
        await Detect(agent);
        Assert.True(agent.ReceivedOnline);

        var offline = HealthyAgent();
        await new WindowsUpdateModule(offline, TimeSpan.FromSeconds(5)).DetectAsync(TestContext.Create().WithoutNetwork(), CancellationToken.None);
        Assert.False(offline.ReceivedOnline);
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
    public async Task Local_reads_happen_while_the_online_search_is_still_running()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new FakeUpdateAgent { Results = HealthyAgent().Results, Hold = hold };
        var cim = HealthyCim();

        var detecting = Detect(agent, cim: cim);

        // Le serveur n'a pas encore répondu, mais les correctifs installés et Defender sont déjà lus.
        Assert.False(detecting.IsCompleted);
        Assert.Contains(WindowsUpdateModule.QfeQuery, cim.Received);
        Assert.Contains(WindowsUpdateModule.DefenderQuery, cim.Received);

        hold.SetResult();
        var findings = await detecting;
        Assert.Equal("aucune", Get(findings, "M03.pending-updates").Current);
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
        Assert.Contains("après le 13/10/2026", finding.Advice, StringComparison.Ordinal);
        Assert.Contains("KB5054156", finding.Advice, StringComparison.Ordinal);
        Assert.DoesNotContain("révision", finding.Advice, StringComparison.Ordinal);
        Assert.True(finding.Fixable);
        Assert.Equal("ms-settings:windowsupdate", finding.SettingsPage);
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
        Assert.Contains("support terminé le 14/10/2025", finding.Current, StringComparison.Ordinal);
        Assert.Contains("hors périmètre", finding.Explanation, StringComparison.Ordinal);
        Assert.Null(finding.SettingsPage);
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
        Assert.Equal("ms-settings:windowsupdate", finding.SettingsPage);
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

        // Une stratégie ne se retire pas depuis les Paramètres : pas de bouton.
        Assert.Null(finding.SettingsPage);
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
            Update("Realtek - Audio", "00000000-0000-0000-0000-000000000000", browseOnly: true, isDriver: true),
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
        Assert.False(pending.Fixable);
        Assert.Contains("release-health", pending.Advice, StringComparison.Ordinal);
        Assert.Equal("ms-settings:windowsupdate", pending.SettingsPage);

        Assert.Equal(FindingStatus.Info, optional.Status);
        Assert.Equal("1 facultative : Aperçu cumulatif 2026-09 (KB5124010)", optional.Current);
        Assert.Contains("vous pouvez attendre", optional.Advice, StringComparison.Ordinal);
        Assert.Contains("point de restauration", optional.Advice, StringComparison.Ordinal);
        Assert.True(optional.Fixable);
        Assert.Equal("ms-settings:windowsupdate", optional.SettingsPage);

        // Les pilotes ont leur propre constat, informatif, avec la page des mises à jour facultatives de Windows.
        var drivers = Get(findings, "M03.driver-updates");
        Assert.Equal(FindingStatus.Info, drivers.Status);
        // Facultatif (BrowseOnly) et automatique séparés : seul le facultatif se choisit dans « Mises à jour facultatives ».
        Assert.Equal("1 pilote facultatif : Realtek - Audio ; 1 pilote installé automatiquement : NVIDIA - Display", drivers.Current);
        Assert.Equal(WindowsUpdateModule.OptionalUpdatesPage, drivers.SettingsPage);
        Assert.False(drivers.Fixable);
        Assert.StartsWith("N'installez un pilote facultatif que si un appareil pose problème", drivers.Advice, StringComparison.Ordinal);
        Assert.Contains("version du constructeur (Module 9", drivers.Advice, StringComparison.Ordinal);
        Assert.Contains("Faites d'abord un point de restauration", drivers.Advice, StringComparison.Ordinal);

        // Texte vu par le Microsoft Store : des faits, aucun jugement, aucune promesse, aucun blocage proposé.
        foreach (var text in new[] { optional.Explanation, optional.Advice, drivers.Advice })
        {
            foreach (var banned in BannedInStoreTexts)
            {
                Assert.DoesNotContain(banned, text, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Rien ne décourage un correctif de sécurité.
        Assert.DoesNotContain("attendre", pending.Advice, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("E_INVALIDARG")]
    [InlineData("E_NOTIMPL")]
    [InlineData("E_POINTER")]
    public async Task Unexpected_error_on_the_search_thread_is_unknown_instead_of_closing_maus(string hresult)
    {
        // Ce que l'interop COM produit pour ces codes : sur le fil dédié, une telle erreur non attrapée fermait MAUS sans trace.
        Func<IReadOnlyList<PendingUpdate>> search = hresult switch
        {
            "E_INVALIDARG" => () => throw new ArgumentException("Paramètre incorrect."),
            "E_NOTIMPL" => () => throw new NotImplementedException(),
            _ => ComWindowsUpdateAgentTests.ReadThroughNullPointer,
        };
        var agent = new FakeUpdateAgent { Results = HealthyAgent().Results, SearchOnThread = search };

        var findings = await Detect(agent);

        var pending = Get(findings, "M03.pending-updates");
        Assert.Equal(FindingStatus.Unknown, pending.Status);
        Assert.Contains("Réponse inattendue de l'agent Windows Update", pending.Explanation, StringComparison.Ordinal);
        Assert.Equal(FindingStatus.Unknown, Get(findings, "M03.optional-updates").Status);
        Assert.Equal(FindingStatus.Ok, Get(findings, "M03.last-install").Status);
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
        Assert.Contains("Mises à jour qui ont échoué", finding.Advice, StringComparison.Ordinal);
        Assert.Equal("ms-settings:windowsupdate", finding.SettingsPage);
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
        var agent = new FakeUpdateAgent { Results = new AutomaticUpdatesResults(new DateTime(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc)) };

        var finding = Get(await Detect(agent), "M03.last-search");

        Assert.Equal(FindingStatus.Warning, finding.Status);
        Assert.Contains("il y a 23 jours", finding.Current, StringComparison.Ordinal);

        // Page d'accueil de Windows Update, jamais « windowsupdate-action » qui lancerait une recherche dès l'ouverture.
        Assert.Equal("ms-settings:windowsupdate", finding.SettingsPage);
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
        Assert.Equal("ms-settings:windowsdefender", finding.SettingsPage);
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

    private const string DriverClient = "Device Driver Retrieval Client";
    private const string DriverService = "8b24b027-1dee-babb-9a95-3517dfb9c552";
    private const string StoreService = "855e8a7c-ecb4-4ca3-b045-1dfa50104289";
    private const int Blocked = unchecked((int)0x80240016);
    private static readonly string[] NoneNoted = ["None"];
    private static readonly string[] HoldNumber = ["52796844"];
    private static readonly string[] OtherReason = ["Other"];
    private static readonly string[] BannedInStoreTexts = ["bug", "garanti", "de toute façon", "toujours", "sans risque", "bloqu"];
    private static readonly string[] WaysAround = ["DisableWUfBSafeguards", "contourn", "désactiv", "opt out", "opt-out", "Assistant", "forcer"];

    /// <summary>Une ligne d'historique sous la forme relevée sur le PC du porteur le 2/11/2026.</summary>
    private static UpdateHistoryEntry Line(
        string title, int day, int result = 4, int code = Blocked, string? client = "MoUpdateOrchestrator", string? service = DriverService, int operation = 1, string? id = null) =>
        new(id ?? title, title, new DateTime(2026, 9, day, 10, 0, 0, DateTimeKind.Utc), operation, result, result == 2 ? 0 : code, client, service);

    private static Task<Finding> Failed(params UpdateHistoryEntry[] lines) => Failed(HealthyAgent(), lines);

    private static async Task<Finding> Failed(FakeUpdateAgent agent, params UpdateHistoryEntry[] lines)
    {
        agent.History = [.. lines];
        return Get(await Detect(agent), "M03.failed-updates");
    }

    /// <summary>Windows propose encore cette mise à jour : la liste lue par le même audit la contient.</summary>
    private static FakeUpdateAgent StillPending(string id, string? title = null)
    {
        var agent = HealthyAgent();
        agent.Updates.Add(new PendingUpdate(id, title ?? id, [], null, false));
        return agent;
    }

    /// <summary>La recherche a échoué : la liste des mises à jour en attente n'est pas lue.</summary>
    private static FakeUpdateAgent PendingUnread()
    {
        var agent = HealthyAgent();
        agent.SearchError = Marshal.GetExceptionForHR(unchecked((int)0x80072EE7));
        return agent;
    }

    private const string DefenderTitle = "Mise à jour de la sélection disjointe pour Microsoft Defender Antivirus – 2267602 Ko (version 1.463.83.0) – Canal actuel (large)";
    private const string MaybeReplaced = "D'autres mises à jour de Windows se sont installées depuis : celle-ci a peut-être été remplacée par une plus récente, MAUS ne peut pas le savoir.";

    private static FakeRegistry Hold(string? gwx, string? version = "GE24H2", string? status = null, object? ids = null, object? reasons = null)
    {
        var registry = HealthyRegistry();
        if (gwx is not null)
        {
            registry.Set(RegistryHive.LocalMachine, WindowsUpdateModule.HoldStatusKey, "GStatus", gwx);
        }

        if (version is not null)
        {
            var key = $@"{WindowsUpdateModule.HoldIndicatorsKey}\{version}";
            registry.Set(RegistryHive.LocalMachine, key, "GStatus", status ?? gwx ?? "2");
            registry.Set(RegistryHive.LocalMachine, key, "GatedBlockId", ids ?? NoneNoted);
            registry.Set(RegistryHive.LocalMachine, key, "GatedBlockReason", reasons ?? NoneNoted);
        }

        return registry;
    }

    [Fact]
    public async Task Failed_update_says_which_how_many_times_and_the_code_without_a_cause()
    {
        var finding = await Failed(
            StillPending("2026-09 Correctif de sécurité (KB5000009)"),
            Line("2026-09 Correctif de sécurité (KB5000009)", 20),
            Line("2026-09 Correctif de sécurité (KB5000009)", 18, code: unchecked((int)0x80070643)));

        Assert.Equal(FindingStatus.Info, finding.Status);
        Assert.Contains("Windows et autres produits Microsoft : 2026-09 Correctif de sécurité (KB5000009) : 2 échecs", finding.Current, StringComparison.Ordinal);
        Assert.Contains("code 0x80240016", finding.Current, StringComparison.Ordinal);
        Assert.Contains("pas de réussite notée depuis", finding.Current, StringComparison.Ordinal);
        Assert.Contains("setupdiag", finding.Advice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Résolution des problèmes", finding.Advice, StringComparison.Ordinal);
        Assert.Equal(WindowsUpdateModule.WindowsUpdatePage, finding.SettingsPage);
    }

    [Fact]
    public async Task Windows_loop_uses_the_threshold_chosen_by_maus_and_says_so()
    {
        var lines = Enumerable.Range(1, WindowsUpdateModule.LoopFailureCount).Select(day => Line("Correctif (KB5000009)", day)).ToArray();

        var finding = await Failed(StillPending("Correctif (KB5000009)"), lines);

        Assert.Equal(FindingStatus.Improvable, finding.Status);
        Assert.Equal(Severity.Low, finding.Severity);
        Assert.Contains("seuil choisi par MAUS", finding.Expected, StringComparison.Ordinal);
        Assert.Contains("choisie par MAUS", Maus.Core.Reporting.ThresholdNotes.For(finding.Id), StringComparison.Ordinal);

        var justBelow = await Failed(StillPending("Correctif (KB5000009)"), lines[1..]);
        Assert.Equal(FindingStatus.Info, justBelow.Status);
        Assert.Null(justBelow.Expected);
    }

    [Fact]
    public async Task A_later_success_is_shown_and_ends_the_loop()
    {
        var finding = await Failed(
            Line("Correctif (KB5000009)", 1),
            Line("Correctif (KB5000009)", 2),
            Line("Correctif (KB5000009)", 3),
            Line("Correctif (KB5000009)", 4, result: 2));

        Assert.Equal(FindingStatus.Info, finding.Status);
        Assert.Contains("réussie ensuite", finding.Current, StringComparison.Ordinal);
        Assert.Null(finding.Advice);
        Assert.Null(finding.SettingsPage);
    }

    [Fact]
    public async Task A_success_before_the_last_failure_is_not_a_later_success()
    {
        var finding = await Failed(Line("Correctif (KB5000009)", 1, result: 2), Line("Correctif (KB5000009)", 2));

        Assert.Contains("pas de réussite notée depuis", finding.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drivers_and_store_apps_are_separate_and_never_a_windows_problem()
    {
        var lines = new List<UpdateHistoryEntry>();
        for (var day = 1; day <= WindowsUpdateModule.LoopFailureCount + 2; day++)
        {
            lines.Add(Line("INTEL - System - 10.1.16.7", day, client: DriverClient));
            lines.Add(Line("9NRZT3Q9R3DL-Microsoft.WindowsAppRuntime.2", day, client: "Acquisition;setup-StartProductInstallWithOptionsAsync", service: StoreService));
        }

        var finding = await Failed([.. lines]);

        Assert.Equal(FindingStatus.Info, finding.Status);
        Assert.Contains("Pilotes : INTEL - System - 10.1.16.7", finding.Current, StringComparison.Ordinal);
        Assert.Contains("Applications du Store : 9NRZT3Q9R3DL", finding.Current, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows et autres", finding.Current, StringComparison.Ordinal);
        Assert.Contains("pas Windows lui-même", finding.Advice, StringComparison.Ordinal);
        Assert.Null(finding.SettingsPage);
    }

    [Theory]
    [InlineData("INTEL - System - 10.1.16.7", DriverClient, DriverService, "Driver")]
    [InlineData("Dell Inc. - Monitor - 1.0.0.0", "MoUpdateOrchestrator", DriverService, "Driver")]
    [InlineData("Realtek SoftwareComponent Driver Update (1.0.1026.0)", "MoUpdateOrchestrator", DriverService, "Driver")]
    [InlineData("Intel - System - 9/19/2017 12:00:00 AM - 11.7.0.1000", "MoUpdateOrchestrator", DriverService, "Driver")]
    [InlineData("2026-10 Correctif de sécurité (KB5127648) (26300.9711)", "MoUpdateOrchestrator", DriverService, "Windows")]
    [InlineData("2026-09 Mise à jour critique (KB5128942)", "OOBE ZDP", DriverService, "Windows")]
    [InlineData("Mise à jour pour Windows Security platform - KB5007651 (version 10.0.29429.1000)", "MoUpdateOrchestrator", null, "Windows")]
    [InlineData("Mise à jour de la sélection disjointe pour Microsoft Defender Antivirus – 2267602 Ko (version 1.463.83.0) – Canal actuel (large)", "Windows Defender", null, "Windows")]
    [InlineData("9NF8H0H7WMLT-NVIDIACorp.NVIDIAControlPanel", "Acquisition;DSIC-svchost-StartProductInstallWithOptionsAsync", StoreService, "Store")]
    [InlineData("Autre chose", "Autre", StoreService, "Store")]
    [InlineData("Autre chose", "Autre", null, "Windows")]
    public void History_lines_sort_as_seen_on_a_real_pc(string title, string? client, string? service, string expected) =>
        Assert.Equal(expected, UpdateParsers.ClassifyHistory(Line(title, 1, client: client, service: service)).ToString());

    [Fact]
    public async Task Old_failures_uninstalls_and_aborted_installs_are_not_counted()
    {
        var old = Line("Correctif (KB5000001)", 1) with { DateUtc = Now.UtcDateTime.AddDays(-WindowsUpdateModule.RecentFailureDays - 1) };

        var finding = await Failed(
            old,
            Line("Correctif (KB5000002)", 2, operation: 2),
            Line("Correctif (KB5000003)", 3, result: 5),
            Line("Correctif (KB5000004)", 4, result: 3));

        Assert.StartsWith("aucun échec noté par Windows depuis le", finding.Current, StringComparison.Ordinal);
        Assert.Equal(FindingStatus.Info, finding.Status);
    }

    [Fact]
    public async Task No_failure_says_from_when_windows_has_traces()
    {
        var finding = await Failed(Line("Correctif (KB5000001)", 20, result: 2));

        Assert.Contains(new DateOnly(2026, 9, 20).ToString("d", Maus.Core.Localization.Texts.Culture), finding.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task More_than_three_failures_in_a_family_are_counted_not_all_listed()
    {
        var finding = await Failed([.. Enumerable.Range(1, 5).Select(i => Line($"Intel - System - 1.0.0.{i}", i, client: DriverClient))]);

        Assert.Contains("(et 2 de plus)", finding.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_update_id_is_counted_once_whatever_its_title()
    {
        var finding = await Failed(Line("Titre A (KB5000001)", 1, id: "x"), Line("Titre B (KB5000001)", 2, id: "X"));

        Assert.Contains("2 échecs", finding.Current, StringComparison.Ordinal);
        Assert.DoesNotContain("Titre A", finding.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreadable_history_is_unknown_never_no_failure()
    {
        var agent = HealthyAgent();
        agent.History = null;
        Assert.Equal(FindingStatus.Unknown, Get(await Detect(agent), "M03.failed-updates").Status);

        var expectedIds = (await Detect()).Select(f => f.Id).ToList();

        // Ce que l'interop COM produit pour E_INVALIDARG, E_NOTIMPL ou E_POINTER : ce seul constat devient « indéterminé », les autres restent.
        Exception[] errors =
        [
            new InvalidOperationException("service arrêté"),
            new ArgumentException("E_INVALIDARG"),
            new NotImplementedException("E_NOTIMPL"),
            Record.Exception(() => ComWindowsUpdateAgentTests.ReadThroughNullPointer())!,
        ];
        foreach (var error in errors)
        {
            var failing = new FakeUpdateAgent { HistoryError = error, Results = HealthyAgent().Results };
            var findings = await Detect(failing);
            Assert.Equal(FindingStatus.Unknown, Get(findings, "M03.failed-updates").Status);
            Assert.Equal(expectedIds, findings.Select(f => f.Id).ToList());
            Assert.NotEqual(FindingStatus.Unknown, Get(findings, "M03.windows-support").Status);
        }
    }

    [Fact]
    public async Task Defender_definitions_never_count_toward_the_threshold()
    {
        var lines = Enumerable.Range(1, WindowsUpdateModule.LoopFailureCount + 2).Select(day => Line(DefenderTitle, day)).ToArray();

        var finding = await Failed(StillPending(DefenderTitle), lines);

        Assert.Equal(FindingStatus.Info, finding.Status);
        Assert.Null(finding.Expected);
        Assert.Null(finding.Advice);
        Assert.Contains("2267602", finding.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_still_offered_under_another_id_but_the_same_kb_stays_improvable()
    {
        var lines = Enumerable.Range(1, WindowsUpdateModule.LoopFailureCount).Select(day => Line("2026-09 Correctif cumulatif (KB5000009)", day)).ToArray();

        var finding = await Failed(StillPending("autre-identifiant", "2026-09 Correctif cumulatif (KB5000009) (26300.9711)"), lines);

        Assert.Equal(FindingStatus.Improvable, finding.Status);
        Assert.DoesNotContain(MaybeReplaced, finding.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_windows_no_longer_offers_is_information_and_may_have_been_replaced()
    {
        var lines = Enumerable.Range(1, WindowsUpdateModule.LoopFailureCount + 1).Select(day => Line("2026-08 Correctif cumulatif (KB5000008)", day))
            .Append(Line("2026-09 Correctif cumulatif (KB5000009)", 20, result: 2))
            .ToArray();

        var finding = await Failed(StillPending("autre", "2026-09 Pilote (KB5000010)"), lines);

        Assert.Equal(FindingStatus.Info, finding.Status);
        Assert.Null(finding.Expected);
        Assert.Null(finding.Advice);
        Assert.Null(finding.SettingsPage);
        Assert.Contains("pas de réussite notée depuis", finding.Current, StringComparison.Ordinal);
        Assert.EndsWith(MaybeReplaced, finding.Current, StringComparison.Ordinal);

        // Rien d'installé depuis : MAUS ne dit pas qu'elle a peut-être été remplacée.
        var alone = await Failed(StillPending("autre", "2026-09 Pilote (KB5000010)"), lines[..^1]);
        Assert.Equal(FindingStatus.Info, alone.Status);
        Assert.DoesNotContain(MaybeReplaced, alone.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_pending_list_only_a_later_windows_install_ends_the_loop()
    {
        var lines = Enumerable.Range(1, WindowsUpdateModule.LoopFailureCount).Select(day => Line("2026-08 Correctif cumulatif (KB5000008)", day)).ToArray();

        var nothingSince = await Failed(PendingUnread(), lines);
        Assert.Equal(FindingStatus.Improvable, nothingSince.Status);
        Assert.Contains("Résolution des problèmes", nothingSince.Advice, StringComparison.Ordinal);

        // Définitions de Defender, pilote et application du Store installés depuis : ce ne sont pas des mises à jour de Windows.
        var routineSince = await Failed(
            PendingUnread(),
            [.. lines,
                Line(DefenderTitle, 20, result: 2, client: "Windows Defender", service: null),
                Line("INTEL - System - 10.1.16.7", 21, result: 2, client: DriverClient),
                Line("9NRZT3Q9R3DL-Microsoft.WindowsAppRuntime.2", 22, result: 2, client: "Acquisition;x", service: StoreService)]);
        Assert.Equal(FindingStatus.Improvable, routineSince.Status);
        Assert.DoesNotContain(MaybeReplaced, routineSince.Current, StringComparison.Ordinal);

        var windowsSince = await Failed(PendingUnread(), [.. lines, Line("2026-09 Correctif cumulatif (KB5000009)", 20, result: 2)]);
        Assert.Equal(FindingStatus.Info, windowsSince.Status);
        Assert.Null(windowsSince.Expected);
        Assert.Null(windowsSince.Advice);
        Assert.EndsWith(MaybeReplaced, windowsSince.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_without_a_code_says_so_instead_of_zero()
    {
        var finding = await Failed(Line("Correctif (KB5000009)", 2, code: 0));

        Assert.Contains("sans code noté", finding.Current, StringComparison.Ordinal);
        Assert.DoesNotContain("0x00000000", finding.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_history_cut_at_the_limit_says_how_many_lines_were_read()
    {
        var full = Enumerable.Range(0, WindowsUpdateModule.MaxHistoryEntries - 1)
            .Select(i => Line($"9NRZT3Q9R{i:000}-Microsoft.App", 1, result: 2, client: "Acquisition;x", service: StoreService) with { DateUtc = Now.UtcDateTime.AddHours(-i) })
            .Append(Line("Correctif (KB5000009)", 2))
            .ToArray();

        var cut = await Failed(full);
        Assert.Contains($"(sur les {WindowsUpdateModule.MaxHistoryEntries} dernières lignes de l'historique)", cut.Current, StringComparison.Ordinal);

        var shorter = await Failed(full[1..]);
        Assert.DoesNotContain("dernières lignes", shorter.Current, StringComparison.Ordinal);
    }

    [Fact]
    public void A_very_long_title_is_not_a_driver_and_does_not_hang()
    {
        var title = string.Concat(Enumerable.Repeat("a - ", 5000)) + "x";

        Assert.False(UpdateParsers.IsDriverTitle(title));
        Assert.Equal(UpdateFamily.Windows, UpdateParsers.ClassifyHistory(Line(title, 1, client: "Autre", service: null)));
    }

    [Fact]
    public async Task Empty_history_says_so()
    {
        Assert.Equal("historique vide", (await Failed()).Current);
    }

    [Fact]
    public async Task Hold_noted_by_windows_gives_number_and_reason_and_says_to_wait()
    {
        var registry = Hold("0", ids: HoldNumber, reasons: OtherReason);

        var finding = Get(await Detect(registry: registry), "M03.safeguard-hold");

        Assert.Equal(FindingStatus.Info, finding.Status);
        Assert.Equal("Windows note une retenue pour la version 24H2, numéro 52796844, motif indiqué par Windows : Other", finding.Current);
        Assert.Contains("Il suffit d'attendre", finding.Advice, StringComparison.Ordinal);
        Assert.Contains("rester notée à tort", finding.Advice, StringComparison.Ordinal);
        Assert.Contains("release-health", finding.Advice, StringComparison.Ordinal);
        Assert.Equal(WindowsUpdateModule.WindowsUpdatePage, finding.SettingsPage);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData(null)]
    public async Task Hold_texts_never_offer_a_way_around(string? gwx)
    {
        var finding = Get(await Detect(registry: Hold(gwx, status: gwx ?? "0", ids: HoldNumber)), "M03.safeguard-hold");
        var text = $"{finding.Current} {finding.Explanation} {finding.Advice}";

        foreach (var banned in WaysAround)
        {
            Assert.DoesNotContain(banned, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task No_hold_is_what_windows_says_as_read_on_a_real_pc()
    {
        var registry = Hold("2", version: "GE26H2");
        registry.Set(RegistryHive.LocalMachine, $@"{WindowsUpdateModule.HoldIndicatorsKey}\UNV", "DataVer", "2775");

        var finding = Get(await Detect(registry: registry), "M03.safeguard-hold");

        Assert.Equal("Windows n'indique aucune retenue", finding.Current);
        Assert.Null(finding.Advice);
        Assert.Null(finding.SettingsPage);
    }

    [Fact]
    public async Task An_old_version_key_does_not_contradict_the_status_microsoft_documents()
    {
        var registry = Hold("2", status: "0", ids: HoldNumber);

        var finding = Get(await Detect(registry: registry), "M03.safeguard-hold");

        Assert.Equal("Windows n'indique aucune retenue", finding.Current);
    }

    [Fact]
    public async Task Without_the_main_status_the_version_key_decides()
    {
        var finding = Get(await Detect(registry: Hold(null, status: "0", ids: "52796844")), "M03.safeguard-hold");

        Assert.StartsWith("Windows note une retenue pour la version 24H2, numéro 52796844", finding.Current, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("1", null)]
    [InlineData(null, "7")]
    public async Task Undocumented_or_missing_status_concludes_nothing(string? gwx, string? status)
    {
        var registry = Hold(gwx, version: status is null ? null : "GE24H2", status: status);

        var finding = Get(await Detect(registry: registry), "M03.safeguard-hold");

        Assert.Equal("Windows n'en dit rien sur ce PC", finding.Current);
        Assert.Equal(FindingStatus.Info, finding.Status);
    }

    [Fact]
    public async Task Hold_refused_by_windows_asks_for_rights()
    {
        var registry = HealthyRegistry().Deny(RegistryHive.LocalMachine, WindowsUpdateModule.HoldIndicatorsKey);

        Assert.Equal(FindingStatus.Unknown, Get(await Detect(registry: registry), "M03.safeguard-hold").Status);
    }

    /// <summary>Agent Windows Update simulé : aucune recherche réseau.</summary>
    internal sealed class FakeUpdateAgent : IWindowsUpdateAgent
    {
        public List<PendingUpdate> Updates { get; } = [];

        public AutomaticUpdatesResults? Results { get; init; }

        public Exception? ResultsError { get; init; }

        public Exception? SearchError { get; set; }

        public string? ReceivedCriteria { get; private set; }

        public AutomaticUpdatesResults? GetAutomaticUpdatesResults() => ResultsError is null ? Results : throw ResultsError;

        /// <summary><c>null</c> : Windows ne donne pas l'historique.</summary>
        public List<UpdateHistoryEntry>? History { get; set; } = [];

        public Exception? HistoryError { get; init; }

        public IReadOnlyList<UpdateHistoryEntry>? GetHistory(int maxEntries) => HistoryError is null ? History : throw HistoryError;

        /// <summary>Si posé, la recherche ne répond qu'une fois ce signal donné (serveur lent).</summary>
        public TaskCompletionSource? Hold { get; init; }

        /// <summary>Si posée, la recherche tourne comme la vraie : sur le fil dédié de <see cref="ComWindowsUpdateAgent"/>.</summary>
        public Func<IReadOnlyList<PendingUpdate>>? SearchOnThread { get; init; }

        public bool? ReceivedOnline { get; private set; }

        public async Task<IReadOnlyList<PendingUpdate>> SearchAsync(string criteria, bool online, TimeSpan timeout, CancellationToken cancellationToken)
        {
            ReceivedCriteria = criteria;
            ReceivedOnline = online;
            if (SearchOnThread is not null)
            {
                return await ComWindowsUpdateAgent.RunOnDedicatedThread(SearchOnThread, timeout, cancellationToken).ConfigureAwait(false);
            }

            if (Hold is not null)
            {
                await Hold.Task.ConfigureAwait(false);
            }

            return SearchError is null ? Updates : throw SearchError;
        }
    }
}
