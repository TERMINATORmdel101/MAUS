using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Maus.Core.Platform;
using Microsoft.Win32;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M02Repair;

/// <summary>
/// Module 2 — Réparation de Windows. En V0.1 (audit seul), le module lit les signaux de santé : 30 jours de journaux
/// (erreurs matérielles WHEA, écrans bleus, arrêts brutaux, disque), test mémoire, indice de fiabilité, prérequis des
/// réparations (redémarrage en attente, espace libre) et état de WMI. Les réparations (chkdsk, DISM, SFC…) arrivent en V0.2.
/// </summary>
public sealed class WindowsHealthModule : IAuditModule
{
    internal const string WheaProvider = "Microsoft-Windows-WHEA-Logger";
    internal const string KernelPowerProvider = "Microsoft-Windows-Kernel-Power";
    internal const string WerProvider = "Microsoft-Windows-WER-SystemErrorReporting";
    internal const string VolmgrProvider = "volmgr";
    internal const string DiskProvider = "disk";
    internal const string NtfsProvider = "Ntfs";
    internal const string MemoryDiagnosticsProvider = "Microsoft-Windows-MemoryDiagnostics-Results";

    internal const string CbsRebootPendingKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending";
    internal const string WindowsUpdateRebootKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired";
    internal const string SessionManagerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager";

    internal const string WmiProbeQuery = "SELECT Caption FROM Win32_OperatingSystem";

    internal const int LookbackDays = 30;
    internal const int MemoryTestLookbackDays = 90;
    internal const int ReliabilityLookbackDays = 7;
    internal const long MinimumFreeBytes = 20L * 1024 * 1024 * 1024;
    internal const double MinimumStabilityIndex = 5.0;

    internal static readonly int[] WheaFatalIds = [1, 18, 20, 46];
    internal static readonly int[] WheaCorrectedIds = [17, 19, 47];
    internal static readonly int[] DiskIds = [7, 51, 153];

    private static string HardwareCategory => T("Stabilité matérielle");
    private static string CrashCategory => T("Plantages et arrêts");
    private static string DiskCategory => T("Disque");
    private static string PrerequisiteCategory => T("Prérequis des réparations");
    private static string GeneralCategory => T("État général");

    private static readonly int[] WheaIds = [.. WheaFatalIds, .. WheaCorrectedIds];
    private static readonly int[] KernelPowerIds = [41];
    private static readonly int[] WerIds = [1001];
    private static readonly int[] VolmgrIds = [46];
    private static readonly int[] NtfsIds = [55];
    private static readonly int[] MemoryHealthyIds = [1101, 1201];
    private static readonly int[] MemoryErrorIds = [1102, 1202];
    private static readonly int[] MemoryResultIds = [.. MemoryHealthyIds, .. MemoryErrorIds];

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private readonly string _windowsDirectory;

