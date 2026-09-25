using System.Globalization;
using Maus.Core.Platform;
using Microsoft.Win32;
using static Maus.Core.Modules.M04Privacy.PrivacyFindings;
using static Maus.Core.Modules.M04Privacy.PrivacyKeys;

namespace Maus.Core.Modules.M04Privacy;

/// <summary>
/// Contrôles de registre du Module 4 qui dépendent de l'édition ou combinent plusieurs valeurs.
/// Sur Famille, rien ne garantit que les clés <c>Policies</c> soient honorées : seul l'état effectif compte alors.
/// </summary>
internal static class PrivacyRegistryChecks
{
    private const RegistryHive Hklm = RegistryHive.LocalMachine;
    private const RegistryHive Hkcu = RegistryHive.CurrentUser;

    /// <summary>Premier build où <c>HideRecommendedPersonalizedSites</c> existe (22H2, 22621.1928).</summary>
    private const int HideRecommendedSitesMinBuild = 22621;

    private static readonly string[] SettingsSuggestionValues =
        ["SubscribedContent-338393Enabled", "SubscribedContent-353694Enabled", "SubscribedContent-353696Enabled"];

    private static readonly string[] TipsAndSilentAppsValues =
        ["SubscribedContent-338389Enabled", "SoftLandingEnabled", "SilentInstalledAppsEnabled"];

    private static readonly string[] LockScreenTipsValues =
        ["RotatingLockScreenOverlayEnabled", "SubscribedContent-338387Enabled"];

    private static readonly string[] ActivityHistoryValues =
        ["EnableActivityFeed", "PublishUserActivities", "UploadUserActivities"];

    /// <summary>
    /// Niveau des données de diagnostic : la stratégie prime sur l'état choisi dans Paramètres.
    /// 0 n'est honoré que sur Entreprise, Éducation et Server ; ailleurs, Windows le traite comme 1.
    /// </summary>
    public static Finding DiagnosticData(IRegistryReader registry, WindowsInfo windows)
    {
        const string id = "M04.diagnostic-data";
        const string title = "Données de diagnostic au minimum de l'édition";
        return Guard(id, title, Diagnostic, () =>
        {
            var policy = registry.GetDword(Hklm, DataCollectionPolicy, "AllowTelemetry");
            var level = policy ?? registry.GetDword(Hklm, DataCollectionState, "AllowTelemetry");
            if (level is null)
            {
                return Finding.Unknown(id, title, "Le niveau des données de diagnostic n'a pas été trouvé dans le registre.", Diagnostic);
            }

            var current = DescribeDiagnosticLevel(level.Value) + (policy is null ? string.Empty : " (imposé par stratégie)");
            const string explanation =
                "Windows envoie à Microsoft des données de diagnostic sur l'état du PC. Le niveau « Requises » se limite à ce qui " +
                "garde Windows sûr et à jour ; « Facultatives » y ajoute des informations sur votre usage (sites visités, applications, " +
                "saisie). Le gain de vitesse est faible : le bénéfice est surtout moins de collecte.";

            if (windows.IsEnterpriseOrEducation)
            {
                return Choice(id, title, Diagnostic, level <= 0, current, "0 (diagnostic désactivé), possible sur Entreprise et Éducation", explanation,
                    "Sur Entreprise et Éducation, le niveau 0 est honoré : il se règle par la stratégie AllowTelemetry = 0.");
            }

            if (level <= 0)
            {
                return Info(id, title, Diagnostic, current, "1 (Requises), le minimum sur cette édition",
                    "Le niveau 0 n'est honoré que sur Entreprise, Éducation et Server : sur cette édition, il équivaut à 1 (Requises). " +
                    "La collecte est donc déjà au minimum possible.");
            }

            return Choice(id, title, Diagnostic, level == 1, current, "1 (Requises), le minimum sur Famille et Pro", explanation,
                "Désactiver « Envoyer des données de diagnostic facultatives » dans Paramètres > Confidentialité et sécurité > Diagnostics et commentaires.");
        });
    }

