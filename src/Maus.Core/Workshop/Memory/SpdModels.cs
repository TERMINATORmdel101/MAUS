namespace Maus.Core.Workshop.Memory;

/// <summary>Famille d'un timing, comme les classent les passionnés d'overclocking mémoire.</summary>
public enum TimingGroup
{
    Primary,
    Secondary,
    Tertiary,
}

/// <summary>Un timing mémoire : en cycles d'horloge (ce que le BIOS affiche) et, quand c'est parlant, en nanosecondes.</summary>
public sealed record MemoryTiming(string Key, TimingGroup Group, int? Clocks, double? Nanoseconds = null);

public enum ProfileKind
{
    /// <summary>Profil standard JEDEC (celui que la carte mère utilise sans réglage).</summary>
    Jedec,

    /// <summary>Profil Intel XMP (2.0 en DDR4, 3.0 en DDR5).</summary>
    Xmp,

    /// <summary>Profil AMD EXPO (DDR5).</summary>
    Expo,
}

/// <summary>Un profil de la puce SPD : fréquence, timings et tensions annoncés par le fabricant de la barrette.</summary>
public sealed record SpdProfile(
    ProfileKind Kind,
    int Number,
    string? Version,
    string? Name,
    int SpeedMts,
    double TckNs,
    IReadOnlyList<int> SupportedCl,
    IReadOnlyList<MemoryTiming> Timings,
    double? Vdd,
    double? Vddq,
    double? Vpp)
{
    private static readonly string[] SummaryKeys = ["tCL", "tRCD", "tRP", "tRAS"];

    /// <summary>Somme de contrôle du profil (XMP 3.0 et EXPO) ; <c>null</c> quand le format n'en prévoit pas.</summary>
    public bool? ChecksumOk { get; init; }

    /// <summary>Latence réelle d'accès en nanosecondes (CL × durée d'un cycle) : le bon moyen de comparer deux kits.</summary>
    public double? TrueLatencyNs => Timings.FirstOrDefault(t => t.Key == "tCL")?.Clocks is { } cl ? cl * 2000.0 / SpeedMts : null;

    /// <summary>« CL-tRCD-tRP-tRAS », par exemple « 30-38-38-96 ».</summary>
    public string Summary => string.Join('-', SummaryKeys
        .Select(key => Timings.FirstOrDefault(t => t.Key == key)?.Clocks?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"));
}

/// <summary>Contenu brut d'une puce SPD, lu par le pilote PawnIO, avec les noms de fabricants résolus par la bibliothèque de lecture.</summary>
public sealed record SpdImage(int Slot, byte[] Bytes, string? ModuleManufacturer = null, string? DramManufacturer = null);

/// <summary>Tout ce que la puce SPD d'une barrette dit d'elle-même.</summary>
public sealed record SpdModule
{
    public required int Slot { get; init; }

    /// <summary>« DDR4 » ou « DDR5 ».</summary>
    public required string MemoryType { get; init; }

    /// <summary>UDIMM, SO-DIMM, RDIMM, CUDIMM…</summary>
    public required string ModuleType { get; init; }

    public long CapacityBytes { get; init; }

    public int Ranks { get; init; }

    /// <summary>Largeur des puces (x4, x8, x16).</summary>
    public int DeviceWidth { get; init; }

    public int DieDensityGbit { get; init; }

    public int DiesPerPackage { get; init; } = 1;

    public int BankGroups { get; init; }

    public int BanksPerGroup { get; init; }

    public int RowBits { get; init; }

    public int ColumnBits { get; init; }

    /// <summary>Sous-canaux par barrette (2 en DDR5).</summary>
    public int SubChannels { get; init; } = 1;

    public bool Ecc { get; init; }

    public string PartNumber { get; init; } = string.Empty;

    public string? ModuleManufacturer { get; init; }

    public string? DramManufacturer { get; init; }

    public int? DramStepping { get; init; }

    public int? ManufacturedYear { get; init; }

    public int? ManufacturedWeek { get; init; }

    public string SpdRevision { get; init; } = string.Empty;

    /// <summary>Somme de contrôle de la zone de base : faux = lecture abîmée ou SPD modifiée, les valeurs sont alors douteuses.</summary>
    public bool ChecksumOk { get; init; }

    public bool HasThermalSensor { get; init; }

    /// <summary>Circuit d'alimentation embarqué (DDR5), par exemple « PMIC5100 ».</summary>
    public string? Pmic { get; init; }

    /// <summary>JEDEC d'abord, puis XMP et EXPO.</summary>
    public IReadOnlyList<SpdProfile> Profiles { get; init; } = [];

    /// <summary>Organisation lisible, par exemple « 2 rangs, puces x8 de 16 Gbit ».</summary>
    public string Organization => Localization.Texts.T("{0} rang(s), puces x{1} de {2} Gbit", Ranks, DeviceWidth, DieDensityGbit);
}
