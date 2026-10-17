using System.Globalization;
using Maus.Core.Rules;
using Maus.Core.Workshop.Memory.PawnIo;

namespace Maus.Core.Workshop.Memory;

/// <summary>Un timing du contrôleur mémoire Intel : où le lire dans les registres d'un canal.</summary>
public sealed record ImcRegisterField
{
    public required string Key { get; init; }

    public TimingGroup Group { get; init; } = TimingGroup.Primary;

    /// <summary>Nom du registre dans la documentation (TC_PRE, TC_ODT…).</summary>
    public string? Register { get; init; }

    /// <summary>Décalage en hexadécimal, relatif à la base du canal.</summary>
    public required string Offset { get; init; }

    /// <summary>Taille de lecture : 4 ou 8 octets (registre documenté en 64 bits, décalage aligné).</summary>
    public int Size { get; init; } = 4;

    /// <summary>Bits « haut:bas », tels que documentés.</summary>
    public required string Bits { get; init; }

    /// <summary>Constante ajoutée à la valeur lue, quand le registre stocke (valeur − constante).</summary>
    public int Add { get; init; }

    /// <summary>Types de mémoire concernés (vide : tous).</summary>
    public IReadOnlyList<string> MemoryTypes { get; init; } = [];

    /// <summary>« dual » : deux sources indépendantes ; « single » : une seule.</summary>
    public string Confidence { get; init; } = "dual";

    public IReadOnlyList<string> Sources { get; init; } = [];
}

/// <summary>Réglage codé du contrôleur (type de mémoire, command rate, gear…) : champ et table de correspondance.</summary>
public sealed record ImcSetting
{
    /// <summary>« MemoryType », « CommandRate », « Gear »…</summary>
    public required string Key { get; init; }

    public required string Offset { get; init; }

    /// <summary>Décalage relatif à la base du canal (par défaut) ou absolu dans MCHBAR.</summary>
    public bool PerChannel { get; init; } = true;

    public int Size { get; init; } = 4;

    public required string Bits { get; init; }

    /// <summary>Valeur numérique → libellé (« 0 » → « DDR4 ») ; une valeur absente de la table n'est pas affichée.</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();

    /// <summary>Modèles CPUID concernés, quand la famille en couvre d'autres pour lesquels ce registre n'est pas établi (vide : tous).</summary>
    public IReadOnlyList<string> CpuModels { get; init; } = [];

    public IReadOnlyList<string> Sources { get; init; } = [];
}

/// <summary>Une horloge lue dans MCHBAR : rapport × fréquence de référence.</summary>
public sealed record ImcClock
{
    /// <summary>« DCLK » (horloge mémoire, moitié du débit en MT/s), « RING », « SA »…</summary>
    public required string Key { get; init; }

    /// <summary>Décalage absolu dans MCHBAR.</summary>
    public required string Offset { get; init; }

    public int Size { get; init; } = 4;

    public required string Bits { get; init; }

    /// <summary>Référence fixe, en MHz.</summary>
    public double? MultiplierMhz { get; init; }

    /// <summary>Bit(s) qui choisissent la référence, lus à <see cref="ReferenceOffset"/> (par défaut, le même registre).</summary>
    public string? ReferenceBits { get; init; }

    public string? ReferenceOffset { get; init; }

    /// <summary>Valeur des bits de référence → fréquence de référence en MHz.</summary>
    public IReadOnlyDictionary<string, double> ReferenceMhz { get; init; } = new Dictionary<string, double>();

    /// <summary>Facteur appliqué au produit (0,5 quand le registre donne une horloge double de DCLK).</summary>
    public double Scale { get; init; } = 1;

    /// <summary>
    /// Bits du mode « gear » (Ice Lake et après), lus à <see cref="GearOffset"/> : le produit est multiplié par le facteur
    /// de <see cref="GearFactors"/>. Un mode absent de la table rend l'horloge inconnue plutôt que fausse.
    /// </summary>
    public string? GearBits { get; init; }