    public static string DescribeDiagnosticLevel(int level) => level switch
    {
        0 => "0 (diagnostic désactivé)",
        1 => "1 (Requises)",
        2 => "2 (Améliorées, ancien niveau de Windows 10)",
        3 => "3 (Facultatives)",
        _ => $"{level.ToString(CultureInfo.InvariantCulture)} (valeur inconnue)",
    };

    /// <summary>Garde-fous <c>LimitDiagnosticLogCollection</c> et <c>LimitDumpCollection</c> (Pro et plus, Windows 11 21H2+).</summary>
    public static Finding DiagnosticLogs(IRegistryReader registry, WindowsInfo windows)
    {
        const string id = "M04.diagnostic-logs";
        const string title = "Journaux et vidages mémoire facultatifs limités";
        return Guard(id, title, Diagnostic, () =>
        {
            var logs = registry.GetDword(Hklm, DataCollectionPolicy, "LimitDiagnosticLogCollection");
            var dumps = registry.GetDword(Hklm, DataCollectionPolicy, "LimitDumpCollection");
            var current = $"journaux : {DescribeLimit(logs)} ; vidages mémoire : {DescribeLimit(dumps)}";
            const string expected = "journaux et vidages limités (1 et 1)";

            if (windows.IsHomeEdition)
            {
                return Info(id, title, Diagnostic, current, expected,
                    "Ces deux stratégies sont réservées aux éditions Pro et plus : elles n'ont pas d'effet garanti sur Windows Famille. " +
                    "Au niveau « Requises », ces journaux ne sont de toute façon pas envoyés.");
            }

            return Choice(id, title, Diagnostic, logs == 1 && dumps == 1, current, expected,
                "Ces stratégies empêchent l'envoi des journaux de diagnostic et des vidages mémoire facultatifs. Elles n'ont pas d'effet " +
                "au niveau « Requises », mais servent de garde-fou si les données facultatives sont réactivées un jour.",
                "Poser les stratégies LimitDiagnosticLogCollection et LimitDumpCollection à 1.");
        });
    }

    public static Finding FeedbackFrequency(IRegistryReader registry, bool policiesHonored)
    {
        const string id = "M04.feedback-frequency";
        const string title = "Demandes de commentaires désactivées";
        return Guard(id, title, Diagnostic, () =>
        {
            var policy = registry.GetDword(Hklm, DataCollectionPolicy, "DoNotShowFeedbackNotifications");
            var perPeriod = registry.GetDword(Hkcu, SiufRules, "NumberOfSIUFInPeriod");
            var byPolicy = policiesHonored && policy == 1;
            var current = byPolicy
                ? "jamais (imposé par stratégie)"
                : perPeriod switch
                {
                    null => "automatique (par défaut)",
                    0 => "jamais",
                    var n => $"{n.Value.ToString(CultureInfo.InvariantCulture)} demande(s) par période",
                };
            return Choice(id, title, Diagnostic, byPolicy || perPeriod == 0, current, "jamais",
                "Windows affiche de temps en temps des questionnaires pour recueillir votre avis. Les couper évite ces interruptions, sans aucun effet sur le fonctionnement du PC.",
                "Régler « Fréquence des commentaires » sur « Jamais » dans Paramètres > Confidentialité et sécurité > Diagnostics et commentaires.");
        });
    }

    /// <summary>Rapports d'erreurs : conservés par défaut (utiles au diagnostic, voir Module 2). Leur coupure n'est qu'une option avancée déconseillée.</summary>
    public static Finding ErrorReporting(IRegistryReader registry)
    {
        const string id = "M04.error-reporting";
        const string title = "Rapports d'erreurs Windows conservés";
        return Guard(id, title, Diagnostic, () =>
        {
            var disabled = registry.GetDword(Hklm, ErrorReportingPolicy, "Disabled") == 1
                || registry.GetDword(Hklm, ErrorReportingSettings, "Disabled") == 1;
            const string explanation =
                "Les rapports d'erreurs signalent les plantages à Microsoft et proposent parfois une solution. MAUS les laisse actifs : " +
                "ils servent au diagnostic des pannes (voir Module 2).";
            return disabled
                ? Info(id, title, Diagnostic, "désactivés", "actifs (conseillé)", explanation,
                    "Les rapports d'erreurs sont coupés : c'est déconseillé, sauf choix délibéré. Réactivez-les si des plantages sont à diagnostiquer.")
                : new Finding
                {
                    Id = id,
                    Title = title,
                    Category = Diagnostic,
                    Status = FindingStatus.Ok,
                    Severity = Severity.Info,
                    Current = "actifs",
                    Expected = "actifs (conseillé)",
                    Explanation = explanation,
                };
        });
    }

