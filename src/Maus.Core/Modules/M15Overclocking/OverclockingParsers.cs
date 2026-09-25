using System.Text.RegularExpressions;

namespace Maus.Core.Modules.M15Overclocking;

/// <summary>Génération d'un Ryzen X3D : les possibilités de réglage diffèrent d'une génération à l'autre.</summary>
internal enum X3DGeneration
{
    None,
    Ryzen5000,
    Ryzen7000,
    Ryzen9000,
    Other,
}

/// <summary>Outil de réglage ou de test reconnu dans la liste des programmes installés.</summary>
internal sealed record TuningTool(string Key, string Name, bool IsStabilityTool, Regex DisplayName);

/// <summary>Outil trouvé dans les clés Uninstall, avec la version et l'éditeur déclarés par son installateur.</summary>
internal sealed record InstalledTool(TuningTool Tool, string? Version, string? Publisher)
{
    /// <summary>Nom et version, par exemple « MSI Afterburner 4.6.7 Beta 2 ».</summary>
    public string Label => string.IsNullOrWhiteSpace(Version) ? Tool.Name : $"{Tool.Name} {Version.Trim()}";
}

/// <summary>Analyses sans accès au système utilisées par le module 15.</summary>
internal static partial class OverclockingParsers
{
    public const string YouTubeSearch = "https://www.youtube.com/results?search_query=";

    /// <summary>Outils officiels de réglage, puis outils de test de stabilité, reconnus par leur nom affiché (DisplayName).</summary>
    public static IReadOnlyList<TuningTool> KnownTools { get; } =
    [
        new("afterburner", "MSI Afterburner", false, AfterburnerName()),
        new("rtss", "RivaTuner Statistics Server", false, RivaTunerName()),
        new("ryzen-master", "AMD Ryzen Master", false, RyzenMasterName()),
        new("xtu", "Intel XTU", false, XtuName()),
        new("nvidia-app", "NVIDIA App", false, NvidiaAppName()),
        new("adrenalin", "AMD Software: Adrenalin Edition", false, AdrenalinName()),
        new("intel-graphics", "Intel Graphics Software", false, IntelGraphicsName()),
        new("hwinfo", "HWiNFO", true, HwInfoName()),
        new("occt", "OCCT", true, OcctName()),
        new("cinebench", "Cinebench", true, CinebenchName()),
        new("3dmark", "3DMark", true, ThreeDMarkName()),
        new("memtest86", "MemTest86", true, MemTestName()),
    ];

