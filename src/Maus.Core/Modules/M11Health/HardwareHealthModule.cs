using System.Globalization;
using Maus.Core.Platform;
using Microsoft.Win32;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M11Health;

/// <summary>
/// Module 11 — Mini-benchmark santé. En V0.1, lecture passive uniquement : santé et usure des disques, TRIM,
/// espace libre, type du disque système, débit mémoire théorique et bridage signalé par le firmware.
/// Le test actif (processeur, mémoire, carte graphique, stockage) arrive en V0.3.
/// </summary>
public sealed class HardwareHealthModule : IAuditModule
{
    internal const string DiskQuery = "SELECT DeviceId, FriendlyName, MediaType, BusType, HealthStatus, Size, SpindleSpeed FROM MSFT_PhysicalDisk";
    internal const string ReliabilityQuery = "SELECT DeviceId, Wear, Temperature, TemperatureMax, ReadErrorsUncorrected, ReadLatencyMax, PowerOnHours FROM MSFT_StorageReliabilityCounter";
    internal const string PartitionQuery = "SELECT DiskNumber, DriveLetter FROM MSFT_Partition";
    internal const string MemoryQuery = "SELECT Capacity, ConfiguredClockSpeed, Speed, DeviceLocator, BankLabel, SMBIOSMemoryType FROM Win32_PhysicalMemory";
    internal const string ProcessorPowerProvider = "Microsoft-Windows-Kernel-Processor-Power";

    private static string StorageCategory => T("Stockage");
    private static string MemoryCategory => T("Mémoire");
    private static string ProcessorCategory => T("Processeur");
    private static string BenchmarkCategory => T("Mini-benchmark");

    /// <summary>Seuil maison d'usure d'un SSD (fiche technique, Module 11).</summary>
    private const int WearWarningPercent = 80;

    private const double FreeSpaceWarningRatio = 0.15;
    private const long ReadLatencyProblemMs = 10_000;

    private static readonly string[] TrimArguments = ["behavior", "query", "DisableDeleteNotify"];
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    public string Id => "M11";

    public string Title => T("Mini-benchmark santé");

    public int Order => 110;