    public static Finding TailoredExperiences(IRegistryReader registry, bool policiesHonored)
    {
        const string id = "M04.tailored-experiences";
        const string title = "Expériences personnalisées désactivées";
        return Guard(id, title, Offers, () =>
        {
            var policy = registry.GetDword(Hkcu, CloudContentPolicy, "DisableTailoredExperiencesWithDiagnosticData");
            var enabled = registry.GetDword(Hkcu, PrivacyUser, "TailoredExperiencesWithDiagnosticDataEnabled");
            var byPolicy = policiesHonored && policy == 1;
            if (!byPolicy && IsUndocumented(enabled))
            {
                return Undocumented(id, title, Offers, enabled!.Value, "désactivées");
            }

            var current = byPolicy ? "désactivées (imposé par stratégie)" : DescribeToggle(enabled, "désactivées", "activées");
            return Choice(id, title, Offers, byPolicy || enabled == 0, current, "désactivées",
                "Microsoft peut se servir de vos données de diagnostic pour vous proposer des astuces, des publicités et des recommandations personnalisées.",
                policiesHonored
                    ? "Couper les expériences (ou offres) personnalisées dans Paramètres > Confidentialité et sécurité > Diagnostics et commentaires, ou poser la stratégie DisableTailoredExperiencesWithDiagnosticData = 1."
                    : "Couper les expériences (ou offres) personnalisées dans Paramètres > Confidentialité et sécurité > Diagnostics et commentaires (ms-settings:privacy-feedback).");
        });
    }

    public static Finding AdvertisingId(IRegistryReader registry, bool policiesHonored)
    {
        const string id = "M04.advertising-id";
        const string title = "Identifiant de publicité désactivé";
        return Guard(id, title, Offers, () =>
        {
            var policy = registry.GetDword(Hklm, AdvertisingPolicy, "DisabledByGroupPolicy");
            var enabled = registry.GetDword(Hkcu, AdvertisingUser, "Enabled");
            var byPolicy = policiesHonored && policy == 1;
            if (!byPolicy && IsUndocumented(enabled))
            {
                return Undocumented(id, title, Offers, enabled!.Value, "désactivé");
            }

            var current = byPolicy ? "désactivé (imposé par stratégie)" : DescribeToggle(enabled, "désactivé", "activé");
            return Choice(id, title, Offers, byPolicy || enabled == 0, current, "désactivé",
                "L'identifiant de publicité permet aux applications de vous montrer des publicités ciblées d'après votre usage. " +
                "Le couper ne supprime pas les publicités, mais elles ne sont plus personnalisées.",
                "Couper « Autoriser les applications à me montrer des publicités personnalisées à l'aide de mon identifiant de publicité » dans Paramètres > Confidentialité et sécurité > Recommandations et offres.");
        });
    }

    public static Finding SettingsSuggestions(IRegistryReader registry) => ContentDeliveryGroup(
        registry,
        "M04.settings-suggestions",
        "Contenu suggéré dans Paramètres désactivé",
        Offers,
        SettingsSuggestionValues,
        "L'application Paramètres peut afficher des suggestions et des offres de Microsoft (applications, services, abonnements).",
        "Couper « Me montrer du contenu suggéré dans l'application Paramètres » dans Paramètres > Confidentialité et sécurité > Recommandations et offres.");

    public static Finding TipsAndSilentApps(IRegistryReader registry) => ContentDeliveryGroup(
        registry,
        "M04.tips-silent-apps",
        "Astuces et installations silencieuses d'applications désactivées",
        Tips,
        TipsAndSilentAppsValues,
        "Windows affiche des astuces et des suggestions, et peut installer sans vous le demander des applications mises en avant par Microsoft (jeux, réseaux sociaux).",
        "Couper « Obtenir des conseils et des suggestions lors de l'utilisation de Windows » dans Paramètres > Système > Notifications > Paramètres supplémentaires.");

