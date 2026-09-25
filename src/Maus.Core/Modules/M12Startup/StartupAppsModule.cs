using System.Globalization;
using System.Runtime.InteropServices;
using Maus.Core.Platform;
using Microsoft.Win32;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M12Startup;

/// <summary>
/// Module 12 — Applications au démarrage. Liste ce qui se lance à l'ouverture de session (registre Run et RunOnce,
/// dossiers Démarrage, tâches des applications du Store), classe chaque entrée par famille grâce au catalogue
/// <c>m12-startup-catalog.json</c> et signale les entrées suspectes. Ajoute la durée du dernier démarrage,
/// les tâches planifiées lancées à l'ouverture de session et les services tiers automatiques.
/// </summary>
public sealed class StartupAppsModule : Fixes.IFixableModule
{
    internal const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string RunOncePath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    internal const string Wow64RunPath = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    internal const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    internal const string StoreTasksPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
    internal const string ServicesQuery = "SELECT Name, DisplayName, PathName, StartMode FROM Win32_Service WHERE StartMode = 'Auto'";
    internal const string PerformanceLog = "Microsoft-Windows-Diagnostics-Performance/Operational";
    internal const string TaskSchedulerScope = CimScopes.TaskScheduler;
    internal const string TasksQuery = "SELECT TaskName, TaskPath, State, Triggers, Actions FROM MSFT_ScheduledTask";

    private static string SummaryCategory => T("Vue d'ensemble");
    private static string UnknownCategory => T("Inconnu");
    private static string SuspiciousCategory => T("Suspect");
    private static string MeasureCategory => T("Mesure du démarrage");
    private static string OtherSourcesCategory => T("Autres lancements automatiques");
    private static string SettingsAdvice => T("Réglage : Paramètres > Applications > Démarrage (ms-settings:startupapps).");

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly Lazy<StartupCatalog> Catalog = new(StartupCatalog.LoadEmbedded);

    private static RegistrySource[] RegistrySources =>     [
        new("hkcu-run", T("registre de l'utilisateur (Run)"), RegistryHive.CurrentUser, RunPath, "Run"),
        new("hklm-run", T("registre de la machine (Run)"), RegistryHive.LocalMachine, RunPath, "Run"),
        new("hklm-run32", "registre de la machine, programmes 32 bits (WOW6432Node\\Run)", RegistryHive.LocalMachine, Wow64RunPath, "Run32"),
        new("hkcu-runonce", T("exécution unique de l'utilisateur (RunOnce)"), RegistryHive.CurrentUser, RunOncePath, null),
        new("hklm-runonce", T("exécution unique de la machine (RunOnce)"), RegistryHive.LocalMachine, RunOncePath, null),
    ];

    private readonly IStartupEnvironment _environment;

    public StartupAppsModule()
        : this(new WindowsStartupEnvironment())
    {
    }

    internal StartupAppsModule(IStartupEnvironment environment)
    {
        _environment = environment;
    }

    public string Id => "M12";

    public string Title => T("Applications au démarrage");

    public int Order => 120;

