using Maus.App.Appearance;

namespace Maus.App.Tests;

/// <summary>Les textes colorés et atténués restent lisibles sur leurs fonds pastel : au moins 4,5:1 (WCAG 2.1, niveau AA).</summary>
public sealed class PastelContrastTests
{
    private const double Minimum = 4.5;

    private static double Channel(uint value)
    {
        var c = value / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(uint rgb) =>
        (0.2126 * Channel((rgb >> 16) & 0xFF)) + (0.7152 * Channel((rgb >> 8) & 0xFF)) + (0.0722 * Channel(rgb & 0xFF));

    private static double Contrast(uint a, uint b)
    {
        var (light, dark) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (light + 0.05) / (dark + 0.05);
    }

    /// <summary>Texte noir à cette opacité, posé sur ce fond.</summary>
    private static uint Over(uint background, double opacity)
    {
        uint Mix(int shift) => (uint)Math.Round(((background >> shift) & 0xFF) * (1 - opacity));
        return (Mix(16) << 16) | (Mix(8) << 8) | Mix(0);
    }

    [Fact]
    public void Each_ink_is_readable_on_its_fill()
    {
        foreach (var (tone, colors) in Pastel.Colors)
        {
            Assert.True(Contrast(colors.Ink, colors.Fill) >= Minimum, $"{tone}, thème clair : {Contrast(colors.Ink, colors.Fill):0.00}");
            Assert.True(Contrast(colors.DarkInk, colors.DarkFill) >= Minimum, $"{tone}, thème sombre : {Contrast(colors.DarkInk, colors.DarkFill):0.00}");
        }
    }

    [Fact]
    public void Disabled_text_stays_readable_on_every_fill()
    {
        foreach (var (tone, colors) in Pastel.Colors)
        {
            Assert.True(Contrast(Pastel.DisabledText, colors.Fill) >= Minimum, $"{tone}, thème clair : {Contrast(Pastel.DisabledText, colors.Fill):0.00}");
            Assert.True(Contrast(Pastel.DarkDisabledText, colors.DarkFill) >= Minimum, $"{tone}, thème sombre : {Contrast(Pastel.DarkDisabledText, colors.DarkFill):0.00}");
        }

        // Fond de la fenêtre et des cartes.
        Assert.True(Contrast(Pastel.DisabledText, 0xF3F3F3) >= Minimum);
        Assert.True(Contrast(Pastel.DarkDisabledText, 0x202020) >= Minimum);
        Assert.True(Contrast(Pastel.DarkDisabledText, 0x2B2B2B) >= Minimum);
    }

    [Fact]
    public void Each_ink_stays_readable_on_a_card_with_no_tint()
    {
        foreach (var (tone, colors) in Pastel.Colors)
        {
            Assert.True(Contrast(colors.Ink, 0xFFFFFF) >= Minimum, $"{tone}, thème clair : {Contrast(colors.Ink, 0xFFFFFF):0.00}");
            Assert.True(Contrast(colors.DarkInk, 0x2B2B2B) >= Minimum, $"{tone}, thème sombre : {Contrast(colors.DarkInk, 0x2B2B2B):0.00}");
        }
    }

    [Fact]
    public void Muted_text_stays_readable_on_every_fill_in_the_light_theme()
    {
        // Texte du thème clair (noir à 89,4 %) atténué à 72 %, valeur de MutedOpacity.
        const double opacity = 0.894 * 0.72;
        foreach (var (tone, colors) in Pastel.Colors)
        {
            var contrast = Contrast(Over(colors.Fill, opacity), colors.Fill);
            Assert.True(contrast >= Minimum, $"{tone} : {contrast:0.00}");
        }
    }
}
