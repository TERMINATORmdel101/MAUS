using Maus.Core.Modules.M04Privacy;
using Maus.Core.Platform;
using Maus.Core.Rules;
using Microsoft.Win32;
using static Maus.Core.Modules.M04Privacy.PrivacyKeys;

namespace Maus.Core.Tests.Modules.M04Privacy;

public class PrivacyModuleTests
{
    private const RegistryHive Hklm = RegistryHive.LocalMachine;
    private const RegistryHive Hkcu = RegistryHive.CurrentUser;

    private static readonly WindowsInfo Pro = new("Windows 11 Pro", "Professional", "25H2", 26200, 1000);
    private static readonly WindowsInfo Home = new("Windows 11 Famille", "Core", "25H2", 26200, 1000);
    private static readonly WindowsInfo Enterprise = new("Windows 11 Entreprise", "Enterprise", "25H2", 26200, 1000);

    [Fact]
    public void Module_metadata_follows_the_spec()
    {
        var module = new PrivacyModule();

        Assert.Equal("M04", module.Id);
        Assert.Equal("Confidentialité et télémétrie", module.Title);
        Assert.Equal(40, module.Order);
    }

    [Fact]
    public async Task Fresh_install_reports_optimisations_but_never_warnings()
    {
        var findings = await Detect(FreshRegistry(), FreshCim(), new FakePackages().Add(CopilotPackage));

        Assert.InRange(findings.Count, 5, 25);
        Assert.All(findings, f => Assert.StartsWith("M04.", f.Id, StringComparison.Ordinal));
        Assert.Equal(findings.Count, findings.Select(f => f.Id).Distinct().Count());
        Assert.DoesNotContain(findings, f => f.Status is FindingStatus.Problem or FindingStatus.Warning);
        Assert.Equal("M04.diagnostic-data", findings[0].Id);

        foreach (var id in new[]
        {
            "M04.diagnostic-data", "M04.diagtrack", "M04.diagnostic-logs", "M04.ceip-tasks", "M04.feedback-frequency",
            "M04.tailored-experiences", "M04.advertising-id", "M04.settings-suggestions", "M04.language-list",
            "M04.app-launch-tracking", "M04.implicit-text", "M04.implicit-ink", "M04.tips-silent-apps",
            "M04.start-recommendations", "M04.lockscreen-tips", "M04.web-search", "M04.search-highlights",
            "M04.activity-history", "M04.delivery-optimization",
        })
        {
            var finding = Single(findings, id);
            Assert.Equal(FindingStatus.Improvable, finding.Status);
            Assert.True(finding.Fixable, id);
            Assert.False(string.IsNullOrWhiteSpace(finding.Advice), id);
        }

        Assert.Equal(FindingStatus.Ok, Single(findings, "M04.online-speech").Status);
        Assert.Equal(FindingStatus.Ok, Single(findings, "M04.error-reporting").Status);
        Assert.Equal(FindingStatus.Ok, Single(findings, "M04.recall").Status);
        var copilot = Single(findings, "M04.copilot");
        Assert.Equal(FindingStatus.Info, copilot.Status);
        Assert.True(copilot.Fixable);
    }

