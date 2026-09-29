using System.Globalization;
using System.Runtime.InteropServices;
using Maus.Core.Platform;
using Microsoft.Win32;
using static Maus.Core.Modules.M04Privacy.PrivacyFindings;
using static Maus.Core.Modules.M04Privacy.PrivacyKeys;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M04Privacy;

/// <summary>Contrôles du Module 4 par WMI et par l'inventaire des applications : service DiagTrack, tâches CEIP, pair-à-pair, Copilot et Recall.</summary>
internal static class PrivacySystemChecks
{
    /// <summary>Valeurs de <c>MSFT_ScheduledTask.State</c>.</summary>
    internal const long TaskDisabled = 1;

    /// <summary>Valeurs de <c>Win32_OptionalFeature.InstallState</c>.</summary>
    internal const long FeatureEnabled = 1;
    internal const long FeatureDisabled = 2;
    internal const long FeatureAbsent = 3;

    private static string DiagTrackExplanation => T("Le service « Expériences des utilisateurs connectés et télémétrie » (DiagTrack) gère l'envoi des événements et journaux de diagnostic. " +
        "Le désactiver doit couper cet envoi (à confirmer par une capture réseau) ; comme au niveau 0, les informations d'échec des mises à jour ne partent plus.");

    private static string DiagTrackAdvice => T("Option avancée, à cocher séparément : désactiver DiagTrack. Microsoft ne recevra plus les informations d'échec de Windows Update (voir Module 3).");

    private static readonly string[] CeipTasks = ["Consolidator", "UsbCeip"];

    /// <summary>DiagTrack (« Expériences des utilisateurs connectés et télémétrie ») : automatique et démarré par défaut ; sa coupure est une option avancée.</summary>
    public static Finding DiagTrack(ICimReader cim)
    {
        const string id = "M04.diagtrack";
        var title = T("Service de télémétrie DiagTrack (option avancée)");
        return Guard(id, title, Diagnostic, () =>
        {
            var service = FirstRow(cim.Query(DiagTrackQuery));
            if (service is null)
            {
                return Choice(id, title, Diagnostic, true, T("service absent"), T("désactivé (option avancée)"), DiagTrackExplanation, DiagTrackAdvice);
            }

            var startMode = service.GetString("StartMode");
            var disabled = string.Equals(startMode, "Disabled", StringComparison.OrdinalIgnoreCase);
            var current = $"{DescribeStartMode(startMode)}, {DescribeServiceState(service.GetString("State"))}";
            return Choice(id, title, Diagnostic, disabled, current, T("désactivé (option avancée)"), DiagTrackExplanation, DiagTrackAdvice);
        });
    }

