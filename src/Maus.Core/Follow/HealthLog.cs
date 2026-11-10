using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maus.Core.Platform;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Follow;

/// <summary>Un disque tel qu'il a été lu à un audit ; ce qui n'a pas été lu reste vide, jamais à zéro.</summary>
/// <param name="Id">Numéro du disque dans Windows.</param>
/// <param name="Name">Modèle du disque : ce numéro peut passer à un autre disque, les courbes ne se mélangent pas.</param>
/// <param name="Source">Journal NVMe ou compteurs de fiabilité : leurs erreurs ne comptent pas la même chose.</param>
public sealed record HealthLogDisk(string Id, string Name, string Source, double? WearPercent = null, double? Errors = null);

/// <summary>Capacité d'une batterie, en pourcentage de sa capacité d'origine.</summary>
public sealed record HealthLogBattery(string Id, double CapacityPercent);

/// <summary>Un démarrage mesuré par Windows : sa date à lui, pas celle de l'audit qui l'a lu.</summary>
public sealed record DatedBootTime(DateTimeOffset At, double Seconds);

/// <summary>Résultat d'un test de refroidissement.</summary>
/// <param name="DatedByFile">Ancien chiffre de <c>cooling.txt</c>, sans date : celle du fichier le remplace.</param>
public sealed record HealthLogCooling(double RiseC, double? IdleC = null, double? PeakC = null, double? BackInSeconds = null, bool DatedByFile = false);

/// <summary>Une ligne du carnet : un audit, ou un test de refroidissement.</summary>
public sealed record HealthLogEntry(DateTimeOffset At)
{
    public bool Weekly { get; init; }

    /// <summary>Audit fait avec les droits administrateur : sans eux, l'absence d'une valeur ne dit rien du matériel.</summary>
    public bool Elevated { get; init; }

    public IReadOnlyList<HealthLogDisk>? Disks { get; init; }

    public IReadOnlyList<HealthLogBattery>? Batteries { get; init; }

    /// <summary>Le dernier démarrage lu à cet audit, et ceux que le carnet n'avait pas encore.</summary>
    public IReadOnlyList<DatedBootTime>? Boots { get; init; }

    public HealthLogCooling? Cooling { get; init; }
}

/// <summary>
/// Carnet de santé du PC : ce que l'audit lit déjà (usure et erreurs des disques, capacité de la batterie, temps de démarrage) et
/// la chauffe au test de refroidissement, datés, pour comparer ce PC à lui-même. Aucune prédiction, aucun seuil.
/// </summary>
public static class HealthLog
{
    public const string SourceNvme = "nvme";
    public const string SourceReliability = "reliability";

    private const string DiskPrefix = "M11.disk-";
    private const string NvmeSuffix = "-nvme";
    private const string ReliabilitySuffix = "-reliability";
    private const string BatteryPrefix = "M16.battery.";
    private const string BootFinding = "M12.boot-time";
    private const string BootPrefix = "boot:";

    /// <summary>Démarrages notés au plus dans une ligne (l'audit lit 60 jours de journal).</summary>
    internal const int BootsInALine = 60;

