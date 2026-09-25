namespace Maus.Core.Modules.M14Display;

/// <summary>Connecteur vidéo (<c>DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY</c>), valeurs de wingdi.h.</summary>
internal enum OutputTechnology : uint
{
    Hd15 = 0,
    SVideo = 1,
    CompositeVideo = 2,
    ComponentVideo = 3,
    Dvi = 4,
    Hdmi = 5,
    Lvds = 6,
    DJpn = 8,
    Sdi = 9,
    DisplayPortExternal = 10,
    DisplayPortEmbedded = 11,
    UdiExternal = 12,
    UdiEmbedded = 13,
    SdtvDongle = 14,
    Miracast = 15,
    IndirectWired = 16,
    IndirectVirtual = 17,
    DisplayPortUsbTunnel = 18,
    Internal = 0x80000000,
    Other = 0xFFFFFFFF,
}

/// <summary>Encodage des couleurs sur le câble (<c>DISPLAYCONFIG_COLOR_ENCODING</c>).</summary>
internal enum ColorEncoding
{
    Rgb = 0,
    YCbCr444 = 1,
    YCbCr422 = 2,
    YCbCr420 = 3,
    Intensity = 4,
}

/// <summary>Mode de couleur actif (<c>DISPLAYCONFIG_ADVANCED_COLOR_MODE</c>, build 26100 et plus).</summary>
internal enum AdvancedColorMode
{
    Sdr = 0,
    Wcg = 1,
    Hdr = 2,
}

/// <summary>Un mode proposé par <c>EnumDisplaySettingsExW</c> pour la source de l'écran.</summary>
internal sealed record DisplayMode(int Width, int Height, int RefreshHz);

/// <summary>Couleur avancée lue par <c>DisplayConfigGetDeviceInfo</c> (version 2 si disponible, sinon version 1).</summary>
internal sealed record AdvancedColorInfo(
    bool HdrSupported,
    bool HdrActive,
    ColorEncoding Encoding,
    int BitsPerColorChannel,
    AdvancedColorMode? ActiveMode = null);

/// <summary>Un chemin d'affichage actif : un écran, sa source et l'adaptateur qui le pilote.</summary>
internal sealed record DisplayPath
{
    /// <summary>Nom de l'écran (EDID), vide pour certaines dalles internes.</summary>
    public string MonitorName { get; init; } = string.Empty;

    /// <summary>Nom GDI de la source, par exemple <c>\\.\DISPLAY1</c>.</summary>
    public string SourceName { get; init; } = string.Empty;

    /// <summary>Chemin de l'adaptateur (<c>DISPLAYCONFIG_ADAPTER_NAME</c>), qui contient l'identifiant PnP du GPU.</summary>
    public string? AdapterDevicePath { get; init; }

    /// <summary>Nom de l'adaptateur selon <c>EnumDisplayDevicesW</c> (par exemple « NVIDIA GeForce RTX 2080 Ti »).</summary>
    public string? AdapterName { get; init; }

    public OutputTechnology Output { get; init; } = OutputTechnology.Other;

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Fréquence exacte du chemin (<c>targetInfo.refreshRate</c>), ou <c>null</c> si illisible.</summary>
    public double? RefreshHz { get; init; }

    /// <summary>Résolution native (mode préféré de l'écran), si connue.</summary>
    public int? NativeWidth { get; init; }

    public int? NativeHeight { get; init; }

    public IReadOnlyList<DisplayMode> Modes { get; init; } = [];

    public AdvancedColorInfo? Color { get; init; }
}

/// <summary>Lecture de la configuration d'affichage active, en lecture seule.</summary>
internal interface IDisplayConfigReader
{
    /// <summary>Chemins actifs, ou <c>null</c> si l'API est indisponible (session sans affichage, service…).</summary>
    IReadOnlyList<DisplayPath>? ReadActivePaths();
}
