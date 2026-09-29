using System.Text.RegularExpressions;
using Maus.Core.Hardware;
using Maus.Core.Rules;

namespace Maus.Core.Modules.M09Gpu;

/// <summary>Façon de lire et de comparer la version d'un pilote.</summary>
public enum DriverVersionScheme
{
    /// <summary>Numéro commercial NVIDIA (617.14), déduit de la version Windows (32.0.16.1714).</summary>
    NvidiaMarketing,

    /// <summary>Numéro AMD Software : Adrenalin Edition (26.9.1), lu dans le registre du pilote.</summary>
    AmdSoftware,

    /// <summary>Version Windows du pilote (32.0.101.9033), utilisée telle quelle par Intel.</summary>
    WindowsDriver,
}

/// <summary>Dernière version officielle connue d'une branche de pilotes (catalogue <c>m09-gpu-drivers.json</c>).</summary>
public sealed record GpuDriverBranch
{
    public required string Id { get; init; }

    public required HardwareVendor Vendor { get; init; }

    public required string Label { get; init; }

    /// <summary>Expression régulière testée sur le nom du GPU (insensible à la casse).</summary>
    public required string NamePattern { get; init; }

    public required DriverVersionScheme Scheme { get; init; }

    /// <summary>Version dans le format du <see cref="Scheme"/>.</summary>
    public required string LatestVersion { get; init; }

    /// <summary>Version Windows correspondante, quand elle est connue (repli si le numéro commercial est illisible).</summary>
    public string? LatestDriverVersion { get; init; }

    public required DateOnly ReleasedOn { get; init; }

    public required string DownloadUrl { get; init; }

    public string? Note { get; init; }
}

/// <summary>Catalogue des pilotes graphiques : dernières versions et motifs de noms de GPU.</summary>
public sealed record GpuDriverCatalog
{
    public required DateOnly CheckedOn { get; init; }

    /// <summary>Écart (en jours) entre le pilote installé et le dernier publié au-delà duquel une mise à jour est conseillée.</summary>
    public int SignificantGapDays { get; init; } = 90;

    /// <summary>Âge (en jours) au-delà duquel un pilote est jugé ancien, même sans catalogue.</summary>
    public int MaxDriverAgeDays { get; init; } = 183;

    /// <summary>
    /// Âge (en jours) au-delà duquel le catalogue lui-même est trop vieux pour dire « à jour » : il est revérifié chaque mois
    /// (CLAUDE.md), 45 jours laissent une marge. Choix de MAUS sur sa propre fraîcheur, pas une donnée technique.
    /// </summary>
    public int CatalogMaxAgeDays { get; init; } = 45;

    public IReadOnlyList<GpuDriverBranch> Branches { get; init; } = [];

    public IReadOnlyDictionary<string, string> VendorDownloads { get; init; } = new Dictionary<string, string>();

    /// <summary>Motifs des GPU capables d'exploiter Resizable BAR, par fabricant.</summary>
    public IReadOnlyDictionary<string, string> ResizableBarCapable { get; init; } = new Dictionary<string, string>();

    /// <summary>GPU dont DLSS Frame Generation exige HAGS.</summary>
    public string FrameGenerationPattern { get; init; } = string.Empty;

    private static readonly Lazy<GpuDriverCatalog> Embedded = new(() => EmbeddedCatalog.Load<GpuDriverCatalog>("m09-gpu-drivers.json"));

    public static GpuDriverCatalog Default => Embedded.Value;

    /// <summary>Première branche du fabricant dont le motif correspond au nom du GPU.</summary>
    public GpuDriverBranch? FindBranch(HardwareVendor vendor, string gpuName) =>
        Branches.FirstOrDefault(b => b.Vendor == vendor && IsMatch(gpuName, b.NamePattern));

    public string DownloadUrlFor(HardwareVendor vendor) =>
        Lookup(VendorDownloads, vendor) ?? "https://www.microsoft.com/windows";

    public bool IsResizableBarCapable(HardwareVendor vendor, string gpuName) =>
        Lookup(ResizableBarCapable, vendor) is { } pattern && IsMatch(gpuName, pattern);

    public bool NeedsHagsForFrameGeneration(string gpuName) =>
        FrameGenerationPattern.Length > 0 && IsMatch(gpuName, FrameGenerationPattern);

    private static string? Lookup(IReadOnlyDictionary<string, string> map, HardwareVendor vendor) =>
        map.FirstOrDefault(kv => kv.Key.Equals(vendor.ToString(), StringComparison.OrdinalIgnoreCase)).Value;

    private static bool IsMatch(string text, string pattern) =>
        Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}
