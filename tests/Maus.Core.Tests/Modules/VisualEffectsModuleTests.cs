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
    public async Task Windows_defaults_are_read_from_windows_own_declarations_applied_alone_and_reverted()
    {
        const string root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects";
        const string advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        const string dsh = @"SOFTWARE\Policies\Microsoft\Dsh";
        var registry = new FakeRegistry();
        void Spi(string name, int defaultValue, uint get, uint set) => registry
            .Set(Microsoft.Win32.RegistryHive.LocalMachine, $@"{root}\{name}", "Type", "checkbox")
            .Set(Microsoft.Win32.RegistryHive.LocalMachine, $@"{root}\{name}", "DefaultValue", defaultValue)
            .Set(Microsoft.Win32.RegistryHive.LocalMachine, $@"{root}\{name}", "SPIActionGet", (int)get)
            .Set(Microsoft.Win32.RegistryHive.LocalMachine, $@"{root}\{name}", "SPIActionSet", (int)set);

        Spi("MenuAnimation", 1, SpiGet.MenuAnimation, SpiSet.MenuAnimation);
        Spi("DragFullWindows", 1, SpiGet.DragFullWindows, SpiSet.DragFullWindows);
        Spi("CursorShadow", 0, SpiGet.CursorShadow, SpiSet.CursorShadow);
        Spi("AnimateMinMax", 1, SpiGet.Animation, 0x0049);
        Spi("Unknown", 1, 0x0071, 0x0072);
        registry
            .Set(Microsoft.Win32.RegistryHive.LocalMachine, $@"{root}\TaskbarAnimations", "Type", "checkbox")
            .Set(Microsoft.Win32.RegistryHive.LocalMachine, $@"{root}\TaskbarAnimations", "DefaultValue", 1)
            .Set(Microsoft.Win32.RegistryHive.LocalMachine, $@"{root}\TaskbarAnimations", "HKeyRoot", unchecked((int)0x80000001))
            .Set(Microsoft.Win32.RegistryHive.LocalMachine, $@"{root}\TaskbarAnimations", "RegPath", advanced)
            .Set(Microsoft.Win32.RegistryHive.LocalMachine, $@"{root}\TaskbarAnimations", "ValueName", "TaskbarAnimations")
            .Set(Microsoft.Win32.RegistryHive.LocalMachine, $@"{root}\TaskbarAnimations", "CheckedValue", 1)
            .Set(Microsoft.Win32.RegistryHive.LocalMachine, $@"{root}\TaskbarAnimations", "UncheckedValue", 0)
            .Set(Microsoft.Win32.RegistryHive.LocalMachine, $@"{root}\ThumbnailsOrIcon", "Type", "checkbox")
            .Set(Microsoft.Win32.RegistryHive.LocalMachine, $@"{root}\ThumbnailsOrIcon", "DefaultValue", 1)
            .Set(Microsoft.Win32.RegistryHive.LocalMachine, $@"{root}\ThumbnailsOrIcon", "CLSID", "{71E32BAA-73EE-40a1-933C-F166F0192B72}")
            .Set(Microsoft.Win32.RegistryHive.LocalMachine, $@"{root}\DWMEnabled", "DefaultValue", 1)
            .Set(Microsoft.Win32.RegistryHive.CurrentUser, advanced, "TaskbarAnimations", 0)
            .Set(Microsoft.Win32.RegistryHive.LocalMachine, dsh, "AllowNewsAndInterests", 0);
        var parameters = WindowsDefaults();
        parameters.Values[SpiGet.MenuAnimation] = false;
        parameters.MinimizeAnimation = false;
        var audit = TestContext.Create(registry, parameters: parameters);
        var module = new VisualEffectsModule();

        var before = await module.DetectAsync(audit, CancellationToken.None);
        var finding = before.Single(f => f.Id == "M06.windows-defaults");
        Assert.Equal(FindingStatus.Info, finding.Status);
        Assert.True(finding.Fixable);
        Assert.Equal("4 réglage(s) différent(s) des valeurs de Windows (5 cases comparées)", finding.Current);

        var change = module.Plan(audit, before).Single(c => c.Id == "M06.windows-defaults");
        Assert.True(change.Advanced);
        Assert.False(change.Recommended);
        Assert.Equal(4, change.Writes.Count);
        Assert.DoesNotContain(change.Writes, w => w.Key.Name == "VisualFXSetting");

        var engine = new Maus.Core.Fixes.FixEngine(TestFixContext.Create(audit, registry, parameters));
        var applied = engine.Apply([change], new Maus.Core.Fixes.ApplyOptions());
        Assert.All(applied.Changes, c => Assert.Equal(Maus.Core.Fixes.ChangeStatus.Applied, c.Status));
        Assert.True(parameters.Values[SpiGet.MenuAnimation]);
        Assert.True(parameters.MinimizeAnimation);
        Assert.Equal(1, registry.GetValue(Microsoft.Win32.RegistryHive.CurrentUser, advanced, "TaskbarAnimations"));
        Assert.Null(registry.GetValue(Microsoft.Win32.RegistryHive.LocalMachine, dsh, "AllowNewsAndInterests"));
        var after = (await module.DetectAsync(audit, CancellationToken.None)).Single(f => f.Id == "M06.windows-defaults");
        Assert.Equal("identiques aux valeurs de Windows (5 cases comparées)", after.Current);
        Assert.False(after.Fixable);

        Assert.True(engine.Revert(applied.Session!.Id).Completed);
        Assert.Equal(
            before.Select(f => (f.Id, f.Status, f.Current)),
            (await module.DetectAsync(audit, CancellationToken.None)).Select(f => (f.Id, f.Status, f.Current)));
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
