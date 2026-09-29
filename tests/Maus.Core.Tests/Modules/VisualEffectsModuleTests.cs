using Maus.Core.Modules.M06Visual;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Tests.Modules;

public class VisualEffectsModuleTests
{
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    [Fact]
    public async Task Default_windows_settings_are_reported_as_optimisations()
    {
        var parameters = new FakeSystemParameters { MinimizeAnimation = true };
        foreach (var action in new[] { SpiGet.DragFullWindows, SpiGet.FontSmoothing, SpiGet.ClientAreaAnimation, SpiGet.MenuAnimation, SpiGet.DropShadow })
        {
            parameters.Values[action] = true;
        }

        var findings = await new VisualEffectsModule().DetectAsync(TestContext.Create(parameters: parameters), CancellationToken.None);

        Assert.Equal(FindingStatus.Improvable, findings.Single(f => f.Id == "M06.taskview").Status);
        Assert.Equal(FindingStatus.Improvable, findings.Single(f => f.Id == "M06.widgets").Status);
        Assert.Equal("ms-settings:taskbar", findings.Single(f => f.Id == "M06.widgets").SettingsPage);
        Assert.Equal(FindingStatus.Improvable, findings.Single(f => f.Id == "M06.minimize-animation").Status);
        Assert.Null(findings.Single(f => f.Id == "M06.minimize-animation").SettingsPage);
        Assert.Equal(FindingStatus.Improvable, findings.Single(f => f.Id == "M06.menu-animation").Status);
        Assert.Equal(FindingStatus.Ok, findings.Single(f => f.Id == "M06.keep.fonts").Status);
        Assert.Equal(FindingStatus.Ok, findings.Single(f => f.Id == "M06.keep.thumbnails").Status);
        Assert.DoesNotContain(findings, f => f.Status is FindingStatus.Problem or FindingStatus.Warning);
    }

    [Fact]
    public async Task Tuned_settings_are_compliant()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton", 0)
            .Set(RegistryHive.CurrentUser, Advanced, "TaskbarAnimations", 0)
            .Set(RegistryHive.CurrentUser, Advanced, "ListviewShadow", 0)
            .Set(RegistryHive.CurrentUser, Advanced, "TaskbarDa", 0)
            .Set(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0)
            .Set(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 3)
            .Set(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\DWM", "EnableAeroPeek", 0);
        var parameters = new FakeSystemParameters { MinimizeAnimation = false };
        parameters.Values[SpiGet.DragFullWindows] = true;
        parameters.Values[SpiGet.FontSmoothing] = true;
        foreach (var action in new[] { SpiGet.ClientAreaAnimation, SpiGet.MenuAnimation, SpiGet.TooltipAnimation, SpiGet.SelectionFade, SpiGet.CursorShadow, SpiGet.DropShadow, SpiGet.ComboBoxAnimation, SpiGet.ListBoxSmoothScrolling })
        {
            parameters.Values[action] = false;
        }

        var findings = await new VisualEffectsModule().DetectAsync(TestContext.Create(registry, parameters: parameters), CancellationToken.None);

        Assert.All(findings, f => Assert.Equal(FindingStatus.Ok, f.Status));
    }

    [Fact]
    public async Task Widgets_policy_alone_is_enough()
    {
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0);

        var findings = await new VisualEffectsModule().DetectAsync(TestContext.Create(registry), CancellationToken.None);

        Assert.Equal(FindingStatus.Ok, findings.Single(f => f.Id == "M06.widgets").Status);
        Assert.Null(findings.Single(f => f.Id == "M06.widgets").SettingsPage);
    }
}

public class VisualEffectsFixTests
{
    private static FakeSystemParameters WindowsDefaults()
    {
        var parameters = new FakeSystemParameters { MinimizeAnimation = true };
        foreach (var action in new[] { SpiGet.DragFullWindows, SpiGet.FontSmoothing, SpiGet.ClientAreaAnimation, SpiGet.MenuAnimation, SpiGet.TooltipAnimation, SpiGet.SelectionFade, SpiGet.DropShadow, SpiGet.ComboBoxAnimation, SpiGet.ListBoxSmoothScrolling })
        {
            parameters.Values[action] = true;
        }

        parameters.Values[SpiGet.CursorShadow] = false;
        return parameters;
    }

    [Fact]
    public async Task Apply_then_revert_returns_to_the_initial_state()
    {
        var registry = new FakeRegistry()
            .Set(Microsoft.Win32.RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowTaskViewButton", 1)
            .Set(Microsoft.Win32.RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1);
        var parameters = WindowsDefaults();
        var audit = TestContext.Create(registry, parameters: parameters);

        var (plan, _) = await Fixes.RoundTrip.AssertAsync(new VisualEffectsModule(), audit, registry, parameters);

        Assert.Equal("M06.visualfx-mode", plan[^1].Id);
        Assert.Contains(plan, c => c.Id == "M06.widgets");
        Assert.DoesNotContain(plan, c => c.Id.StartsWith("M06.keep", StringComparison.Ordinal));
        Assert.Null(registry.GetValue(Microsoft.Win32.RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests"));
    }

    [Fact]
    public async Task Drag_and_font_smoothing_use_ui_param_and_are_restored()
    {
        var registry = new FakeRegistry();
        var parameters = WindowsDefaults();
        parameters.Values[SpiGet.FontSmoothing] = false;
        var audit = TestContext.Create(registry, parameters: parameters);
        var module = new VisualEffectsModule();

        var plan = module.Plan(audit, await module.DetectAsync(audit, CancellationToken.None));
        var fonts = plan.Single(c => c.Id == "M06.keep.fonts");
        Assert.True(fonts.Writes.Single().Key.SpiUseUiParam);
        Assert.Equal("1", fonts.Writes.Single().Value!.Data);
        Assert.False(plan.Single(c => c.Id == "M06.menu-animation").Writes.Single().Key.SpiUseUiParam);
    }

    [Fact]
    public async Task Widgets_policy_is_not_proposed_on_home_edition()
    {
        var audit = TestContext.Create(windows: new WindowsInfo("Windows 11 Famille", "Core", "25H2", 26200, 1000));
        var module = new VisualEffectsModule();

        var plan = module.Plan(audit, await module.DetectAsync(audit, CancellationToken.None));

        Assert.DoesNotContain(plan, c => c.Id == "M06.widgets");
    }

    [Fact]
    public async Task Findings_with_a_fix_are_marked_fixable()
    {
        var module = new VisualEffectsModule();
        var findings = await module.DetectAsync(TestContext.Create(), CancellationToken.None);

        Assert.True(findings.Single(f => f.Id == "M06.taskview").Fixable);
    }
}
