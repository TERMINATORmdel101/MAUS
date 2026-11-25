using Maus.Core.Modules.M04Privacy;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Tests.Modules.M04Privacy;

public class PrivacyFixTests
{
    private static FakeRegistry WindowsDefaults() => new FakeRegistry()
        .Set(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", 3)
        .Set(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config", "DODownloadMode", 3)
        .Set(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 1);

    [Fact]
    public async Task Pro_edition_round_trip_returns_to_the_initial_state()
    {
        var registry = WindowsDefaults();
        var audit = TestContext.Create(registry);

        var (plan, _) = await Fixes.RoundTrip.AssertAsync(new PrivacyModule(), audit, registry);

        Assert.Contains(plan, c => c.Id == "M04.advertising-id" && c.Writes.Count == 2);
        Assert.Contains(plan, c => c.Id == "M04.language-list");
        Assert.False(plan.Single(c => c.Id == "M04.web-search").Recommended);
        Assert.Equal("1", plan.Single(c => c.Id == "M04.diagnostic-data").Writes.Single().Value!.Data);
    }

    [Fact]
    public async Task Home_edition_round_trip_returns_to_the_initial_state_and_warns_about_the_policies()
    {
        var registry = WindowsDefaults();
        var audit = TestContext.Create(registry, windows: new WindowsInfo("Windows 11 Famille", "Core", "25H2", 26200, 1000));

        var (plan, _) = await Fixes.RoundTrip.AssertAsync(new PrivacyModule(), audit, registry);

        Assert.All(plan.Where(c => c.Id is "M04.diagnostic-data" or "M04.web-search" or "M04.activity-history"),
            c => Assert.Contains("n'est pas garanti", c.Description, StringComparison.Ordinal));
        Assert.Contains(plan, c => c.Id == "M04.diagnostic-data");
    }

    [Fact]
    public async Task Enterprise_edition_round_trip_returns_to_the_initial_state()
    {
        var registry = WindowsDefaults();
        var audit = TestContext.Create(registry, windows: new WindowsInfo("Windows 11 Entreprise", "Enterprise", "25H2", 26200, 1000));

        var (plan, _) = await Fixes.RoundTrip.AssertAsync(new PrivacyModule(), audit, registry);

        Assert.DoesNotContain("n'est pas garanti", plan.Single(c => c.Id == "M04.diagnostic-data").Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recall_snapshots_are_blocked_by_policy_with_a_warning_and_restored_by_deleting_it()
    {
        var registry = WindowsDefaults();
        var cim = new FakeCim().Answer(PrivacyKeys.RecallQuery, new Dictionary<string, object?> { ["Name"] = "Recall", ["InstallState"] = 1u });
        var audit = TestContext.Create(registry, cim);

        var (plan, _) = await Fixes.RoundTrip.AssertAsync(new PrivacyModule(), audit, registry);

        var recall = plan.Single(c => c.Id == "M04.recall");
        Assert.False(recall.Recommended);
        Assert.Contains("supprimés", recall.Warning, StringComparison.Ordinal);
        Assert.Equal(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI\DisableAIDataAnalysis", recall.Writes.Single().Key.Describe());
        Assert.Null(registry.GetValue(RegistryHive.LocalMachine, PrivacyKeys.WindowsAiPolicy, "DisableAIDataAnalysis"));
    }

    [Fact]
    public async Task Home_edition_gets_no_policy_only_changes()
    {
        var registry = WindowsDefaults();
        var audit = TestContext.Create(registry, windows: new WindowsInfo("Windows 11 Famille", "Core", "25H2", 26200, 1000));
        var module = new PrivacyModule();

        var plan = module.Plan(audit, await module.DetectAsync(audit, CancellationToken.None));

        Assert.DoesNotContain(plan, c => c.Id is "M04.diagnostic-logs" or "M04.delivery-optimization" or "M04.recall");
        Assert.Single(plan.Single(c => c.Id == "M04.advertising-id").Writes);
    }

    /// <summary>
    /// Une stratégie posée fait dire à Paramètres « géré par votre organisation » et grise l'interrupteur : chaque correction qui
    /// en pose une le dit avant d'appliquer, y compris sur Famille ; celles qui n'en posent pas ne le disent pas.
    /// </summary>
    [Theory]
    [InlineData("Professional", "Windows 11 Pro")]
    [InlineData("Core", "Windows 11 Famille")]
    public async Task Every_change_that_sets_a_policy_says_what_settings_will_show(string edition, string name)
    {
        var audit = TestContext.Create(WindowsDefaults(), windows: new WindowsInfo(name, edition, "25H2", 26200, 1000));
        var module = new PrivacyModule();

        var plan = module.Plan(audit, await module.DetectAsync(audit, CancellationToken.None));

        Assert.Contains(plan, c => c.SetsPolicy && c.Recommended);
        Assert.All(plan.Where(c => c.SetsPolicy), c => Assert.Contains("gérés par votre organisation", c.Risk, StringComparison.Ordinal));
        Assert.All(plan.Where(c => !c.SetsPolicy), c => Assert.DoesNotContain("organisation", c.Risk ?? string.Empty, StringComparison.Ordinal));
        Assert.Contains("Windows Insider", plan.Single(c => c.Id == "M04.diagnostic-data").Risk, StringComparison.Ordinal);
        Assert.Contains("gérés par votre organisation", plan.Single(c => c.Id == "M04.activity-history").Risk, StringComparison.Ordinal);
    }

    [Fact]
    public void Removing_a_policy_or_writing_outside_policies_is_not_setting_a_policy()
    {
        static Maus.Core.Fixes.PlannedChange With(params Maus.Core.Fixes.SettingWrite[] writes) => new() { Id = "x", ModuleId = "M04", Title = "t", Description = "d", Writes = writes };
        var policy = Maus.Core.Fixes.SettingKey.Registry("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed");
        var plain = Maus.Core.Fixes.SettingKey.Registry("HKCU", @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled");

        Assert.True(With(new Maus.Core.Fixes.SettingWrite(policy, Maus.Core.Fixes.SettingValue.Dword(0))).SetsPolicy);
        Assert.False(With(new Maus.Core.Fixes.SettingWrite(policy, null)).SetsPolicy);
        Assert.False(With(new Maus.Core.Fixes.SettingWrite(plain, Maus.Core.Fixes.SettingValue.Dword(0))).SetsPolicy);
        Assert.Null(Maus.Core.Fixes.PlannedChange.WithPolicyRisk(null, setsPolicy: false));
        Assert.StartsWith("Risque.", Maus.Core.Fixes.PlannedChange.WithPolicyRisk("Risque.", setsPolicy: true), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Level_zero_is_offered_but_not_preselected_on_enterprise()
    {
        var registry = WindowsDefaults();
        var audit = TestContext.Create(registry, windows: new WindowsInfo("Windows 11 Entreprise", "Enterprise", "25H2", 26200, 1000));
        var module = new PrivacyModule();

        var plan = module.Plan(audit, await module.DetectAsync(audit, CancellationToken.None));
        var diagnostic = plan.Single(c => c.Id == "M04.diagnostic-data");

        Assert.Equal("0", diagnostic.Writes.Single().Value!.Data);
        Assert.False(diagnostic.Recommended);
        Assert.NotNull(diagnostic.Risk);
    }
}
