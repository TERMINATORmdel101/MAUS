using System.Globalization;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Preferences;

/// <summary>Thème de l'interface.</summary>
public enum ThemeChoice
{
    /// <summary>Comme Windows (clair ou sombre), et suit ses changements.</summary>
    System,
    Light,
    Dark,
}

/// <summary>Couleur d'accentuation de l'interface (boutons, sélection). Les couleurs d'état des constats ne changent jamais.</summary>
public enum AccentChoice
{
    /// <summary>Teinte du processeur (Intel, AMD) et touche de celle de la carte graphique (Nvidia, AMD, Intel).</summary>
    Components,

    /// <summary>Couleur d'accentuation choisie dans Windows.</summary>
    Windows,

    /// <summary>Bleu du logo MAUS.</summary>
    Maus,
    Blue,
    Red,
    Green,
    Violet,
    Orange,
    Teal,
}

/// <summary>Animations de l'interface.</summary>
public enum AnimationChoice
{
    /// <summary>Selon le réglage « Effets d'animation » de Windows.</summary>
    System,
    On,
    Off,
}

/// <summary>Marque d'un composant, pour la teinte « selon vos composants ». Seule une teinte est reprise, jamais un logo.</summary>
public enum ComponentBrand
{
    Other,
    Intel,
    Amd,
    Nvidia,
}

/// <summary>Marques du processeur et de la carte graphique principale.</summary>
public sealed record ComponentBrands(ComponentBrand Cpu, ComponentBrand Graphics)
{
    public static ComponentBrands Unknown { get; } = new(ComponentBrand.Other, ComponentBrand.Other);

    /// <summary>Marque d'après la chaîne fabricant du CPUID (« GenuineIntel », « AuthenticAMD »).</summary>
    public static ComponentBrand FromCpuVendor(string? vendor) => vendor switch
    {
        "GenuineIntel" => ComponentBrand.Intel,
        "AuthenticAMD" => ComponentBrand.Amd,
        _ => ComponentBrand.Other,
    };
}

/// <summary>Couleur en rouge, vert, bleu (0 à 255).</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Parse(string hex)
    {
        var value = hex.TrimStart('#');
        return new Rgb(
            byte.Parse(value.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}");

    /// <summary>Mélange avec <paramref name="other"/> : 0 = cette couleur, 1 = l'autre.</summary>
    public Rgb Mix(Rgb other, double amount) => new(
        (byte)Math.Round(R + ((other.R - R) * amount)),
        (byte)Math.Round(G + ((other.G - G) * amount)),
        (byte)Math.Round(B + ((other.B - B) * amount)));

    /// <summary>Luminance relative (WCAG 2.x).</summary>
    public double Luminance()
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(R)) + (0.7152 * Channel(G)) + (0.0722 * Channel(B));
    }

    /// <summary>Rapport de contraste WCAG entre deux couleurs (1 à 21).</summary>
    public double ContrastWith(Rgb other)
    {
        var (a, b) = (Luminance(), other.Luminance());
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}

/// <summary>
/// Déclinaisons d'une couleur d'accentuation, sur le modèle de Windows (trois plus claires, trois plus foncées). Les fonds
/// portant du texte blanc (thème clair) ou noir (thème sombre) sont ajustés pour un contraste d'au moins 4,5:1 (WCAG AA).
/// </summary>
public sealed record AccentVariants(Rgb Base, Rgb Light1, Rgb Light2, Rgb Light3, Rgb Dark1, Rgb Dark2, Rgb Dark3, Rgb OnWhiteText)
{
    public const double MinimumContrast = 4.5;

    private static readonly Rgb White = new(255, 255, 255);
    private static readonly Rgb Black = new(0, 0, 0);

    public static AccentVariants From(Rgb color)
    {
        var dark1 = Darkest(color, 0.2);
        return new AccentVariants(
            color,
            Lightest(color, 0.25),
            Lightest(color, 0.5),
            Lightest(color, 0.7),
            dark1,
            color.Mix(Black, 0.4),
            color.Mix(Black, 0.6),
            dark1);
    }

    /// <summary>Assombrit au moins de <paramref name="minimum"/>, et assez pour porter du texte blanc lisible.</summary>
    private static Rgb Darkest(Rgb color, double minimum)
    {
        for (var amount = minimum; amount < 1; amount += 0.02)
        {
            var candidate = color.Mix(Black, amount);
            if (candidate.ContrastWith(White) >= MinimumContrast)
            {
                return candidate;
            }
        }

        return color.Mix(Black, 0.9);
    }

