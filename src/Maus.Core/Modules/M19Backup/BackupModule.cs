using System.Globalization;
using Maus.Core.Fixes;
using Maus.Core.Platform;
using Microsoft.Win32;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M19Backup;

/// <summary>
/// Module 19 — Sauvegardes : « si le disque lâche demain, perdez-vous vos fichiers ? ». Protection du système et points de
/// restauration, sauvegarde des dossiers par OneDrive, Historique des fichiers. Lecture seule.
/// </summary>
public sealed class BackupModule : IAuditModule
{
    internal const string RestorePointsQuery = "SELECT SequenceNumber, CreationTime FROM SystemRestore";
    internal const string ShellFolders = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";
    internal const string OneDriveAccounts = @"Software\Microsoft\OneDrive\Accounts";
    internal const int RecentRestorePointDays = 30;

    /// <summary>
    /// Page « Système > Informations système » des Paramètres (Microsoft Learn, « Launch Windows Settings ») : elle porte le lien
    /// « Protection du système » cité par le conseil. La fenêtre de la protection du système elle-même n'a pas d'adresse ms-settings.
    /// </summary>
    internal const string AboutPage = "ms-settings:about";

    private static string Category => T("Sauvegardes");

    public string Id => "M19";

    public string Title => T("Sauvegardes");

    public int Order => 190;