    /// <summary>Outil reconnu d'après le nom affiché d'un programme installé, ou <c>null</c>.</summary>
    public static TuningTool? MatchTool(string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? null : KnownTools.FirstOrDefault(t => t.DisplayName.IsMatch(displayName.Trim()));

    /// <summary>
    /// Modèle court d'un processeur pour une recherche de tutoriel :
    /// « Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz » donne « i7-8700K », « AMD Ryzen 7 7800X3D 8-Core Processor » donne « Ryzen 7 7800X3D ».
    /// </summary>
    public static string CpuModel(string? name)
    {
        var text = CleanTrademarks(name);
        var match = IntelCoreModel().Match(text);
        if (match.Success)
        {
            return match.Value;
        }

        match = IntelUltraModel().Match(text);
        if (match.Success)
        {
            return "Core " + Spaces().Replace(match.Value, " ");
        }

        match = RyzenModel().Match(text);
        if (match.Success)
        {
            return Spaces().Replace(match.Value, " ");
        }

        text = CpuNoise().Replace(text, " ");
        return Spaces().Replace(text, " ").Trim();
    }

    /// <summary>Modèle court d'une carte graphique : « NVIDIA GeForce RTX 2080 Ti » donne « RTX 2080 Ti », « Intel(R) Arc(TM) B580 Graphics » donne « Arc B580 ».</summary>
    public static string GpuModel(string? name)
    {
        var text = Spaces().Replace(CleanTrademarks(name), " ").Trim();
        var shortened = GpuBrandPrefix().Replace(text, string.Empty);
        shortened = GpuGraphicsSuffix().Replace(shortened, string.Empty).Trim();
        return shortened.Length == 0 ? text : shortened;
    }

    /// <summary>Intel à coefficient débloqué : suffixe K, KF ou KS, ou gamme X et XE (plateformes HEDT).</summary>
    public static bool IsUnlockedIntel(string model) => UnlockedIntelSuffix().IsMatch(model);

    public static X3DGeneration X3DGenerationOf(string? name)
    {
        var match = X3DModel().Match(name ?? string.Empty);
        if (!match.Success)
        {
            return X3DGeneration.None;
        }

        return match.Groups["gen"].Value switch
        {
            "5" => X3DGeneration.Ryzen5000,
            "7" => X3DGeneration.Ryzen7000,
            "9" => X3DGeneration.Ryzen9000,
            _ => X3DGeneration.Other,
        };
    }

    /// <summary>Recherche de vidéos : seul le modèle matériel et le nom de l'outil partent dans l'adresse.</summary>
    public static string TutorialUrl(string query) => YouTubeSearch + Uri.EscapeDataString(Spaces().Replace(query, " ").Trim());

    /// <summary>
    /// Modèle de PC pour une recherche de tutoriel : premier mot du fabricant, sans ponctuation, suivi du modèle
    /// (« ASUSTeK COMPUTER INC. » et « ROG Strix G513QY » donnent « ASUSTeK ROG Strix G513QY »).
    /// </summary>
    public static string PcModel(string? manufacturer, string? model)
    {
        var brand = (manufacturer ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.TrimEnd('.', ',') ?? string.Empty;
        var text = model?.Trim() ?? string.Empty;
        if (brand.Length > 0 && text.StartsWith(brand, StringComparison.OrdinalIgnoreCase))
        {
            brand = string.Empty;
        }

        return Spaces().Replace($"{brand} {text}", " ").Trim();
    }

    /// <summary>Utilitaire du constructeur d'un portable et site officiel, d'après le fabricant déclaré par le BIOS.</summary>
    public static (string Utility, string Site)? LaptopUtility(string? manufacturer)
    {
        var text = manufacturer ?? string.Empty;
        (string Key, string Utility, string Site)[] vendors =
        [
            ("ASUS", "Armoury Crate ou MyASUS", "asus.com"),
            ("Lenovo", "Lenovo Vantage", "lenovo.com"),
            ("HP", "OMEN Gaming Hub ou HP Command Center", "hp.com"),
            ("Hewlett", "OMEN Gaming Hub ou HP Command Center", "hp.com"),
            ("Alienware", "Alienware Command Center", "dell.com"),
            ("Dell", "Alienware Command Center ou Dell Power Manager", "dell.com"),
            ("Micro-Star", "MSI Center", "msi.com"),
            ("MSI", "MSI Center", "msi.com"),
            ("Acer", "NitroSense ou PredatorSense", "acer.com"),
            ("Gigabyte", "Gigabyte Control Center", "gigabyte.com"),
            ("Razer", "Razer Synapse", "razer.com"),
        ];
        foreach (var vendor in vendors)
        {
            var matches = vendor.Key.Length <= 3
                ? Regex.IsMatch(text, $@"\b{vendor.Key}\b", RegexOptions.IgnoreCase)
                : text.Contains(vendor.Key, StringComparison.OrdinalIgnoreCase);
            if (matches)
            {
                return (vendor.Utility, vendor.Site);
            }
        }

        return null;
    }

    private static string CleanTrademarks(string? text) =>
        Trademarks().Replace(text ?? string.Empty, string.Empty);

    [GeneratedRegex(@"\(R\)|\(TM\)|®|™", RegexOptions.IgnoreCase)]
    private static partial Regex Trademarks();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\bi[3579]-\d{4,5}[A-Z]{0,2}\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntelCoreModel();

    [GeneratedRegex(@"\bUltra\s+[3579]\s+\d{3}[A-Z]{0,2}\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntelUltraModel();

    [GeneratedRegex(@"\bRyzen\s+(?:Threadripper\s+)?(?:PRO\s+)?(?:\d\s+)?(?:PRO\s+)?\d{4}[A-Z0-9]*\b", RegexOptions.IgnoreCase)]
    private static partial Regex RyzenModel();

    [GeneratedRegex(@"\bCPU\b|@\s*[\d.]+\s*GHz|\b\d+-Core\b|\bProcessor\b|\bw(?:ith|/)\s+Radeon\b.*$|\b\d+(?:th|nd|rd|st) Gen\b", RegexOptions.IgnoreCase)]
    private static partial Regex CpuNoise();

    [GeneratedRegex(@"\d(?:K|KF|KS|X|XE)$", RegexOptions.IgnoreCase)]
    private static partial Regex UnlockedIntelSuffix();

    [GeneratedRegex(@"\b(?<gen>\d)\d{3}X3D", RegexOptions.IgnoreCase)]
    private static partial Regex X3DModel();

    [GeneratedRegex(@"^(?:(?:NVIDIA|GeForce|AMD|Radeon|Intel)\s+)+", RegexOptions.IgnoreCase)]
    private static partial Regex GpuBrandPrefix();

    [GeneratedRegex(@"\s+Graphics$", RegexOptions.IgnoreCase)]
    private static partial Regex GpuGraphicsSuffix();

    [GeneratedRegex(@"^MSI Afterburner\b", RegexOptions.IgnoreCase)]
    private static partial Regex AfterburnerName();

    [GeneratedRegex(@"^RivaTuner Statistics Server\b", RegexOptions.IgnoreCase)]
    private static partial Regex RivaTunerName();

    [GeneratedRegex(@"\bRyzen\s+Master\b", RegexOptions.IgnoreCase)]
    private static partial Regex RyzenMasterName();

    [GeneratedRegex(@"\bExtreme Tuning Utility\b", RegexOptions.IgnoreCase)]
    private static partial Regex XtuName();

    [GeneratedRegex(@"^NVIDIA App(?:\s+[\d.]+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex NvidiaAppName();

    [GeneratedRegex(@"^AMD Software\b", RegexOptions.IgnoreCase)]
    private static partial Regex AdrenalinName();

    [GeneratedRegex(@"^Intel.*\b(?:Graphics Software|Arc.*Control)\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntelGraphicsName();

    [GeneratedRegex(@"^HWiNFO", RegexOptions.IgnoreCase)]
    private static partial Regex HwInfoName();

    [GeneratedRegex(@"^OCCT\b", RegexOptions.IgnoreCase)]
    private static partial Regex OcctName();

    [GeneratedRegex(@"\bCinebench\b", RegexOptions.IgnoreCase)]
    private static partial Regex CinebenchName();

    [GeneratedRegex(@"^3DMark\b", RegexOptions.IgnoreCase)]
    private static partial Regex ThreeDMarkName();

    [GeneratedRegex(@"\bMemTest86\b", RegexOptions.IgnoreCase)]
    private static partial Regex MemTestName();
}