    public string? GearOffset { get; init; }

    public IReadOnlyDictionary<string, double> GearFactors { get; init; } = new Dictionary<string, double>();

    /// <summary>Rapports plausibles d'après les sources ; une valeur hors de cette plage est tenue pour inconnue.</summary>
    public int? MinRatio { get; init; }

    public int? MaxRatio { get; init; }

    /// <summary>Modèles CPUID concernés, quand la famille en couvre d'autres pour lesquels ce registre n'est pas établi (vide : tous).</summary>
    public IReadOnlyList<string> CpuModels { get; init; } = [];

    public IReadOnlyList<string> Sources { get; init; } = [];
}

public sealed record ImcChannelDefinition
{
    public required string Name { get; init; }

    /// <summary>Base du canal dans MCHBAR, en hexadécimal.</summary>
    public required string Base { get; init; }

    /// <summary>Registre (absolu) qui dit si le canal est garni, et masque des bits utiles ; absent : le canal est supposé garni.</summary>
    public string? PresenceOffset { get; init; }

    public string? PresenceMask { get; init; }
}

/// <summary>Carte des registres d'une famille de contrôleurs mémoire Intel, chargée depuis le catalogue JSON.</summary>
public sealed record ImcFamily
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Modèles CPUID : « 0x9E » pour la famille 6, « 0x12:0x01 » (famille:modèle) pour les autres.</summary>
    public IReadOnlyList<string> CpuModels { get; init; } = [];

    /// <summary>Taille de la fenêtre MCHBAR que le module IntelMCHBAR laisse lire pour cette génération (il refuse tout au-delà).</summary>
    public int MchbarWindowBytes { get; init; }

    /// <summary>Types de mémoire possibles ; s'il n'y en a qu'un, il sert quand aucun registre ne le donne.</summary>
    public IReadOnlyList<string> MemoryTypes { get; init; } = [];

    public IReadOnlyList<ImcChannelDefinition> Channels { get; init; } = [];

    public IReadOnlyList<ImcSetting> Settings { get; init; } = [];

    public IReadOnlyList<ImcClock> Clocks { get; init; } = [];

    /// <summary>Modèles pour lesquels le pilote Linux intel-uncore-frequency lit la plage du ring dans les MSR 0x620 et 0x621.</summary>
    public IReadOnlyList<string> UncoreMsrModels { get; init; } = [];

    public IReadOnlyList<ImcRegisterField> Fields { get; init; } = [];

    /// <summary>Timings sans source publique fiable pour cette famille, donc non affichés.</summary>
    public IReadOnlyList<string> Unavailable { get; init; } = [];

    /// <summary>Carte comparée à un outil de référence sur un vrai processeur de la famille.</summary>
    public bool VerifiedOnHardware { get; init; }

    public IReadOnlyList<string> Sources { get; init; } = [];
}

public sealed record ImcSettingValue(string Key, string Value);

/// <summary>Horloge lue : rapport, fréquence obtenue et fréquence de référence utilisée (gardée pour la fiche copiée).</summary>
public sealed record ImcClockValue(string Key, int Ratio, double Mhz, double? ReferenceMhz = null);

/// <summary>Contrôle de l'horloge mémoire lue dans le contrôleur avec la vitesse que le BIOS déclare à Windows (SMBIOS).</summary>
public enum ImcClockCheck
{
    /// <summary>Pas de vitesse déclarée par Windows, ou type de mémoire non contrôlé.</summary>
    NotChecked,

    /// <summary>Même vitesse que Windows.</summary>
    Confirmed,

    /// <summary>Le registre donnait exactement le double de la vitesse de Windows : horloge divisée par deux.</summary>
    Halved,

    /// <summary>Vitesse différente de celle de Windows, sans rapport simple : affichée telle quelle, à vérifier.</summary>
    Unconfirmed,
}

