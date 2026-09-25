using System.Globalization;
using System.Text.RegularExpressions;
using Maus.Core.Hardware;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M10Memory;

/// <summary>
/// Module 10 — RAM : XMP / EXPO et dual channel. Repère une mémoire bridée à sa vitesse JEDEC par défaut, le simple canal
/// et les kits mélangés. Ne modifie rien : XMP et EXPO s'activent uniquement dans le BIOS.
/// </summary>
public sealed partial class MemoryModule : IAuditModule
{
    internal const string MemoryQuery =
        "SELECT Capacity, Speed, ConfiguredClockSpeed, PartNumber, Manufacturer, DeviceLocator, BankLabel, SMBIOSMemoryType FROM Win32_PhysicalMemory";

    private static string SpeedCategory => T("Vitesse (XMP / EXPO)");
    private static string LayoutCategory => T("Barrettes et canaux");
    private const string SpeedId = "M10.xmp";
    private static string SpeedTitle => T("Mémoire à sa vitesse annoncée (XMP / EXPO)");
    private const string ChannelId = "M10.dual-channel";
    private static string ChannelTitle => T("Mémoire en double canal (dual channel)");

    private static string StabilityAdvice => T("En cas de plantages ou d'écrans bleus : BIOS récent (Module 8), test de la mémoire avec MemTest86, TestMem5 ou OCCT, puis profil plus lent.");

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    public string Id => "M10";

    public string Title => T("RAM : XMP / EXPO et dual channel");

    public int Order => 100;

    public Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<CimRow> rows;
        try
        {
            rows = context.Cim.Query(MemoryQuery);
        }
        catch (MausAccessDeniedException)
        {
            return Result(
                Finding.AdminRequired(SpeedId, SpeedTitle, SpeedCategory),
                Finding.AdminRequired(ChannelId, ChannelTitle, LayoutCategory));
        }
        catch (DataSourceUnavailableException)
        {
            return Result(NoData(SpeedId, SpeedTitle, SpeedCategory), NoData(ChannelId, ChannelTitle, LayoutCategory));
        }

        var dimms = rows.Select((row, index) => Dimm.From(row, index + 1)).Where(d => d.CapacityBytes is not 0).ToList();
        if (dimms.Count == 0)
        {
            return Result(NoData(SpeedId, SpeedTitle, SpeedCategory), NoData(ChannelId, ChannelTitle, LayoutCategory));
        }

        var findings = new List<Finding>
        {
            DetectSpeed(dimms, context.Hardware),
            DetectChannels(dimms, context.Hardware),
        };
        if (DetectMixedKit(dimms) is { } mixed)
        {
            findings.Add(mixed);
        }

