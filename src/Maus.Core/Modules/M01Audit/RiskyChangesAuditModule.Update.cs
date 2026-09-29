using System.Globalization;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M01Audit;

/// <summary>Catégorie « Windows Update » : services, serveur imposé, pause, version figée, tâches de maintenance.</summary>
public sealed partial class RiskyChangesAuditModule
{
    private const string UpdateCategory = "Windows Update";
    private const string WuPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string WuAuPolicyKey = WuPolicyKey + @"\AU";
    private const string WuUxSettingsKey = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    private const string UpdateTasksFolder = @"\Microsoft\Windows\UpdateOrchestrator";

    /// <summary>Page « Windows Update » des Paramètres (Microsoft Learn, « Launch Windows Settings ») : bouton « Reprendre les mises à jour ».</summary>
    internal const string WindowsUpdatePage = "ms-settings:windowsupdate";

    /// <summary>Durée de pause maximale proposée par Paramètres > Windows Update.</summary>
    private const int MaxPauseDays = 35;

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private static readonly string[] UpdateServices = ["wuauserv", "UsoSvc", "WaaSMedicSvc", "BITS"];

    /// <summary>Tâches qui lancent la recherche de mises à jour ; les autres varient selon la build.</summary>
    private static readonly HashSet<string> ScanTasks = new(StringComparer.OrdinalIgnoreCase) { "Schedule Scan", "Schedule Scan Static Task" };

    private static readonly Check UpdateServicesCheck = new("M01.wu-services", "Services de Windows Update", UpdateCategory, Severity.Critical, Fixable: true);

    private static Check WsusCheck => new("M01.wu-server", T("Serveur de mises à jour imposé"), UpdateCategory, Severity.Critical, Fixable: true);

    private static Check PauseCheck => new("M01.wu-pause", T("Pause des mises à jour"), UpdateCategory, Severity.High, Fixable: true, SettingsPage: WindowsUpdatePage);

    private static Check TargetVersionCheck => new("M01.wu-target-version", T("Version de Windows figée"), UpdateCategory, Severity.High, Fixable: true);

    private static Check UpdateTasksCheck => new("M01.wu-tasks", T("Tâches de maintenance de Windows Update"), UpdateCategory, Severity.Medium, Fixable: true);

    private static Finding DetectUpdateServices(IRegistryReader registry)
    {
        var services = UpdateServices.Select(name => ServiceStart.Read(registry, name)).ToList();
        return EvaluateServices(
            UpdateServicesCheck,
            services,
            T("Windows Update s'appuie sur ces services pour chercher, télécharger et installer les correctifs de sécurité. " +
            "Désactivés, ils bloquent toutes les mises à jour, et Windows ne peut plus se réparer seul."),
            T("Rétablir le type de démarrage d'origine de ces services (manuel ou automatique selon le service)."),
            _ => UpdateServicesCheck.Severity);
    }

    /// <summary>Verdict commun aux listes de services : seuls « désactivé » et « absent » sont signalés, jamais un écart automatique/manuel qui dépend de la build.</summary>
    private static Finding EvaluateServices(Check check, IReadOnlyList<ServiceStart> services, string explanation, string advice, Func<ServiceStart, Severity> severityOf)
    {
        var expected = T("aucun service désactivé ou absent");
        var broken = services.Where(s => s.IsBroken).ToList();
        if (broken.Count == 0)
        {
            return services.All(s => s.Denied)
                ? check.AdminRequired()
                : check.Compliant(string.Join(" ; ", services.Select(s => s.Describe())), expected, explanation);
        }

        var severity = broken.Select(severityOf).Max();
        return check.Deviation(string.Join(" ; ", broken.Select(s => s.Describe())), expected, explanation, advice, severity);
    }

    private static Finding DetectWsus(IRegistryReader registry, bool managed)
    {
        var explanation = T("Un PC personnel reçoit ses mises à jour directement des serveurs de Microsoft. Certains scripts détournent Windows Update " +
            "vers un serveur d'entreprise (WSUS) inexistant pour bloquer toutes les mises à jour, y compris celles de sécurité.");
        var expected = T("serveurs de Microsoft");
        var advice = T("Supprimer WUServer, WUStatusServer, DoNotConnectToWindowsUpdateInternetLocations et UseWUServer pour revenir aux serveurs de Microsoft.");
        var server = registry.GetString(Hklm, WuPolicyKey, "WUServer");
        var statusServer = registry.GetString(Hklm, WuPolicyKey, "WUStatusServer");
        var noInternet = registry.GetDword(Hklm, WuPolicyKey, "DoNotConnectToWindowsUpdateInternetLocations") == 1;
        var useServer = registry.GetDword(Hklm, WuAuPolicyKey, "UseWUServer") == 1;

        var traces = new List<string>();
        if (IsSet(server))
        {
            traces.Add($"WUServer = {server}");
        }

        if (IsSet(statusServer) && !string.Equals(statusServer, server, StringComparison.OrdinalIgnoreCase))
        {
            traces.Add($"WUStatusServer = {statusServer}");
        }

        if (noInternet)
        {
            traces.Add("DoNotConnectToWindowsUpdateInternetLocations = 1");
        }

        if (!useServer && traces.Count == 0)
        {
            return WsusCheck.Compliant(expected, expected, explanation);
        }

        if (managed)
        {
            return WsusCheck.Neutral(
                useServer ? T("serveur de l'organisation : {0}", server ?? T("non précisé")) : Join(traces),
                expected,
                T("Sur un PC géré, un serveur de mises à jour d'entreprise (WSUS) est normal : il est choisi par votre service informatique."));
        }

        if (useServer)
        {
            return WsusCheck.Deviation(
                IsSet(server) ? T("serveur imposé : {0}", server) : T("UseWUServer = 1 sans adresse de serveur"),
                expected,
                explanation,
                advice);
        }

        return WsusCheck.Deviation(
            T("valeurs résiduelles : {0}", Join(traces)),
            expected,
            T("Ces valeurs ne sont pas actives seules (UseWUServer est absent), mais ce sont des traces d'un blocage de Windows Update. ") + explanation,
            advice,
            Severity.Medium);
    }