public sealed record ImcChannelTimings(string Channel, IReadOnlyList<MemoryTiming> Timings);

/// <summary>Ce que le contrôleur mémoire Intel applique réellement, lu dans ses registres.</summary>
public sealed record IntelMemoryReport(
    string Family,
    bool VerifiedOnHardware,
    IReadOnlyList<string> Channels,
    string? MemoryType,
    IReadOnlyList<ImcSettingValue> Settings,
    IReadOnlyList<ImcClockValue> Clocks,
    double? RingMinMhz,
    double? RingMaxMhz,
    IReadOnlyList<MemoryTiming> Timings,
    IReadOnlyList<ImcChannelTimings> PerChannel,
    bool ChannelsDiffer,
    IReadOnlyList<string> Unavailable,
    ImcClockCheck ClockCheck = ImcClockCheck.NotChecked,
    int? WindowsSpeedMts = null,
    bool RcdRpShared = false)
{
    /// <summary>Horloge mémoire DCLK en MHz ; le débit vaut le double, en MT/s.</summary>
    public double? DclkMhz => Clocks.FirstOrDefault(c => c.Key == "DCLK")?.Mhz;

    public double? RingMhz => Clocks.FirstOrDefault(c => c.Key == "RING")?.Mhz;
}

/// <summary>
/// Contrôleur mémoire des processeurs Intel Core, par le module officiel PawnIO IntelMCHBAR (lecture seule).
/// La carte des registres de chaque génération vient du catalogue <c>intel-memory-controller.json</c> : chaque champ y cite
/// ses sources (fiches techniques Intel, memtest86+, coreboot). Aucun champ n'est deviné.
/// </summary>
public static class IntelMemoryController
{
    /// <summary>MSR du ring (uncore) : pilote Linux intel-uncore-frequency (MSR_UNCORE_RATIO_LIMIT, MSR_UNCORE_PERF_STATUS).</summary>
    private const uint UncoreRatioLimitMsr = 0x620;
    private const uint UncorePerfStatusMsr = 0x621;

    private static readonly Lazy<IReadOnlyList<ImcFamily>> Catalog = new(() => EmbeddedCatalog.Load<List<ImcFamily>>("intel-memory-controller.json"));

    public static IReadOnlyList<ImcFamily> Families => Catalog.Value;

    /// <summary>Famille de contrôleur d'un processeur Intel, ou <c>null</c> si MAUS ne la connaît pas.</summary>
    public static ImcFamily? FamilyFor(int cpuFamily, int cpuModel) =>
        Families.FirstOrDefault(f => f.CpuModels.Any(m => Matches(m, cpuFamily, cpuModel)));

    /// <summary>Écart toléré avec la vitesse de Windows (arrondis du BIOS : 3466 contre 3467).</summary>
    public const int SpeedToleranceMts = 50;