    /// <summary>Mesures du constat de durée de démarrage : un démarrage par clé, sa date en secondes Unix, indépendante de la langue.</summary>
    public static Dictionary<string, double>? BootMeasures(IEnumerable<(DateTime At, double? Seconds)> boots)
    {
        ArgumentNullException.ThrowIfNull(boots);
        var measures = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (at, seconds) in boots)
        {
            if (seconds is > 0 and var value && at.Year >= 2000)
            {
                measures[BootPrefix + new DateTimeOffset(at).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)] = value;
            }
        }

        return measures.Count == 0 ? null : measures;
    }

    /// <summary>La ligne de cet audit ; <c>null</c> si rien n'a pu être lu : une ligne vide ne dirait rien.</summary>
    public static HealthLogEntry? FromAudit(IEnumerable<ModuleResult> results, IReadOnlyList<HealthLogEntry> existing, DateTimeOffset at, bool weekly, bool elevated)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(existing);

        var findings = results.SelectMany(r => r.Findings).Where(f => f.Measures is not null).ToList();
        var disks = ReadDisks(findings);
        var batteries = findings
            .Where(f => f.Id.StartsWith(BatteryPrefix, StringComparison.Ordinal) && f.Measures!.ContainsKey(MeasureHistory.Health))
            .Select(f => new HealthLogBattery(f.Id[BatteryPrefix.Length..], f.Measures![MeasureHistory.Health]))
            .ToList();
        var boots = ReadBoots(findings, existing);
        if (disks.Count == 0 && batteries.Count == 0 && boots.Count == 0)
        {
            return null;
        }

        return new HealthLogEntry(at)
        {
            Weekly = weekly,
            Elevated = elevated,
            Disks = disks.Count == 0 ? null : disks,
            Batteries = batteries.Count == 0 ? null : batteries,
            Boots = boots.Count == 0 ? null : boots,
        };
    }

    /// <summary>Démarrages datés, du plus ancien au plus récent, sans doublon ; vide si aucun n'a été mesuré.</summary>
    public static IReadOnlyList<DatedBootTime> BootTimes(IEnumerable<HealthLogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return [.. entries.SelectMany(e => e.Boots ?? []).DistinctBy(b => b.At).OrderBy(b => b.At)];
    }

    /// <summary>Clés des courbes qu'une ligne contient, pour garder la ligne où chacune commence.</summary>
    internal static IEnumerable<string> SeriesKeys(HealthLogEntry entry)
    {
        // Fichier retouché : une ligne vide ne compte pas, elle ne doit pas faire échouer l'écriture du carnet.
        foreach (var disk in (entry.Disks ?? []).OfType<HealthLogDisk>())
        {
            yield return "disk|" + disk.Id + "|" + disk.Name + "|" + disk.Source;
        }

        foreach (var battery in (entry.Batteries ?? []).OfType<HealthLogBattery>())
        {
            yield return "battery|" + battery.Id;
        }

        if (entry.Boots is { Count: > 0 })
        {
            yield return "boot";
        }
    }

    /// <summary>Le journal NVMe du disque passe avant ses compteurs de fiabilité, comme dans l'audit.</summary>
    private static List<HealthLogDisk> ReadDisks(List<Finding> findings)
    {
        var disks = new Dictionary<string, HealthLogDisk>(StringComparer.Ordinal);
        foreach (var (suffix, source) in new[] { (NvmeSuffix, SourceNvme), (ReliabilitySuffix, SourceReliability) })
        {
            foreach (var finding in findings.Where(f => f.Id.Length > DiskPrefix.Length + suffix.Length
                && f.Id.StartsWith(DiskPrefix, StringComparison.Ordinal) && f.Id.EndsWith(suffix, StringComparison.Ordinal)))
            {
                double? wear = finding.Measures!.TryGetValue(MeasureHistory.Wear, out var used) ? used : null;
                double? errors = finding.Measures.TryGetValue(MeasureHistory.Errors, out var count) ? count : null;
                if (wear is not null || errors is not null)
                {
                    var id = finding.Id[DiskPrefix.Length..^suffix.Length];
                    disks.TryAdd(id, new HealthLogDisk(id, finding.MeasuredItem ?? id, source, wear, errors));
                }
            }
        }

        return [.. disks.Values.OrderBy(d => d.Id, StringComparer.Ordinal)];
    }

    /// <summary>Un démarrage se lit à chaque audit tant qu'on n'a pas redémarré : il ne compte qu'une fois.</summary>
    private static List<DatedBootTime> ReadBoots(List<Finding> findings, IReadOnlyList<HealthLogEntry> existing)
    {
        if (findings.Find(f => f.Id == BootFinding)?.Measures is not { } measures)
        {
            return [];
        }

        var read = new List<DatedBootTime>();
        foreach (var (key, seconds) in measures)
        {
            if (key.StartsWith(BootPrefix, StringComparison.Ordinal) && seconds > 0
                && long.TryParse(key.AsSpan(BootPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)
                && unix is > 0 and < 253402300800)
            {
                read.Add(new DatedBootTime(DateTimeOffset.FromUnixTimeSeconds(unix), seconds));
            }
        }

        var known = existing.SelectMany(e => e.Boots ?? []).Select(b => b.At).ToHashSet();
        var newest = read.Count == 0 ? default : read.Max(b => b.At);
        return [.. read.Where(b => b.At == newest || !known.Contains(b.At)).OrderBy(b => b.At).TakeLast(BootsInALine)];
    }
}

