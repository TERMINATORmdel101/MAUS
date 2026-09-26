using Maus.Core.Preferences;
using Microsoft.Win32;

namespace Maus.Core.Tests.Preferences;

public class AppearanceTests
{
    private static readonly Rgb White = new(255, 255, 255);
    private static readonly Rgb Black = new(0, 0, 0);

    public static TheoryData<string> Hues => ["#0068B5", "#D71E28", "#76B900", "#1F4FBF", "#0078D4", "#FFB900", "#00CC6A", "#8764B8", "#FFFFFF", "#000000"];

    [Theory]
    [MemberData(nameof(Hues))]
    public void Accent_fills_keep_text_readable_in_both_themes(string hex)
    {
        var variants = AccentVariants.From(Rgb.Parse(hex));

        // Thème clair : texte blanc sur les fonds foncés ; thème sombre : texte noir sur les fonds clairs (WCAG AA, 4,5:1).
        Assert.True(variants.Dark1.ContrastWith(White) >= AccentVariants.MinimumContrast, $"{hex} : Dark1 {variants.Dark1}");
        Assert.True(variants.OnWhiteText.ContrastWith(White) >= AccentVariants.MinimumContrast, $"{hex} : bouton {variants.OnWhiteText}");
        Assert.True(variants.Light2.ContrastWith(Black) >= AccentVariants.MinimumContrast, $"{hex} : Light2 {variants.Light2}");
        Assert.True(variants.Light3.Luminance() >= variants.Light1.Luminance() && variants.Dark3.Luminance() <= variants.Dark1.Luminance());
    }

    [Fact]
    public void Contrast_matches_the_wcag_reference_values()
    {
        Assert.Equal(21, White.ContrastWith(Black), 1);
        Assert.Equal(1, White.ContrastWith(White), 3);
        Assert.Equal("#0068B5", Rgb.Parse("#0068b5").ToString());
    }

    [Fact]
    public void Components_mode_takes_the_cpu_hue_and_a_touch_of_the_graphics_card()
    {
        var intelNvidia = AccentPalette.For(AccentChoice.Components, new ComponentBrands(ComponentBrand.Intel, ComponentBrand.Nvidia), null);
        Assert.Equal(AccentPalette.IntelHue, intelNvidia.Primary);
        Assert.Equal(AccentPalette.NvidiaHue, intelNvidia.Secondary);

        var amdApu = AccentPalette.For(AccentChoice.Components, new ComponentBrands(ComponentBrand.Amd, ComponentBrand.Amd), null);
        Assert.Equal((AccentPalette.AmdHue, (Rgb?)null), amdApu);

        var unknownCpu = AccentPalette.For(AccentChoice.Components, new ComponentBrands(ComponentBrand.Other, ComponentBrand.Nvidia), null);
        Assert.Equal((AccentPalette.NvidiaHue, (Rgb?)null), unknownCpu);

        Assert.Equal((AccentPalette.Maus, (Rgb?)null), AccentPalette.For(AccentChoice.Components, ComponentBrands.Unknown, null));
    }

    [Fact]
    public void Windows_mode_uses_the_windows_accent_or_falls_back_to_maus_blue()
    {
        var windows = Rgb.Parse("#E3008C");
        Assert.Equal(windows, AccentPalette.For(AccentChoice.Windows, ComponentBrands.Unknown, windows).Primary);
        Assert.Equal(AccentPalette.Maus, AccentPalette.For(AccentChoice.Windows, ComponentBrands.Unknown, null).Primary);
        Assert.Equal(Rgb.Parse("#10893E"), AccentPalette.For(AccentChoice.Green, ComponentBrands.Unknown, windows).Primary);
    }

    [Theory]
    [InlineData("GenuineIntel", ComponentBrand.Intel)]
    [InlineData("AuthenticAMD", ComponentBrand.Amd)]
    [InlineData("HygonGenuine", ComponentBrand.Other)]
    [InlineData(null, ComponentBrand.Other)]
    public void Cpu_vendor_gives_the_brand(string? vendor, ComponentBrand expected) =>
        Assert.Equal(expected, ComponentBrands.FromCpuVendor(vendor));

    [Fact]
    public void Discrete_graphics_card_wins_over_integrated_intel_graphics()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, GraphicsBrand.DisplayClassKey + @"\0000", "MatchingDeviceId", @"pci\ven_8086&dev_3e92")
            .Set(RegistryHive.LocalMachine, GraphicsBrand.DisplayClassKey + @"\0001", "MatchingDeviceId", @"PCI\VEN_10DE&DEV_2484")
            .Set(RegistryHive.LocalMachine, GraphicsBrand.DisplayClassKey + @"\Properties", "Security", "x");

        Assert.Equal(ComponentBrand.Nvidia, GraphicsBrand.Read(registry));
        Assert.Equal(ComponentBrand.Amd, GraphicsBrand.FromDeviceIds([@"pci\ven_8086&dev_3e92", @"pci\ven_1002&dev_73bf"]));
        Assert.Equal(ComponentBrand.Intel, GraphicsBrand.FromDeviceIds([@"pci\ven_8086&dev_3e92"]));
        Assert.Equal(ComponentBrand.Other, GraphicsBrand.FromDeviceIds([@"root\basicdisplay"]));
        Assert.Equal(ComponentBrand.Other, GraphicsBrand.Read(new FakeRegistry()));
    }

    [Fact]
    public void Refresh_interval_outside_the_proposed_values_falls_back_to_one_second()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), UserPreferences.Default.RefreshInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(500), (UserPreferences.Default with { RefreshMilliseconds = 500 }).RefreshInterval);
        Assert.Equal(TimeSpan.FromSeconds(1), (UserPreferences.Default with { RefreshMilliseconds = 3 }).RefreshInterval);
    }

    [Fact]
    public void Update_starts_from_the_saved_file_so_choices_made_elsewhere_are_kept()
    {
        var store = new MemoryStore(UserPreferences.Default with { Theme = ThemeChoice.Dark, Language = "es" });

        var saved = store.Update(p => p with { GameBarProfile = 2 });

        Assert.Equal((ThemeChoice.Dark, "es", (int?)2), (saved.Theme, saved.Language, saved.GameBarProfile));
        Assert.Same(saved, store.Saved);
    }

    private sealed class MemoryStore(UserPreferences initial) : IPreferencesStore
    {
        public UserPreferences Saved { get; private set; } = initial;

        public UserPreferences Load() => Saved;

        public void Save(UserPreferences preferences) => Saved = preferences;
    }
}
