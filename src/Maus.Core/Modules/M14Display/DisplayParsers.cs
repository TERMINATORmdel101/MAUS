using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M14Display;

/// <summary>Conversions pures entre les valeurs brutes de Windows et le modèle du module.</summary>
internal static class DisplayParsers
{
    /// <summary>
    /// <c>DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2</c> : bit 0 advancedColorSupported, bit 1 advancedColorActive,
    /// bit 3 advancedColorLimitedByPolicy, bit 4 highDynamicRangeSupported, bit 5 highDynamicRangeUserEnabled,
    /// bit 6 wideColorSupported, bit 7 wideColorUserEnabled.
    /// </summary>
    public static AdvancedColorInfo FromAdvancedColorInfo2(uint value, int encoding, uint bitsPerChannel, int activeColorMode)
    {
        var mode = Enum.IsDefined((AdvancedColorMode)activeColorMode) ? (AdvancedColorMode?)activeColorMode : null;
        return new AdvancedColorInfo(
            // Bit 4 = highDynamicRangeSupported ; un HDR actif prouve à lui seul la prise en charge (constaté : bit à 0 avec le HDR actif).
            HdrSupported: (value & 0x10) != 0 || mode == AdvancedColorMode.Hdr,
            HdrActive: mode == AdvancedColorMode.Hdr,
            Encoding: ToEncoding(encoding),
            BitsPerColorChannel: (int)bitsPerChannel,
            ActiveMode: mode);
    }

    /// <summary>
    /// <c>DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO</c> (avant le build 26100) : bit 0 advancedColorSupported,
    /// bit 1 advancedColorEnabled, bit 2 wideColorEnforced (écran SDR à gamut étendu, pas du HDR).
    /// </summary>
    public static AdvancedColorInfo FromAdvancedColorInfo(uint value, int encoding, uint bitsPerChannel)
    {
        var wideColorOnly = (value & 0x4) != 0;
        return new AdvancedColorInfo(
            HdrSupported: (value & 0x1) != 0 && !wideColorOnly,
            HdrActive: (value & 0x2) != 0 && !wideColorOnly,
            Encoding: ToEncoding(encoding),
            BitsPerColorChannel: (int)bitsPerChannel);
    }

    /// <summary>
    /// <c>\\?\PCI#VEN_10DE&amp;DEV_1E04&amp;SUBSYS_86751043&amp;REV_A1#4&amp;f71f481&amp;0&amp;0008#{5b45201d-…}</c>
    /// donne <c>PCI\VEN_10DE&amp;DEV_1E04&amp;SUBSYS_86751043&amp;REV_A1\4&amp;f71f481&amp;0&amp;0008</c>.
    /// </summary>
    public static string? PnpIdFromAdapterPath(string? adapterDevicePath)
    {
        if (string.IsNullOrWhiteSpace(adapterDevicePath))
        {
            return null;
        }

        var path = adapterDevicePath.Trim();
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            path = path[4..];
        }

        var guid = path.LastIndexOf("#{", StringComparison.Ordinal);
        if (guid > 0)
        {
            path = path[..guid];
        }

        return path.Contains('#', StringComparison.Ordinal) ? path.Replace('#', '\\') : null;
    }

    /// <summary>Fréquence maximale proposée à une résolution donnée (dans les deux orientations), ou <c>null</c>.</summary>
    public static int? MaxRefreshAt(IReadOnlyList<DisplayMode> modes, int width, int height)
    {
        var matching = modes.Where(m => (m.Width == width && m.Height == height) || (m.Width == height && m.Height == width)).ToList();
        return matching.Count == 0 ? null : matching.Max(m => m.RefreshHz);
    }

    /// <summary>Plus grande résolution proposée (nombre de pixels), utilisée quand le mode préféré est illisible.</summary>
    public static (int Width, int Height)? LargestResolution(IReadOnlyList<DisplayMode> modes) =>
        modes.Count == 0 ? null : modes.OrderByDescending(m => (long)m.Width * m.Height).Select(m => (m.Width, m.Height)).First();

    /// <summary>
    /// Valeur <c>DirectXUserGlobalSettings</c> (« SwapEffectUpgradeEnable=1;VRROptimizeEnable=0;AutoHDREnable=1; »)
    /// découpée en paires clé-valeur.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseDirectXSettings(string? value)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in (value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0].Length > 0)
            {
                settings[parts[0]] = parts[1];
            }
        }

        return settings;
    }

    public static bool IsInternal(OutputTechnology output) =>
        output is OutputTechnology.Internal or OutputTechnology.DisplayPortEmbedded or OutputTechnology.UdiEmbedded or OutputTechnology.Lvds;

    public static string ConnectorLabel(OutputTechnology output) => output switch
    {
        OutputTechnology.Hdmi => "HDMI",
        OutputTechnology.DisplayPortExternal => "DisplayPort",
        OutputTechnology.DisplayPortUsbTunnel => T("DisplayPort par USB-C"),
        OutputTechnology.Dvi => "DVI",
        OutputTechnology.Hd15 => T("VGA (analogique)"),
        OutputTechnology.Miracast => T("Miracast (sans fil)"),
        OutputTechnology.IndirectWired => T("adaptateur USB ou station d'accueil"),
        OutputTechnology.IndirectVirtual => T("écran virtuel"),
        _ when IsInternal(output) => T("dalle intégrée"),
        _ => T("autre connecteur"),
    };

    public static string EncodingLabel(ColorEncoding encoding) => encoding switch
    {
        ColorEncoding.Rgb => "RGB",
        ColorEncoding.YCbCr444 => "YCbCr 4:4:4",
        ColorEncoding.YCbCr422 => "YCbCr 4:2:2",
        ColorEncoding.YCbCr420 => "YCbCr 4:2:0",
        _ => T("niveaux de gris"),
    };

    public static bool IsChromaSubsampled(ColorEncoding encoding) => encoding is ColorEncoding.YCbCr422 or ColorEncoding.YCbCr420;

    private static ColorEncoding ToEncoding(int value) =>
        Enum.IsDefined((ColorEncoding)value) ? (ColorEncoding)value : ColorEncoding.Rgb;
}