    /// <summary>Éclaircit au moins de <paramref name="minimum"/>, et assez pour porter du texte noir lisible.</summary>
    private static Rgb Lightest(Rgb color, double minimum)
    {
        for (var amount = minimum; amount < 1; amount += 0.02)
        {
            var candidate = color.Mix(White, amount);
            if (candidate.ContrastWith(Black) >= MinimumContrast)
            {
                return candidate;
            }
        }

        return color.Mix(White, 0.9);
    }
}

/// <summary>Teintes proposées et règle « selon vos composants ».</summary>
public static class AccentPalette
{
    /// <summary>Bleu du logo MAUS.</summary>
    public static Rgb Maus { get; } = Rgb.Parse("#1F4FBF");

    /// <summary>Teintes évoquant les marques ; ce ne sont que des couleurs, pas des logos ni des marques déposées.</summary>
    public static Rgb IntelHue { get; } = Rgb.Parse("#0068B5");

    public static Rgb AmdHue { get; } = Rgb.Parse("#D71E28");

    public static Rgb NvidiaHue { get; } = Rgb.Parse("#76B900");

    /// <summary>Couleurs fixes, reprises de la palette d'accentuation de Windows.</summary>
    public static Rgb Fixed(AccentChoice choice) => choice switch
    {
        AccentChoice.Blue => Rgb.Parse("#0078D4"),
        AccentChoice.Red => Rgb.Parse("#D13438"),
        AccentChoice.Green => Rgb.Parse("#10893E"),
        AccentChoice.Violet => Rgb.Parse("#8764B8"),
        AccentChoice.Orange => Rgb.Parse("#CA5010"),
        AccentChoice.Teal => Rgb.Parse("#038387"),
        _ => Maus,
    };

    public static Rgb? Hue(ComponentBrand brand) => brand switch
    {
        ComponentBrand.Intel => IntelHue,
        ComponentBrand.Amd => AmdHue,
        ComponentBrand.Nvidia => NvidiaHue,
        _ => null,
    };

    /// <summary>
    /// Teinte principale et teinte secondaire (touche discrète, par exemple le dégradé de la barre de navigation).
    /// « Selon vos composants » : le processeur donne la teinte principale, la carte graphique la secondaire si elle est
    /// d'une autre marque. <paramref name="windowsAccent"/> est la couleur d'accentuation de Windows, si elle est connue.
    /// </summary>
    public static (Rgb Primary, Rgb? Secondary) For(AccentChoice choice, ComponentBrands brands, Rgb? windowsAccent)
    {
        switch (choice)
        {
            case AccentChoice.Components:
                var cpu = Hue(brands.Cpu);
                var gpu = Hue(brands.Graphics);
                var primary = cpu ?? gpu ?? Maus;
                return (primary, gpu is { } g && g != primary ? g : null);
            case AccentChoice.Windows:
                return (windowsAccent ?? Maus, null);
            case AccentChoice.Maus:
                return (Maus, null);
            default:
                return (Fixed(choice), null);
        }
    }
}

/// <summary>
/// Marque de la carte graphique principale, lue dans le registre (classe des cartes d'affichage), sans pilote ni WMI.
/// Une carte Nvidia ou AMD l'emporte sur la partie graphique intégrée d'un processeur Intel.
/// </summary>
public static class GraphicsBrand
{
    /// <summary>Classe « Display » de Windows (GUID documenté par Microsoft : System-Defined Device Setup Classes).</summary>
    public const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static ComponentBrand Read(IRegistryReader registry)
    {
        var vendors = new List<string>();
        try
        {
            foreach (var subKey in registry.GetSubKeyNames(RegistryHive.LocalMachine, DisplayClassKey))
            {
                if (subKey.Length == 4 && subKey.All(char.IsAsciiDigit)
                    && registry.GetValue(RegistryHive.LocalMachine, $@"{DisplayClassKey}\{subKey}", "MatchingDeviceId") is string id)
                {
                    vendors.Add(id);
                }
            }
        }
        catch (MausAccessDeniedException)
        {
            return ComponentBrand.Other;
        }

        return FromDeviceIds(vendors);
    }

    /// <summary>Marque d'après les identifiants PCI (« pci\ven_10de&amp;dev_2484 ») : Nvidia, puis AMD, puis Intel.</summary>
    public static ComponentBrand FromDeviceIds(IEnumerable<string> deviceIds)
    {
        var ids = deviceIds.Select(id => id.ToUpperInvariant()).ToList();
        bool Has(string vendor) => ids.Any(id => id.Contains($"VEN_{vendor}", StringComparison.Ordinal));
        return Has("10DE") ? ComponentBrand.Nvidia
            : Has("1002") ? ComponentBrand.Amd
            : Has("8086") ? ComponentBrand.Intel
            : ComponentBrand.Other;
    }
}
