using Maus.Core.Hardware;
using Maus.Core.Modules.M05Power;
using Microsoft.Win32;

namespace Maus.Core.Tests.Modules.M05Power;

public class PowerFixTests
{
    private const string SessionPower = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";

    private static readonly HardwareProfile Laptop = new()
    {
        FormFactor = FormFactor.Laptop,
        HasBattery = true,
        Cpu = new CpuInfo("AMD Ryzen 7 5800H with Radeon Graphics", HardwareVendor.Amd, 8, 16, 3200),
    };

    private static readonly HardwareProfile Desktop = new()
    {
        FormFactor = FormFactor.Desktop,
        Cpu = new CpuInfo("Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz", HardwareVendor.Intel, 6, 12, 3700),
    };

    [Fact]
    public async Task Laptop_returns_to_balanced_and_fast_startup_is_disabled_then_restored()
    {
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, SessionPower, "HiberbootEnabled", 1);
        var platform = new FakePowerPlatform
        {
            ActiveScheme = PowerSchemes.HighPerformance,
            Capabilities = FakePowerPlatform.Caps(hiberFile: true, batteries: true),
        };
        var audit = TestContext.Create(registry, hardware: Laptop);

        var (plan, _) = await Fixes.RoundTrip.AssertAsync(new PowerModule(platform), audit, registry, power: platform);

        var scheme = plan.Single(c => c.Id == "M05.power-plan");
        Assert.True(scheme.Recommended);
        Assert.Equal(PowerSchemes.Balanced.ToString("D"), scheme.Writes.Single().Value!.Data);
        Assert.Contains(plan, c => c.Id == "M05.fast-startup");
        Assert.Equal(PowerSchemes.HighPerformance, platform.ActiveScheme);
    }

    [Fact]
    public async Task Desktop_high_performance_is_offered_but_not_preselected()
    {
        var platform = new FakePowerPlatform { ActiveScheme = PowerSchemes.Balanced };
        var audit = TestContext.Create(hardware: Desktop);
        var module = new PowerModule(platform);

        var plan = module.Plan(audit, await module.DetectAsync(audit, CancellationToken.None));

        var scheme = plan.Single(c => c.Id == "M05.power-plan");
        Assert.False(scheme.Recommended);
        Assert.NotNull(scheme.Risk);
        Assert.Equal(PowerSchemes.HighPerformance.ToString("D"), scheme.Writes.Single().Value!.Data);
    }

    [Fact]
    public async Task Missing_scheme_fails_cleanly_and_keeps_the_current_one()
    {
        var registry = new FakeRegistry();
        var platform = new FakePowerPlatform { ActiveScheme = PowerSchemes.Balanced };
        platform.InstalledSchemes.Remove(PowerSchemes.HighPerformance);
        var audit = TestContext.Create(registry, hardware: Desktop);
        var module = new PowerModule(platform);
        var plan = module.Plan(audit, await module.DetectAsync(audit, CancellationToken.None));

        var engine = new Maus.Core.Fixes.FixEngine(TestFixContext.Create(audit, registry, power: platform));
        var result = engine.Apply(plan.Where(c => c.Id == "M05.power-plan").ToList(), new Maus.Core.Fixes.ApplyOptions());

        Assert.Equal(Maus.Core.Fixes.ChangeStatus.Failed, result.Changes.Single().Status);
        Assert.Equal(PowerSchemes.Balanced, platform.ActiveScheme);
    }
}