    public Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken)
    {
        var findings = new List<Finding>();
        var entries = CollectEntries(context, findings);
        cancellationToken.ThrowIfCancellationRequested();

        var windowsDirectory = _environment.Expand("%SystemRoot%").TrimEnd('\\');
        var used = new HashSet<string>(StringComparer.Ordinal);
        var entryFindings = entries.Select(e => Describe(context.Files, e, windowsDirectory, used)).ToList();

        findings.Add(Summary(entries, entryFindings));
        findings.AddRange(entryFindings);
        findings.Add(DetectBootTime(context));
        findings.Add(DetectLogonTasks(context.Cim));
        findings.Add(DetectThirdPartyServices(context, windowsDirectory));

        // Les constats les plus graves d'abord ; l'ordre de lecture est conservé à gravité égale.
        return Task.FromResult<IReadOnlyList<Finding>>(findings.OrderByDescending(f => f.Status.Rank()).ToList());
    }

    /// <summary>
    /// Étape Plan : désactivation comme le Gestionnaire des tâches (valeur binaire <c>StartupApproved</c> : 03 puis la date),
    /// sans jamais supprimer la valeur Run. Familles « sans problème » et entrées orphelines pré-cochées, le reste au choix.
    /// </summary>
    public IReadOnlyList<Fixes.PlannedChange> Plan(AuditContext context, IReadOnlyList<Finding> findings)
    {
        var byId = findings.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var entries = CollectEntries(context, []);
        var windowsDirectory = _environment.Expand("%SystemRoot%").TrimEnd('\\');
        var used = new HashSet<string>(StringComparer.Ordinal);
        var disabled = new byte[12];
        disabled[0] = 0x03;
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(disabled.AsSpan(4), context.Now.ToFileTime());

        var changes = new List<Fixes.PlannedChange>();
        foreach (var entry in entries)
        {
            var described = Describe(context.Files, entry, windowsDirectory, used);
            if (entry.ApprovalKey is null || !entry.Approval.Enabled || entry.RunOnce
                || !byId.TryGetValue(described.Id, out var finding) || !finding.Fixable || finding.Status == FindingStatus.Ok)
            {
                continue;
            }

            var match = Catalog.Value.Match(entry.MatchName, entry.Executable is null ? null : StartupParsers.FileNameOf(entry.Executable), entry.PackageName);
            changes.Add(new Fixes.PlannedChange
            {
                Id = finding.Id,
                ModuleId = Id,
                Title = T("Ne plus lancer au démarrage : {0}", finding.Title),
                Description = T("Désactive l'entrée comme le Gestionnaire des tâches, sans rien désinstaller : l'application se lance toujours quand vous l'ouvrez.") +
                              (match is { } known ? T(" Vous perdez : {0}.", T(known.Family.Loses)) : string.Empty),
                Category = finding.Category,
                Gain = T("Ouverture de session plus rapide, moins de mémoire occupée en arrière-plan."),
                Recommended = finding.Status == FindingStatus.Improvable,
                Writes =
                [
                    new Fixes.SettingWrite(
                        Fixes.SettingKey.Registry(entry.ApprovalHive == RegistryHive.LocalMachine ? "HKLM" : "HKCU", $@"{ApprovedPath}\{entry.ApprovalKey}", entry.Name),
                        Fixes.SettingValue.Binary(disabled)),
                ],
            });
        }

        return changes;
    }

    private List<StartupEntry> CollectEntries(AuditContext context, List<Finding> findings)
    {
        var entries = new List<StartupEntry>();
        foreach (var source in RegistrySources)
        {
            ReadRegistrySource(context.Registry, source, entries, findings);
        }

        ReadFolderSource(context, "user-folder", T("dossier Démarrage de l'utilisateur"), _environment.UserStartupFolder, RegistryHive.CurrentUser, entries, findings);
        ReadFolderSource(context, "common-folder", T("dossier Démarrage commun"), _environment.CommonStartupFolder, RegistryHive.LocalMachine, entries, findings);
        ReadStoreTasks(context, entries, findings);
        return entries;
    }

    private void ReadRegistrySource(IRegistryReader registry, RegistrySource source, List<StartupEntry> entries, List<Finding> findings)
    {
        try
        {
            foreach (var name in registry.GetValueNames(source.Hive, source.Path))
            {
                var command = registry.GetString(source.Hive, source.Path, name);
                if (name.Length == 0 || string.IsNullOrWhiteSpace(command))
                {
                    continue;
                }

                var approval = source.ApprovedKey is null
                    ? new ApprovalState(true, null)
                    : StartupParsers.ParseApproval(ReadApproval(registry, source.Hive, source.ApprovedKey, name));
                var (executable, arguments) = StartupParsers.SplitCommand(_environment.Expand(command));
                entries.Add(new StartupEntry(source.Id, source.Label, name, name, command, executable, arguments, approval, source.ApprovedKey is null, null, null)
                {
                    ApprovalHive = source.Hive,
                    ApprovalKey = source.ApprovedKey,
                });
            }
        }
        catch (MausAccessDeniedException)
        {
            findings.Add(Finding.AdminRequired($"M12.source-{source.Id}", T("Lecture du {0}", source.Label), OtherSourcesCategory));
        }
    }

    private static byte[]? ReadApproval(IRegistryReader registry, RegistryHive hive, string approvedKey, string name)
    {
        try
        {
            return registry.GetBinary(hive, $@"{ApprovedPath}\{approvedKey}", name);
        }
        catch (MausAccessDeniedException)
        {
            return null;
        }
    }

    private void ReadFolderSource(AuditContext context, string id, string label, string? folder, RegistryHive approvalHive, List<StartupEntry> entries, List<Finding> findings)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        IReadOnlyList<string> files;
        try
        {
            files = context.Files.EnumerateFiles(folder);
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or IOException)
        {
            findings.Add(Finding.AdminRequired($"M12.source-{id}", T("Lecture du {0}", label), OtherSourcesCategory));
            return;
        }

        foreach (var file in files)
        {
            var fileName = StartupParsers.FileNameOf(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isShortcut = fileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
            var target = isShortcut ? _environment.ResolveShortcut(file) : file;
            var approval = StartupParsers.ParseApproval(ReadApproval(context.Registry, approvalHive, "StartupFolder", fileName));
            var command = isShortcut ? $"{file} → {target ?? "cible non lisible"}" : file;
            entries.Add(new StartupEntry(id, label, fileName, Path.GetFileNameWithoutExtension(fileName), command, target, string.Empty, approval, false, null, null)
            {
                ApprovalHive = approvalHive,
                ApprovalKey = "StartupFolder",
            });
        }
    }

    /// <summary>Tâches StartupTask des applications du Store : état 2 (activé) ou 4 (activé par stratégie), sinon désactivé.</summary>
    private static void ReadStoreTasks(AuditContext context, List<StartupEntry> entries, List<Finding> findings)
    {
        IReadOnlyList<InstalledPackage> packages;
        try
        {
            packages = context.Packages.GetUserPackages();
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or COMException or UnauthorizedAccessException)
        {
            packages = [];
        }

        try
        {
            foreach (var familyName in context.Registry.GetSubKeyNames(RegistryHive.CurrentUser, StoreTasksPath))
            {
                var familyPath = $@"{StoreTasksPath}\{familyName}";
                foreach (var taskId in context.Registry.GetSubKeyNames(RegistryHive.CurrentUser, familyPath))
                {
                    var state = context.Registry.GetDword(RegistryHive.CurrentUser, $@"{familyPath}\{taskId}", "State");
                    if (state is null)
                    {
                        continue;
                    }

                    var package = packages.FirstOrDefault(p => p.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase));
                    var packageName = package?.Name ?? familyName.Split('_')[0];
                    var approval = new ApprovalState(state is 2 or 4, null);
                    entries.Add(new StartupEntry(
                        "store", T("tâche de démarrage d'une application du Store"), taskId, taskId, $"{packageName} ({taskId})",
                        null, string.Empty, approval, false, packageName, package is null ? null : StartupParsers.PublisherName(package.Publisher)));
                }
            }
        }
        catch (MausAccessDeniedException)
        {
            findings.Add(Finding.AdminRequired("M12.source-store", T("Lecture des applications du Store lancées au démarrage"), OtherSourcesCategory));
        }
    }

    private static Finding Describe(IFileSystemReader files, StartupEntry entry, string windowsDirectory, HashSet<string> usedIds)
    {
        var executable = string.IsNullOrWhiteSpace(entry.Executable) ? null : entry.Executable;
        var fileName = executable is null ? null : StartupParsers.FileNameOf(executable);
        var isLocalPath = executable is not null && Path.IsPathFullyQualified(executable);
        var exists = isLocalPath && SafeExists(files, executable!);
        var (company, product, _) = exists ? SafeVersionInfo(files, executable!) : (null, null, null);
        company = string.IsNullOrWhiteSpace(company) ? entry.Publisher : company.Trim();

        var match = Catalog.Value.Match(entry.MatchName, fileName, entry.PackageName);
        var suspicion = executable is null ? null : StartupParsers.SuspicionReason(executable, entry.Arguments, company);
        var orphan = isLocalPath && !exists && !IsUnder(executable!, windowsDirectory);
        var displayName = match?.App.Name
            ?? (string.IsNullOrWhiteSpace(product) ? entry.PackageName ?? entry.Name : product.Trim());

        var id = UniqueId($"M12.{entry.SourceId}-{StartupParsers.Slug(entry.PackageName is null ? entry.Name : $"{entry.PackageName}-{entry.Name}")}", usedIds);
        var enabled = entry.Approval.Enabled;
        var isStore = entry.SourceId == "store";
        var state = entry.RunOnce
            ? T("exécution unique au prochain démarrage")
            : enabled
                ? T("activé")
                : entry.Approval.DisabledOnUtc is { } date
                    ? T("désactivé le {0}", date.ToLocalTime().ToString("dd/MM/yyyy", French))
                    : T("désactivé");
        var publisher = company ?? (isStore ? T("application du Store") : executable is null ? null : T("éditeur inconnu"));

        var explanation = T("Source : {0}. Commande : {1}.", entry.SourceLabel, entry.Command);
        if (match is { } known)
        {
            explanation += T(" En le désactivant, vous perdez : {0}.", T(known.Family.Loses));
        }
        else
        {
            explanation += T(" Cette entrée ne figure pas dans le catalogue de MAUS : vérifiez l'éditeur et le chemin avant de décider.");
        }

        var (status, severity, expected, advice, fixable, category) =
            Verdict(entry, match?.Family, suspicion, orphan, enabled, isStore);
        if (suspicion is not null)
        {
            explanation += T(" Signal d'alerte : {0}.", suspicion);
        }

        if (orphan)
        {
            explanation += T(" Le programme visé n'existe plus sur le disque : l'entrée ne sert plus à rien.");
        }

        var title = match is { } m
            ? $"{m.App.Name} ({T(m.Family.Item)})"
            : T("{0} (non répertorié)", displayName);

        return new Finding
        {
            Id = id,
            Title = title,
            Category = category ?? Optional(match?.Family.Label) ?? UnknownCategory,
            Status = status,
            Severity = severity,
            Current = publisher is null ? state : $"{state} · {publisher}",
            Expected = expected,
            Explanation = explanation,
            Advice = advice,
            Fixable = fixable,
        };
    }

    private static (FindingStatus Status, Severity Severity, string? Expected, string? Advice, bool Fixable, string? Category) Verdict(
        StartupEntry entry, StartupFamily? family, string? suspicion, bool orphan, bool enabled, bool isStore)
    {
        var storeAdvice = isStore ? " " + SettingsAdvice : string.Empty;
        if (suspicion is not null)
        {
            var advice = T("Lancez une analyse complète avec Microsoft Defender (voir Module 1). En cas de doute, désactivez l'entrée et ne lancez pas ce programme.");
            return enabled
                ? (FindingStatusExtensions.ForDeviation(Severity.Medium), Severity.Medium, T("vérifié par une analyse antivirus"), advice, false, SuspiciousCategory)
                : (FindingStatus.Info, Severity.Medium, T("vérifié par une analyse antivirus"), T("L'entrée est déjà désactivée. ") + advice, false, SuspiciousCategory);
        }

        if (entry.RunOnce)
        {
            return (FindingStatus.Info, Severity.Info, null,
                T("Affichage seul : cette commande s'exécutera une seule fois, puis Windows l'effacera."), false, null);
        }

        if (!enabled)
        {
            return family?.Advice == StartupAdvice.AlwaysKeep
                ? (FindingStatus.Info, Severity.Info, T("activé"), T("Cette protection est désactivée au démarrage : réactivez-la. ") + SettingsAdvice, false, null)
                : (FindingStatus.Ok, Severity.Low, T("désactivé si inutile"), null, false, null);
        }

        if (orphan)
        {
            return (FindingStatus.Improvable, Severity.Low, T("désactivé (programme absent)"),
                T("Désactivez cette entrée : elle ne lance plus rien. ") + SettingsAdvice, !isStore, null);
        }

        if (family is null)
        {
            return (FindingStatus.Info, Severity.Info, T("à vous de décider"),
                T("Si vous ne connaissez pas ce programme, recherchez son éditeur avant de le désactiver.") + storeAdvice, !isStore, null);
        }

        return family.Advice switch
        {
            StartupAdvice.Disable => (FindingStatus.Improvable, Severity.Low, T("désactivé"), T(family.Recommendation) + storeAdvice, !isStore, null),
            StartupAdvice.BrowserSettings => (FindingStatus.Improvable, Severity.Low, T("désactivé dans le navigateur"), T(family.Recommendation), false, null),
            StartupAdvice.DependsOnUse => (FindingStatus.Info, Severity.Info, T("selon votre usage"), T(family.Recommendation) + storeAdvice, !isStore, null),
            StartupAdvice.KeepIfUsed => (FindingStatus.Info, Severity.Info, T("activé si vous utilisez ce service"), T(family.Recommendation), false, null),
            _ => (FindingStatus.Ok, Severity.Info, T("activé"), T(family.Recommendation), false, null),
        };
    }

    private static Finding Summary(List<StartupEntry> entries, List<Finding> entryFindings)
    {
        var active = entries.Count(e => e.Approval.Enabled && !e.RunOnce);
        var improvable = entryFindings.Count(f => f.Status == FindingStatus.Improvable);
        var suspicious = entryFindings.Count(f => f.Status == FindingStatus.Warning);
        var current = T("{0} lancement(s) actif(s) sur {1} entrée(s), dont {2} à désactiver sans problème", active, entries.Count, improvable);
        if (suspicious > 0)
        {
            current += T(" et {0} suspecte(s)", suspicious);
        }

        return new Finding
        {
            Id = "M12.summary",
            Title = T("Programmes lancés à l'ouverture de session"),
            Category = SummaryCategory,
            Status = FindingStatus.Info,
            Current = current,
            Explanation = T("Chaque application lancée au démarrage retarde l'ouverture de session, occupe de la mémoire et parfois le processeur "
                + "en arrière-plan. Au-delà des réglages Windows, c'est ici que se gagne la performance au quotidien, surtout avec 8 Go de "
                + "mémoire ou un disque dur. Désactiver une entrée ne désinstalle rien : l'application se lance toujours quand vous l'ouvrez. "
                + "MAUS indique l'éditeur et le chemin de chaque programme ; les programmes non signés sont signalés par le Module 1."),
            Advice = T("Pour désactiver une entrée : Paramètres > Applications > Démarrage, ou Gestionnaire des tâches > Applications de démarrage. "
                + "Rien n'est supprimé et tout se réactive en un clic. MAUS le propose aussi dans l'onglet Corrections, familles « sans problème » pré-cochées."),
        };
    }

    /// <summary>Événement 100 (durée du démarrage) et 101 (application qui ralentit le démarrage) du journal Diagnostics-Performance.</summary>
    private static Finding DetectBootTime(AuditContext context)
    {
        const string id = "M12.boot-time";
        var title = T("Durée du dernier démarrage mesurée par Windows");
        IReadOnlyList<EventRecordInfo> events;
        try
        {
            events = context.EventLogs.Query(PerformanceLog, null, [100, 101], context.Now.DateTime.AddDays(-60), maxEvents: 200);
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(id, title, MeasureCategory);
        }
        catch (DataSourceUnavailableException)
        {
            return Finding.Unknown(id, title, T("Le journal des performances de démarrage est absent sur ce PC."), MeasureCategory);
        }

        var boot = events.Where(e => e.Id == 100).OrderByDescending(e => e.TimeCreated).FirstOrDefault();
        var seconds = boot is null ? null : StartupParsers.ParseMilliseconds(boot.Data, "BootTime");
        if (boot is null || seconds is null)
        {
            return Finding.Unknown(id, title,
                T("Aucune mesure de démarrage enregistrée ces 60 derniers jours. Avec le démarrage rapide actif, Windows en enregistre rarement (voir Module 5)."),
                MeasureCategory);
        }

        var explanation = T("Durée mesurée par Windows entre l'allumage et un bureau utilisable. C'est la référence pour juger le gain "
            + "après avoir désactivé des applications au démarrage. Avec le démarrage rapide actif, la mesure n'est prise qu'après un vrai "
            + "redémarrage et peut être faussée (voir Module 5).");
        if (StartupParsers.ParseMilliseconds(boot.Data, "MainPathBootTime") is { } mainPath)
        {
            explanation += T(" Dont {0} s avant l'affichage du bureau.", mainPath.ToString("0.0", French));
        }

        var slowApps = events
            .Where(e => e.Id == 101)
            .Select(e => (Name: e.Data.GetValueOrDefault("FriendlyName") is { Length: > 0 } friendly ? friendly : e.Data.GetValueOrDefault("Name"),
                Delay: StartupParsers.ParseMilliseconds(e.Data, "DegradationTime")))
            .Where(a => !string.IsNullOrWhiteSpace(a.Name) && a.Delay is > 0)
            .GroupBy(a => a.Name!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Delay: g.Max(a => a.Delay!.Value)))
            .OrderByDescending(a => a.Delay)
            .Take(3)
            .ToList();
        if (slowApps.Count > 0)
        {
            explanation += T(" Programmes signalés par Windows comme ralentissant le démarrage : ")
                + string.Join(", ", slowApps.Select(a => $"{a.Name} (+{a.Delay.ToString("0.0", French)} s)")) + ".";
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = MeasureCategory,
            Status = FindingStatus.Info,
            Current = $"{seconds.Value.ToString("0.0", French)} s, le {boot.TimeCreated.ToString("dd/MM/yyyy", French)}",
            Explanation = explanation,
            Advice = T("Notez cette durée, désactivez les entrées inutiles, redémarrez, puis comparez."),
        };
    }

    /// <summary>Tâches planifiées hors dossier \Microsoft\, activées et lancées à l'ouverture de session (MSFT_ScheduledTask).</summary>
    private static Finding DetectLogonTasks(ICimReader cim)
    {
        const string id = "M12.logon-tasks";
        var title = T("Tâches planifiées lancées à l'ouverture de session");
        IReadOnlyList<CimRow> rows;
        try
        {
            rows = cim.Query(TasksQuery, TaskSchedulerScope);
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(id, title, OtherSourcesCategory);
        }
        catch (DataSourceUnavailableException)
        {
            return Finding.Unknown(id, title, T("La liste des tâches planifiées est indisponible sur ce PC."), OtherSourcesCategory);
        }

        var found = rows
            .Select(StartupParsers.ParseLogonTask)
            .OfType<LogonTaskInfo>()
            .Select(t => $"{t.Name} ({t.Command ?? "action non exécutable"})")
            .ToList();

        return new Finding
        {
            Id = id,
            Title = title,
            Category = OtherSourcesCategory,
            Status = found.Count == 0 ? FindingStatus.Ok : FindingStatus.Info,
            Current = found.Count == 0 ? T("aucune tâche tierce") : T("{0} tâche(s)", found.Count),
            Explanation = T("Certains programmes se lancent par le Planificateur de tâches plutôt que par la liste de démarrage : ils n'apparaissent "
                + "pas dans le Gestionnaire des tâches. Les tâches de Windows (dossier Microsoft) sont exclues ; sans droits administrateur, "
                + "les tâches d'autres comptes peuvent manquer.")
                + (found.Count == 0 ? string.Empty : T(" Tâches trouvées : ") + string.Join(" ; ", found) + "."),
            Advice = found.Count == 0 ? null : T("Affichage seul pour l'instant : une prochaine version proposera de désactiver une tâche, avec votre accord."),
            Fixable = found.Count > 0,
        };
    }

    /// <summary>Services en démarrage automatique dont l'éditeur n'est pas Microsoft (affichage seul).</summary>
    private Finding DetectThirdPartyServices(AuditContext context, string windowsDirectory)
    {
        const string id = "M12.third-party-services";
        var title = T("Services tiers démarrant avec Windows");
        IReadOnlyList<CimRow> rows;
        try
        {
            rows = context.Cim.Query(ServicesQuery);
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(id, title, OtherSourcesCategory);
        }
        catch (DataSourceUnavailableException)
        {
            return Finding.Unknown(id, title, T("La liste des services est indisponible."), OtherSourcesCategory);
        }

        var services = new List<string>();
        var companies = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var (executable, _) = StartupParsers.SplitCommand(_environment.Expand(row.GetString("PathName") ?? string.Empty));
            if (executable.Length == 0)
            {
                continue;
            }

            if (!companies.TryGetValue(executable, out var company))
            {
                company = Path.IsPathFullyQualified(executable) ? SafeVersionInfo(context.Files, executable).Company?.Trim() : null;
                companies[executable] = company;
            }

            var isMicrosoft = string.IsNullOrEmpty(company)
                ? IsUnder(executable, windowsDirectory) || !Path.IsPathFullyQualified(executable)
                : company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
            if (!isMicrosoft)
            {
                var name = row.GetString("DisplayName") is { Length: > 0 } display ? display : row.GetString("Name") ?? "?";
                services.Add(string.IsNullOrEmpty(company) ? name : $"{name} ({company})");
            }
        }

        const int shown = 12;
        var list = string.Join(", ", services.Order(StringComparer.CurrentCultureIgnoreCase).Take(shown));
        return new Finding
        {
            Id = id,
            Title = title,
            Category = OtherSourcesCategory,
            Status = FindingStatus.Info,
            Current = T("{0} service(s) tiers en démarrage automatique", services.Count),
            Explanation = T("Services installés par d'autres éditeurs que Microsoft (pilotes, antivirus, outils de mise à jour, anti-triche) : "
                + "ils démarrent avec Windows, avant même l'ouverture de session.")
                + (services.Count == 0 ? string.Empty : $" {list}{(services.Count > shown ? $", et {services.Count - shown} autre(s)" : string.Empty)}."),
            Advice = T("Affichage seul : ne désactivez pas un service sans savoir à quoi il sert. MAUS ne les modifiera pas."),
        };
    }

    private static bool SafeExists(IFileSystemReader files, string path)
    {
        try
        {
            return files.FileExists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or MausAccessDeniedException)
        {
            return false;
        }
    }

    private static (string? Company, string? Product, string? Version) SafeVersionInfo(IFileSystemReader files, string path)
    {
        try
        {
            return files.GetVersionInfo(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or MausAccessDeniedException)
        {
            return (null, null, null);
        }
    }

    private static bool IsUnder(string path, string directory) =>
        directory.Length > 0 && path.StartsWith(directory + "\\", StringComparison.OrdinalIgnoreCase);

    private static string UniqueId(string id, HashSet<string> used)
    {
        var candidate = id;
        for (var i = 2; !used.Add(candidate); i++)
        {
            candidate = $"{id}-{i}";
        }

        return candidate;
    }

    private sealed record RegistrySource(string Id, string Label, RegistryHive Hive, string Path, string? ApprovedKey);

    /// <summary>Une entrée de démarrage, quelle que soit sa source.</summary>
    private sealed record StartupEntry(
        string SourceId,
        string SourceLabel,
        string Name,
        string MatchName,
        string Command,
        string? Executable,
        string Arguments,
        ApprovalState Approval,
        bool RunOnce,
        string? PackageName,
        string? Publisher)
    {
        /// <summary>Ruche et sous-clé <c>StartupApproved</c> qui portent l'état ; <c>null</c> si l'entrée ne se désactive pas ainsi (Store, RunOnce).</summary>
        public RegistryHive ApprovalHive { get; init; }

        public string? ApprovalKey { get; init; }
    }
}