    private static Finding DetectUpdatePause(AuditContext context)
    {
        var explanation = T("Paramètres > Windows Update permet de suspendre les mises à jour 5 semaines au plus. " +
            "Certains outils repoussent cette limite de plusieurs années : le PC ne reçoit alors plus aucun correctif de sécurité.");
        var expected = T("aucune pause, ou 5 semaines au plus");
        var advice = T("Reprendre les mises à jour dans Paramètres > Windows Update, puis supprimer la valeur FlightSettingsMaxPauseDays si elle existe.");
        var maxDays = context.Registry.GetDword(Hklm, WuUxSettingsKey, "FlightSettingsMaxPauseDays");
        var expiry = context.Registry.GetString(Hklm, WuUxSettingsKey, "PauseUpdatesExpiryTime");

        if (maxDays > MaxPauseDays)
        {
            return PauseCheck.Deviation(T("durée maximale de pause portée à {0} jours", maxDays), expected, explanation, advice);
        }

        if (!IsSet(expiry))
        {
            return PauseCheck.Compliant(T("aucune pause"), expected, explanation);
        }

        if (!DateTimeOffset.TryParse(expiry, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var until))
        {
            return PauseCheck.Unknown(T("Date de fin de pause illisible : {0}", expiry));
        }

        if (until <= context.Now)
        {
            return PauseCheck.Compliant(T("aucune pause en cours"), expected, explanation);
        }

        var date = until.ToOffset(context.Now.Offset).ToString("d MMMM yyyy", French);
        if (until > context.Now.AddDays(MaxPauseDays + 1))
        {
            return PauseCheck.Deviation(T("en pause jusqu'au {0}", date), expected, explanation, advice);
        }

        return PauseCheck.Neutral(
            T("en pause jusqu'au {0}", date),
            expected,
            T("Les mises à jour ont été suspendues depuis Paramètres : Windows les reprendra seul à la date indiquée."),
            T("Pensez à reprendre les mises à jour plus tôt si un correctif de sécurité important est annoncé."),
            PauseCheck.SettingsPage);
    }

    private static Finding DetectTargetVersion(IRegistryReader registry, bool managed)
    {
        var explanation = T("Cette stratégie bloque Windows sur une version précise et empêche les mises à jour de fonctionnalités. " +
            "Quand cette version n'est plus prise en charge par Microsoft, le PC ne reçoit plus aucun correctif de sécurité.");
        var expected = T("non figée");
        var advice = T("Supprimer TargetReleaseVersion, TargetReleaseVersionInfo et ProductVersion pour laisser Windows suivre les versions prises en charge.");
        var enabled = registry.GetDword(Hklm, WuPolicyKey, "TargetReleaseVersion") == 1;
        var version = registry.GetString(Hklm, WuPolicyKey, "TargetReleaseVersionInfo");
        var product = registry.GetString(Hklm, WuPolicyKey, "ProductVersion");
        if (!enabled && !IsSet(version) && !IsSet(product))
        {
            return TargetVersionCheck.Compliant(expected, expected, explanation);
        }

        var label = string.Join(" ", new[] { product, version }.Where(IsSet));
        if (label.Length == 0)
        {
            label = T("version non précisée");
        }

        if (managed)
        {
            return TargetVersionCheck.Neutral(
                T("figée sur {0}", label),
                expected,
                T("Sur un PC géré, figer la version de Windows est un choix courant du service informatique, qui la fait évoluer lui-même."));
        }

        return enabled
            ? TargetVersionCheck.Deviation(T("figée sur {0}", label), expected, explanation, advice)
            : TargetVersionCheck.Deviation(T("valeurs inactives : {0}", label), expected, T("Ces valeurs sont inactives (TargetReleaseVersion n'est pas à 1), mais restent une trace de blocage. ") + explanation, advice, Severity.Low);
    }

    private Finding DetectUpdateTasks(AuditContext context)
    {
        var explanation = T("Ces tâches planifiées lancent la recherche et l'installation des mises à jour en arrière-plan. " +
            "Désactivées, Windows ne vérifie plus seul la présence de correctifs.");
        var expected = T("tâches de recherche actives");
        var advice = T("Réactiver les tâches du dossier UpdateOrchestrator dans le Planificateur de tâches.");
        var tasks = _tasks.GetTasks(UpdateTasksFolder);
        if (tasks.Count == 0)
        {
            // Sans droits administrateur, ce dossier protégé paraît vide.
            return context.IsElevated
                ? UpdateTasksCheck.Deviation(T("aucune tâche trouvée"), expected, explanation, advice)
                : UpdateTasksCheck.AdminRequired();
        }

        var enabled = tasks.Count(t => t.Enabled);
        if (enabled == 0)
        {
            return UpdateTasksCheck.Deviation(T("{0} tâche(s), toutes désactivées", tasks.Count), expected, explanation, advice);
        }

        var disabledScans = tasks.Where(t => !t.Enabled && ScanTasks.Contains(t.Name)).Select(t => t.Name).ToList();
        if (disabledScans.Count > 0)
        {
            return UpdateTasksCheck.Deviation(T("désactivée(s) : {0}", Join(disabledScans)), expected, explanation, advice);
        }

        return UpdateTasksCheck.Compliant(T("{0} tâche(s) active(s) sur {1}", enabled, tasks.Count), expected, explanation);
    }
}