    public WindowsHealthModule()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.Windows))
    {
    }

    /// <summary>Dossier Windows injecté pour les tests (minidumps, lecteur système).</summary>
    internal WindowsHealthModule(string windowsDirectory)
    {
        _windowsDirectory = string.IsNullOrEmpty(windowsDirectory) ? @"C:\Windows" : windowsDirectory;
    }

    public string Id => "M02";

    public string Title => T("Réparation de Windows");

    public int Order => 20;

    /// <summary>La lecture de l'indice de fiabilité par WMI peut être lente sur certains PC.</summary>
    public TimeSpan Timeout => TimeSpan.FromSeconds(90);

    private string SystemRoot => Path.GetPathRoot(_windowsDirectory) is { Length: > 0 } root ? root : @"C:\";

    internal static string VolumeQuery(string driveLetter) =>
        $"SELECT DriveLetter, DirtyBitSet FROM Win32_Volume WHERE DriveLetter = '{driveLetter}'";

    /// <summary>
    /// Mesures des 7 derniers jours seulement : la classe garde des centaines de relevés horaires, lents à tout lire.
    /// La date suit le format CIM (DMTF), en temps universel.
    /// </summary>
    internal static string ReliabilityQuery(DateTimeOffset now) =>
        "SELECT SystemStabilityIndex, TimeGenerated FROM Win32_ReliabilityStabilityMetrics WHERE TimeGenerated >= '"
        + now.UtcDateTime.AddDays(-ReliabilityLookbackDays).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
        + ".000000+000'";

    public Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken)
    {
        var now = context.Now.LocalDateTime;
        var since = now.AddDays(-LookbackDays);
        var logs = context.EventLogs;

        var whea = SystemLogQuery.Run(logs, WheaProvider, WheaIds, since);
        var kernelPower = SystemLogQuery.Run(logs, KernelPowerProvider, KernelPowerIds, since);
        var wer = SystemLogQuery.Run(logs, WerProvider, WerIds, since);
        var volmgr = SystemLogQuery.Run(logs, VolmgrProvider, VolmgrIds, since);
        var disk = SystemLogQuery.Run(logs, DiskProvider, DiskIds, since);
        var ntfs = SystemLogQuery.Run(logs, NtfsProvider, NtfsIds, since);
        var memory = SystemLogQuery.Run(logs, MemoryDiagnosticsProvider, MemoryResultIds, now.AddDays(-MemoryTestLookbackDays));
        cancellationToken.ThrowIfCancellationRequested();

        var crashes = CrashSummary.From(kernelPower, wer);
        var instability = whea.Events.Count > 0 || crashes.BlueScreens > 0;

        var findings = new List<Finding>
        {
            DetectWheaFatal(whea),
            DetectWheaCorrected(whea),
            DetectBlueScreens(crashes, kernelPower, wer, volmgr),
            DetectUnexpectedShutdowns(kernelPower),
            DetectMemoryTest(memory, instability),
            DetectDiskErrors(disk, ntfs),
            DetectDirtyVolume(context),
            DetectPendingReboot(context.Registry),
            DetectFreeSpace(context.Files),
            DetectReliability(context.Cim, context.Now),
            DetectWmi(context.Cim),
            DetectMinidumps(context.Files),
        };

        return Task.FromResult<IReadOnlyList<Finding>>(findings);
    }

    private static Finding DetectWheaFatal(SystemLogQuery whea)
    {
        const string id = "M02.whea-fatal";
        var title = T("Erreurs matérielles graves (WHEA)");
        if (SystemLogQuery.FirstFailure(id, title, HardwareCategory, whea) is { } failure)
        {
            return failure;
        }

        var events = whea.Events.Where(e => WheaFatalIds.Contains(e.Id)).ToList();
        return new Finding
        {
            Id = id,
            Title = title,
            Category = HardwareCategory,
            Status = events.Count == 0 ? FindingStatus.Ok : FindingStatusExtensions.ForDeviation(Severity.High),
            Severity = Severity.High,
            Current = DescribeEvents(events, whea.Truncated, T("aucune"), "erreur", "erreurs"),
            Expected = T("aucune"),
            Explanation = T("Le processeur, la mémoire ou le bus PCI Express signalent à Windows les erreurs qu'ils détectent. "
                + "Une erreur « irrécupérable » provoque en général un plantage : ce n'est pas un défaut de Windows, "
                + "et aucune réparation logicielle ne la corrige."),
            Advice = events.Count == 0
                ? null
                : T("Cause fréquente : overclocking ou profil mémoire XMP/EXPO instable. Revenez aux réglages d'origine du BIOS, "
                    + "surveillez les températures, puis consultez les Modules 10 (RAM : XMP/EXPO) et 15 (overclocking)."),
        };
    }

    private static Finding DetectWheaCorrected(SystemLogQuery whea)
    {
        const string id = "M02.whea-corrected";
        var title = T("Erreurs matérielles corrigées (WHEA)");
        if (SystemLogQuery.FirstFailure(id, title, HardwareCategory, whea) is { } failure)
        {
            return failure;
        }

        var events = whea.Events.Where(e => WheaCorrectedIds.Contains(e.Id)).ToList();
        var byId = string.Join(", ", events.GroupBy(e => e.Id).OrderBy(g => g.Key).Select(g => $"ID {g.Key} : {g.Count()}"));
        return new Finding
        {
            Id = id,
            Title = title,
            Category = HardwareCategory,
            Status = events.Count == 0 ? FindingStatus.Ok : FindingStatusExtensions.ForDeviation(Severity.Medium),
            Severity = Severity.Medium,
            Current = events.Count == 0
                ? DescribeEvents(events, whea.Truncated, T("aucune"), "erreur", "erreurs")
                : $"{DescribeEvents(events, whea.Truncated, "aucune", "erreur", "erreurs")} ({byId})",
            Expected = T("aucune"),
            Explanation = T("Le matériel a détecté puis corrigé lui-même une erreur. Une erreur isolée est sans conséquence, "
                + "mais leur répétition annonce souvent une instabilité : overclocking, profil mémoire, température ou composant fatigué."),
            Advice = events.Count == 0
                ? null
                : T("{0} en {1} jours. "
                    + "Ce n'est pas un problème de Windows : vérifiez overclocking, profil XMP/EXPO et températures (Modules 10 et 15).", CountText(events.Count, whea.Truncated, T("erreur matérielle corrigée"), T("erreurs matérielles corrigées")), LookbackDays),
        };
    }

    private static Finding DetectBlueScreens(CrashSummary crashes, SystemLogQuery kernelPower, SystemLogQuery wer, SystemLogQuery volmgr)
    {
        const string id = "M02.bluescreens";
        var title = T("Écrans bleus (arrêts sur erreur système)");
        if (SystemLogQuery.FirstFailure(id, title, CrashCategory, kernelPower, wer) is { } failure)
        {
            return failure;
        }

        var dumpFailures = volmgr.Succeeded ? volmgr.Events.Count : 0;
        if (crashes.BlueScreens == 0)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = CrashCategory,
                Status = FindingStatus.Ok,
                Severity = Severity.Medium,
                Current = T("aucun en {0} jours", LookbackDays),
                Expected = T("aucun"),
                Explanation = BlueScreenExplanation,
            };
        }

        var severity = crashes.BlueScreens >= 3 ? Severity.High : Severity.Medium;
        var current = T("{0} en {1} jours", Plural(crashes.BlueScreens, T("écran bleu"), T("écrans bleus")), LookbackDays);
        if (crashes.LatestCode is { } code)
        {
            current += T(", dernier le {0} : code {1}", FormatDate(crashes.LatestTime), HealthParsers.DescribeBugcheck(code));
        }

        if (dumpFailures > 0)
        {
            current += T(" ; vidage mémoire impossible {0} (volmgr 46)", Plural(dumpFailures, "fois", "fois"));
        }

        var advice = new List<string>();
        if (crashes.LatestCode is { } latest && HealthParsers.BugcheckHint(latest) is { } hint)
        {
            advice.Add(T("Piste pour le dernier code : {0}.", hint));
        }

        advice.Add(T("Si les écrans bleus ont commencé après un overclocking ou l'activation de XMP/EXPO, revenez aux réglages d'origine "
            + "(Modules 10 et 15) ; un pilote graphique récent peut aussi être en cause (Module 9)."));
        if (dumpFailures > 0)
        {
            advice.Add(T("Le fichier de vidage n'a pas pu être écrit : vérifiez que le fichier d'échange est actif sur le disque système."));
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = CrashCategory,
            Status = FindingStatusExtensions.ForDeviation(severity),
            Severity = severity,
            Current = current,
            Expected = T("aucun"),
            Explanation = BlueScreenExplanation,
            Advice = string.Join(' ', advice),
        };
    }

    private static string BlueScreenExplanation => T("Un écran bleu est un arrêt d'urgence de Windows face à une erreur grave, le plus souvent causée par un pilote "
        + "ou par un matériel instable. Windows note son code d'erreur, qui oriente la recherche de la cause.");

    private static Finding DetectUnexpectedShutdowns(SystemLogQuery kernelPower)
    {
        const string id = "M02.unexpected-shutdowns";
        var title = T("Arrêts brutaux sans écran bleu");
        if (SystemLogQuery.FirstFailure(id, title, CrashCategory, kernelPower) is { } failure)
        {
            return failure;
        }

        var shutdowns = kernelPower.Events.Where(e => !CrashSummary.HasBugcheck(e)).ToList();
        var forced = shutdowns.Count(e => HealthParsers.IsNonZero(e.Data.GetValueOrDefault("PowerButtonTimestamp")));
        var cuts = shutdowns.Count - forced;
        var details = new List<string>();
        if (forced > 0)
        {
            details.Add(Plural(forced, T("arrêt forcé par appui long sur le bouton"), T("arrêts forcés par appui long sur le bouton")));
        }

        if (cuts > 0)
        {
            details.Add(Plural(cuts, T("coupure sans arrêt propre"), T("coupures sans arrêt propre")));
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = CrashCategory,
            Status = shutdowns.Count == 0 ? FindingStatus.Ok : FindingStatusExtensions.ForDeviation(Severity.Medium),
            Severity = Severity.Medium,
            Current = shutdowns.Count == 0
                ? T("aucun en {0} jours", LookbackDays)
                : T("{0} en {1} jours : {2}", Plural(shutdowns.Count, T("arrêt brutal"), T("arrêts brutaux")), LookbackDays, string.Join(", ", details)),
            Expected = T("aucun"),
            Explanation = T("L'événement Kernel-Power 41 signale un redémarrage sans arrêt propre. Sans code d'erreur, il s'agit "
                + "d'une coupure de courant, d'un appui long sur le bouton d'alimentation (souvent parce que le PC était figé) "
                + "ou d'une alimentation défaillante."),
            Advice = shutdowns.Count == 0
                ? null
                : T("Si le PC s'éteint seul : vérifiez le bloc d'alimentation, la multiprise et les températures. "
                    + "S'il se fige avant l'arrêt : pensez à l'overclocking ou au profil mémoire (Modules 10 et 15)."),
        };
    }

    private static Finding DetectMemoryTest(SystemLogQuery memory, bool instability)
    {
        const string id = "M02.memory-test";
        var title = T("Test de la mémoire Windows");
        if (SystemLogQuery.FirstFailure(id, title, HardwareCategory, memory) is { } failure)
        {
            return failure;
        }

        var explanation = T("L'outil Diagnostic de la mémoire Windows (mdsched.exe) teste la mémoire vive au redémarrage "
            + "et note son résultat dans le journal Système.");
        var latest = memory.Events.OrderByDescending(e => e.TimeCreated).FirstOrDefault();
        if (latest is null)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = HardwareCategory,
                Status = FindingStatus.Info,
                Current = T("aucun test depuis {0} jours", MemoryTestLookbackDays),
                Explanation = explanation,
                Advice = instability
                    ? T("Des signes d'instabilité existent : lancez « Diagnostic de la mémoire Windows » (mdsched.exe), qui teste la mémoire au prochain redémarrage.")
                    : null,
            };
        }

        var hasErrors = MemoryErrorIds.Contains(latest.Id);
        return new Finding
        {
            Id = id,
            Title = title,
            Category = HardwareCategory,
            Status = hasErrors ? FindingStatusExtensions.ForDeviation(Severity.High) : FindingStatus.Ok,
            Severity = Severity.High,
            Current = hasErrors
                ? T("erreurs détectées (test du {0})", FormatDate(latest.TimeCreated))
                : T("aucune erreur (test du {0})", FormatDate(latest.TimeCreated)),
            Expected = T("aucune erreur"),
            Explanation = explanation,
            Advice = hasErrors
                ? T("La mémoire vive a renvoyé des erreurs : désactivez le profil XMP/EXPO (Module 10), puis refaites le test. "
                    + "Si les erreurs persistent aux réglages d'origine, une barrette est probablement défaillante.")
                : null,
        };
    }

    private static Finding DetectDiskErrors(SystemLogQuery disk, SystemLogQuery ntfs)
    {
        const string id = "M02.disk-errors";
        var title = T("Erreurs de disque dans le journal");
        if (SystemLogQuery.FirstFailure(id, title, DiskCategory, disk, ntfs) is { } failure)
        {
            return failure;
        }

        var total = disk.Events.Count + ntfs.Events.Count;
        if (total == 0)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = DiskCategory,
                Status = FindingStatus.Ok,
                Severity = Severity.Medium,
                Current = T("aucune en {0} jours", LookbackDays),
                Expected = T("aucune"),
                Explanation = DiskExplanation,
            };
        }

        var kinds = disk.Events.GroupBy(e => e.Id).OrderBy(g => g.Key)
            .Select(g => $"{DiskEventLabel(g.Key)} ×{g.Count()}")
            .ToList();
        if (ntfs.Events.Count > 0)
        {
            kinds.Add(T("structure du système de fichiers endommagée (Ntfs 55) ×{0}", ntfs.Events.Count));
        }

        var devices = disk.Events
            .Select(e => e.Data.Values.FirstOrDefault(v => v.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        var current = T("{0} en {1} jours : {2}", CountText(total, disk.Truncated || ntfs.Truncated, T("événement"), T("événements")), LookbackDays, string.Join(", ", kinds));
        if (devices.Count > 0)
        {
            current += $" ; {(devices.Count == 1 ? "périphérique" : "périphériques")} : {string.Join(", ", devices)}";
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = DiskCategory,
            Status = FindingStatusExtensions.ForDeviation(Severity.Medium),
            Severity = Severity.Medium,
            Current = current,
            Expected = T("aucune"),
            Explanation = DiskExplanation,
            Advice = T("Sauvegardez vos données importantes, puis vérifiez la santé du disque avec le Module 11. "
                + "Un disque externe ou une clé USB mal branchés produisent aussi ces erreurs.")
                + (ntfs.Events.Count > 0 ? T(" La vérification du système de fichiers (chkdsk) sera proposée en V0.2.") : string.Empty),
            Fixable = ntfs.Events.Count > 0,
        };
    }

    private static string DiskExplanation => T("Windows note les secteurs illisibles, les lectures relancées et les dommages du système de fichiers. "
        + "Ces erreurs annoncent souvent un disque fatigué ou une connexion défaillante, ce qu'aucune réparation de Windows ne corrige.");

    private static string DiskEventLabel(int id) => id switch
    {
        7 => T("secteur défectueux (disk 7)"),
        51 => T("erreur pendant une pagination (disk 51)"),
        153 => T("lecture ou écriture relancée (disk 153)"),
        _ => $"disk {id}",
    };

    private Finding DetectDirtyVolume(AuditContext context)
    {
        const string id = "M02.volume-dirty";
        var title = T("Volume système marqué « à vérifier »");
        var driveLetter = SystemRoot.TrimEnd('\\');
        bool? dirty;
        try
        {
            var rows = context.Cim.Query(VolumeQuery(driveLetter));
            dirty = rows.Count > 0 ? rows[0].GetBool("DirtyBitSet") : null;
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(id, title, DiskCategory);
        }
        catch (Exception ex) when (ex is DataSourceUnavailableException or ManagementException or COMException)
        {
            return Finding.Unknown(id, title, T("Lecture de l'état du volume impossible par WMI."), DiskCategory);
        }

        if (dirty is null)
        {
            return context.IsElevated
                ? Finding.Unknown(id, title, T("Windows n'a pas indiqué l'état du volume système."), DiskCategory)
                : Finding.AdminRequired(id, title, DiskCategory);
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = DiskCategory,
            Status = dirty.Value ? FindingStatusExtensions.ForDeviation(Severity.Medium) : FindingStatus.Ok,
            Severity = Severity.Medium,
            Current = dirty.Value ? T("{0} marqué comme à vérifier", driveLetter) : T("{0} sain", driveLetter),
            Expected = T("sain"),
            Explanation = T("Windows marque un volume « à vérifier » (dirty bit) quand il détecte une incohérence du système de fichiers "
                + "ou un arrêt brutal pendant une écriture. Une vérification chkdsk est alors lancée au démarrage."),
            Advice = dirty.Value
                ? T("Redémarrez pour laisser Windows vérifier le disque ; la réparation hors ligne (chkdsk /spotfix) sera proposée en V0.2.")
                : null,
            Fixable = dirty.Value,
        };
    }

    private static Finding DetectPendingReboot(IRegistryReader registry)
    {
        const string id = "M02.pending-reboot";
        var title = T("Redémarrage en attente");
        var reasons = new List<string>();
        var denied = 0;

        void Check(Func<bool> probe, string reason)
        {
            try
            {
                if (probe())
                {
                    reasons.Add(reason);
                }
            }
            catch (MausAccessDeniedException)
            {
                denied++;
            }
        }

        Check(() => registry.KeyExists(RegistryHive.LocalMachine, CbsRebootPendingKey), T("installation de composants Windows"));
        Check(() => registry.KeyExists(RegistryHive.LocalMachine, WindowsUpdateRebootKey), "Windows Update");
        Check(() => HasPendingRenames(registry), T("fichiers à remplacer au démarrage"));

        if (reasons.Count == 0 && denied > 0)
        {
            return Finding.AdminRequired(id, title, PrerequisiteCategory);
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = PrerequisiteCategory,
            Status = reasons.Count == 0 ? FindingStatus.Ok : FindingStatusExtensions.ForDeviation(Severity.Medium),
            Severity = Severity.Medium,
            Current = reasons.Count == 0 ? T("aucun") : T("oui : {0}", string.Join(", ", reasons)),
            Expected = T("aucun"),
            Explanation = T("Tant qu'un redémarrage est en attente, Windows n'a pas fini d'installer des mises à jour ou de remplacer "
                + "des fichiers. Les réparations (DISM, SFC) doivent attendre : elles échoueraient ou donneraient de faux résultats."),
            Advice = reasons.Count == 0
                ? null
                : T("Redémarrez le PC (Démarrer > Marche/Arrêt > Redémarrer, et non « Arrêter »), puis relancez l'audit."),
        };
    }

    private static bool HasPendingRenames(IRegistryReader registry) =>
        IsNonEmpty(registry.GetValue(RegistryHive.LocalMachine, SessionManagerKey, "PendingFileRenameOperations"))
        || IsNonEmpty(registry.GetValue(RegistryHive.LocalMachine, SessionManagerKey, "PendingFileRenameOperations2"));

    private static bool IsNonEmpty(object? value) => value switch
    {
        string[] lines => lines.Any(line => !string.IsNullOrWhiteSpace(line)),
        string text => !string.IsNullOrWhiteSpace(text),
        byte[] bytes => bytes.Any(b => b != 0),
        _ => false,
    };

    private Finding DetectFreeSpace(IFileSystemReader files)
    {
        const string id = "M02.disk-space";
        var title = T("Espace libre sur le disque système");
        (long FreeBytes, long TotalBytes)? space;
        try
        {
            space = files.GetDriveSpace(SystemRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or MausAccessDeniedException)
        {
            space = null;
        }

        if (space is not { } drive)
        {
            return Finding.Unknown(id, title, T("Lecture de l'espace libre de {0} impossible.", SystemRoot), PrerequisiteCategory);
        }

        var enough = drive.FreeBytes >= MinimumFreeBytes;
        return new Finding
        {
            Id = id,
            Title = title,
            Category = PrerequisiteCategory,
            Status = enough ? FindingStatus.Ok : FindingStatusExtensions.ForDeviation(Severity.Medium),
            Severity = Severity.Medium,
            Current = T("{0} libres sur {1} ({2})", FormatGigabytes(drive.FreeBytes), FormatGigabytes(drive.TotalBytes), SystemRoot.TrimEnd('\\')),
            Expected = T("au moins {0}", FormatGigabytes(MinimumFreeBytes)),
            Explanation = T("Les réparations de Windows (DISM, mises à jour, réinstallation sur place) téléchargent et décompressent "
                + "des fichiers. En dessous de 20 Go libres, elles risquent d'échouer faute de place."),
            Advice = enough
                ? null
                : T("Libérez de l'espace : Paramètres > Système > Stockage > Recommandations de nettoyage."),
        };
    }

    private static Finding DetectReliability(ICimReader cim, DateTimeOffset now)
    {
        const string id = "M02.reliability-index";
        var title = T("Indice de fiabilité de Windows");
        IReadOnlyList<CimRow> rows;
        try
        {
            rows = cim.Query(ReliabilityQuery(now));
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(id, title, GeneralCategory);
        }
        catch (Exception ex) when (ex is DataSourceUnavailableException or ManagementException or COMException)
        {
            return Finding.Unknown(id, title, T("L'indice de fiabilité n'est pas disponible sur ce PC."), GeneralCategory);
        }

        var latest = rows
            .Select(row => (Time: row.GetDateTime("TimeGenerated"), Index: HealthParsers.ToDouble(row["SystemStabilityIndex"])))
            .Where(sample => sample.Index is not null)
            .OrderByDescending(sample => sample.Time ?? DateTime.MinValue)
            .FirstOrDefault();
        if (latest.Index is not { } index)
        {
            return Finding.Unknown(
                id,
                title,
                T("Aucun indice de fiabilité calculé depuis {0} jours : PC récemment installé, "
                    + "ou tâche planifiée RacTask désactivée.", ReliabilityLookbackDays),
                GeneralCategory);
        }

        var good = index >= MinimumStabilityIndex;
        var current = index.ToString("0.0", French) + " / 10";
        if (latest.Time is { } time)
        {
            current += T(" (calculé le {0})", FormatDate(time));
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = GeneralCategory,
            Status = good ? FindingStatus.Ok : FindingStatusExtensions.ForDeviation(Severity.Medium),
            Severity = Severity.Medium,
            Current = current,
            Expected = T("au moins {0} / 10", MinimumStabilityIndex.ToString("0", French)),
            Explanation = T("Windows calcule chaque heure une note de stabilité de 1 à 10 à partir des plantages d'applications, "
                + "des erreurs de Windows et des échecs d'installation. Une note basse résume une instabilité récente."),
            Advice = good
                ? null
                : T("Ouvrez l'Observateur de fiabilité (tapez « fiabilité » dans Démarrer) pour voir les incidents récents, "
                    + "puis consultez les autres constats de ce module."),
        };
    }

    private static Finding DetectWmi(ICimReader cim)
    {
        const string id = "M02.wmi";
        var title = T("Service WMI (informations système)");
        string? problem;
        try
        {
            problem = cim.Query(WmiProbeQuery).Count > 0 ? null : T("aucune réponse à une requête de base");
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(id, title, GeneralCategory);
        }
        catch (Exception ex) when (ex is DataSourceUnavailableException or ManagementException or COMException)
        {
            problem = T("requête de base en échec");
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = GeneralCategory,
            Status = problem is null ? FindingStatus.Ok : FindingStatusExtensions.ForDeviation(Severity.Medium),
            Severity = Severity.Medium,
            Current = problem ?? T("répond normalement"),
            Expected = T("répond normalement"),
            Explanation = T("WMI permet à Windows et aux logiciels, dont MAUS, de lire l'état du système et du matériel. "
                + "Un dépôt WMI abîmé fausse les diagnostics et bloque certains outils."),
            Advice = problem is null
                ? null
                : T("La vérification puis la récupération du dépôt WMI (winmgmt /verifyrepository, /salvagerepository) seront proposées en V0.2."),
            Fixable = problem is not null,
        };
    }

    private Finding DetectMinidumps(IFileSystemReader files)
    {
        const string id = "M02.minidumps";
        var title = T("Fichiers de vidage après plantage");
        var folder = Path.Combine(_windowsDirectory, "Minidump");
        IReadOnlyList<string> dumps;
        try
        {
            dumps = files.EnumerateFiles(folder, "*.dmp");
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(id, title, CrashCategory);
        }
        catch (IOException)
        {
            return Finding.Unknown(id, title, T("Lecture du dossier {0} impossible.", folder), CrashCategory);
        }

        var newest = dumps.Select(HealthParsers.ParseMinidumpDate).OfType<DateOnly>().DefaultIfEmpty().Max();
        var current = dumps.Count == 0 ? T("aucun") : Plural(dumps.Count, "fichier", "fichiers");
        if (dumps.Count > 0 && newest != default)
        {
            current += T(" (le plus récent du {0})", newest.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = CrashCategory,
            Status = FindingStatus.Info,
            Current = current,
            Explanation = T("À chaque écran bleu, Windows peut enregistrer un petit fichier de vidage (.dmp) dans {0}. "
                + "Il permet d'identifier le pilote ou le composant en cause.", folder),
            Advice = dumps.Count == 0
                ? null
                : T("Conservez ces fichiers si vous demandez de l'aide : ils s'analysent avec WinDbg (Microsoft Store)."),
        };
    }

    private static string DescribeEvents(List<EventRecordInfo> events, bool truncated, string none, string singular, string plural)
    {
        if (events.Count == 0)
        {
            return T("{0} en {1} jours", none, LookbackDays);
        }

        var latest = events.Max(e => e.TimeCreated);
        return T("{0} en {1} jours (dernière le {2})", CountText(events.Count, truncated, singular, plural), LookbackDays, FormatDate(latest));
    }

    private static string CountText(int count, bool truncated, string singular, string plural) =>
        truncated ? T("au moins {0} {1}", count, plural) : Plural(count, singular, plural);

    private static string Plural(int count, string singular, string plural) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {(count > 1 ? plural : singular)}";

    private static string FormatDate(DateTime date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string FormatGigabytes(long bytes) => (bytes / 1024d / 1024 / 1024).ToString("0.0", French) + " Go";

    /// <summary>Écrans bleus des 30 derniers jours, d'après Kernel-Power 41 (code non nul) et WER 1001.</summary>
    private sealed record CrashSummary(int BlueScreens, long? LatestCode, DateTime LatestTime)
    {
        public static bool HasBugcheck(EventRecordInfo e) =>
            HealthParsers.ParseBugcheckCode(e.Data.GetValueOrDefault("BugcheckCode")) is > 0;

        public static CrashSummary From(SystemLogQuery kernelPower, SystemLogQuery wer)
        {
            var fromKernel = kernelPower.Events
                .Where(HasBugcheck)
                .Select(e => (e.TimeCreated, Code: HealthParsers.ParseBugcheckCode(e.Data.GetValueOrDefault("BugcheckCode"))))
                .ToList();
            var fromWer = wer.Events
                .Select(e => (e.TimeCreated, Code: HealthParsers.ParseBugcheckCode(e.Data.GetValueOrDefault("param1"))))
                .ToList();

            // Un même plantage laisse souvent les deux traces : on retient le plus grand des deux décomptes.
            var count = Math.Max(fromKernel.Count, fromWer.Count);
            var latest = fromWer.Concat(fromKernel)
                .Where(c => c.Code is > 0)
                .OrderByDescending(c => c.TimeCreated)
                .FirstOrDefault();
            return new CrashSummary(count, latest.Code, latest.TimeCreated);
        }
    }
}
