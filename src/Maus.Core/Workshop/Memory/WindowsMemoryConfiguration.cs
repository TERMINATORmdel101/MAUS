using System.Globalization;
using System.Text;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop.Memory;

/// <summary>Réglages réellement appliqués à une barrette, tels que le BIOS les déclare à Windows (SMBIOS type 17).</summary>
public sealed record MemorySlotConfiguration(
    string Slot,
    string? PartNumber,
    string? Manufacturer,
    long CapacityBytes,
    int? ConfiguredMts,
    int? RatedMts,
    double? ConfiguredVolts,
    string? MemoryType);

/// <summary>
/// Vitesse appliquée et tension déclarée de la mémoire, lues sans pilote par WMI (<c>Win32_PhysicalMemory</c>, table SMBIOS
/// du BIOS). La vitesse est fiable ; la tension est celle que le BIOS inscrit dans sa table, souvent la tension par défaut
/// plutôt que celle réglée (constaté chez le porteur : 1,25 V déclarés pour 1,45 V réglés), donc jamais présentée comme mesurée.
/// </summary>
public static class WindowsMemoryConfiguration
{
    public const string Query =
        "SELECT DeviceLocator, BankLabel, PartNumber, Manufacturer, Capacity, Speed, ConfiguredClockSpeed, ConfiguredVoltage, SMBIOSMemoryType FROM Win32_PhysicalMemory";

    /// <summary>Écart toléré entre la vitesse appliquée et celle d'un profil (arrondis du BIOS, 3466 contre 3467…).</summary>
    private const int SpeedToleranceMts = 50;

    public static IReadOnlyList<MemorySlotConfiguration> Read(ICimReader cim)
    {
        try
        {
            return Parse(cim.Query(Query));
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or DataSourceUnavailableException)
        {
            return [];
        }
    }

    public static IReadOnlyList<MemorySlotConfiguration> Parse(IEnumerable<CimRow> rows) => rows
        .Select(row => new MemorySlotConfiguration(
            Clean(row.GetString("DeviceLocator")) ?? Clean(row.GetString("BankLabel")) ?? "?",
            Clean(row.GetString("PartNumber")),
            Clean(row.GetString("Manufacturer")),
            row.GetInt64("Capacity") ?? 0,
            Positive(row.GetInt64("ConfiguredClockSpeed")),
            Positive(row.GetInt64("Speed")),
            // ConfiguredVoltage est en millivolts ; 0 signifie « inconnu ».
            Positive(row.GetInt64("ConfiguredVoltage")) is { } millivolts ? millivolts / 1000.0 : null,
            MemoryTypeName(row.GetInt64("SMBIOSMemoryType"))))
        .ToList();

    /// <summary>Une ligne par barrette : emplacement, référence, capacité, vitesse et tension appliquées.</summary>
    public static string Describe(IReadOnlyList<MemorySlotConfiguration> slots)
    {
        if (slots.Count == 0)
        {
            return T("Windows ne donne aucune information sur les barrettes de ce PC.");
        }

        var text = new StringBuilder();
        foreach (var slot in slots)
        {
            if (text.Length > 0)
            {
                text.AppendLine();
            }

            text.Append(T("{0} : {1} {2}, {3:0.#} Go {4} · {5} · {6}",
                slot.Slot,
                slot.Manufacturer ?? string.Empty,
                slot.PartNumber ?? T("référence inconnue"),
                slot.CapacityBytes / (1024.0 * 1024 * 1024),
                slot.MemoryType ?? string.Empty,
                slot.ConfiguredMts is { } mts ? T("{0} MT/s appliqués", mts) : T("vitesse inconnue"),
                slot.ConfiguredVolts is { } volts ? T("{0} V déclarés par le BIOS", volts.ToString("0.000", CultureInfo.InvariantCulture)) : T("tension non déclarée par le BIOS")));
        }

        return text.ToString();
    }

    /// <summary>
    /// Compare la vitesse appliquée aux profils lus dans la puce SPD : profil XMP/EXPO actif, vitesse standard (JEDEC)
    /// alors qu'un profil plus rapide existe, ou réglage manuel au-delà de tous les profils.
    /// </summary>
    public static string? CompareWithSpd(IReadOnlyList<MemorySlotConfiguration> slots, IReadOnlyList<SpdModule> modules)
    {
        var applied = slots.Select(s => s.ConfiguredMts).OfType<int>().DefaultIfEmpty(0).Max();
        var profiles = modules.SelectMany(m => m.Profiles).ToList();
        if (applied == 0 || profiles.Count == 0)
        {
            return null;
        }

        var fastest = profiles.MaxBy(p => p.SpeedMts)!;
        var jedecMax = profiles.Where(p => p.Kind == ProfileKind.Jedec).Select(p => p.SpeedMts).DefaultIfEmpty(0).Max();
        if (applied > fastest.SpeedMts + SpeedToleranceMts)
        {
            return T("Vitesse appliquée : {0} MT/s, au-delà du profil le plus rapide de la puce SPD ({1} MT/s) : la mémoire a été réglée à la main dans le BIOS (overclocking). Les timings et la tension réels ne figurent dans aucun profil.", applied, fastest.SpeedMts);
        }

        var match = profiles
            .Where(p => p.Kind != ProfileKind.Jedec && Math.Abs(p.SpeedMts - applied) <= SpeedToleranceMts)
            .OrderBy(p => Math.Abs(p.SpeedMts - applied))
            .FirstOrDefault();
        if (match is not null)
        {
            return T("Vitesse appliquée : {0} MT/s, celle du profil {1} : ce profil est probablement activé dans le BIOS.", applied, MemoryDetails.ProfileName(match));
        }

        if (applied <= jedecMax + SpeedToleranceMts && fastest.Kind != ProfileKind.Jedec && fastest.SpeedMts > applied + SpeedToleranceMts)
        {
            return T("Vitesse appliquée : {0} MT/s, la vitesse standard (JEDEC). Le profil {1} ({2} MT/s) n'est pas activé dans le BIOS.", applied, MemoryDetails.ProfileName(fastest), fastest.SpeedMts);
        }

        return T("Vitesse appliquée : {0} MT/s, différente des profils de la puce SPD : réglage personnalisé dans le BIOS.", applied);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? Positive(long? value) => value is > 0 and <= int.MaxValue ? (int)value.Value : null;

    private static string? MemoryTypeName(long? smbiosType) => smbiosType switch
    {
        24 => "DDR3",
        26 => "DDR4",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => null,
    };
}
