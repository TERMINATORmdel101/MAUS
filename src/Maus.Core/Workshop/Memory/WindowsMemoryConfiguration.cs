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
        ArgumentNullException.ThrowIfNull(cim);

        try
        {
            return Parse(cim.Query(Query));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return [];
        }
    }

    /// <summary>
    /// Nom de l'emplacement. Certaines cartes répètent « DIMM 1 » sur chaque canal : le nom du banc (« P0 CHANNEL A ») est alors
    /// ajouté, pour que chaque barrette garde le sien.
    /// </summary>
    private static string SlotName(CimRow row, HashSet<string> repeated)
    {
        var locator = Clean(row.GetString("DeviceLocator"));
        var bank = Clean(row.GetString("BankLabel"));
        return locator is not null && bank is not null && repeated.Contains(locator) ? $"{bank} / {locator}" : locator ?? bank ?? "?";
    }

    /// <summary>Type de mémoire que déclare le BIOS, s'il est connu : celui de la première barrette qui en donne un.</summary>
    public static string? MemoryTypeOf(IReadOnlyList<MemorySlotConfiguration> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        return slots.Select(s => s.MemoryType).FirstOrDefault(t => t is not null);
    }

    public static IReadOnlyList<MemorySlotConfiguration> Parse(IEnumerable<CimRow> rows)
    {
        var all = rows.ToList();
        var repeated = all.Select(row => Clean(row.GetString("DeviceLocator"))).OfType<string>()
            .GroupBy(locator => locator, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return all.Select(row => new MemorySlotConfiguration(
            SlotName(row, repeated),
            Clean(row.GetString("PartNumber")),
            Clean(row.GetString("Manufacturer")),
            row.GetInt64("Capacity") ?? 0,
            // Les vieux BIOS donnent la vitesse en MHz, soit la moitié : MemorySpeeds la convertit, comme dans le module 10.
            Modules.M10Memory.MemorySpeeds.FromRow(row, Modules.M10Memory.MemorySpeeds.Generation(row.GetInt64("SMBIOSMemoryType"))).Configured,
            // ConfiguredVoltage est en millivolts ; 0 signifie « inconnu ».
            Positive(row.GetInt64("ConfiguredVoltage")) is { } millivolts ? millivolts / 1000.0 : null,
            Modules.M10Memory.MemorySpeeds.TypeName(row.GetInt64("SMBIOSMemoryType"))))
            .ToList();
    }

    /// <summary>Une ligne par barrette : emplacement, référence, capacité, vitesse et tension appliquées.</summary>
    public static string Describe(IReadOnlyList<MemorySlotConfiguration> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);

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
        // Une puce lue à moitié n'a montré que son profil standard : rien à en conclure sur la vitesse appliquée.
        var profiles = modules.Where(m => !m.FirstPageOnly).SelectMany(m => m.Profiles).ToList();
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

        // Barrette sans profil XMP ni EXPO : rien de personnalisé à annoncer.
        if (profiles.All(p => p.Kind == ProfileKind.Jedec))
        {
            return applied >= jedecMax - SpeedToleranceMts
                ? T("Vitesse appliquée : {0} MT/s, celle du profil standard (JEDEC), le seul de cette barrette.", applied)
                : T("Vitesse appliquée : {0} MT/s, en dessous du profil standard (JEDEC) : limite du processeur ou de la carte mère, ou réglage du BIOS.", applied);
        }

        return T("Vitesse appliquée : {0} MT/s, différente des profils de la puce SPD : réglage personnalisé dans le BIOS.", applied);
    }

    private static string? Clean(string? value) => Hardware.HardwareText.Clean(value);

    private static int? Positive(long? value) => value is > 0 and <= int.MaxValue ? (int)value.Value : null;
}