    [Fact]
    public async Task Deviations_open_the_settings_page_named_in_the_advice()
    {
        var findings = await Detect(FreshRegistry(), FreshCim(), new FakePackages().Add(CopilotPackage));

        foreach (var (id, page) in new[]
        {
            ("M04.diagnostic-data", "ms-settings:privacy-feedback"),
            ("M04.feedback-frequency", "ms-settings:privacy-feedback"),
            ("M04.tailored-experiences", "ms-settings:privacy-feedback"),
            ("M04.advertising-id", "ms-settings:privacy-general"),
            ("M04.settings-suggestions", "ms-settings:privacy-general"),
            ("M04.tips-silent-apps", "ms-settings:notifications"),
            ("M04.lockscreen-tips", "ms-settings:lockscreen"),
            ("M04.start-recommendations", "ms-settings:personalization-start"),
            ("M04.search-highlights", "ms-settings:search-permissions"),
            ("M04.activity-history", "ms-settings:privacy-activityhistory"),
        })
        {
            Assert.Equal(page, Single(findings, id).SettingsPage);
        }

        // Stratégies, service, tâches planifiées, page « Rechercher » sans adresse documentée, mode 1 laissé au choix, corrections de MAUS : aucun bouton.
        foreach (var id in new[]
        {
            "M04.diagtrack", "M04.diagnostic-logs", "M04.ceip-tasks", "M04.web-search", "M04.delivery-optimization",
            "M04.copilot", "M04.recall", "M04.error-reporting",
        })
        {
            Assert.Null(Single(findings, id).SettingsPage);
        }
    }

    [Fact]
    public async Task Tuned_pc_is_fully_compliant()
    {
        var findings = await Detect(TunedRegistry(), TunedCim(), new FakePackages().Add("Microsoft.MicrosoftOfficeHub"));

        Assert.All(findings, f => Assert.True(f.Status == FindingStatus.Ok, $"{f.Id} : {f.Status} ({f.Current})"));
        Assert.All(findings, f => Assert.False(f.Fixable, f.Id));
        Assert.All(findings, f => Assert.Null(f.SettingsPage));
    }

    [Theory]
    [InlineData(null, 3, "Professional", FindingStatus.Improvable)]
    [InlineData(null, 2, "Professional", FindingStatus.Improvable)]
    [InlineData(null, 1, "Professional", FindingStatus.Ok)]
    [InlineData(null, 0, "Professional", FindingStatus.Info)]
    [InlineData(0, null, "Core", FindingStatus.Info)]
    [InlineData(1, 3, "Professional", FindingStatus.Ok)]
    [InlineData(3, 1, "Professional", FindingStatus.Improvable)]
    [InlineData(0, 3, "Enterprise", FindingStatus.Ok)]
    [InlineData(null, 1, "Education", FindingStatus.Improvable)]
    [InlineData(null, null, "Professional", FindingStatus.Unknown)]
    public void Diagnostic_level_depends_on_policy_state_and_edition(int? policy, int? state, string edition, FindingStatus expected)
    {
        var registry = new FakeRegistry();
        if (policy is not null)
        {
            registry.Set(Hklm, DataCollectionPolicy, "AllowTelemetry", policy.Value);
        }

        if (state is not null)
        {
            registry.Set(Hklm, DataCollectionState, "AllowTelemetry", state.Value);
        }

        var finding = PrivacyRegistryChecks.DiagnosticData(registry, new WindowsInfo("Windows 11", edition, "25H2", 26200, 1000));

        Assert.Equal(expected, finding.Status);
    }

    [Fact]
    public void Level_zero_on_pro_is_explained_as_equivalent_to_required()
    {
        var registry = new FakeRegistry().Set(Hklm, DataCollectionPolicy, "AllowTelemetry", 0);

        var finding = PrivacyRegistryChecks.DiagnosticData(registry, Pro);

        Assert.Contains("équivaut à 1", finding.Explanation, StringComparison.Ordinal);
        Assert.Contains("imposé par stratégie", finding.Current, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "0 (diagnostic désactivé)")]
    [InlineData(1, "1 (Requises)")]
    [InlineData(3, "3 (Facultatives)")]
    [InlineData(7, "7 (valeur inconnue)")]
    public void Diagnostic_levels_are_labelled_in_plain_french(int level, string label) =>
        Assert.Equal(label, PrivacyRegistryChecks.DescribeDiagnosticLevel(level));

