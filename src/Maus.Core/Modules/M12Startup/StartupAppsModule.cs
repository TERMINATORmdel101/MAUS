using System.Globalization;
using System.Runtime.InteropServices;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Modules.M12Startup;

/// <summary>
/// Module 12 — Applications au démarrage. Liste ce qui se lance à l'ouverture de session (registre Run et RunOnce,
/// dossiers Démarrage, tâches des applications du Store), classe chaque entrée par famille grâce au catalogue
/// <c>m12-startup-catalog.json</c> et signale les entrées suspectes. Ajoute la durée du dernier démarrage,
/// les tâches planifiées lancées à l'ouverture de session et les services tiers automatiques.
/// </summary>
public sealed class StartupAppsModule : IAuditModule
{
    internal const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string RunOncePath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    internal const string Wow64RunPath = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    internal const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    internal const string StoreTasksPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
    internal const string ServicesQuery = "SELECT Name, DisplayName, PathName, StartMode FROM Win32_Service WHERE StartMode = 'Auto'";
    internal const string PerformanceLog = "Microsoft-Windows-Diagnostics-Performance/Operational";
    internal const string TaskSchedulerScope = @"root\Microsoft\Windows\TaskScheduler";
    internal const string TasksQuery = "SELECT TaskName, TaskPath, State, Triggers, Actions FROM MSFT_ScheduledTask";

    private const string SummaryCategory = "Vue d'ensemble";
    private const string UnknownCategory = "Inconnu";
    private const string SuspiciousCategory = "Suspect";
    private const string MeasureCategory = "Mesure du démarrage";
    private const string OtherSourcesCategory = "Autres lancements automatiques";
    private const string SettingsAdvice = "Réglage : Paramètres > Applications > Démarrage (ms-settings:startupapps).";

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly Lazy<StartupCatalog> Catalog = new(StartupCatalog.LoadEmbedded);