    public static Finding LockScreenTips(IRegistryReader registry) => ContentDeliveryGroup(
        registry,
        "M04.lockscreen-tips",
        "Astuces sur l'écran de verrouillage désactivées",
        Tips,
        LockScreenTipsValues,
        "L'écran de verrouillage peut afficher des anecdotes et des astuces, qui servent aussi à promouvoir des produits Microsoft.",
        "Décocher « Afficher des anecdotes, des astuces et plus encore sur l'écran de verrouillage » dans Paramètres > Personnalisation > Écran de verrouillage.");

    public static Finding StartRecommendations(IRegistryReader registry, WindowsInfo windows)
    {
        const string id = "M04.start-recommendations";
        const string title = "Recommandations du menu Démarrer désactivées";
        return Guard(id, title, Tips, () =>
        {
            var iris = registry.GetDword(Hkcu, ExplorerAdvanced, "Start_IrisRecommendations");
            var policy = registry.GetDword(Hkcu, ExplorerPolicy, "HideRecommendedPersonalizedSites");
            var byPolicy = !windows.IsHomeEdition && windows.Build >= HideRecommendedSitesMinBuild && policy == 1;
            if (!byPolicy && IsUndocumented(iris))
            {
                return Undocumented(id, title, Tips, iris!.Value, "désactivées");
            }

            var current = byPolicy ? "masquées (imposé par stratégie)" : DescribeToggle(iris, "désactivées", "activées");
            return Choice(id, title, Tips, byPolicy || iris == 0, current, "désactivées",
                "Le menu Démarrer peut recommander des astuces, des raccourcis, de nouvelles applications et des sites web.",
                "Couper « Afficher les recommandations d'astuces, de raccourcis, de nouvelles applications et plus » dans Paramètres > Personnalisation > Démarrer.");
        });
    }

    /// <summary>
    /// Résultats web dans Rechercher. L'interrupteur de KB5120998 n'a pas de valeur de registre documentée :
    /// on s'appuie sur les stratégies connues, et <c>BingSearchEnabled</c> n'est qu'un indice.
    /// </summary>
    public static Finding WebSearch(IRegistryReader registry, WindowsInfo windows)
    {
        const string id = "M04.web-search";
        const string title = "Résultats web dans Rechercher désactivés";
        return Guard(id, title, Search, () =>
        {
            var suggestionsOff = registry.GetDword(Hkcu, ExplorerPolicy, "DisableSearchBoxSuggestions") == 1
                || registry.GetDword(Hklm, ExplorerPolicy, "DisableSearchBoxSuggestions") == 1;
            var connectedOff = windows.IsEnterpriseOrEducation && registry.GetDword(Hklm, SearchPolicy, "ConnectedSearchUseWeb") == 0;
            var bing = registry.GetDword(Hkcu, SearchUser, "BingSearchEnabled");
            const string explanation =
                "Ce que vous tapez dans la recherche de Windows part vers Bing pour afficher des résultats web. " +
                "Les couper garde la recherche sur le PC : elle est plus rapide et plus discrète.";
            const string advice =
                "Couper « Recherches sur le web » dans Paramètres > Confidentialité et sécurité > Rechercher (mise à jour KB5120998, déployée progressivement). " +
                "Sinon, MAUS pourra poser la stratégie DisableSearchBoxSuggestions, dont l'effet sur le web reste à vérifier.";

            if (suggestionsOff || connectedOff)
            {
                return Choice(id, title, Search, true, "désactivés (imposé par stratégie)", "désactivés", explanation, advice);
            }

            if (bing == 0)
            {
                return Info(id, title, Search, "probablement désactivés (BingSearchEnabled = 0)", "désactivés",
                    explanation + " L'ancien réglage BingSearchEnabled est coupé, mais il n'est plus garanti sur les versions récentes de Windows.",
                    "Vérifier l'interrupteur « Recherches sur le web » dans Paramètres > Confidentialité et sécurité > Rechercher.");
            }

            return Choice(id, title, Search, false, "activés", "désactivés", explanation, advice);
        });
    }