/// <summary>Le carnet, dans le profil de l'utilisateur (<c>health-log.json</c>) : rien n'est envoyé, rien n'y sert à écrire dans Windows.</summary>
/// <param name="path">Fichier du carnet.</param>
/// <param name="oldCoolingPath">Ancien <c>cooling.txt</c> : un seul chiffre, repris avec la date du fichier tant que le carnet n'a aucun test.</param>
/// <param name="wait">Attente entre deux tentatives de lecture (remplaçable pour les tests).</param>
public sealed class HealthLogStore(string path, string? oldCoolingPath = null, Action<TimeSpan>? wait = null)
{
    private const int KeepAudits = 400;
    private const int KeepCooling = 60;
    private const int LongestName = 120;

    /// <summary>Lignes gardées parce qu'une courbe y commence : bien plus que les disques et batteries d'un PC.</summary>
    private const int KeepFirsts = 256;

    /// <summary>Disques ou batteries lus au plus dans une ligne (fichier retouché).</summary>
    private const int MostInALine = 64;

    /// <summary>Un disque NVMe note son usure sur un octet : 255 au plus.</summary>
    private const double HighestWear = 255;

    private const double HighestErrors = 1e18;

    private const double HighestBattery = 300;

    /// <summary>Un démarrage ne dure pas plus d'une journée, un refroidissement non plus.</summary>
    private const double HighestSeconds = 86400;

    private const double LowestTemperature = -50;