    private static readonly RegistrySource[] RegistrySources =
    [
        new("hkcu-run", "registre de l'utilisateur (Run)", RegistryHive.CurrentUser, RunPath, "Run"),
        new("hklm-run", "registre de la machine (Run)", RegistryHive.LocalMachine, RunPath, "Run"),
        new("hklm-run32", "registre de la machine, programmes 32 bits (WOW6432Node\\Run)", RegistryHive.LocalMachine, Wow64RunPath, "Run32"),
        new("hkcu-runonce", "exécution unique de l'utilisateur (RunOnce)", RegistryHive.CurrentUser, RunOncePath, null),
        new("hklm-runonce", "exécution unique de la machine (RunOnce)", RegistryHive.LocalMachine, RunOncePath, null),
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

    public string Title => "Applications au démarrage";

    public int Order => 120;

    public Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken)
    {
        var findings = new List<Finding>();
        var entries = new List<StartupEntry>();
        foreach (var source in RegistrySources)
        {
            ReadRegistrySource(context.Registry, source, entries, findings);
        }

        ReadFolderSource(context, "user-folder", "dossier Démarrage de l'utilisateur", _environment.UserStartupFolder, RegistryHive.CurrentUser, entries, findings);
        ReadFolderSource(context, "common-folder", "dossier Démarrage commun", _environment.CommonStartupFolder, RegistryHive.LocalMachine, entries, findings);
        ReadStoreTasks(context, entries, findings);
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
                entries.Add(new StartupEntry(source.Id, source.Label, name, name, command, executable, arguments, approval, source.ApprovedKey is null, null, null));
            }
        }
        catch (MausAccessDeniedException)
        {
            findings.Add(Finding.AdminRequired($"M12.source-{source.Id}", $"Lecture du {source.Label}", OtherSourcesCategory));
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
            findings.Add(Finding.AdminRequired($"M12.source-{id}", $"Lecture du {label}", OtherSourcesCategory));
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
            entries.Add(new StartupEntry(id, label, fileName, Path.GetFileNameWithoutExtension(fileName), command, target, string.Empty, approval, false, null, null));
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
                        "store", "tâche de démarrage d'une application du Store", taskId, taskId, $"{packageName} ({taskId})",
                        null, string.Empty, approval, false, packageName, package is null ? null : StartupParsers.PublisherName(package.Publisher)));
                }
            }
        }
        catch (MausAccessDeniedException)
        {
            findings.Add(Finding.AdminRequired("M12.source-store", "Lecture des applications du Store lancées au démarrage", OtherSourcesCategory));
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
            ? "exécution unique au prochain démarrage"
            : enabled
                ? "activé"
                : entry.Approval.DisabledOnUtc is { } date
                    ? $"désactivé le {date.ToLocalTime().ToString("dd/MM/yyyy", French)}"
                    : "désactivé";
        var publisher = company ?? (isStore ? "application du Store" : executable is null ? null : "éditeur inconnu");

        var explanation = $"Source : {entry.SourceLabel}. Commande : {entry.Command}.";
        if (match is { } known)
        {
            explanation += $" En le désactivant, vous perdez : {known.Family.Loses}.";
        }
        else
        {
            explanation += " Cette entrée ne figure pas dans le catalogue de MAUS : vérifiez l'éditeur et le chemin avant de décider.";
        }

        var (status, severity, expected, advice, fixable, category) =
            Verdict(entry, match?.Family, suspicion, orphan, enabled, isStore);
        if (suspicion is not null)
        {
            explanation += $" Signal d'alerte : {suspicion}.";
        }

        if (orphan)
        {
            explanation += " Le programme visé n'existe plus sur le disque : l'entrée ne sert plus à rien.";
        }

        var title = match is { } m
            ? $"{m.App.Name} ({m.Family.Item})"
            : $"{displayName} (non répertorié)";

        return new Finding
        {
            Id = id,
            Title = title,
            Category = category ?? match?.Family.Label ?? UnknownCategory,
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
            var advice = "Lancez une analyse complète avec Microsoft Defender (voir Module 1). En cas de doute, désactivez l'entrée et ne lancez pas ce programme.";
            return enabled
                ? (FindingStatusExtensions.ForDeviation(Severity.Medium), Severity.Medium, "vérifié par une analyse antivirus", advice, false, SuspiciousCategory)
                : (FindingStatus.Info, Severity.Medium, "vérifié par une analyse antivirus", "L'entrée est déjà désactivée. " + advice, false, SuspiciousCategory);
        }

        if (entry.RunOnce)
        {
            return (FindingStatus.Info, Severity.Info, null,
                "Affichage seul : cette commande s'exécutera une seule fois, puis Windows l'effacera.", false, null);
        }

        if (!enabled)
        {
            return family?.Advice == StartupAdvice.AlwaysKeep
                ? (FindingStatus.Info, Severity.Info, "activé", "Cette protection est désactivée au démarrage : réactivez-la. " + SettingsAdvice, false, null)
                : (FindingStatus.Ok, Severity.Low, "désactivé si inutile", null, false, null);
        }

        if (orphan)
        {
            return (FindingStatus.Improvable, Severity.Low, "désactivé (programme absent)",
                "Désactivez cette entrée : elle ne lance plus rien. " + SettingsAdvice, !isStore, null);
        }

        if (family is null)
        {
            return (FindingStatus.Info, Severity.Info, "à vous de décider",
                "Si vous ne connaissez pas ce programme, recherchez son éditeur avant de le désactiver." + storeAdvice, !isStore, null);
        }

        return family.Advice switch
        {
            StartupAdvice.Disable => (FindingStatus.Improvable, Severity.Low, "désactivé", family.Recommendation + storeAdvice, !isStore, null),
            StartupAdvice.BrowserSettings => (FindingStatus.Improvable, Severity.Low, "désactivé dans le navigateur", family.Recommendation, false, null),
            StartupAdvice.DependsOnUse => (FindingStatus.Info, Severity.Info, "selon votre usage", family.Recommendation + storeAdvice, !isStore, null),
            StartupAdvice.KeepIfUsed => (FindingStatus.Info, Severity.Info, "activé si vous utilisez ce service", family.Recommendation, false, null),
            _ => (FindingStatus.Ok, Severity.Info, "activé", family.Recommendation, false, null),
        };
    }

    private static Finding Summary(List<StartupEntry> entries, List<Finding> entryFindings)
    {
        var active = entries.Count(e => e.Approval.Enabled && !e.RunOnce);
        var improvable = entryFindings.Count(f => f.Status == FindingStatus.Improvable);
        var suspicious = entryFindings.Count(f => f.Status == FindingStatus.Warning);
        var current = $"{active} lancement(s) actif(s) sur {entries.Count} entrée(s), dont {improvable} à désactiver sans problème";
        if (suspicious > 0)
        {
            current += $" et {suspicious} suspecte(s)";
        }

        return new Finding
        {
            Id = "M12.summary",
            Title = "Programmes lancés à l'ouverture de session",
            Category = SummaryCategory,
            Status = FindingStatus.Info,
            Current = current,
            Explanation = "Chaque application lancée au démarrage retarde l'ouverture de session, occupe de la mémoire et parfois le processeur "
                + "en arrière-plan. Au-delà des réglages Windows, c'est ici que se gagne la performance au quotidien, surtout avec 8 Go de "
                + "mémoire ou un disque dur. Désactiver une entrée ne désinstalle rien : l'application se lance toujours quand vous l'ouvrez. "
                + "En V0.1, MAUS indique l'éditeur et le chemin de chaque programme ; la vérification de la signature numérique viendra ensuite.",
            Advice = "Pour désactiver une entrée : Paramètres > Applications > Démarrage, ou Gestionnaire des tâches > Applications de démarrage. "
                + "Rien n'est supprimé et tout se réactive en un clic. La V0.2 le proposera directement, familles « sans problème » pré-cochées.",
        };
    }

    /// <summary>Événement 100 (durée du démarrage) et 101 (application qui ralentit le démarrage) du journal Diagnostics-Performance.</summary>
    private static Finding DetectBootTime(AuditContext context)
    {
        const string id = "M12.boot-time";
        const string title = "Durée du dernier démarrage mesurée par Windows";
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
            return Finding.Unknown(id, title, "Le journal des performances de démarrage est absent sur ce PC.", MeasureCategory);
        }

        var boot = events.Where(e => e.Id == 100).OrderByDescending(e => e.TimeCreated).FirstOrDefault();
        var seconds = boot is null ? null : StartupParsers.ParseMilliseconds(boot.Data, "BootTime");
        if (boot is null || seconds is null)
        {
            return Finding.Unknown(id, title,
                "Aucune mesure de démarrage enregistrée ces 60 derniers jours. Avec le démarrage rapide actif, Windows en enregistre rarement (voir Module 5).",
                MeasureCategory);
        }

        var explanation = "Durée mesurée par Windows entre l'allumage et un bureau utilisable. C'est la référence pour juger le gain "
            + "après avoir désactivé des applications au démarrage. Avec le démarrage rapide actif, la mesure n'est prise qu'après un vrai "
            + "redémarrage et peut être faussée (voir Module 5).";
        if (StartupParsers.ParseMilliseconds(boot.Data, "MainPathBootTime") is { } mainPath)
        {
            explanation += $" Dont {mainPath.ToString("0.0", French)} s avant l'affichage du bureau.";
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
            explanation += " Programmes signalés par Windows comme ralentissant le démarrage : "
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
            Advice = "Notez cette durée, désactivez les entrées inutiles, redémarrez, puis comparez.",
        };
    }

    /// <summary>Tâches planifiées hors dossier \Microsoft\, activées et lancées à l'ouverture de session (MSFT_ScheduledTask).</summary>
    private static Finding DetectLogonTasks(ICimReader cim)
    {
        const string id = "M12.logon-tasks";
        const string title = "Tâches planifiées lancées à l'ouverture de session";
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
            return Finding.Unknown(id, title, "La liste des tâches planifiées est indisponible sur ce PC.", OtherSourcesCategory);
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
            Current = found.Count == 0 ? "aucune tâche tierce" : $"{found.Count} tâche(s)",
            Explanation = "Certains programmes se lancent par le Planificateur de tâches plutôt que par la liste de démarrage : ils n'apparaissent "
                + "pas dans le Gestionnaire des tâches. Les tâches de Windows (dossier Microsoft) sont exclues ; sans droits administrateur, "
                + "les tâches d'autres comptes peuvent manquer."
                + (found.Count == 0 ? string.Empty : " Tâches trouvées : " + string.Join(" ; ", found) + "."),
            Advice = found.Count == 0 ? null : "Affichage seul pour l'instant : la V0.2 proposera de désactiver une tâche, avec votre accord.",
            Fixable = found.Count > 0,
        };
    }

    /// <summary>Services en démarrage automatique dont l'éditeur n'est pas Microsoft (affichage seul).</summary>
    private Finding DetectThirdPartyServices(AuditContext context, string windowsDirectory)
    {
        const string id = "M12.third-party-services";
        const string title = "Services tiers démarrant avec Windows";
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
            return Finding.Unknown(id, title, "La liste des services est indisponible.", OtherSourcesCategory);
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
            Current = $"{services.Count} service(s) tiers en démarrage automatique",
            Explanation = "Services installés par d'autres éditeurs que Microsoft (pilotes, antivirus, outils de mise à jour, anti-triche) : "
                + "ils démarrent avec Windows, avant même l'ouverture de session."
                + (services.Count == 0 ? string.Empty : $" {list}{(services.Count > shown ? $", et {services.Count - shown} autre(s)" : string.Empty)}."),
            Advice = "Affichage seul : ne désactivez pas un service sans savoir à quoi il sert. MAUS ne les modifiera pas.",
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
        string? Publisher);
}