    /// <summary>Tâches <c>Consolidator</c> et <c>UsbCeip</c> du programme d'amélioration de l'expérience utilisateur.</summary>
    public static Finding CeipTasksState(ICimReader cim)
    {
        const string id = "M04.ceip-tasks";
        var title = T("Tâches du programme d'amélioration (CEIP) désactivées");
        return Guard(id, title, Diagnostic, () =>
        {
            var tasks = cim.Query(CeipTasksQuery, TaskSchedulerScope)
                .Select(row => (Name: row.GetString("TaskName") ?? string.Empty, State: row.GetInt64("State")))
                .Where(task => CeipTasks.Contains(task.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();
            var current = tasks.Count == 0
                ? T("tâches absentes")
                : string.Join(" ; ", tasks.Select(task => $"{task.Name} : {DescribeTaskState(task.State)}"));
            return Choice(id, title, Diagnostic, tasks.All(task => task.State == TaskDisabled), current, T("désactivées"),
                T("Les tâches planifiées Consolidator et UsbCeip rassemblent et envoient à Microsoft des statistiques d'utilisation du PC et des périphériques USB."),
                T("Désactiver les deux tâches dans le Planificateur de tâches (Microsoft > Windows > Customer Experience Improvement Program). La tâche Appraiser est conservée (voir Module 3)."));
        });
    }

    /// <summary>
    /// Mode de l'optimisation de la distribution, lu comme <c>Get-DOConfig</c> (classe WMI <c>MSFT_DeliveryOptimizationConfig</c>),
    /// avec repli sur la stratégie puis sur la configuration locale.
    /// </summary>
    public static Finding DeliveryOptimization(ICimReader cim, IRegistryReader registry)
    {
        const string id = "M04.delivery-optimization";
        var title = T("Téléchargements des mises à jour sans pair-à-pair");
        return Guard(id, title, Downloads, () =>
        {
            var (mode, source, denied) = ReadDeliveryOptimizationMode(cim, registry);
            if (mode is null)
            {
                return denied
                    ? Finding.AdminRequired(id, title, Downloads)
                    : Finding.Unknown(id, title, T("Le mode de l'optimisation de la distribution n'a pas pu être lu."), Downloads);
            }

            var current = DescribeDownloadMode(mode.Value) + (source is null ? string.Empty : $" ({source})");
            return Choice(id, title, Downloads, mode is 0 or 99, current, T("0 : téléchargement depuis Microsoft uniquement (HTTP)"),
                T("L'optimisation de la distribution peut télécharger des morceaux de mises à jour depuis d'autres PC et envoyer les vôtres en retour. " +
                "En mode 3, votre connexion sert aussi à des PC inconnus sur Internet."),
                mode switch
                {
                    1 => T("Le mode 0 (HTTP seul) est recommandé. Gardez le mode 1 si plusieurs PC de la maison se partagent les mises à jour."),
                    100 => T("Le mode 100 (contournement) est déprécié : choisir le mode 0."),
                    _ => T("Couper « Autoriser les téléchargements à partir d'autres PC » dans Paramètres > Windows Update > Options avancées > Optimisation de la distribution, " +
                         "ou le limiter aux PC du réseau local (mode 1) si vous avez plusieurs PC."),
                },
                // Seul le conseil par défaut renvoie vers Paramètres ; ceux des modes 1 (choix valable) et 100 (contournement) n'y renvoient pas.
                mode is 1 or 100 ? null : DeliveryOptimizationPage);
        });
    }

    public static string DescribeDownloadMode(long mode) => mode switch
    {
        0 => T("0 : HTTP seul, sans pair-à-pair"),
        1 => T("1 : PC du réseau local"),
        2 => T("2 : groupe de PC"),
        3 => T("3 : PC du réseau local et d'Internet"),
        99 => T("99 : simple, sans pair-à-pair"),
        100 => T("100 : contournement (déprécié)"),
        _ => T("{0} : valeur inconnue", mode.ToString(CultureInfo.InvariantCulture)),
    };

    /// <summary>L'app Copilot se retire au choix (option avancée) ; Microsoft 365 Copilot (<c>Microsoft.MicrosoftOfficeHub</c>) n'est jamais concernée.</summary>
    public static Finding Copilot(IPackageInventory packages)
    {
        const string id = "M04.copilot";
        const string title = "Application Copilot";
        return Guard(id, title, Ai, () =>
        {
            InstalledPackage? copilot;
            try
            {
                copilot = packages.GetUserPackages().FirstOrDefault(p => p.Name.Equals(CopilotPackage, StringComparison.OrdinalIgnoreCase));
            }
            catch (COMException)
            {
                return Finding.Unknown(id, title, T("L'inventaire des applications n'a pas pu être lu."), Ai);
            }

            var explanation = T("Copilot est l'assistant d'IA de Microsoft. Windows fonctionne très bien sans lui. Son retrait suit la méthode publiée par Microsoft ; " +
                "l'application Microsoft 365 Copilot n'est jamais touchée.");
            return copilot is null
                ? new Finding
                {
                    Id = id,
                    Title = title,
                    Category = Ai,
                    Status = FindingStatus.Ok,
                    Severity = Severity.Info,
                    Current = T("non installée"),
                    Expected = T("au choix : conservée ou désinstallée"),
                    Explanation = explanation,
                }
                : Info(id, title, Ai, T("installée (version {0})", copilot.Version), T("au choix : conservée ou désinstallée"), explanation,
                    T("Si vous n'utilisez pas Copilot, vous pourrez la désinstaller (option avancée). Elle se réinstalle depuis le Microsoft Store."),
                    fixable: true);
        });
    }

    /// <summary>Recall n'existe que sur les PC Copilot+ et ne capture rien sans consentement : son sort est laissé au choix de l'utilisateur.</summary>
    public static Finding Recall(ICimReader cim, IRegistryReader registry)
    {
        const string id = "M04.recall";
        var title = T("Recall (instantanés de l'écran)");
        return Guard(id, title, Ai, () =>
        {
            var feature = FirstRow(cim.Query(RecallQuery));
            var expected = T("au choix : utilisé ou bloqué");
            if (feature is null)
            {
                return Inactive(T("non disponible sur ce PC"),
                    T("Recall n'existe que sur les PC Copilot+ (processeur neuronal de 40 TOPS, 16 Go de mémoire, 256 Go de stockage, chiffrement du disque). Il n'y a rien à régler ici."));
            }

            var state = feature.GetInt64("InstallState");
            if (state is FeatureDisabled or FeatureAbsent)
            {
                return Inactive(state == FeatureDisabled ? T("désactivé") : T("retiré"), T("La fonction Recall n'est pas active sur ce PC."));
            }

            if (state != FeatureEnabled)
            {
                return Finding.Unknown(id, title, T("L'état de la fonction Recall n'a pas pu être déterminé."), Ai);
            }

            var blocked = registry.GetDword(RegistryHive.LocalMachine, WindowsAiPolicy, "AllowRecallEnablement") == 0
                || registry.GetDword(RegistryHive.LocalMachine, WindowsAiPolicy, "DisableAIDataAnalysis") == 1
                || registry.GetDword(RegistryHive.CurrentUser, WindowsAiPolicy, "DisableAIDataAnalysis") == 1;
            if (blocked)
            {
                return Inactive(T("installé, instantanés bloqués par stratégie"), T("Recall est présent, mais les stratégies de Windows l'empêchent d'enregistrer des instantanés."));
            }

            return Info(id, title, Ai, T("installé, activation au choix"), expected,
                T("Recall enregistre régulièrement des instantanés de l'écran pour vous permettre de retrouver ce que vous avez vu. Il ne capture rien sans votre accord."),
                T("Si vous n'utilisez pas Recall, MAUS pourra bloquer ses instantanés (DisableAIDataAnalysis = 1). Attention : cela supprime les instantanés déjà enregistrés ; une confirmation sera demandée."),
                fixable: true);

            Finding Inactive(string current, string explanation) => new()
            {
                Id = id,
                Title = title,
                Category = Ai,
                Status = FindingStatus.Ok,
                Severity = Severity.Info,
                Current = current,
                Expected = expected,
                Explanation = explanation,
            };
        });
    }

    /// <summary>Mode lu par WMI, sinon dans le registre ; <c>Denied</c> signale un refus d'accès WMI sans repli possible.</summary>
    private static (long? Mode, string? Source, bool Denied) ReadDeliveryOptimizationMode(ICimReader cim, IRegistryReader registry)
    {
        var denied = false;
        try
        {
            var config = FirstRow(cim.Query(DeliveryOptimizationQuery, DeliveryOptimizationScope));
            if (config?.GetInt64("DownloadMode") is { } mode)
            {
                return (mode, DescribeProvider(config.GetInt64("DownloadModeProvider")), false);
            }
        }
        catch (MausAccessDeniedException)
        {
            denied = true;
        }
        catch (DataSourceUnavailableException)
        {
            // Service absent ou classe WMI indisponible : repli sur le registre ci-dessous.
        }

        if (registry.GetDword(RegistryHive.LocalMachine, DeliveryOptimizationPolicy, "DODownloadMode") is { } policy)
        {
            return (policy, T("imposé par stratégie"), false);
        }

        if (registry.GetDword(RegistryHive.LocalMachine, DeliveryOptimizationConfig, "DODownloadMode") is { } local)
        {
            return (local, null, false);
        }

        return (null, null, denied);
    }

    private static CimRow? FirstRow(IReadOnlyList<CimRow> rows) => rows.Count > 0 ? rows[0] : null;

    private static string? DescribeProvider(long? provider) => provider switch
    {
        5 => T("imposé par stratégie"),
        7 => T("imposé par la gestion à distance"),
        8 => T("choisi dans Paramètres"),
        9 => T("réglé par un administrateur"),
        99 => T("réglage par défaut"),
        _ => null,
    };

    private static string DescribeTaskState(long? state) => state switch
    {
        TaskDisabled => T("désactivée"),
        2 => T("en file d'attente"),
        3 => T("prête"),
        4 => T("en cours d'exécution"),
        _ => T("état inconnu"),
    };

    private static string DescribeStartMode(string? startMode) => startMode?.ToUpperInvariant() switch
    {
        "AUTO" => T("démarrage automatique"),
        "MANUAL" => T("démarrage manuel"),
        "DISABLED" => T("désactivé"),
        "BOOT" or "SYSTEM" => T("démarrage système"),
        _ => T("démarrage inconnu"),
    };

    private static string DescribeServiceState(string? state) => state?.ToUpperInvariant() switch
    {
        "RUNNING" => T("en cours d'exécution"),
        "STOPPED" => T("arrêté"),
        "START PENDING" => T("en cours de démarrage"),
        "STOP PENDING" => T("en cours d'arrêt"),
        "PAUSED" => T("en pause"),
        _ => T("état inconnu"),
    };
}
