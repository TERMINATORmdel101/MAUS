using Maus.Core.Hardware;
using Maus.Core.Modules.M07GameBar;
using Maus.Core.Platform;
using Microsoft.Win32;
using static Maus.Core.Modules.M07GameBar.GameBarKeys;

namespace Maus.Core.Tests.Modules.M07GameBar;

public class GameBarModuleTests
{
    private const RegistryHive Hklm = RegistryHive.LocalMachine;
    private const RegistryHive Hkcu = RegistryHive.CurrentUser;

    private static readonly HardwareProfile X3DDesktop = new()
    {
        FormFactor = FormFactor.Desktop,
        Cpu = new CpuInfo("AMD Ryzen 9 7950X3D 16-Core Processor", HardwareVendor.Amd, 16, 32, 4201),
    };

    private static readonly HardwareProfile SingleCcdX3D = new()
    {
        FormFactor = FormFactor.Desktop,
        Cpu = new CpuInfo("AMD Ryzen 7 7800X3D 8-Core Processor", HardwareVendor.Amd, 8, 16, 4201),
    };

    private static readonly WindowsInfo Home = new("Windows 11 Famille", "Core", "25H2", 26200, 1000);

    [Fact]
    public void Module_metadata_follows_the_spec()
    {
        var module = new GameBarModule();

        Assert.Equal("M07", module.Id);
        Assert.Equal("Xbox Game Bar", module.Title);
        Assert.Equal(70, module.Order);
    }

    [Fact]
    public async Task Default_windows_without_xbox_app_proposes_profile_1()
    {
        var findings = await Detect(DefaultRegistry(), new FakePackages().Add(GameBarPackage, "7.326.8061.0"));

        Assert.InRange(findings.Count, 5, 25);
        Assert.All(findings, f => Assert.StartsWith("M07.", f.Id, StringComparison.Ordinal));
        Assert.Equal(findings.Count, findings.Select(f => f.Id).Distinct().Count());
        Assert.DoesNotContain(findings, f => f.Status is FindingStatus.Problem or FindingStatus.Warning or FindingStatus.Unknown);
        Assert.DoesNotContain(findings, f => f.Id == "M07.x3d-vcache");
        Assert.Equal("M07.background-recording", findings[0].Id);

        Assert.Equal(FindingStatus.Ok, Single(findings, "M07.background-recording").Status);
        Assert.Equal("désactivé (par défaut)", Single(findings, "M07.background-recording").Current);
        Assert.Equal("installée (version 7.326.8061.0)", Single(findings, "M07.gamebar-package").Current);
        Assert.Equal(FindingStatus.Ok, Single(findings, "M07.game-mode").Status);
        Assert.Equal("activé (par défaut)", Single(findings, "M07.game-mode").Current);
        Assert.StartsWith("Profil 1", Single(findings, "M07.profile").Current, StringComparison.Ordinal);
        Assert.Equal(FindingStatus.Ok, Single(findings, "M07.gamedvr-policy").Status);

        var captures = Single(findings, "M07.captures");
        Assert.Equal(FindingStatus.Improvable, captures.Status);
        Assert.Equal("activées", captures.Current);
        Assert.True(captures.Fixable);
        Assert.Equal("ms-settings:gaming-gamedvr", captures.SettingsPage);

        var controller = Single(findings, "M07.controller-button");
        Assert.Equal(FindingStatus.Improvable, controller.Status);
        Assert.Equal("activée (par défaut)", controller.Current);
        Assert.NotNull(controller.Advice);
        Assert.Equal("ms-settings:gaming-gamebar", controller.SettingsPage);

        // Conformes ou simples informations : pas de bouton « Ouvrir dans Windows ».
        foreach (var id in new[] { "M07.background-recording", "M07.game-mode", "M07.gamebar-package", "M07.profile", "M07.gamedvr-policy" })
        {
            Assert.Null(Single(findings, id).SettingsPage);
        }
    }

    [Fact]
    public async Task Tuned_profile_1_is_compliant()
    {
        var registry = new FakeRegistry()
            .Set(Hkcu, GameDvrUser, "HistoricalCaptureEnabled", 0)
            .Set(Hkcu, GameDvrUser, "AppCaptureEnabled", 0)
            .Set(Hkcu, GameConfigStore, "GameDVR_Enabled", 0)
            .Set(Hkcu, GameBarUser, "UseNexusForGameBarEnabled", 0)
            .Set(Hkcu, GameBarUser, "AutoGameModeEnabled", 1);

        var findings = await Detect(registry, new FakePackages().Add(GameBarPackage));

        Assert.All(findings, f => Assert.True(f.Status is FindingStatus.Ok or FindingStatus.Info, $"{f.Id} : {f.Status}"));
        Assert.All(findings, f => Assert.False(f.Fixable, f.Id));
        Assert.Equal("désactivées", Single(findings, "M07.captures").Current);
        Assert.Equal("désactivée", Single(findings, "M07.controller-button").Current);
    }