    /// <summary>
    /// Lit les timings et horloges du contrôleur ; <c>null</c> si aucun canal n'est garni. <paramref name="windowsSpeedMts"/> :
    /// vitesse appliquée que le BIOS déclare à Windows (SMBIOS), pour contrôler l'horloge lue (voir <see cref="CheckClock"/>).
    /// </summary>
    public static IntelMemoryReport? Read(ImcFamily family, int cpuFamily, int cpuModel, IMchbarReader mchbar, IMsrReader? msr, int? windowsSpeedMts = null)
    {
        ArgumentNullException.ThrowIfNull(family);

        var cache = new Dictionary<(int Offset, int Size), ulong>();
        ulong Raw(int offset, int size)
        {
            if (!cache.TryGetValue((offset, size), out var value))
            {
                value = size == 8 ? mchbar.ReadQword(offset) : mchbar.ReadDword(offset);
                cache[(offset, size)] = value;
            }

            return value;
        }

        var present = family.Channels.Where(c => IsPresent(c, Raw)).ToList();
        if (present.Count == 0)
        {
            return null;
        }

        bool ForThisCpu(IReadOnlyList<string> models) => models.Count == 0 || models.Any(m => Matches(m, cpuFamily, cpuModel));

        var clocks = family.Clocks
            .Where(c => ForThisCpu(c.CpuModels))
            .Select(c => ReadClock(c, Raw))
            .OfType<ImcClockValue>()
            .ToList();
        var firstType = MemoryTypeOf(family, present[0], ForThisCpu, Raw);
        var (dclk, check) = CheckClock(clocks.FirstOrDefault(c => c.Key == "DCLK")?.Mhz, firstType, windowsSpeedMts);
        if (check == ImcClockCheck.Halved)
        {
            var index = clocks.FindIndex(c => c.Key == "DCLK");
            clocks[index] = clocks[index] with { Mhz = dclk!.Value };
        }

        var perChannel = present.Select(channel =>
        {
            var baseOffset = Hex(channel.Base);
            var settings = family.Settings
                .Where(s => ForThisCpu(s.CpuModels))
                .Select(s => Decode(s, s.PerChannel ? baseOffset : 0, Raw) is { } value ? new ImcSettingValue(s.Key, value) : null)
                .OfType<ImcSettingValue>()
                .ToList();
            var type = settings.FirstOrDefault(s => s.Key == "MemoryType")?.Value ?? (family.MemoryTypes.Count == 1 ? family.MemoryTypes[0] : null);
            var timings = family.Fields
                .Where(f => f.MemoryTypes.Count == 0 || type is null || f.MemoryTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
                .Select(f =>
                {
                    var clocksValue = (int)Extract(Raw(baseOffset + Hex(f.Offset), f.Size), f.Bits) + f.Add;
                    return new MemoryTiming(f.Key, f.Group, clocksValue, Nanoseconds(f.Key, clocksValue, dclk));
                })
                .ToList();
            timings.AddRange(Derive(timings, type));
            return (channel.Name, Type: type, Settings: settings, Timings: timings);
        }).ToList();

        var first = perChannel[0];
        var differ = perChannel.Skip(1).Any(c => !c.Timings.Select(t => t.Clocks).SequenceEqual(first.Timings.Select(t => t.Clocks)));

        double? ringMin = null, ringMax = null;
        if (msr is not null && family.UncoreMsrModels.Any(m => Matches(m, cpuFamily, cpuModel)))
        {
            if (!clocks.Any(c => c.Key == "RING") && msr.Read(UncorePerfStatusMsr) is { } status && (int)(status & 0x7F) is > 0 and var current)
            {
                clocks.Add(new ImcClockValue("RING", current, current * 100.0));
            }

            if (msr.Read(UncoreRatioLimitMsr) is { } limits)
            {
                ringMax = (limits & 0x7F) is > 0 and var max ? max * 100.0 : null;
                ringMin = ((limits >> 8) & 0x7F) is > 0 and var min ? min * 100.0 : null;
            }
        }

        return new IntelMemoryReport(
            family.DisplayName,
            family.VerifiedOnHardware,
            present.Select(c => c.Name).ToList(),
            first.Type,
            first.Settings.Where(s => s.Key != "MemoryType").ToList(),
            clocks,
            ringMin,
            ringMax,
            first.Timings,
            perChannel.Select(c => new ImcChannelTimings(c.Name, c.Timings)).ToList(),
            differ,
            family.Unavailable.Where(key => !first.Timings.Any(t => t.Key == key)).ToList(),
            check,
            windowsSpeedMts,
            SharesField(family, "tRCD", "tRP"));
    }

    /// <summary>
    /// Contrôle l'horloge mémoire DCLK avec la vitesse que le BIOS déclare à Windows. Le débit vaut deux fois DCLK (fiche Intel
    /// 336465-001, MC_BIOS_REQ : « QCLK » = 2 × DCLK). Sur le PC du porteur (i7-8700K, référence 133 MHz), rapport × référence
    /// donne bien DCLK (comparé à CPU-Z) ; sur un autre PC Coffee Lake en DDR4-4000 (rapport d'essai du 16/10/2026), le même
    /// calcul donnait le double. Aucune source publiée n'explique l'écart : MAUS ne le devine pas, il compare. Même vitesse que
    /// Windows : confirmée ; exactement le double, en DDR4 : divisée par deux et signalée ; autre écart : affichée, à vérifier.
    /// Les vieux BIOS déclarent la fréquence en MHz (moitié des MT/s) : elle est d'abord convertie, comme dans le module 10.
    /// </summary>
    public static (double? DclkMhz, ImcClockCheck Check) CheckClock(double? dclkMhz, string? memoryType, int? windowsSpeedMts)
    {
        if (dclkMhz is not { } dclk || windowsSpeedMts is not > 0 || memoryType is not ("DDR4" or "DDR5"))
        {
            return (dclkMhz, ImcClockCheck.NotChecked);
        }

        var windows = Modules.M10Memory.MemorySpeeds.ToTransfersPerSecond(memoryType == "DDR5" ? 5 : 4, windowsSpeedMts.Value);
        var register = dclk * 2;
        if (Math.Abs(register - windows) <= SpeedToleranceMts)
        {
            return (dclk, ImcClockCheck.Confirmed);
        }

        return memoryType == "DDR4" && Math.Abs(register - (2 * windows)) <= 2 * SpeedToleranceMts
            ? (dclk / 2, ImcClockCheck.Halved)
            : (dclk, ImcClockCheck.Unconfirmed);
    }

    /// <summary>
    /// tRTP et tWR, que les contrôleurs Skylake à Comet Lake n'exposent pas : coreboot programme le contrôleur Intel avec
    /// tRDPRE = tRTP et tWRPRE = 4 + tCWL + tWR (src/northbridge/intel/haswell/native_raminit/timings_refresh.c), soit la
    /// relation JEDEC « écriture → fermeture = WL + BL/2 + tWR » pour des rafales de 8 (DDR3, DDR4). Calculés seulement quand
    /// le registre manque et que les timings nécessaires sont lus.
    /// </summary>
    public static IEnumerable<MemoryTiming> Derive(IReadOnlyList<MemoryTiming> timings, string? memoryType)
    {
        ArgumentNullException.ThrowIfNull(timings);
        if (memoryType is not ("DDR4" or "DDR3"))
        {
            yield break;
        }

        int? Clocks(string key) => timings.FirstOrDefault(t => t.Key == key)?.Clocks;
        if (Clocks("tRTP") is null && Clocks("tRDPRE") is { } rdpre and > 0)
        {
            yield return new MemoryTiming("tRTP", TimingGroup.Secondary, rdpre, Derived: true);
        }

        if (Clocks("tWR") is null && Clocks("tWRPRE") is { } wrpre && Clocks("tCWL") is { } cwl && wrpre - cwl - 4 is > 0 and var wr)
        {
            yield return new MemoryTiming("tWR", TimingGroup.Secondary, wr, Derived: true);
        }
    }

    /// <summary>Deux timings lus dans le même champ (Skylake à Comet Lake : « tRP (and tRCD) », fiche Intel 336465-001, TC_PRE).</summary>
    private static bool SharesField(ImcFamily family, string first, string second) =>
        family.Fields.FirstOrDefault(f => f.Key == first) is { } a
        && family.Fields.FirstOrDefault(f => f.Key == second) is { } b
        && Hex(a.Offset) == Hex(b.Offset) && a.Bits == b.Bits && a.Size == b.Size;

    /// <summary>Type de mémoire d'un canal : registre de la génération, ou type unique de la famille.</summary>
    private static string? MemoryTypeOf(ImcFamily family, ImcChannelDefinition channel, Func<IReadOnlyList<string>, bool> forThisCpu, Func<int, int, ulong> raw)
    {
        var setting = family.Settings.FirstOrDefault(s => s.Key == "MemoryType" && forThisCpu(s.CpuModels));
        var value = setting is null ? null : Decode(setting, setting.PerChannel ? Hex(channel.Base) : 0, raw);
        return value ?? (family.MemoryTypes.Count == 1 ? family.MemoryTypes[0] : null);
    }

    /// <summary>Extrait les bits « haut:bas » d'une valeur lue.</summary>
    public static ulong Extract(ulong raw, string bits)
    {
        ArgumentNullException.ThrowIfNull(bits);

        var parts = bits.Split(':');
        var high = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var low = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : high;
        var width = high - low + 1;
        var mask = width >= 64 ? ulong.MaxValue : (1UL << width) - 1;
        return (raw >> low) & mask;
    }

    public static int Hex(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return int.Parse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>« 0x9E » désigne le modèle 0x9E de la famille 6 ; « 0x12:0x01 », le modèle 1 de la famille 0x12.</summary>
    private static bool Matches(string entry, int cpuFamily, int cpuModel)
    {
        var parts = entry.Split(':');
        return parts.Length == 2
            ? Hex(parts[0]) == cpuFamily && Hex(parts[1]) == cpuModel
            : cpuFamily == 6 && Hex(entry) == cpuModel;
    }

    private static bool IsPresent(ImcChannelDefinition channel, Func<int, int, ulong> raw)
    {
        if (channel.PresenceOffset is null)
        {
            return true;
        }

        var mask = channel.PresenceMask is null ? uint.MaxValue : (ulong)(uint)Hex(channel.PresenceMask);
        // Contrôleur absent (deuxième contrôleur des Core 11e génération et après) : le registre se lit tout à un.
        var value = raw(Hex(channel.PresenceOffset), 4);
        return value != uint.MaxValue && (value & mask) != 0;
    }

    private static string? Decode(ImcSetting setting, int baseOffset, Func<int, int, ulong> raw)
    {
        var value = Extract(raw(baseOffset + Hex(setting.Offset), setting.Size), setting.Bits).ToString(CultureInfo.InvariantCulture);
        return setting.Values.TryGetValue(value, out var label) ? label : null;
    }

    private static ImcClockValue? ReadClock(ImcClock clock, Func<int, int, ulong> raw)
    {
        var ratio = (int)Extract(raw(Hex(clock.Offset), clock.Size), clock.Bits);
        if (ratio <= 0 || ratio < clock.MinRatio || ratio > clock.MaxRatio)
        {
            return null;
        }

        double? reference = clock.MultiplierMhz;
        if (clock.ReferenceBits is { } bits)
        {
            var selector = Extract(raw(Hex(clock.ReferenceOffset ?? clock.Offset), 4), bits).ToString(CultureInfo.InvariantCulture);
            reference = clock.ReferenceMhz.TryGetValue(selector, out var mhz) ? mhz : null;
        }

        var gear = 1.0;
        if (clock.GearBits is { } gearBits)
        {
            var selector = Extract(raw(Hex(clock.GearOffset ?? clock.Offset), 4), gearBits).ToString(CultureInfo.InvariantCulture);
            if (!clock.GearFactors.TryGetValue(selector, out gear))
            {
                return null;
            }
        }

        return reference is { } r ? new ImcClockValue(clock.Key, ratio, ratio * r * gear * clock.Scale, r) : null;
    }

    /// <summary>
    /// Durée en nanosecondes de la latence CAS (latence réelle) et des timings de rafraîchissement, ceux qu'on compare
    /// habituellement en temps : cycles × 1000 / DCLK (MHz), soit cycles × 2000 / débit (MT/s).
    /// </summary>
    private static double? Nanoseconds(string key, int clocks, double? dclkMhz) =>
        dclkMhz is > 0 && key is "tCL" or "tRFC" or "tRFC2" or "tRFC4" or "tRFCpb" or "tRFCsb" or "tREFI" ? clocks * 1000.0 / dclkMhz.Value : null;
}