    public async Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken)
    {
        var findings = new List<Finding>();
        var systemLetter = SystemDriveLetter(context.Registry);

        var disks = ReadDisks(context.Cim, findings);
        if (disks is not null)
        {
            findings.Add(DetectSystemDisk(context.Cim, disks, systemLetter));
            findings.AddRange(disks.Select(DetectDiskHealth));
            findings.AddRange(DetectReliability(context.Cim, disks));
        }

        findings.Add(await DetectTrimAsync(context.Commands, disks, cancellationToken).ConfigureAwait(false));
        findings.Add(DetectFreeSpace(context.Files, systemLetter));
        findings.Add(DetectMemoryBandwidth(context.Cim));
        findings.Add(DetectFirmwareThrottling(context));
        findings.Add(BenchmarkNotice());

        // Les constats les plus graves d'abord ; l'ordre de lecture est conservé à gravité égale.
        return findings.OrderByDescending(f => f.Status.Rank()).ToList();
    }

    private static char SystemDriveLetter(IRegistryReader registry)
    {
        try
        {
            var root = registry.GetString(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "SystemRoot");
            return HealthParsers.DriveLetterOf(root) ?? 'C';
        }
        catch (MausAccessDeniedException)
        {
            return 'C';
        }
    }

    private static List<PhysicalDiskInfo>? ReadDisks(ICimReader cim, List<Finding> findings)
    {
        const string id = "M11.disks";
        var title = T("Santé des disques");
        try
        {
            var disks = cim.Query(DiskQuery, CimScopes.Storage)
                .Select(row => new PhysicalDiskInfo(
                    row.GetString("DeviceId") ?? "?",
                    row.GetString("FriendlyName")?.Trim() is { Length: > 0 } name ? name : T("sans nom"),
                    row.GetInt64("MediaType") ?? 0,
                    row.GetInt64("BusType") ?? 0,
                    row.GetInt64("HealthStatus"),
                    row.GetInt64("Size"),
                    row.GetInt64("SpindleSpeed")))
                .OrderBy(d => d.DeviceId, StringComparer.Ordinal)
                .ToList();
            if (disks.Count == 0)
            {
                findings.Add(Finding.Unknown(id, title, T("Aucun disque physique n'a été renvoyé par Windows."), StorageCategory));
                return null;
            }

            return disks;
        }
        catch (MausAccessDeniedException)
        {
            findings.Add(Finding.AdminRequired(id, title, StorageCategory));
        }
        catch (DataSourceUnavailableException)
        {
            findings.Add(Finding.Unknown(id, title, T("L'inventaire des disques (Storage Management) est indisponible sur ce PC."), StorageCategory));
        }

        return null;
    }

    /// <summary>Le disque qui porte la lettre du dossier Windows : sur un disque dur, tout le système ralentit.</summary>
    private static Finding DetectSystemDisk(ICimReader cim, IReadOnlyList<PhysicalDiskInfo> disks, char systemLetter)
    {
        const string id = "M11.system-disk";
        var title = T("Windows installé sur un SSD");
        PhysicalDiskInfo? disk;
        try
        {
            var diskNumber = cim.Query(PartitionQuery, CimScopes.Storage)
                .Where(row => HealthParsers.DriveLetterOf(row["DriveLetter"]) == systemLetter)
                .Select(row => row.GetInt64("DiskNumber"))
                .FirstOrDefault(n => n is not null);
            disk = diskNumber is null
                ? null
                : disks.FirstOrDefault(d => d.DeviceId == diskNumber.Value.ToString(CultureInfo.InvariantCulture));
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(id, title, StorageCategory);
        }
        catch (DataSourceUnavailableException)
        {
            disk = null;
        }

        if (disk is null || (!disk.IsHdd && !disk.IsSsd))
        {
            return Finding.Unknown(id, title, T("Impossible de relier le lecteur {0}: à un disque physique de type connu.", systemLetter), StorageCategory);
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = StorageCategory,
            Status = disk.IsHdd ? FindingStatusExtensions.ForDeviation(Severity.Medium) : FindingStatus.Ok,
            Severity = Severity.Medium,
            Current = $"{disk.MediaLabel} ({disk.Name})",
            Expected = "SSD",
            Explanation = T("Le lecteur {0}: contient Windows. Sur un disque dur, le démarrage, les mises à jour et l'ouverture des applications "
                + "sont plusieurs fois plus lents que sur un SSD.", systemLetter),
            Advice = disk.IsHdd
                ? T("Passer Windows sur un SSD est l'amélioration la plus visible sur un PC ancien. Un SSD SATA ou NVMe d'entrée de gamme suffit ; "
                    + "le clonage du disque se fait avec l'outil fourni par le fabricant du SSD.")
                : null,
        };
    }

    private static Finding DetectDiskHealth(PhysicalDiskInfo disk)
    {
        var id = $"M11.disk-{Slug(disk.DeviceId)}-health";
        var title = T("Santé du disque {0}", disk.Name);
        var size = disk.SizeBytes is > 0 ? $" · {HealthParsers.FormatGigabytes(disk.SizeBytes.Value)}" : string.Empty;
        var bus = disk.MediaLabel.Contains(disk.BusLabel, StringComparison.Ordinal) ? string.Empty : $" ({disk.BusLabel})";
        var (status, label, advice) = disk.HealthStatus switch
        {
            0 => (FindingStatus.Ok, T("sain"), (string?)null),
            1 => (FindingStatus.Warning, "avertissement",
                T("Sauvegardez vos données importantes dès maintenant et surveillez ce disque : Windows y a détecté un début d'anomalie.")),
            2 => (FindingStatus.Problem, T("défaillant"),
                T("Sauvegardez immédiatement vos données : Windows considère ce disque comme défaillant. Prévoyez son remplacement.")),
            _ => (FindingStatus.Unknown, T("état non communiqué"), null),
        };

        return new Finding
        {
            Id = id,
            Title = title,
            Category = StorageCategory,
            Status = status,
            Severity = disk.HealthStatus == 1 ? Severity.Medium : Severity.High,
            Current = $"{label} · {disk.MediaLabel}{bus}{size}",
            Expected = T("sain"),
            Explanation = T("État de santé global que Windows attribue au disque d'après ses propres diagnostics (SMART). "
                + "Un disque en avertissement ou défaillant peut perdre des données à tout moment."),
            Advice = advice,
        };
    }

    private static List<Finding> DetectReliability(ICimReader cim, IReadOnlyList<PhysicalDiskInfo> disks)
    {
        const string id = "M11.disk-reliability";
        var title = T("Usure, erreurs et température des disques");
        IReadOnlyList<CimRow> rows;
        try
        {
            rows = cim.Query(ReliabilityQuery, CimScopes.Storage);
        }
        catch (MausAccessDeniedException)
        {
            return [Finding.AdminRequired(id, title, StorageCategory)];
        }
        catch (DataSourceUnavailableException)
        {
            return [Finding.Unknown(id, title, T("Les compteurs de fiabilité des disques sont indisponibles sur ce PC."), StorageCategory)];
        }

        return disks.Select(disk => DetectDiskReliability(disk, rows.FirstOrDefault(r => r.GetString("DeviceId") == disk.DeviceId))).ToList();
    }

    private static Finding DetectDiskReliability(PhysicalDiskInfo disk, CimRow? row)
    {
        var id = $"M11.disk-{Slug(disk.DeviceId)}-reliability";
        var title = T("Usure, erreurs et température du disque {0}", disk.Name);
        if (row is null)
        {
            return Finding.Unknown(id, title, T("Ce disque ne transmet pas ses compteurs de fiabilité (fréquent pour les disques USB ou derrière un contrôleur RAID)."), StorageCategory);
        }

        var wear = row.GetInt64("Wear");
        var temperature = row.GetInt64("Temperature");
        var temperatureMax = row.GetInt64("TemperatureMax");
        var readErrors = row.GetInt64("ReadErrorsUncorrected");
        var latency = row.GetInt64("ReadLatencyMax");
        var hours = row.GetInt64("PowerOnHours");

        var shown = new List<string>();
        var problems = new List<string>();
        var warnings = new List<string>();
        if (disk.IsSsd && wear is not null)
        {
            shown.Add(T("usure {0} %", wear));
            if (wear >= WearWarningPercent)
            {
                warnings.Add(T("usure de {0} % (alerte à partir de {1} %)", wear, WearWarningPercent));
            }
        }

        if (temperature is > 0)
        {
            shown.Add(temperatureMax is > 0 ? T("{0} °C (limite {1} °C)", temperature, temperatureMax) : $"{temperature} °C");
            if (temperatureMax is > 0 && temperature >= temperatureMax)
            {
                problems.Add(T("température à la limite de fonctionnement du disque"));
            }
        }

        if (readErrors is not null)
        {
            shown.Add(readErrors == 0 ? T("aucune erreur de lecture non corrigée") : T("{0} erreur(s) de lecture non corrigée(s)", readErrors));
            if (readErrors > 0)
            {
                problems.Add(T("{0} erreur(s) de lecture que le disque n'a pas pu corriger", readErrors));
            }
        }

        if (latency is > 0)
        {
            shown.Add(T("lecture la plus lente : {0} ms", latency));
            if (latency > ReadLatencyProblemMs)
            {
                problems.Add(T("une lecture a pris plus de 10 secondes"));
            }
        }

        if (hours is > 0)
        {
            shown.Add(T("{0} h de fonctionnement", hours));
        }

        if (shown.Count == 0)
        {
            return Finding.Unknown(id, title, T("Le disque renvoie des compteurs de fiabilité vides."), StorageCategory);
        }

        var severity = problems.Count > 0 ? Severity.High : warnings.Count > 0 ? Severity.Medium : Severity.High;
        var deviating = problems.Count > 0 || warnings.Count > 0;
        return new Finding
        {
            Id = id,
            Title = title,
            Category = StorageCategory,
            Status = deviating ? FindingStatusExtensions.ForDeviation(severity) : FindingStatus.Ok,
            Severity = severity,
            Current = string.Join(" · ", shown),
            Expected = T("usure < {0} %, aucune erreur non corrigée, température sous la limite", WearWarningPercent),
            Explanation = T("Compteurs tenus par le disque lui-même : usure des cellules (SSD), erreurs de lecture non corrigées, "
                + "température et temps de réponse. Ils annoncent souvent une panne avant qu'elle ne survienne."),
            Advice = deviating
                ? T("À surveiller : {0}. Sauvegardez vos données importantes", string.Join(", ", problems.Concat(warnings)))
                    + (problems.Count > 0 ? T(" sans attendre et prévoyez le remplacement du disque.") : T(" et prévoyez le remplacement du disque à moyen terme."))
                : null,
        };
    }

    private static async Task<Finding> DetectTrimAsync(ICommandRunner commands, IReadOnlyList<PhysicalDiskInfo>? disks, CancellationToken cancellationToken)
    {
        const string id = "M11.trim";
        var title = T("TRIM activé pour les SSD");
        CommandResult result;
        try
        {
            result = await commands.RunAsync("fsutil.exe", TrimArguments, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DataSourceUnavailableException or InvalidOperationException or MausAccessDeniedException)
        {
            return Finding.Unknown(id, title, T("La commande fsutil n'a pas pu être lancée."), StorageCategory);
        }

        var value = result.TimedOut || result.ExitCode != 0 ? null : HealthParsers.ParseNtfsDisableDeleteNotify(result.StandardOutput);
        if (value is not (0 or 1))
        {
            return Finding.Unknown(id, title, T("Réponse de fsutil illisible : état de TRIM inconnu."), StorageCategory);
        }

        var hasSsd = disks?.Any(d => d.IsSsd) ?? true;
        var enabled = value == 0;
        return new Finding
        {
            Id = id,
            Title = title,
            Category = StorageCategory,
            Status = enabled ? FindingStatus.Ok : hasSsd ? FindingStatusExtensions.ForDeviation(Severity.Medium) : FindingStatus.Info,
            Severity = Severity.Medium,
            Current = enabled ? T("activé (DisableDeleteNotify = 0)") : T("désactivé (DisableDeleteNotify = 1)") + (hasSsd ? string.Empty : T(", sans effet : aucun SSD détecté")),
            Expected = T("activé (DisableDeleteNotify = 0)"),
            Explanation = T("TRIM indique au SSD quels blocs sont libérés. Sans lui, le SSD ralentit en écriture au fil du temps et s'use plus vite."),
            Advice = enabled || !hasSsd
                ? null
                : T("Réactiver TRIM (commande fsutil behavior set DisableDeleteNotify 0), puis lancer une optimisation du lecteur. "
                    + "Une prochaine version le proposera, avec retour arrière possible."),
            Fixable = !enabled && hasSsd,
        };
    }

    private static Finding DetectFreeSpace(IFileSystemReader files, char systemLetter)
    {
        const string id = "M11.free-space";
        var title = T("Espace libre sur le disque système");
        (long FreeBytes, long TotalBytes)? space;
        try
        {
            space = files.GetDriveSpace($"{systemLetter}:\\");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or MausAccessDeniedException)
        {
            space = null;
        }

        if (space is not { TotalBytes: > 0 } drive)
        {
            return Finding.Unknown(id, title, T("Espace du lecteur {0}: illisible.", systemLetter), StorageCategory);
        }

        var ratio = (double)drive.FreeBytes / drive.TotalBytes;
        var low = ratio < FreeSpaceWarningRatio;
        return new Finding
        {
            Id = id,
            Title = title,
            Category = StorageCategory,
            Status = low ? FindingStatusExtensions.ForDeviation(Severity.Medium) : FindingStatus.Ok,
            Severity = Severity.Medium,
            Current = T("{0} libres sur {1} ({2})", HealthParsers.FormatGigabytes(drive.FreeBytes), HealthParsers.FormatGigabytes(drive.TotalBytes), ratio.ToString("0 %", French)),
            Expected = T("au moins 15 % libres"),
            Explanation = T("Windows a besoin de place sur {0}: pour ses mises à jour, sa mémoire virtuelle et ses fichiers temporaires. "
                + "Un SSD presque plein ralentit aussi en écriture.", systemLetter),
            Advice = low
                ? T("Libérez de la place : Paramètres > Système > Stockage > Recommandations de nettoyage, puis désinstallez les jeux et applications inutilisés.")
                : null,
        };
    }

    /// <summary>Débit théorique = canaux × vitesse configurée (MT/s) × 8 octets, par exemple 55,5 Go/s pour 2 × 3 467 MT/s.</summary>
    private static Finding DetectMemoryBandwidth(ICimReader cim)
    {
        const string id = "M11.ram-bandwidth";
        var title = T("Débit mémoire théorique");
        IReadOnlyList<CimRow> modules;
        try
        {
            modules = cim.Query(MemoryQuery);
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(id, title, MemoryCategory);
        }
        catch (DataSourceUnavailableException)
        {
            modules = [];
        }

        var speeds = modules
            .Select(m => m.GetInt64("ConfiguredClockSpeed") is > 0 and var configured ? configured : m.GetInt64("Speed"))
            .OfType<long>()
            .Where(s => s > 0)
            .ToList();
        if (modules.Count == 0 || speeds.Count == 0)
        {
            return Finding.Unknown(id, title, T("Vitesse des barrettes mémoire non communiquée par le BIOS."), MemoryCategory);
        }

        var mts = speeds.Min();
        var (channels, estimated) = HealthParsers.CountMemoryChannels(
            modules.Select(m => (m.GetString("DeviceLocator"), m.GetString("BankLabel"))).ToList());
        var gigabytesPerSecond = channels * mts * 8 / 1000d;
        var capacity = modules.Sum(m => m.GetInt64("Capacity") ?? 0);
        var type = modules.Select(m => HealthParsers.MemoryTypeLabel(m.GetInt64("SMBIOSMemoryType"))).FirstOrDefault(t => t is not null);
        var description = T("{0} barrette(s)", modules.Count)
            + (capacity > 0 ? T(", {0} au total", HealthParsers.FormatGigabytes(capacity)) : string.Empty)
            + (type is null ? string.Empty : $" ({type})");

        return new Finding
        {
            Id = id,
            Title = title,
            Category = MemoryCategory,
            Status = FindingStatus.Info,
            Current = $"{gigabytesPerSecond.ToString("0.0", French)} Go/s",
            Explanation = T("Calcul : {0} canal(aux){1} × {2} MT/s × 8 octets. {3}. "
                + "C'est un plafond théorique : le test de la mémoire vive de l'Atelier mesure le débit réel, à comparer à cette valeur.", channels, (estimated ? T(" (estimation)") : string.Empty), mts, description),
            Advice = T("Nombre de canaux et profil XMP/EXPO : voir Module 10."),
        };
    }

    /// <summary>Événement 37 de Kernel-Processor-Power : le firmware limite la vitesse du processeur.</summary>
    private static Finding DetectFirmwareThrottling(AuditContext context)
    {
        const string id = "M11.firmware-throttling";
        var title = T("Processeur non bridé par le firmware");
        IReadOnlyList<EventRecordInfo> events;
        try
        {
            events = context.EventLogs.Query("System", ProcessorPowerProvider, [37], context.Now.DateTime.AddDays(-30), maxEvents: 100);
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(id, title, ProcessorCategory);
        }
        catch (DataSourceUnavailableException)
        {
            return Finding.Unknown(id, title, T("Journal Système indisponible."), ProcessorCategory);
        }

        var laptop = context.Hardware.IsLaptop;
        var throttled = events.Count > 0;
        return new Finding
        {
            Id = id,
            Title = title,
            Category = ProcessorCategory,
            Status = !throttled ? FindingStatus.Ok : laptop ? FindingStatus.Info : FindingStatusExtensions.ForDeviation(Severity.Medium),
            Severity = Severity.Medium,
            Current = throttled
                ? T("{0} alerte(s) en 30 jours, la dernière le {1}", events.Count, events.Max(e => e.TimeCreated).ToString("dd/MM/yyyy", French))
                : T("aucune alerte en 30 jours"),
            Expected = T("aucune alerte"),
            Explanation = T("Windows note (événement 37 de Kernel-Processor-Power) chaque fois que le BIOS limite la vitesse du processeur : "
                + "surchauffe, limite de puissance ou alimentation insuffisante.")
                + (laptop ? T(" Sur un portable, c'est souvent normal sur batterie ou avec un chargeur peu puissant.") : string.Empty),
            Advice = throttled
                ? T("Vérifiez la ventilation (poussière, pâte thermique), puis le plan d'alimentation (Module 5) et la version du BIOS (Module 8).")
                : null,
        };
    }

    private static Finding BenchmarkNotice() => new()
    {
        Id = "M11.benchmark",
        Title = T("Tests actifs : dans l'Atelier"),
        Category = BenchmarkCategory,
        Status = FindingStatus.Info,
        Current = T("non lancé (audit seul)"),
        Explanation = T("« Ce test ne rend pas votre PC plus rapide : il vérifie qu'il fonctionne comme prévu. » "
            + "Les tests du processeur, de la mémoire vive et de la mémoire vidéo se lancent depuis l'Atelier (onglet Tests), avec arrêt automatique en cas de surchauffe, "
            + "et leurs résultats sont gardés pour comparer avant et après optimisation. L'audit se limite aux indicateurs passifs ci-dessus. "
            + "La température interne du processeur n'est pas lue : elle exige un pilote noyau, que MAUS n'installe pas."),
        Advice = T("En attendant, les causes de lenteur les plus fréquentes sont vérifiées par les Modules 5 (alimentation), 10 (mémoire) et 12 (démarrage)."),
    };

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars).Trim('-');
        return slug.Length == 0 ? "x" : slug;
    }
}