    public static Finding SearchHighlights(IRegistryReader registry, bool policiesHonored)
    {
        const string id = "M04.search-highlights";
        const string title = "Points forts de la recherche désactivés";
        return Guard(id, title, Search, () =>
        {
            var policy = registry.GetDword(Hklm, SearchPolicy, "EnableDynamicContentInWSB");
            var enabled = registry.GetDword(Hkcu, SearchSettingsUser, "IsDynamicSearchBoxEnabled");
            var byPolicy = policiesHonored && policy == 0;
            if (!byPolicy && IsUndocumented(enabled))
            {
                return Undocumented(id, title, Search, enabled!.Value, "désactivés");
            }

            var current = byPolicy ? "désactivés (imposé par stratégie)" : DescribeToggle(enabled, "désactivés", "activés");
            return Choice(id, title, Search, byPolicy || enabled == 0, current, "désactivés",
                "Les points forts de la recherche affichent dans Rechercher des illustrations, des événements du jour et des tendances chargés depuis Internet.",
                "Couper « Afficher les points forts de la recherche » dans Paramètres > Confidentialité et sécurité > Autorisations de recherche.");
        });
    }

    public static Finding ActivityHistory(IRegistryReader registry, WindowsInfo windows)
    {
        const string id = "M04.activity-history";
        const string title = "Historique des activités désactivé";
        return Guard(id, title, Search, () =>
        {
            var values = ActivityHistoryValues.Select(name => (Name: name, Value: registry.GetDword(Hklm, SystemPolicy, name))).ToList();
            var current = values.All(v => v.Value is null)
                ? "non configuré (historique enregistré sur l'appareil)"
                : string.Join(", ", values.Select(v => $"{v.Name} = {DescribeRaw(v.Value)}"));
            var explanation =
                "L'historique des activités garde la trace des applications, fichiers et pages que vous ouvrez. " +
                "Les trois stratégies coupent son enregistrement et son envoi à Microsoft.";
            if (windows.IsHomeEdition)
            {
                explanation += " Sur Windows Famille, ces stratégies ne sont pas garanties : MAUS relira l'état réel après correction.";
            }

            return Choice(id, title, Search, values.All(v => v.Value == 0), current, "désactivé (0, 0, 0)", explanation,
                "Couper « Stocker mon historique d'activités sur cet appareil » dans Paramètres > Confidentialité et sécurité > Historique des activités.");
        });
    }

    /// <summary>Groupe de valeurs du ContentDeliveryManager : absente vaut 1 (activée), comme le défaut de Windows.</summary>
    private static Finding ContentDeliveryGroup(IRegistryReader registry, string id, string title, string category, string[] names, string explanation, string advice) =>
        Guard(id, title, category, () =>
        {
            var active = names.Count(name => registry.GetDword(Hkcu, ContentDelivery, name) != 0);
            var current = active == 0
                ? "désactivées"
                : $"actives ({active.ToString(CultureInfo.InvariantCulture)} sur {names.Length.ToString(CultureInfo.InvariantCulture)})";
            return Choice(id, title, category, active == 0, current, "désactivées", explanation, advice);
        });

    private static string DescribeToggle(int? value, string off, string on) => value switch
    {
        null => $"{on} (par défaut)",
        0 => off,
        _ => on,
    };

    /// <summary>Un interrupteur ne vaut normalement que 0 ou 1 : toute autre valeur ne permet pas de conclure.</summary>
    private static bool IsUndocumented(int? value) => value is not null and not 0 and not 1;

    /// <summary>Valeur hors de 0 et 1 : simple information, pour ne jamais signaler à tort une optimisation.</summary>
    private static Finding Undocumented(string id, string title, string category, int value, string expected) =>
        Info(id, title, category, $"valeur {value.ToString(CultureInfo.InvariantCulture)} non documentée", expected,
            "La valeur lue dans le registre n'est pas documentée par Microsoft : MAUS ne peut pas dire si ce réglage est actif.",
            "Vérifier l'interrupteur correspondant dans Paramètres > Confidentialité et sécurité.");

    private static string DescribeLimit(int? value) => value switch
    {
        null => "non configuré",
        1 => "limités",
        _ => "non limités",
    };

    private static string DescribeRaw(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "absente";
}
