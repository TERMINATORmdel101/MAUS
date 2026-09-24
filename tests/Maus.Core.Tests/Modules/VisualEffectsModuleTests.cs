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
        Assert.Equal(FindingStatus.Improvable, findings.Single(f => f.Id == "M06.minimize-animation").Status);
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
    }
}
