using Maus.Core.Hardware;
using Maus.Core.Modules.M07GameBar;
using Microsoft.Win32;

namespace Maus.Core.Tests.Modules.M07GameBar;

public class GameBarFixTests
{
    private const string GameDvr = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private const string GameBar = @"Software\Microsoft\GameBar";
    private const string Policy = @"SOFTWARE\Policies\Microsoft\Windows\GameDVR";

    [Fact]
    public async Task Profile_1_round_trip_returns_to_the_initial_state()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.CurrentUser, GameDvr, "HistoricalCaptureEnabled", 1)
            .Set(RegistryHive.CurrentUser, GameBar, "AutoGameModeEnabled", 0);
        var audit = TestContext.Create(registry, packages: new FakePackages().Add("Microsoft.XboxGamingOverlay"));

        var (plan, _) = await Fixes.RoundTrip.AssertAsync(new GameBarModule(), audit, registry);

        Assert.Contains(plan, c => c.Id == "M07.captures");
        Assert.Contains(plan, c => c.Id == "M07.controller-button");
        var lockChange = plan.Single(c => c.Id == "M07.gamedvr-policy");
        Assert.True(lockChange.Advanced);
        Assert.False(lockChange.Recommended);
    }

    [Fact]
    public async Task Xbox_app_users_keep_captures_and_controller_button()
    {
        var registry = new FakeRegistry().Set(RegistryHive.CurrentUser, GameDvr, "HistoricalCaptureEnabled", 1);
        var audit = TestContext.Create(registry, packages: new FakePackages().Add("Microsoft.XboxGamingOverlay").Add("Microsoft.GamingApp"));
        var module = new GameBarModule();

        var plan = module.Plan(audit, await module.DetectAsync(audit, CancellationToken.None));

        Assert.Equal(["M07.background-recording"], plan.Select(c => c.Id));
    }

    [Fact]
    public async Task X3D_removes_the_blocking_policy_and_never_offers_the_lock()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, Policy, "AllowGameDVR", 0)
            .Set(RegistryHive.CurrentUser, GameBar, "AutoGameModeEnabled", 0);
        var hardware = new HardwareProfile
        {
            FormFactor = FormFactor.Desktop,
            Cpu = new CpuInfo("AMD Ryzen 9 7950X3D 16-Core Processor", HardwareVendor.Amd, 16, 32, 4201),
        };
        var audit = TestContext.Create(registry, hardware: hardware, packages: new FakePackages().Add("Microsoft.XboxGamingOverlay"));

        var (plan, _) = await Fixes.RoundTrip.AssertAsync(new GameBarModule(), audit, registry);

        var removal = plan.Single(c => c.Id == "M07.gamedvr-policy");
        Assert.Null(removal.Writes.Single().Value);
        Assert.Contains(plan, c => c.Id == "M07.game-mode");
    }
}

public class GameBarProfileChoiceTests
{
    private static readonly HardwareProfile X3D = new()
    {
        FormFactor = FormFactor.Desktop,
        Cpu = new CpuInfo("AMD Ryzen 9 7950X3D 16-Core Processor", HardwareVendor.Amd, 16, 32, 4201),
    };

    [Fact]
    public async Task User_choice_overrides_the_detected_profile()
    {
        var registry = new FakeRegistry();
        var packages = new FakePackages().Add("Microsoft.XboxGamingOverlay").Add("Microsoft.GamingApp");
        var audit = TestContext.Create(registry, packages: packages)
            .WithPreferences(Maus.Core.Preferences.UserPreferences.Default with { GameBarProfile = 1 });
        var module = new GameBarModule();

        var findings = await module.DetectAsync(audit, CancellationToken.None);
        var plan = module.Plan(audit, findings);

        var profile = findings.Single(f => f.Id == "M07.profile");
        Assert.Equal("Profil de jeu choisi par vous", profile.Title);
        Assert.StartsWith("Profil 1", profile.Current, StringComparison.Ordinal);
        Assert.Contains(plan, c => c.Id == "M07.captures");
        Assert.Contains(plan, c => c.Id == "M07.controller-button");
    }

    [Fact]
    public async Task X3D_owner_can_refuse_profile_3_with_a_warning()
    {
        var audit = TestContext.Create(hardware: X3D, packages: new FakePackages().Add("Microsoft.XboxGamingOverlay"))
            .WithPreferences(Maus.Core.Preferences.UserPreferences.Default with { GameBarProfile = 1 });
        var module = new GameBarModule();

        var findings = await module.DetectAsync(audit, CancellationToken.None);
        var plan = module.Plan(audit, findings);

        Assert.Contains("profil 1", findings.Single(f => f.Id == "M07.x3d-vcache").Advice, StringComparison.Ordinal);
        Assert.NotNull(plan.Single(c => c.Id == "M07.gamedvr-policy").Warning);
    }

    [Fact]
    public async Task Without_choice_the_detected_profile_is_used()
    {
        var audit = TestContext.Create(hardware: X3D, packages: new FakePackages().Add("Microsoft.XboxGamingOverlay"));

        var findings = await new GameBarModule().DetectAsync(audit, CancellationToken.None);

        Assert.Equal("Profil de jeu proposé", findings.Single(f => f.Id == "M07.profile").Title);
        Assert.StartsWith("Profil 3", findings.Single(f => f.Id == "M07.profile").Current, StringComparison.Ordinal);
    }
}