    private const double HighestTemperature = 200;

    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };

    private readonly JsonHistoryFile<HealthLogEntry> _file = new(
        path,
        Options,
        KeepAudits,
        e => e.At,
        _ => T("Le carnet de santé ({0}) est momentanément illisible : il n'est pas réécrit, pour ne rien perdre.", path),
        wait,
        kept: Kept,
        clean: Clean);

    public static HealthLogStore CreateDefault()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MAUS");
        return new(Path.Combine(folder, "health-log.json"), Path.Combine(folder, "cooling.txt"));
    }

    /// <summary>Faux si le carnet n'a pas pu être relu (illisible ou abîmé) ; un carnet absent rend vrai et rien.</summary>
    public bool TryLoad(out IReadOnlyList<HealthLogEntry> entries)
    {
        var read = _file.TryLoad(out var kept);
        var clean = kept.ToList();
        if (read && !clean.Exists(e => e.Cooling is not null) && OldCooling() is { } old)
        {
            clean.Insert(0, old);
        }

        entries = [.. clean.OrderBy(e => e.At)];
        return read;
    }

    /// <summary>Lignes du carnet, de la plus ancienne à la plus récente ; vide si le fichier est absent ou illisible.</summary>
    public IReadOnlyList<HealthLogEntry> Load() => TryLoad(out var entries) ? entries : [];

    /// <summary>
    /// Temps de démarrage mesurés par Windows et déjà notés par le carnet, datés, du plus ancien au plus récent ; vide si aucun.
    /// Comparer deux périodes demande de regarder toute leur plage, jamais un seul chiffre : cette durée varie d'un jour à l'autre.
    /// </summary>
    public IReadOnlyList<DatedBootTime> BootTimes() => HealthLog.BootTimes(Load());

    /// <summary>Dernier test de refroidissement noté ; <c>null</c> s'il n'y en a pas.</summary>
    public HealthLogEntry? LastCooling() => Load().LastOrDefault(e => e.Cooling is not null);

    /// <summary>Note cet audit ; rend sa ligne, ou <c>null</c> s'il n'a rien pu lire (rien n'est alors écrit).</summary>
    /// <exception cref="IOException">Fichier momentanément illisible (laissé intact) ou écriture impossible.</exception>
    /// <exception cref="UnauthorizedAccessException">Écriture refusée.</exception>
    public HealthLogEntry? AddAudit(IEnumerable<ModuleResult> results, DateTimeOffset at, bool weekly, bool elevated)
    {
        if (HealthLog.FromAudit(results, Load(), at, weekly, elevated) is not { } entry)
        {
            return null;
        }

        _file.Add(entry);
        return entry;
    }

    /// <summary>Note un test de refroidissement, après l'ancien chiffre de <c>cooling.txt</c> s'il n'est pas encore dans le carnet.</summary>
    /// <exception cref="IOException">Fichier momentanément illisible (laissé intact) ou écriture impossible.</exception>
    /// <exception cref="UnauthorizedAccessException">Écriture refusée.</exception>
    public void AddCooling(CoolingResult result, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_file.TryLoad(out var kept) && !kept.Any(e => e.Cooling is not null) && OldCooling() is { } old)
        {
            _file.Add(old);
        }

        _file.Add(new HealthLogEntry(at) { Cooling = new HealthLogCooling(result.RiseC, result.IdleC, result.PeakC, result.BackInSeconds) });
    }

    /// <summary>Bornes : les derniers audits et les derniers tests, plus la ligne où chaque courbe commence (la « première valeur »).</summary>
    private static IEnumerable<HealthLogEntry> Kept(IReadOnlyList<HealthLogEntry> all)
    {
        var audits = all.Where(e => e.Cooling is null).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var firsts = new List<HealthLogEntry>();
        foreach (var entry in audits)
        {
            // Toutes les clés de la ligne sont notées, d'où pas d'arrêt à la première nouvelle.
            var startsSeries = false;
            foreach (var key in HealthLog.SeriesKeys(entry))
            {
                startsSeries |= seen.Add(key);
            }

            if (startsSeries && firsts.Count < KeepFirsts)
            {
                firsts.Add(entry);
            }
        }

        return firsts
            .Concat(audits.TakeLast(KeepAudits))
            .Concat(all.Where(e => e.Cooling is not null).TakeLast(KeepCooling))
            .Distinct()
            .OrderBy(e => e.At);
    }

    /// <summary>Fichier modifiable par un programme du même compte : une valeur impossible n'est pas lue, plutôt que montrée.</summary>
    private static HealthLogEntry? Clean(HealthLogEntry entry)
    {
        if (!StoredValues.IsPastDate(entry.At) || (entry.Cooling is { } cooling && !IsPossible(cooling)))
        {
            return null;
        }

        var disks = entry.Disks?.Where(d => d?.Id is not null && d.Name is not null && d.Source is HealthLog.SourceNvme or HealthLog.SourceReliability)
            .Take(MostInALine)
            .Select(d => d with
            {
                Id = StoredValues.Text(d.Id, LongestName),
                Name = StoredValues.Text(d.Name, LongestName),
                WearPercent = StoredValues.Within(d.WearPercent, 0, HighestWear),
                Errors = StoredValues.Within(d.Errors, 0, HighestErrors),
            })
            .Where(d => d.WearPercent is not null || d.Errors is not null)
            .ToList();
        var batteries = entry.Batteries?.Where(b => b?.Id is not null && b.CapacityPercent > 0 && StoredValues.IsWithin(b.CapacityPercent, 0, HighestBattery))
            .Take(MostInALine)
            .Select(b => b with { Id = StoredValues.Text(b.Id, LongestName) })
            .ToList();
        var boots = entry.Boots?.Where(b => b is not null && b.Seconds > 0 && StoredValues.IsWithin(b.Seconds, 0, HighestSeconds) && StoredValues.IsPastDate(b.At))
            .Take(HealthLog.BootsInALine)
            .ToList();
        return entry with
        {
            Disks = disks,
            Batteries = batteries,
            Boots = boots,
            Cooling = entry.Cooling is null ? null : entry.Cooling with { BackInSeconds = StoredValues.Within(entry.Cooling.BackInSeconds, 0, HighestSeconds) },
        };
    }

    /// <summary>Une chauffe impossible ne dit rien du PC : la ligne n'est pas lue.</summary>
    private static bool IsPossible(HealthLogCooling cooling) =>
        StoredValues.IsWithin(cooling.RiseC, -HighestTemperature, HighestTemperature)
        && (cooling.IdleC is null || StoredValues.IsWithin(cooling.IdleC.Value, LowestTemperature, HighestTemperature))
        && (cooling.PeakC is null || StoredValues.IsWithin(cooling.PeakC.Value, LowestTemperature, HighestTemperature));

    private HealthLogEntry? OldCooling()
    {
        if (oldCoolingPath is null)
        {
            return null;
        }

        try
        {
            return AtomicFile.ReadText(oldCoolingPath, wait: wait) is { State: StoredFileState.Read, Text: { } text }
                && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var rise)
                ? Clean(new HealthLogEntry(new DateTimeOffset(File.GetLastWriteTime(oldCoolingPath))) { Cooling = new HealthLogCooling(rise, DatedByFile: true) })
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
