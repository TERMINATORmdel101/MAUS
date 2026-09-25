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
    public async Task Home_edition_gets_no_policy_only_changes()
    {
        var registry = WindowsDefaults();
        var audit = TestContext.Create(registry, windows: new WindowsInfo("Windows 11 Famille", "Core", "25H2", 26200, 1000));
        var module = new PrivacyModule();

        var plan = module.Plan(audit, await module.DetectAsync(audit, CancellationToken.None));

        Assert.DoesNotContain(plan, c => c.Id is "M04.diagnostic-logs" or "M04.delivery-optimization");
        Assert.Single(plan.Single(c => c.Id == "M04.advertising-id").Writes);
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