    [Fact]
    public void Denied_policy_key_becomes_admin_required_not_a_problem()
    {
        var registry = new FakeRegistry().Deny(Hklm, DataCollectionPolicy);

        var finding = PrivacyRegistryChecks.DiagnosticData(registry, Pro);

        Assert.Equal(FindingStatus.Unknown, finding.Status);
        Assert.Contains("administrateur", finding.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_limits_have_no_guaranteed_effect_on_home()
    {
        var registry = new FakeRegistry();

        Assert.Equal(FindingStatus.Info, PrivacyRegistryChecks.DiagnosticLogs(registry, Home).Status);
        Assert.Equal(FindingStatus.Improvable, PrivacyRegistryChecks.DiagnosticLogs(registry, Pro).Status);

        registry.Set(Hklm, DataCollectionPolicy, "LimitDiagnosticLogCollection", 1).Set(Hklm, DataCollectionPolicy, "LimitDumpCollection", 1);
        var finding = PrivacyRegistryChecks.DiagnosticLogs(registry, Pro);
        Assert.Equal(FindingStatus.Ok, finding.Status);
        Assert.Equal("journaux : limités ; vidages mémoire : limités", finding.Current);
    }

    [Fact]
    public void Policies_are_ignored_on_home_where_only_the_effective_setting_counts()
    {
        var registry = new FakeRegistry()
            .Set(Hklm, AdvertisingPolicy, "DisabledByGroupPolicy", 1)
            .Set(Hkcu, CloudContentPolicy, "DisableTailoredExperiencesWithDiagnosticData", 1)
            .Set(Hklm, DataCollectionPolicy, "DoNotShowFeedbackNotifications", 1)
            .Set(Hklm, SearchPolicy, "EnableDynamicContentInWSB", 0);

        Assert.Equal(FindingStatus.Ok, PrivacyRegistryChecks.AdvertisingId(registry, policiesHonored: true).Status);
        Assert.Equal(FindingStatus.Ok, PrivacyRegistryChecks.TailoredExperiences(registry, policiesHonored: true).Status);
        Assert.Equal(FindingStatus.Ok, PrivacyRegistryChecks.FeedbackFrequency(registry, policiesHonored: true).Status);
        Assert.Equal(FindingStatus.Ok, PrivacyRegistryChecks.SearchHighlights(registry, policiesHonored: true).Status);

        Assert.Equal(FindingStatus.Improvable, PrivacyRegistryChecks.AdvertisingId(registry, policiesHonored: false).Status);
        Assert.Equal(FindingStatus.Improvable, PrivacyRegistryChecks.TailoredExperiences(registry, policiesHonored: false).Status);
        Assert.Equal(FindingStatus.Improvable, PrivacyRegistryChecks.FeedbackFrequency(registry, policiesHonored: false).Status);
        Assert.Equal(FindingStatus.Improvable, PrivacyRegistryChecks.SearchHighlights(registry, policiesHonored: false).Status);
    }

    [Fact]
    public async Task Home_edition_run_uses_effective_values()
    {
        var registry = FreshRegistry().Set(Hklm, AdvertisingPolicy, "DisabledByGroupPolicy", 1);

        var findings = await Detect(registry, FreshCim(), new FakePackages(), Home);

        Assert.Equal(FindingStatus.Improvable, Single(findings, "M04.advertising-id").Status);
        Assert.Equal(FindingStatus.Info, Single(findings, "M04.diagnostic-logs").Status);
        Assert.Contains("Famille", Single(findings, "M04.activity-history").Explanation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "activées (par défaut)", FindingStatus.Improvable)]
    [InlineData(1, "activées", FindingStatus.Improvable)]
    [InlineData(2, "valeur 2 non documentée", FindingStatus.Info)]
    [InlineData(0, "désactivées", FindingStatus.Ok)]
    public void Tailored_experiences_toggle_is_described(int? value, string current, FindingStatus expected)
    {
        var registry = new FakeRegistry();
        if (value is not null)
        {
            registry.Set(Hkcu, PrivacyUser, "TailoredExperiencesWithDiagnosticDataEnabled", value.Value);
        }

        var finding = PrivacyRegistryChecks.TailoredExperiences(registry, policiesHonored: true);

        Assert.Equal(expected, finding.Status);
        Assert.Equal(current, finding.Current);
    }

    [Fact]
    public void Undocumented_toggle_values_are_information_unless_a_policy_decides()
    {
        var registry = new FakeRegistry()
            .Set(Hkcu, AdvertisingUser, "Enabled", 2)
            .Set(Hkcu, ExplorerAdvanced, "Start_IrisRecommendations", 5)
            .Set(Hkcu, SearchSettingsUser, "IsDynamicSearchBoxEnabled", 2);

        Assert.Equal(FindingStatus.Info, PrivacyRegistryChecks.AdvertisingId(registry, policiesHonored: true).Status);
        Assert.Equal("valeur 5 non documentée", PrivacyRegistryChecks.StartRecommendations(registry, Pro).Current);
        Assert.Equal(FindingStatus.Info, PrivacyRegistryChecks.SearchHighlights(registry, policiesHonored: true).Status);

        // Le conseil renvoie vers Confidentialité et sécurité : le bouton n'y mène que si l'interrupteur s'y trouve vraiment.
        Assert.Equal("ms-settings:privacy-general", PrivacyRegistryChecks.AdvertisingId(registry, policiesHonored: true).SettingsPage);
        Assert.Equal("ms-settings:search-permissions", PrivacyRegistryChecks.SearchHighlights(registry, policiesHonored: true).SettingsPage);
        Assert.Null(PrivacyRegistryChecks.StartRecommendations(registry, Pro).SettingsPage);

        registry.Set(Hklm, AdvertisingPolicy, "DisabledByGroupPolicy", 1);
        Assert.Equal(FindingStatus.Ok, PrivacyRegistryChecks.AdvertisingId(registry, policiesHonored: true).Status);
    }

    [Theory]
    [InlineData(null, "automatique (par défaut)", FindingStatus.Improvable)]
    [InlineData(0, "jamais", FindingStatus.Ok)]
    [InlineData(1, "1 demande(s) par période", FindingStatus.Improvable)]
    public void Feedback_frequency_is_read_from_siuf_rules(int? perPeriod, string current, FindingStatus expected)
    {
        var registry = new FakeRegistry();
        if (perPeriod is not null)
        {
            registry.Set(Hkcu, SiufRules, "NumberOfSIUFInPeriod", perPeriod.Value);
        }

        var finding = PrivacyRegistryChecks.FeedbackFrequency(registry, policiesHonored: true);

        Assert.Equal(expected, finding.Status);
        Assert.Equal(current, finding.Current);
    }

    [Fact]
    public void Content_delivery_values_count_absent_as_enabled()
    {
        var registry = new FakeRegistry()
            .Set(Hkcu, ContentDelivery, "SubscribedContent-338393Enabled", 0)
            .Set(Hkcu, ContentDelivery, "SubscribedContent-353694Enabled", 0);

        var finding = PrivacyRegistryChecks.SettingsSuggestions(registry);

        Assert.Equal(FindingStatus.Improvable, finding.Status);
        Assert.Equal("actives (1 sur 3)", finding.Current);
    }

    [Fact]
    public void Start_recommendations_policy_counts_on_pro_only()
    {
        var registry = new FakeRegistry().Set(Hkcu, ExplorerPolicy, "HideRecommendedPersonalizedSites", 1);

        Assert.Equal(FindingStatus.Ok, PrivacyRegistryChecks.StartRecommendations(registry, Pro).Status);
        Assert.Equal(FindingStatus.Improvable, PrivacyRegistryChecks.StartRecommendations(registry, Home).Status);
    }

    [Fact]
    public void Web_search_uses_policies_and_treats_bing_value_as_a_hint()
    {
        Assert.Equal(FindingStatus.Improvable, PrivacyRegistryChecks.WebSearch(new FakeRegistry(), Pro).Status);

        var bingOnly = new FakeRegistry().Set(Hkcu, SearchUser, "BingSearchEnabled", 0);
        var hint = PrivacyRegistryChecks.WebSearch(bingOnly, Pro);
        Assert.Equal(FindingStatus.Info, hint.Status);
        Assert.Contains("BingSearchEnabled", hint.Current, StringComparison.Ordinal);

        var machinePolicy = new FakeRegistry().Set(Hklm, ExplorerPolicy, "DisableSearchBoxSuggestions", 1);
        Assert.Equal(FindingStatus.Ok, PrivacyRegistryChecks.WebSearch(machinePolicy, Home).Status);

        var connected = new FakeRegistry().Set(Hklm, SearchPolicy, "ConnectedSearchUseWeb", 0);
        Assert.Equal(FindingStatus.Improvable, PrivacyRegistryChecks.WebSearch(connected, Pro).Status);
        Assert.Equal(FindingStatus.Ok, PrivacyRegistryChecks.WebSearch(connected, Enterprise).Status);
    }

    [Fact]
    public void Activity_history_lists_partial_policies()
    {
        var registry = new FakeRegistry().Set(Hklm, SystemPolicy, "EnableActivityFeed", 0);

        var finding = PrivacyRegistryChecks.ActivityHistory(registry, Pro);

        Assert.Equal(FindingStatus.Improvable, finding.Status);
        Assert.Equal("EnableActivityFeed = 0, PublishUserActivities = absente, UploadUserActivities = absente", finding.Current);
    }

    [Fact]
    public void Disabled_error_reporting_is_only_information()
    {
        var registry = new FakeRegistry().Set(Hklm, ErrorReportingPolicy, "Disabled", 1);

        var finding = PrivacyRegistryChecks.ErrorReporting(registry);

        Assert.Equal(FindingStatus.Info, finding.Status);
        Assert.Contains("Module 2", finding.Explanation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Auto", "Running", FindingStatus.Improvable, "démarrage automatique, en cours d'exécution")]
    [InlineData("Manual", "Stopped", FindingStatus.Improvable, "démarrage manuel, arrêté")]
    [InlineData("Disabled", "Stopped", FindingStatus.Ok, "désactivé, arrêté")]
    public void DiagTrack_service_state_is_parsed(string startMode, string state, FindingStatus expected, string current)
    {
        var cim = new FakeCim().Answer(DiagTrackQuery, Row(("Name", "DiagTrack"), ("StartMode", startMode), ("State", state)));

        var finding = PrivacySystemChecks.DiagTrack(cim);

        Assert.Equal(expected, finding.Status);
        Assert.Equal(current, finding.Current);
        Assert.Contains("avancée", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_or_unreadable_DiagTrack_is_never_a_problem()
    {
        Assert.Equal(FindingStatus.Ok, PrivacySystemChecks.DiagTrack(new FakeCim()).Status);

        var denied = new FakeCim().Throw(DiagTrackQuery, new MausAccessDeniedException("refusé"));
        Assert.Equal(FindingStatus.Unknown, PrivacySystemChecks.DiagTrack(denied).Status);
    }

    [Fact]
    public void Ceip_tasks_state_enum_is_parsed()
    {
        var ready = new FakeCim().Answer(CeipTasksQuery, TaskSchedulerScope, Task("Consolidator", 3), Task("UsbCeip", 1), Task("Other", 3));
        var finding = PrivacySystemChecks.CeipTasksState(ready);
        Assert.Equal(FindingStatus.Improvable, finding.Status);
        Assert.Equal("Consolidator : prête ; UsbCeip : désactivée", finding.Current);

        var disabled = new FakeCim().Answer(CeipTasksQuery, TaskSchedulerScope, Task("Consolidator", 1), Task("UsbCeip", 1));
        Assert.Equal(FindingStatus.Ok, PrivacySystemChecks.CeipTasksState(disabled).Status);

        var absent = PrivacySystemChecks.CeipTasksState(new FakeCim());
        Assert.Equal(FindingStatus.Ok, absent.Status);
        Assert.Equal("tâches absentes", absent.Current);
    }

    [Fact]
    public void Unavailable_task_scheduler_namespace_is_unknown()
    {
        var cim = new FakeCim().Throw(CeipTasksQuery, new DataSourceUnavailableException("absent"), TaskSchedulerScope);

        Assert.Equal(FindingStatus.Unknown, PrivacySystemChecks.CeipTasksState(cim).Status);
    }

    [Theory]
    [InlineData(0, FindingStatus.Ok, null)]
    [InlineData(99, FindingStatus.Ok, null)]
    [InlineData(1, FindingStatus.Improvable, null)]
    [InlineData(2, FindingStatus.Improvable, "ms-settings:delivery-optimization")]
    [InlineData(3, FindingStatus.Improvable, "ms-settings:delivery-optimization")]
    [InlineData(100, FindingStatus.Improvable, null)]
    public void Delivery_optimization_mode_is_read_like_Get_DOConfig(byte mode, FindingStatus expected, string? settingsPage)
    {
        var cim = new FakeCim().Answer(DeliveryOptimizationQuery, DeliveryOptimizationScope,
            Row(("DownloadMode", mode), ("DownloadModeProvider", 8)));

        var finding = PrivacySystemChecks.DeliveryOptimization(cim, new FakeRegistry());

        Assert.Equal(expected, finding.Status);
        Assert.Equal(settingsPage, finding.SettingsPage);
        Assert.StartsWith(PrivacySystemChecks.DescribeDownloadMode(mode), finding.Current, StringComparison.Ordinal);
        Assert.EndsWith("(choisi dans Paramètres)", finding.Current, StringComparison.Ordinal);
    }

    [Fact]
    public void Delivery_optimization_falls_back_to_registry_when_wmi_is_missing()
    {
        var cim = new FakeCim().Throw(DeliveryOptimizationQuery, new DataSourceUnavailableException("absent"), DeliveryOptimizationScope);
        var policy = new FakeRegistry().Set(Hklm, DeliveryOptimizationPolicy, "DODownloadMode", 0);
        var local = new FakeRegistry().Set(Hklm, DeliveryOptimizationConfig, "DODownloadMode", 3);

        var byPolicy = PrivacySystemChecks.DeliveryOptimization(cim, policy);
        Assert.Equal(FindingStatus.Ok, byPolicy.Status);
        Assert.Contains("stratégie", byPolicy.Current, StringComparison.Ordinal);
        Assert.Equal(FindingStatus.Improvable, PrivacySystemChecks.DeliveryOptimization(cim, local).Status);
        Assert.Equal(FindingStatus.Unknown, PrivacySystemChecks.DeliveryOptimization(new FakeCim(), new FakeRegistry()).Status);
    }

    [Fact]
    public void Denied_delivery_optimization_without_fallback_requires_admin()
    {
        var cim = new FakeCim().Throw(DeliveryOptimizationQuery, new MausAccessDeniedException("refusé"), DeliveryOptimizationScope);

        var finding = PrivacySystemChecks.DeliveryOptimization(cim, new FakeRegistry());

        Assert.Equal(FindingStatus.Unknown, finding.Status);
        Assert.Contains("administrateur", finding.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Copilot_app_is_a_choice_and_office_hub_is_ignored()
    {
        var installed = PrivacySystemChecks.Copilot(new FakePackages().Add(CopilotPackage, "1.25.0.0"));
        Assert.Equal(FindingStatus.Info, installed.Status);
        Assert.Equal("installée (version 1.25.0.0)", installed.Current);

        var officeHubOnly = PrivacySystemChecks.Copilot(new FakePackages().Add("Microsoft.MicrosoftOfficeHub"));
        Assert.Equal(FindingStatus.Ok, officeHubOnly.Status);
        Assert.Equal("non installée", officeHubOnly.Current);
    }

    [Fact]
    public void Unreadable_package_inventory_is_unknown()
    {
        Assert.Equal(FindingStatus.Unknown, PrivacySystemChecks.Copilot(new ThrowingPackages(new MausAccessDeniedException("refusé"))).Status);
        Assert.Equal(FindingStatus.Unknown, PrivacySystemChecks.Copilot(new ThrowingPackages(System.Runtime.InteropServices.Marshal.GetExceptionForHR(unchecked((int)0x80004005))!)).Status);
    }

    [Fact]
    public void Recall_is_left_to_the_user()
    {
        var registry = new FakeRegistry();
        Assert.Equal(FindingStatus.Ok, PrivacySystemChecks.Recall(new FakeCim(), registry).Status);

        var enabled = new FakeCim().Answer(RecallQuery, Row(("Name", "Recall"), ("InstallState", 1u)));
        var available = PrivacySystemChecks.Recall(enabled, registry);
        Assert.Equal(FindingStatus.Info, available.Status);
        Assert.True(available.Fixable);
        Assert.Contains("supprime les instantanés", available.Advice, StringComparison.Ordinal);

        registry.Set(Hkcu, WindowsAiPolicy, "DisableAIDataAnalysis", 1);
        Assert.Equal(FindingStatus.Ok, PrivacySystemChecks.Recall(enabled, registry).Status);

        var disabled = new FakeCim().Answer(RecallQuery, Row(("Name", "Recall"), ("InstallState", 2u)));
        Assert.Equal("désactivé", PrivacySystemChecks.Recall(disabled, new FakeRegistry()).Current);

        var unknownState = new FakeCim().Answer(RecallQuery, Row(("Name", "Recall"), ("InstallState", 4u)));
        Assert.Equal(FindingStatus.Unknown, PrivacySystemChecks.Recall(unknownState, new FakeRegistry()).Status);

        var denied = new FakeCim().Throw(RecallQuery, new MausAccessDeniedException("refusé"));
        Assert.Equal(FindingStatus.Unknown, PrivacySystemChecks.Recall(denied, new FakeRegistry()).Status);
    }

    [Fact]
    public void Catalog_rules_are_low_severity_module_rules()
    {
        var rules = EmbeddedCatalog.LoadRegistryRules("m04-privacy-rules.json");

        Assert.NotEmpty(rules);
        Assert.All(rules, rule =>
        {
            Assert.StartsWith("M04.", rule.Id, StringComparison.Ordinal);
            Assert.Equal(Severity.Low, rule.Severity);
            Assert.False(string.IsNullOrWhiteSpace(rule.Advice));
            _ = rule.ParsedHive;
        });
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => new PrivacyModule().DetectAsync(TestContext.Create(), source.Token));
    }

    private static async Task<IReadOnlyList<Finding>> Detect(FakeRegistry registry, FakeCim cim, FakePackages packages, WindowsInfo? windows = null) =>
        await new PrivacyModule().DetectAsync(
            TestContext.Create(registry, cim, windows: windows ?? Pro, packages: packages),
            CancellationToken.None);

    private static Finding Single(IReadOnlyList<Finding> findings, string id) => findings.Single(f => f.Id == id);

    private static Dictionary<string, object?> Row(params (string Name, object? Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value);

    private static Dictionary<string, object?> Task(string name, int state) =>
        Row(("TaskName", name), ("TaskPath", @"\Microsoft\Windows\Customer Experience Improvement Program\"), ("State", state));

    /// <summary>Installation neuve de Windows 11 Pro : diagnostic facultatif, tout activé par défaut.</summary>
    private static FakeRegistry FreshRegistry() => new FakeRegistry()
        .Set(Hklm, DataCollectionState, "AllowTelemetry", 3)
        .Set(Hkcu, PrivacyUser, "TailoredExperiencesWithDiagnosticDataEnabled", 1)
        .Set(Hkcu, AdvertisingUser, "Enabled", 1)
        .Set(Hkcu, @"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 0)
        .Set(Hkcu, @"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 0)
        .Set(Hkcu, ContentDelivery, "SubscribedContent-338389Enabled", 1)
        .Set(Hkcu, ContentDelivery, "SilentInstalledAppsEnabled", 1);

    private static FakeCim FreshCim() => new FakeCim()
        .Answer(DiagTrackQuery, Row(("Name", "DiagTrack"), ("StartMode", "Auto"), ("State", "Running")))
        .Answer(CeipTasksQuery, TaskSchedulerScope, Task("Consolidator", 3), Task("UsbCeip", 3))
        .Answer(DeliveryOptimizationQuery, DeliveryOptimizationScope, Row(("DownloadMode", (byte)1), ("DownloadModeProvider", 99)));

    /// <summary>PC réglé selon les valeurs « Standard » de la fiche, plus l'option avancée DiagTrack.</summary>
    private static FakeRegistry TunedRegistry()
    {
        var registry = new FakeRegistry()
            .Set(Hklm, DataCollectionPolicy, "AllowTelemetry", 1)
            .Set(Hklm, DataCollectionPolicy, "LimitDiagnosticLogCollection", 1)
            .Set(Hklm, DataCollectionPolicy, "LimitDumpCollection", 1)
            .Set(Hklm, DataCollectionPolicy, "DoNotShowFeedbackNotifications", 1)
            .Set(Hkcu, CloudContentPolicy, "DisableTailoredExperiencesWithDiagnosticData", 1)
            .Set(Hklm, AdvertisingPolicy, "DisabledByGroupPolicy", 1)
            .Set(Hkcu, AdvertisingUser, "Enabled", 0)
            .Set(Hkcu, @"Control Panel\International\User Profile", "HttpAcceptLanguageOptOut", 1)
            .Set(Hkcu, ExplorerAdvanced, "Start_TrackProgs", 0)
            .Set(Hkcu, ExplorerAdvanced, "Start_IrisRecommendations", 0)
            .Set(Hkcu, @"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 0)
            .Set(Hkcu, @"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1)
            .Set(Hkcu, @"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1)
            .Set(Hkcu, ExplorerPolicy, "DisableSearchBoxSuggestions", 1)
            .Set(Hklm, SearchPolicy, "EnableDynamicContentInWSB", 0)
            .Set(Hkcu, SiufRules, "NumberOfSIUFInPeriod", 0);
        foreach (var name in new[] { "EnableActivityFeed", "PublishUserActivities", "UploadUserActivities" })
        {
            registry.Set(Hklm, SystemPolicy, name, 0);
        }

        foreach (var name in new[]
        {
            "SubscribedContent-338393Enabled", "SubscribedContent-353694Enabled", "SubscribedContent-353696Enabled",
            "SubscribedContent-338389Enabled", "SoftLandingEnabled", "SilentInstalledAppsEnabled",
            "RotatingLockScreenOverlayEnabled", "SubscribedContent-338387Enabled",
        })
        {
            registry.Set(Hkcu, ContentDelivery, name, 0);
        }

        return registry;
    }

    private static FakeCim TunedCim() => new FakeCim()
        .Answer(DiagTrackQuery, Row(("Name", "DiagTrack"), ("StartMode", "Disabled"), ("State", "Stopped")))
        .Answer(CeipTasksQuery, TaskSchedulerScope, Task("Consolidator", 1), Task("UsbCeip", 1))
        .Answer(DeliveryOptimizationQuery, DeliveryOptimizationScope, Row(("DownloadMode", (byte)0), ("DownloadModeProvider", 5)));

    private sealed class ThrowingPackages(Exception exception) : IPackageInventory
    {
        public IReadOnlyList<InstalledPackage> GetUserPackages() => throw exception;
    }
}
