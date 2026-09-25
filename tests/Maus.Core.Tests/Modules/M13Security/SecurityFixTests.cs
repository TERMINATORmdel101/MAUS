using Maus.Core.Modules.M13Security;
using Microsoft.Win32;

namespace Maus.Core.Tests.Modules.M13Security;

public class SecurityFixTests
{
    [Fact]
    public async Task Disabled_protections_are_restored_and_never_disabled()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, SecurityTradeoffModule.MemoryManagementKey, "FeatureSettingsOverride", 3)
            .Set(RegistryHive.LocalMachine, SecurityTradeoffModule.MemoryManagementKey, "FeatureSettingsOverrideMask", 3)
            .Set(RegistryHive.LocalMachine, SecurityTradeoffModule.CodeIntegrityConfigKey, "VulnerableDriverBlocklistEnable", 0);
        var audit = TestContext.Create(registry);

        var (plan, _) = await Fixes.RoundTrip.AssertAsync(new SecurityTradeoffModule(), audit, registry);

        Assert.Equal(["M13.cpu-mitigations", "M13.driver-blocklist"], plan.Select(c => c.Id).Order());
        Assert.All(plan, c => Assert.Equal(Maus.Core.Fixes.ChangeEffect.Restart, c.Effect));
        Assert.All(plan.Single(c => c.Id == "M13.cpu-mitigations").Writes, w => Assert.Null(w.Value));
    }

    [Fact]
    public async Task Protected_pc_gets_no_change()
    {
        var audit = TestContext.Create();
        var module = new SecurityTradeoffModule();

        Assert.Empty(module.Plan(audit, await module.DetectAsync(audit, CancellationToken.None)));
    }
}
