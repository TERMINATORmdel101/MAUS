using System.Windows;
using System.Windows.Media;

namespace Maus.App.Appearance;

/// <summary>Teintes pastel de MAUS : les quatre couleurs du logo adoucies, plus trois teintes d'appoint.</summary>
public enum Tone
{
    Blue,
    Rose,
    Mint,
    Sand,
    Lilac,
    Aqua,
    Grey,
}

/// <summary>
/// Couleurs pastel (demande du porteur, 05/10/2026) : un fond doux, son contour et une encre foncée lisible dessus, pour le
/// thème clair et pour le thème sombre (où le « pastel » devient une teinte sourde avec une encre claire). Le texte normal
/// garde la couleur du thème, très contrastée sur tous ces fonds.
/// </summary>
/// <remarks>
/// Les pinceaux restent les mêmes objets toute la vie de MAUS : changer de thème change leur couleur, et tout ce qui les
/// affiche (ressources dynamiques comme convertisseurs) suit à chaud. Ils ne servent que sur le fil de l'interface.
/// En contraste élevé, Windows impose ses couleurs : fond de fenêtre et texte de fenêtre.
/// </remarks>
public static class Pastel
{
    /// <summary>Fond, contour et encre en thème clair, puis en thème sombre (RGB).</summary>
    private static readonly Dictionary<Tone, (uint Fill, uint Stroke, uint Ink, uint DarkFill, uint DarkStroke, uint DarkInk)> Colors = new()
    {
        [Tone.Blue] = (0xE4ECFF, 0xC3D3F7, 0x1E3F8F, 0x1F2940, 0x34466F, 0xAFC6FF),
        [Tone.Rose] = (0xFCE6EA, 0xF2C3CC, 0x9A1830, 0x3A2229, 0x5F3843, 0xFFB8C4),
        [Tone.Mint] = (0xDDF4E8, 0xB3E2C9, 0x0B6B41, 0x1A322A, 0x2C5644, 0x93E2BC),
        [Tone.Sand] = (0xFBF0D9, 0xEDD7A5, 0x835800, 0x362D1A, 0x5C4C2C, 0xF3D48E),
        [Tone.Lilac] = (0xEEE7FA, 0xD6C8F0, 0x5A3C99, 0x2C2541, 0x4A3E6B, 0xD0BEFF),
        [Tone.Aqua] = (0xDDF3F5, 0xB1DFE4, 0x0B6470, 0x183236, 0x2A555B, 0x8FDDE6),
        [Tone.Grey] = (0xEEF0F3, 0xD6DAE1, 0x474D5A, 0x2B2E34, 0x41454E, 0xC9CED8),
    };

    private static readonly Dictionary<Tone, (SolidColorBrush Fill, SolidColorBrush Stroke, SolidColorBrush Ink)> Brushes =
        Enum.GetValues<Tone>().ToDictionary(t => t, _ => (new SolidColorBrush(), new SolidColorBrush(), new SolidColorBrush()));

    /// <summary>Fond de la page, à peine teinté (thème clair) ; transparent en thème sombre, qui garde le fond de Windows.</summary>
    public static SolidColorBrush Page { get; } = new();

    static Pastel() => Use(dark: false, highContrast: false);

    public static SolidColorBrush Fill(Tone tone) => Brushes[tone].Fill;

    public static SolidColorBrush Stroke(Tone tone) => Brushes[tone].Stroke;

    public static SolidColorBrush Ink(Tone tone) => Brushes[tone].Ink;

    /// <summary>Teinte d'une lettre de MAUS ou d'un composant de l'atelier, comme <see cref="Views.LetterToBrushConverter"/>.</summary>
    public static Tone OfLetter(string? letter) => letter switch
    {
        "M" or "P" or "W" => Tone.Blue,
        "A" or "G" or "E" => Tone.Rose,
        "U" or "R" or "B" or "N" => Tone.Mint,
        "S" or "C" => Tone.Sand,
        _ => Tone.Grey,
    };

    /// <summary>Teinte d'un verdict : vert, bleu, or et rouge du logo, gris pour l'information et l'indéterminé.</summary>
    public static Tone OfStatus(Maus.Core.FindingStatus status) => status switch
    {
        Maus.Core.FindingStatus.Ok => Tone.Mint,
        Maus.Core.FindingStatus.Improvable => Tone.Blue,
        Maus.Core.FindingStatus.Warning => Tone.Sand,
        Maus.Core.FindingStatus.Problem => Tone.Rose,
        _ => Tone.Grey,
    };

    /// <summary>Met les couleurs du thème, puis publie les pinceaux sous les clés <c>Pastel{Teinte}</c>, <c>…Stroke</c> et <c>…Ink</c>.</summary>
    internal static void Apply(ResourceDictionary resources, bool dark, bool highContrast)
    {
        Use(dark, highContrast);
        foreach (var (tone, (fill, stroke, ink)) in Brushes)
        {
            resources["Pastel" + tone] = fill;
            resources["Pastel" + tone + "Stroke"] = stroke;
            resources["Pastel" + tone + "Ink"] = ink;
        }

        resources["PastelPage"] = Page;
    }

    private static void Use(bool dark, bool highContrast)
    {
        foreach (var (tone, (fill, stroke, ink)) in Brushes)
        {
            if (highContrast)
            {
                fill.Color = SystemColors.WindowColor;
                stroke.Color = SystemColors.WindowTextColor;
                ink.Color = SystemColors.WindowTextColor;
                continue;
            }

            var c = Colors[tone];
            fill.Color = Rgb(dark ? c.DarkFill : c.Fill);
            stroke.Color = Rgb(dark ? c.DarkStroke : c.Stroke);
            ink.Color = Rgb(dark ? c.DarkInk : c.Ink);
        }

        Page.Color = highContrast ? SystemColors.WindowColor : dark ? System.Windows.Media.Colors.Transparent : Rgb(0xF7F8FC);
    }

    private static Color Rgb(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