        findings.Add(DescribeCapacity(dimms));
        findings.AddRange(dimms.Select(DescribeDimm));
        return Task.FromResult<IReadOnlyList<Finding>>(findings);
    }

    /// <summary>
    /// Heuristique de la fiche : une vitesse appliquée égale à une vitesse JEDEC, inférieure à celle qu'annonce la référence
    /// décodée, donne « XMP/EXPO probablement désactivé ». Une référence inconnue ne permet jamais de conclure à la désactivation.
    /// </summary>
    private static Finding DetectSpeed(List<Dimm> dimms, HardwareProfile hardware)
    {
        var configuredSpeeds = dimms.Select(d => d.ConfiguredSpeed).OfType<int>().ToList();
        if (configuredSpeeds.Count == 0)
        {
            return Finding.Unknown(SpeedId, SpeedTitle, T("Le BIOS ne publie pas la vitesse appliquée à la mémoire (ConfiguredClockSpeed)."), SpeedCategory);
        }

        var configured = configuredSpeeds.Min();
        if (dimms.Any(d => MemorySpeeds.IsLowPower(d.SmbiosType)))
        {
            return Speed(FindingStatus.Info, Severity.Info, T("{0} MT/s (mémoire soudée)", configured), null,
                T("Mémoire LPDDR soudée à la carte mère : sa vitesse est fixée par le fabricant du PC, sans profil XMP ni EXPO à activer."));
        }

        var generation = dimms.Select(d => d.Generation).FirstOrDefault(g => g is not null) ?? MemorySpeeds.GuessGeneration(configured);
        var decoded = dimms.Where(d => d.Part is not null).ToList();
        var unknownParts = dimms.Where(d => d.Part is null).Select(d => d.PartNumber ?? T("vide")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var maxJedec = MemorySpeeds.MaxJedecSpeed(generation);

        if (decoded.Count == 0)
        {
            var aboveJedec = maxJedec is { } max && configured > max + MemorySpeeds.Tolerance;
            return new Finding
            {
                Id = SpeedId,
                Title = SpeedTitle,
                Category = SpeedCategory,
                Status = aboveJedec ? FindingStatus.Ok : FindingStatus.Unknown,
                Current = $"{configured} MT/s",
                Expected = T("vitesse annoncée par la référence"),
                Explanation = aboveJedec
                    ? T("La mémoire tourne au-delà de la vitesse standard JEDEC maximale ({0} MT/s en DDR{1}) : " +
                      "un profil XMP/EXPO ou un réglage manuel est donc actif.", maxJedec, generation)
                    : T("Référence non reconnue ({0}) : impossible de connaître la vitesse annoncée. " +
                      "MAUS ne conclut jamais que XMP/EXPO est désactivé sans la connaître.", Quote(unknownParts)),
                Advice = aboveJedec ? null : T("Comparez avec la vitesse inscrite sur l'étiquette de la barrette ou sur la facture (par exemple DDR4-3200 ou DDR5-6000)."),
            };
        }

        var slowest = decoded.MinBy(d => d.Part!.RatedSpeed)!;
        var rated = slowest.Part!.RatedSpeed;
        var allJedec = decoded.All(d => d.Part!.IsJedec);
        var partial = unknownParts.Count > 0 ? T(" Référence non reconnue pour une partie des barrettes ({0}).", Quote(unknownParts)) : string.Empty;
        var expected = T("{0} MT/s (référence {1})", rated, slowest.PartNumber);

        if (configured > rated + MemorySpeeds.Tolerance)
        {
            return Speed(FindingStatus.Info, Severity.Info, $"{configured} MT/s", expected,
                T("La mémoire tourne plus vite que la vitesse annoncée par sa référence : réglage manuel (overclocking) ou profil plus rapide. " +
                "Rien d'anormal si le PC est stable.") + partial,
                StabilityAdvice);
        }

        if (MemorySpeeds.Same(configured, rated))
        {
            return Speed(FindingStatus.Ok, Severity.Low, $"{configured} MT/s", expected,
                (allJedec
                    ? T("Barrettes standard (JEDEC) : elles tournent à leur vitesse nominale sans profil à activer.")
                    : T("La mémoire tourne à la vitesse annoncée : le profil XMP/EXPO est actif.")) + partial);
        }

        if (allJedec)
        {
            return Speed(FindingStatus.Info, Severity.Info, $"{configured} MT/s", expected,
                T("Barrettes standard (JEDEC), qui n'ont pas de profil XMP/EXPO : le processeur ou la carte mère limite probablement leur vitesse. " +
                "C'est normal.") + partial);
        }

        if (!MemorySpeeds.IsJedecSpeed(generation, configured))
        {
            return Speed(FindingStatus.Info, Severity.Info, $"{configured} MT/s", expected,
                T("La mémoire tourne en dessous de la vitesse annoncée, à une vitesse non standard : réglage manuel ou profil plus lent, " +
                "parfois choisi pour la stabilité.") + partial);
        }

        if (hardware.IsLaptop)
        {
            return Speed(FindingStatus.Info, Severity.Info, $"{configured} MT/s", expected,
                T("La mémoire tourne à la vitesse standard JEDEC, en dessous de la vitesse annoncée. Sur un portable, le BIOS propose rarement " +
                "XMP et la vitesse est souvent fixée par le fabricant : rien à régler en général.") + partial);
        }

        return new Finding
        {
            Id = SpeedId,
            Title = T("XMP / EXPO probablement désactivé"),
            Category = SpeedCategory,
            Status = FindingStatusExtensions.ForDeviation(Severity.Low),
            Severity = Severity.Low,
            Current = T("{0} MT/s (vitesse standard JEDEC)", configured),
            Expected = expected,
            Explanation = T("Votre mémoire est vendue pour {0} MT/s mais fonctionne à {1} MT/s : sans profil XMP ou EXPO, " +
                "elle démarre à sa vitesse standard. Le gain d'un profil varie selon les jeux et les applications.", rated, configured) + partial,
            Advice = ProfileAdvice(hardware, generation),
        };
    }

    private static string ProfileAdvice(HardwareProfile hardware, int generation)
    {
        var amd = hardware.Cpu.Vendor == HardwareVendor.Amd;
        var advice = T("Activez le profil {0} dans le BIOS (emplacement du menu : voir le manuel de la carte mère).", ProfileName(hardware, generation));
        if (amd && generation == 5)
        {
            advice += T(" Sur AMD AM5, le premier démarrage peut rester plusieurs minutes sur un écran noir pendant l'entraînement mémoire : " +
                "n'éteignez pas le PC. L'option « Memory Context Restore » évite ensuite de le refaire à chaque démarrage ; " +
                "désactivez-la si l'instabilité persiste.");
        }

        advice += T(" Ce profil est un overclocking de la mémoire et peut annuler la garantie du processeur (Intel le précise pour XMP). " +
            "Sur certaines cartes mères d'entrée de gamme, la vitesse reste bridée même avec le profil. ") + StabilityAdvice;
        return advice;
    }

    /// <summary>XMP (Intel), EXPO (AM5), DOCP (ASUS AM4), A-XMP (MSI AM4).</summary>
    internal static string ProfileName(HardwareProfile hardware, int generation)
    {
        if (hardware.Cpu.Vendor == HardwareVendor.Intel)
        {
            return "XMP";
        }

        if (hardware.Cpu.Vendor != HardwareVendor.Amd)
        {
            return T("XMP ou EXPO");
        }

        if (generation >= 5)
        {
            return T("EXPO (certaines cartes proposent aussi XMP)");
        }

        var board = hardware.BoardManufacturer;
        if (board.Contains("ASUS", StringComparison.OrdinalIgnoreCase))
        {
            return "DOCP";
        }

        return board.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) || board.Contains("MSI", StringComparison.OrdinalIgnoreCase)
            ? "A-XMP"
            : T("XMP (appelé DOCP chez ASUS et A-XMP chez MSI)");
    }

    private static Finding DetectChannels(List<Dimm> dimms, HardwareProfile hardware)
    {
        var explanation = T("Le processeur lit la mémoire par deux canaux en parallèle. Avec une seule barrette, ou deux sur le même canal, la bande passante " +
            "est divisée par deux : c'est très pénalisant pour une puce graphique intégrée (iGPU ou APU) et sensible dans les jeux.");
        var slots = string.Join(", ", dimms.Select(d => d.Slot));
        if (dimms.Any(d => MemorySpeeds.IsLowPower(d.SmbiosType)))
        {
            return Channel(FindingStatus.Info, Severity.Info, T("mémoire soudée ({0} puce(s) déclarée(s))", dimms.Count), null,
                T("Mémoire LPDDR soudée : sa répartition entre canaux est fixée par le fabricant du PC."));
        }

        if (dimms.Count == 1)
        {
            var integrated = hardware.HasDedicatedGpu ? string.Empty : T(" Ce PC semble utiliser une puce graphique intégrée : l'effet est encore plus marqué.");
            return Channel(FindingStatus.Warning, Severity.Medium, T("1 barrette ({0}) : simple canal", slots), T("2 barrettes, une par canal"),
                explanation + integrated,
                hardware.IsLaptop
                    ? T("Si le portable a un emplacement libre, ajoutez une seconde barrette identique (même référence et même capacité). " +
                      "Sur certains portables, une partie de la mémoire est soudée : vérifiez la fiche du fabricant.")
                    : T("Ajoutez une seconde barrette identique (idéalement un kit de 2) dans l'emplacement indiqué par le manuel de la carte mère " +
                      "(souvent A2 et B2)."));
        }

        var channels = dimms.Select(d => d.Channel).ToList();
        if (channels.Any(c => c is null))
        {
            return new Finding
            {
                Id = ChannelId,
                Title = ChannelTitle,
                Category = LayoutCategory,
                Status = FindingStatus.Unknown,
                Current = T("{0} barrettes ({1})", dimms.Count, slots),
                Expected = T("réparties sur deux canaux"),
                Explanation = T("Le nom des emplacements publié par le BIOS ne permet pas de savoir sur quel canal se trouve chaque barrette."),
            };
        }

        var distinct = channels.Distinct(StringComparer.Ordinal).Count();
        if (distinct == 1)
        {
            return Channel(FindingStatus.Warning, Severity.Medium, T("{0} barrettes sur le même canal ({1})", dimms.Count, slots), T("réparties sur deux canaux"),
                explanation,
                T("Déplacez une barrette vers l'autre canal en suivant le manuel de la carte mère (en général les emplacements A2 et B2, " +
                "soit le 2e et le 4e en partant du processeur). Éteignez et débranchez le PC avant de manipuler la mémoire."));
        }

        if (dimms.Count % 2 == 1)
        {
            return Channel(FindingStatus.Info, Severity.Info, T("{0} barrettes sur {1} canaux ({2})", dimms.Count, distinct, slots), T("nombre pair de barrettes"),
                T("Avec un nombre impair de barrettes, une partie de la mémoire fonctionne en simple canal."));
        }

        return Channel(FindingStatus.Ok, Severity.Medium, T("{0} barrettes sur {1} canaux ({2})", dimms.Count, distinct, slots), T("réparties sur deux canaux"), explanation);
    }

    private static Finding? DetectMixedKit(List<Dimm> dimms)
    {
        if (dimms.Count < 2 || dimms.Any(d => MemorySpeeds.IsLowPower(d.SmbiosType)))
        {
            return null;
        }

        const string id = "M10.mixed-kit";
        const string title = "Barrettes identiques";
        var parts = dimms.Select(d => d.PartNumber).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var capacities = dimms.Select(d => d.CapacityBytes).OfType<long>().Distinct().ToList();
        var mixed = parts.Count > 1 || capacities.Count > 1;
        if (!mixed)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = LayoutCategory,
                Status = FindingStatus.Ok,
                Current = parts.Count == 1 ? T("même référence ({0})", parts[0]) : T("mêmes capacités (références non publiées)"),
                Expected = T("barrettes identiques"),
                Explanation = T("Des barrettes identiques, idéalement vendues ensemble en kit, supportent le mieux les profils XMP/EXPO."),
            };
        }

        var details = new List<string>();
        if (parts.Count > 1)
        {
            details.Add(T("références {0}", string.Join(", ", parts)));
        }

        if (capacities.Count > 1)
        {
            details.Add(T("capacités {0}", string.Join(", ", capacities.Select(FormatCapacity))));
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = LayoutCategory,
            Status = FindingStatus.Info,
            Current = T("kit mixte : {0}", string.Join(" ; ", details)),
            Expected = T("barrettes identiques"),
            Explanation = T("Des barrettes différentes fonctionnent ensemble, mais un kit mixte est plus exposé à l'instabilité avec un profil XMP/EXPO."),
            Advice = T("Si le PC plante avec le profil actif, essayez un profil plus lent. ") + StabilityAdvice,
        };
    }

    private static Finding DescribeCapacity(List<Dimm> dimms)
    {
        var total = dimms.Sum(d => d.CapacityBytes ?? 0);
        var types = dimms.Select(d => d.TypeLabel).Distinct(StringComparer.Ordinal).ToList();
        var groups = dimms
            .GroupBy(d => d.CapacityBytes)
            .Select(g => $"{g.Count()} × {(g.Key is { } bytes ? FormatCapacity(bytes) : "capacité inconnue")}");
        return new Finding
        {
            Id = "M10.capacity",
            Title = T("Quantité et type de mémoire"),
            Category = LayoutCategory,
            Status = FindingStatus.Info,
            Current = T("{0} au total ({1}), {2}", FormatCapacity(total), string.Join(" + ", groups), string.Join(" / ", types)),
            Explanation = T("Type lu dans SMBIOS (SMBIOSMemoryType) : DDR4 et DDR5 en barrettes, LPDDR5 soudée sur les portables récents."),
        };
    }

    private static Finding DescribeDimm(Dimm dimm)
    {
        var parts = new List<string>
        {
            dimm.CapacityBytes is { } bytes ? FormatCapacity(bytes) : T("capacité inconnue"),
            dimm.TypeLabel,
        };
        if (dimm.PartNumber is { } partNumber)
        {
            parts.Add(dimm.Part is { } part
                ? T("référence {0} ({1}, {2} MT/s annoncés{3}{4})", partNumber, part.Brand, part.RatedSpeed, (part.CasLatency is { } cl ? $" CL{cl}" : string.Empty), (part.IsJedec ? T(", standard JEDEC") : string.Empty))
                : T("référence {0} (non reconnue)", partNumber));
        }

        if (dimm.ConfiguredSpeed is { } configured)
        {
            parts.Add(T("{0} MT/s appliqués", configured));
        }

        var details = new List<string>();
        if (dimm.BankLabel is { } bank)
        {
            details.Add(T("banque {0}", bank));
        }

        if (dimm.Manufacturer is { } manufacturer)
        {
            details.Add(JedecCode().IsMatch(manufacturer) ? T("fabricant : code JEDEC {0}", manufacturer) : T("fabricant : {0}", manufacturer));
        }

        if (dimm.MaxSpeed is { } max)
        {
            details.Add(T("vitesse maximale SMBIOS : {0} MT/s (informative)", max));
        }

        return new Finding
        {
            Id = $"M10.dimm-{dimm.Index}",
            Title = T("Barrette {0}", dimm.Slot),
            Category = LayoutCategory,
            Status = FindingStatus.Info,
            Current = string.Join(", ", parts),
            Explanation = T("Informations publiées par le BIOS (Win32_PhysicalMemory)") + (details.Count > 0 ? $" ; {string.Join(" ; ", details)}." : "."),
        };
    }

    private static Finding Speed(FindingStatus status, Severity severity, string current, string? expected, string explanation, string? advice = null) => new()
    {
        Id = SpeedId,
        Title = SpeedTitle,
        Category = SpeedCategory,
        Status = status,
        Severity = severity,
        Current = current,
        Expected = expected,
        Explanation = explanation,
        Advice = advice,
    };

    private static Finding Channel(FindingStatus status, Severity severity, string current, string? expected, string explanation, string? advice = null) => new()
    {
        Id = ChannelId,
        Title = ChannelTitle,
        Category = LayoutCategory,
        Status = status,
        Severity = severity,
        Current = current,
        Expected = expected,
        Explanation = explanation,
        Advice = advice,
    };

    private static Finding NoData(string id, string title, string category) =>
        Finding.Unknown(id, title, T("Aucune barrette décrite par le BIOS (Win32_PhysicalMemory vide ou indisponible, fréquent en machine virtuelle)."), category);

    private static Task<IReadOnlyList<Finding>> Result(params Finding[] findings) => Task.FromResult<IReadOnlyList<Finding>>(findings);

    private static string Quote(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"« {v} »"));

    internal static string FormatCapacity(long bytes)
    {
        var gigabytes = bytes / (1024d * 1024 * 1024);
        return gigabytes.ToString(gigabytes >= 1 ? "0.#" : "0.##", French) + " Go";
    }

    [GeneratedRegex(@"^[0-9A-F]{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex JedecCode();

    /// <summary>Une barrette telle que la décrit <c>Win32_PhysicalMemory</c>, vitesses converties en MT/s.</summary>
    private sealed record Dimm(
        int Index,
        string Slot,
        string? BankLabel,
        long? CapacityBytes,
        long? SmbiosType,
        int? Generation,
        int? ConfiguredSpeed,
        int? MaxSpeed,
        string? PartNumber,
        string? Manufacturer,
        string? Channel,
        MemoryPart? Part)
    {
        public string TypeLabel => MemorySpeeds.TypeName(SmbiosType) ?? (SmbiosType is { } type ? T("type SMBIOS {0}", type) : T("type inconnu"));

        public static Dimm From(CimRow row, int index)
        {
            var locator = Clean(row.GetString("DeviceLocator"));
            var bank = Clean(row.GetString("BankLabel"));
            var partNumber = Clean(row.GetString("PartNumber"));
            var part = MemoryPartDecoder.Decode(partNumber);
            var smbiosType = row.GetInt64("SMBIOSMemoryType");
            var generation = MemorySpeeds.Generation(smbiosType) ?? part?.Generation;
            return new Dimm(
                index,
                locator ?? bank ?? $"n° {index}",
                bank,
                row.GetInt64("Capacity"),
                smbiosType,
                generation,
                ToSpeed(row.GetInt64("ConfiguredClockSpeed"), generation),
                ToSpeed(row.GetInt64("Speed"), generation),
                partNumber,
                Clean(row.GetString("Manufacturer")),
                DimmSlotParser.ChannelOf(locator, bank),
                part);
        }

        private static int? ToSpeed(long? raw, int? generation) =>
            raw is > 0 and < 100_000 ? MemorySpeeds.ToTransfersPerSecond(generation, (int)raw.Value) : null;

        /// <summary>Retire les espaces de remplissage et les valeurs vides laissées par certains BIOS.</summary>
        private static string? Clean(string? value)
        {
            var trimmed = value?.Trim().Trim('\0').Trim();
            return string.IsNullOrEmpty(trimmed)
                || trimmed.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("Undefined", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("Not Specified", StringComparison.OrdinalIgnoreCase)
                || trimmed.All(c => c == '0')
                    ? null
                    : trimmed;
        }
    }
}