    [Theory]
    [InlineData(null, FindingStatus.Ok, "désactivé (par défaut)")]
    [InlineData(0, FindingStatus.Ok, "désactivé")]
    [InlineData(1, FindingStatus.Improvable, "activé")]
    public async Task Background_recording_is_cut_in_every_profile(int? value, FindingStatus expected, string current)
    {
        var registry = new FakeRegistry();
        if (value is not null)
        {
            registry.Set(Hkcu, GameDvrUser, "HistoricalCaptureEnabled", value.Value);
        }

        foreach (var hardware in new[] { new HardwareProfile { FormFactor = FormFactor.Desktop }, X3DDesktop })
        {
            var finding = Single(await Detect(registry, new FakePackages().Add(GameBarPackage).Add(XboxAppPackage), hardware), "M07.background-recording");

            Assert.Equal(expected, finding.Status);
            Assert.Equal(current, finding.Current);
            Assert.Equal(expected == FindingStatus.Improvable, finding.Fixable);
            Assert.Equal(expected == FindingStatus.Improvable, finding.Advice is not null);
            Assert.Equal(expected == FindingStatus.Improvable ? "ms-settings:gaming-gamedvr" : null, finding.SettingsPage);
        }
    }

    [Fact]
    public async Task Missing_game_bar_is_a_warning_with_the_reinstall_command()
    {
        var registry = DefaultRegistry()
            .Set(Hkcu, GameConfigStoreChildren + @"\0a1b", "Type", 1)
            .Set(Hkcu, GameConfigStoreChildren + @"\2c3d", "Type", 1);

        var finding = Single(await Detect(registry, new FakePackages()), "M07.gamebar-package");

        Assert.Equal(FindingStatus.Warning, finding.Status);
        Assert.Equal("absente", finding.Current);
        Assert.True(finding.Fixable);
        Assert.Null(finding.SettingsPage); // Réinstallation par le Microsoft Store : aucune page des Paramètres.
        Assert.Contains("ms-gamingoverlay", finding.Explanation, StringComparison.Ordinal);
        Assert.Contains("2 jeu(x)", finding.Explanation, StringComparison.Ordinal);
        Assert.Contains(GameBarStoreId, finding.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_game_bar_without_readable_game_list_is_still_reported()
    {
        var registry = new FakeRegistry().Deny(Hkcu, GameConfigStoreChildren);

        var finding = Single(await Detect(registry, new FakePackages(), X3DDesktop), "M07.gamebar-package");

        Assert.Equal(FindingStatus.Warning, finding.Status);
        Assert.DoesNotContain("jeu(x)", finding.Explanation, StringComparison.Ordinal);
        Assert.Contains("V-Cache", finding.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Game_mode_off_is_improvable_and_a_warning_on_dual_ccd_x3d()
    {
        var registry = new FakeRegistry().Set(Hkcu, GameBarUser, "AutoGameModeEnabled", 0);
        var packages = new FakePackages().Add(GameBarPackage);

        var regular = Single(await Detect(registry, packages), "M07.game-mode");
        Assert.Equal(FindingStatus.Improvable, regular.Status);
        Assert.Equal("désactivé", regular.Current);
        Assert.True(regular.Fixable);
        Assert.Equal("ms-settings:gaming-gamemode", regular.SettingsPage);

        var x3d = Single(await Detect(registry, packages, X3DDesktop), "M07.game-mode");
        Assert.Equal(FindingStatus.Warning, x3d.Status);
        Assert.Contains("V-Cache", x3d.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dual_ccd_x3d_recommends_keeping_game_bar_and_game_mode()
    {
        var findings = await Detect(DefaultRegistry(), new FakePackages().Add(GameBarPackage), X3DDesktop);

        var recommendation = Single(findings, "M07.x3d-vcache");
        Assert.Equal(FindingStatus.Info, recommendation.Status);
        Assert.Contains("7950X3D", recommendation.Current, StringComparison.Ordinal);
        Assert.Contains("Game Bar : installée ; Mode Jeu : activé", recommendation.Current, StringComparison.Ordinal);
        Assert.Contains("Recommandé", recommendation.Advice, StringComparison.Ordinal);

        Assert.StartsWith("Profil 3", Single(findings, "M07.profile").Current, StringComparison.Ordinal);
        Assert.Equal(FindingStatus.Info, Single(findings, "M07.captures").Status);
        Assert.Equal("inchangé (profil 3)", Single(findings, "M07.captures").Expected);
        Assert.Equal(FindingStatus.Info, Single(findings, "M07.controller-button").Status);
        Assert.DoesNotContain(findings, f => f.Status is FindingStatus.Problem or FindingStatus.Warning);
    }

    [Fact]
    public async Task Single_ccd_x3d_is_not_treated_as_dual_ccd()
    {
        var findings = await Detect(DefaultRegistry(), new FakePackages().Add(GameBarPackage), SingleCcdX3D);

        Assert.DoesNotContain(findings, f => f.Id == "M07.x3d-vcache");
        Assert.StartsWith("Profil 1", Single(findings, "M07.profile").Current, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(XboxAppPackage)]
    [InlineData(GamingServicesPackage)]
    public async Task Xbox_app_or_gaming_services_select_the_game_pass_profile(string package)
    {
        var findings = await Detect(DefaultRegistry(), new FakePackages().Add(GameBarPackage).Add(package));

        var profile = Single(findings, "M07.profile");
        Assert.Equal(FindingStatus.Info, profile.Status);
        Assert.StartsWith("Profil 2", profile.Current, StringComparison.Ordinal);
        Assert.Contains("Profil Game Pass détecté : la superposition peut rester", profile.Explanation, StringComparison.Ordinal);

        var captures = Single(findings, "M07.captures");
        Assert.Equal(FindingStatus.Info, captures.Status);
        Assert.Equal("inchangé (profil 2)", captures.Expected);
        Assert.False(captures.Fixable);
        Assert.Equal(FindingStatus.Info, Single(findings, "M07.controller-button").Status);
    }

    [Fact]
    public async Task Profile_lists_xbox_app_and_gaming_services()
    {
        var packages = new FakePackages().Add(GameBarPackage).Add(XboxAppPackage).Add(GamingServicesPackage);

        var profile = Single(await Detect(DefaultRegistry(), packages), "M07.profile");

        Assert.Contains("app Xbox : installée ; Services de jeu : installés", profile.Current, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null, "activées")]
    [InlineData(1, 1, "activées")]
    [InlineData(0, null, "désactivées en partie")]
    [InlineData(1, 0, "désactivées en partie")]
    [InlineData(0, 0, "désactivées")]
    public async Task Capture_values_are_combined(int? gameDvr, int? appCapture, string current)
    {
        var registry = new FakeRegistry();
        if (gameDvr is not null)
        {
            registry.Set(Hkcu, GameConfigStore, "GameDVR_Enabled", gameDvr.Value);
        }

        if (appCapture is not null)
        {
            registry.Set(Hkcu, GameDvrUser, "AppCaptureEnabled", appCapture.Value);
        }

        var finding = Single(await Detect(registry, new FakePackages().Add(GameBarPackage)), "M07.captures");

        Assert.Equal(current, finding.Current);
        Assert.Equal(current == "désactivées" ? FindingStatus.Ok : FindingStatus.Improvable, finding.Status);
    }

    [Fact]
    public async Task Recording_policy_is_an_optional_lock_but_never_on_dual_ccd_x3d()
    {
        var registry = DefaultRegistry().Set(Hklm, GameDvrPolicy, "AllowGameDVR", 0);
        var packages = new FakePackages().Add(GameBarPackage);

        var regular = Single(await Detect(registry, packages), "M07.gamedvr-policy");
        Assert.Equal(FindingStatus.Info, regular.Status);
        Assert.Equal("enregistrement interdit (0)", regular.Current);
        Assert.DoesNotContain("Famille", regular.Explanation, StringComparison.Ordinal);

        var home = Single(await Detect(registry, packages, windows: Home), "M07.gamedvr-policy");
        Assert.Contains("Famille", home.Explanation, StringComparison.Ordinal);

        var x3d = Single(await Detect(registry, packages, X3DDesktop), "M07.gamedvr-policy");
        Assert.Equal(FindingStatus.Warning, x3d.Status);
        Assert.True(x3d.Fixable);

        var allowed = Single(await Detect(DefaultRegistry().Set(Hklm, GameDvrPolicy, "AllowGameDVR", 1), packages), "M07.gamedvr-policy");
        Assert.Equal(FindingStatus.Ok, allowed.Status);
        Assert.Equal("enregistrement autorisé", allowed.Current);
    }

    [Fact]
    public async Task Unreadable_package_inventory_gives_unknown_findings_not_problems()
    {
        var context = TestContext.Create(DefaultRegistry());
        context = new AuditContext
        {
            Registry = context.Registry,
            Cim = context.Cim,
            Commands = context.Commands,
            Packages = new ThrowingPackages(new MausAccessDeniedException("refusé")),
            Windows = context.Windows,
            Hardware = context.Hardware,
        };

        var findings = await new GameBarModule().DetectAsync(context, CancellationToken.None);

        Assert.Equal(FindingStatus.Unknown, Single(findings, "M07.gamebar-package").Status);
        Assert.Equal(FindingStatus.Unknown, Single(findings, "M07.profile").Status);
        Assert.Equal("selon le profil choisi", Single(findings, "M07.captures").Expected);
        Assert.DoesNotContain(findings, f => f.Status is FindingStatus.Problem or FindingStatus.Warning);
    }

    [Fact]
    public async Task Denied_registry_keys_require_admin()
    {
        var registry = new FakeRegistry()
            .Deny(Hkcu, GameDvrUser)
            .Deny(Hkcu, GameBarUser)
            .Deny(Hklm, GameDvrPolicy);

        var findings = await Detect(registry, new FakePackages().Add(GameBarPackage), X3DDesktop);

        foreach (var id in new[] { "M07.background-recording", "M07.game-mode", "M07.x3d-vcache", "M07.captures", "M07.controller-button", "M07.gamedvr-policy" })
        {
            var finding = Single(findings, id);
            Assert.Equal(FindingStatus.Unknown, finding.Status);
            Assert.Contains("administrateur", finding.Explanation, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(true, false, false, 3)]
    [InlineData(true, true, true, 3)]
    [InlineData(false, true, false, 2)]
    [InlineData(false, false, true, 2)]
    [InlineData(false, false, false, 1)]
    public void Proposed_profile_follows_cpu_then_packages(bool x3d, bool xboxApp, bool gamingServices, int expectedProfile)
    {
        var inventory = new FakePackages().Add(GameBarPackage).Add(IdentityProviderPackage);
        if (xboxApp)
        {
            inventory.Add(XboxAppPackage);
        }

        if (gamingServices)
        {
            inventory.Add(GamingServicesPackage);
        }

        var packages = GamingPackages.TryRead(inventory);

        Assert.NotNull(packages);
        Assert.NotNull(packages.IdentityProvider);
        Assert.Equal((GamingProfile)expectedProfile, GamingPackages.Propose(x3d, packages));
    }

    [Fact]
    public void Package_names_are_matched_exactly()
    {
        var packages = GamingPackages.TryRead(new FakePackages().Add("Microsoft.XboxGamingOverlayPreview").Add("Microsoft.GamingAppExtras"));

        Assert.NotNull(packages);
        Assert.Null(packages.GameBar);
        Assert.False(packages.UsesXboxApp);
    }

    [Fact]
    public void Unreadable_inventory_yields_no_profile_except_on_dual_ccd_x3d()
    {
        Assert.Null(GamingPackages.TryRead(new ThrowingPackages(new MausAccessDeniedException("refusé"))));
        Assert.Null(GamingPackages.TryRead(new ThrowingPackages(new DataSourceUnavailableException("absent"))));
        Assert.Null(GamingPackages.TryRead(new ThrowingPackages(System.Runtime.InteropServices.Marshal.GetExceptionForHR(unchecked((int)0x80004005))!)));

        Assert.Null(GamingPackages.Propose(false, null));
        Assert.Equal(GamingProfile.X3D, GamingPackages.Propose(true, null));
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => new GameBarModule().DetectAsync(TestContext.Create(), source.Token));
    }

    private static async Task<IReadOnlyList<Finding>> Detect(
        FakeRegistry registry,
        FakePackages packages,
        HardwareProfile? hardware = null,
        WindowsInfo? windows = null) =>
        await new GameBarModule().DetectAsync(
            TestContext.Create(registry, hardware: hardware, windows: windows, packages: packages),
            CancellationToken.None);

    private static Finding Single(IReadOnlyList<Finding> findings, string id) => findings.Single(f => f.Id == id);

    /// <summary>Valeurs relevées sur une installation récente de Windows 11 (build 26200) : captures actives, rien d'autre de posé.</summary>
    private static FakeRegistry DefaultRegistry() => new FakeRegistry()
        .Set(Hkcu, GameConfigStore, "GameDVR_Enabled", 1);

    private sealed class ThrowingPackages(Exception exception) : IPackageInventory
    {
        public IReadOnlyList<InstalledPackage> GetUserPackages() => throw exception;
    }
}