    public Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<Finding> findings = [DetectRestorePoints(context), DetectPersonalFiles(context)];
        return Task.FromResult(findings);
    }

    private static Finding DetectRestorePoints(AuditContext context)
    {
        const string id = "M19.restore-points";
        var title = T("Protection du système et points de restauration");
        var state = new WmiSystemRestore(context.Registry).GetProtectionState(Maus.Core.Fixes.RestorePointCreator.SystemDrive);
        if (state == ProtectionState.DisabledByPolicy)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = Category,
                Status = FindingStatus.Info,
                Current = T("coupée par une stratégie"),
                Explanation = T("La protection du système est coupée par une stratégie (PC géré ou réglage d'entreprise) : aucun point de restauration n'est possible."),
            };
        }

        DateTime? latest = null;
        var count = 0;
        var pointsRead = true;
        try
        {
            var rows = context.Cim.Query(RestorePointsQuery, CimScopes.SystemRestore);
            count = rows.Count;
            latest = rows.Select(r => ParseDmtf(r.GetString("CreationTime"))).OfType<DateTime>().DefaultIfEmpty().Max() is { } max && max != default ? max : null;
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or DataSourceUnavailableException)
        {
            pointsRead = false;
        }

        var now = context.Now.LocalDateTime;
        var points = !pointsRead
            ? T("liste des points illisible sans droits administrateur")
            : latest is { } last
                ? count > 1 ? T("{0} points, le plus récent le {1:d}", count, last) : T("{0} point, du {1:d}", count, last)
                : T("aucun point de restauration");
        var noRecentPoint = pointsRead && (latest is null || (now - latest.Value).TotalDays > RecentRestorePointDays);
        return state switch
        {
            ProtectionState.Enabled => new Finding
            {
                Id = id,
                Title = title,
                Category = Category,
                Status = latest is { } l && (now - l).TotalDays <= RecentRestorePointDays ? FindingStatus.Ok : FindingStatus.Info,
                Severity = Severity.Low,
                Current = T("activée sur C: · {0}", points),
                Expected = T("activée, avec un point récent"),
                Explanation = T("Un point de restauration permet de revenir à l'état d'avant une mise à jour, un pilote ou un réglage qui pose problème, sans toucher à vos documents. Windows en crée lors des mises à jour importantes, et MAUS avant chacune de ses corrections."),
                Advice = noRecentPoint
                    ? T("Aucun point récent : vous pouvez en créer un maintenant (Paramètres > Système > Informations système > Protection du système > Créer).")
                    : null,
                SettingsPage = noRecentPoint ? AboutPage : null,
            },
            ProtectionState.Disabled => new Finding
            {
                Id = id,
                Title = title,
                Category = Category,
                Status = FindingStatusExtensions.ForDeviation(Severity.Medium),
                Severity = Severity.Medium,
                Current = T("désactivée sur C:"),
                Expected = T("activée"),
                Explanation = T("Sans protection du système, impossible de revenir en arrière après une mise à jour, un pilote ou un réglage qui pose problème. Certains outils d'« optimisation » la coupent pour gagner un peu de place sur le disque."),
                Advice = T("Activez-la : Paramètres > Système > Informations système > Protection du système > Configurer > Activer, avec 3 à 5 % du disque. MAUS peut aussi l'activer, avec votre accord, avant sa première correction."),
                SettingsPage = AboutPage,
            },
            _ => Finding.Unknown(id, title, T("L'état de la protection du système n'a pas pu être lu."), Category),
        };
    }

    private static Finding DetectPersonalFiles(AuditContext context)
    {
        var registry = context.Registry;
        var folders = new (string Value, string Label)[] { ("Personal", T("Documents")), ("Desktop", T("Bureau")), ("My Pictures", T("Images")) };
        // Le mot « OneDrive » dans un chemin ne prouve rien sans compte connecté.
        var hasOneDriveAccount = registry.GetSubKeyNames(RegistryHive.CurrentUser, OneDriveAccounts).Any();
        var inOneDrive = folders
            .Where(f => hasOneDriveAccount && registry.GetString(RegistryHive.CurrentUser, ShellFolders, f.Value) is { } path && path.Contains("OneDrive", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Label)
            .ToList();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var fileHistory = context.Files.FileExists(Path.Combine(localAppData, "Microsoft", "Windows", "FileHistory", "Configuration", "Config1.xml"));
        var found = new List<string>();
        var checks = new List<string>();
        if (inOneDrive.Count > 0)
        {
            found.Add(T("dossiers dans OneDrive ({0}), date de la dernière copie non lue", string.Join(", ", inOneDrive)));
            checks.Add(T("Vérifiez que la synchronisation est à jour : icône OneDrive de la barre des tâches."));
        }

        if (fileHistory)
        {
            found.Add(T("Historique des fichiers configuré, état non vérifié"));
            checks.Add(T("Vérifiez la date de la dernière copie : Panneau de configuration > Historique des fichiers."));
        }

        // Un dossier dans OneDrive ou un fichier de réglages disent qu'un réglage existe, pas qu'une copie se fait encore : jamais vert.
        var detected = found.Count > 0;
        return new Finding
        {
            Id = "M19.personal-files",
            Title = T("Sauvegarde automatique de vos fichiers"),
            Category = Category,
            Status = detected ? FindingStatus.Info : FindingStatusExtensions.ForDeviation(Severity.Medium),
            Severity = Severity.Medium,
            Current = detected ? string.Join(" · ", found) : T("aucune sauvegarde automatique détectée"),
            Expected = T("vos documents copiés ailleurs que sur ce disque"),
            Explanation = T("Un disque qui lâche, un vol, une chute ou un rançongiciel, et des années de photos et de documents disparaissent. Une sauvegarde automatique copie vos fichiers ailleurs que sur ce disque.")
                + (inOneDrive.Count > 0
                    ? " " + T("MAUS voit que ces dossiers sont dans OneDrive, pas si la synchronisation est à jour. Un fichier supprimé l'est aussi en ligne ; d'après Microsoft, il se restaure depuis la corbeille de OneDrive pendant 30 jours avec un compte personnel, et 93 jours avec un compte professionnel ou scolaire, sauf si l'administrateur a changé ce réglage.")
                    : string.Empty),
            Advice = detected
                ? string.Join(' ', checks)
                : T("Activez la sauvegarde des dossiers dans OneDrive (5 Go gratuits), ou l'Historique des fichiers sur un disque externe (Panneau de configuration > Historique des fichiers). Si vous utilisez un autre logiciel de sauvegarde, marquez ce constat « voulu »."),
        };
    }

    private static DateTime? ParseDmtf(string? value) =>
        value is { Length: >= 14 } && DateTime.TryParseExact(value[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date.ToLocalTime()
            : null;
}
