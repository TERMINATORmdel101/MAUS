using Maus.Core.Fixes;
using Maus.Core.Platform;
using static Maus.Core.Modules.M04Privacy.PrivacyKeys;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M04Privacy;

/// <summary>
/// Étape Plan du Module 4 : lignes « Standard » de la fiche qui passent par le registre. Les stratégies ne sont posées
/// que sur Pro et plus ; ailleurs, MAUS écrit l'interrupteur de l'utilisateur, celui que change l'application Paramètres.
/// Services (DiagTrack), tâches planifiées (CEIP) et applications (Copilot, Recall) arrivent dans une étape suivante.
/// </summary>
internal static class PrivacyPlanner
{
    public static IReadOnlyList<PlannedChange> Plan(string moduleId, AuditContext context, IReadOnlyList<Finding> findings)
    {
        var windows = context.Windows;
        var policies = !windows.IsHomeEdition;
        var byId = findings.ToDictionary(f => f.Id, StringComparer.Ordinal);
        bool Deviates(string id) => byId.TryGetValue(id, out var f) && f.Status is FindingStatus.Improvable or FindingStatus.Warning;

        var changes = new List<PlannedChange>();
        void Add(string id, string title, string description, string category, IEnumerable<SettingWrite> writes, Func<PlannedChange, PlannedChange>? tune = null)
        {
            var change = new PlannedChange
            {
                Id = id,
                ModuleId = moduleId,
                Title = title,
                Description = description,
                Category = category,
                Gain = T("Moins de collecte de données et de publicités ; gain de vitesse faible."),
                Writes = writes.ToList(),
            };
            changes.Add(tune is null ? change : tune(change));
        }

        if (Deviates("M04.diagnostic-data"))
        {
            var level = windows.IsEnterpriseOrEducation ? 0 : 1;
            Add("M04.diagnostic-data",
                level == 0 ? T("Couper les données de diagnostic (niveau 0)") : T("Limiter les données de diagnostic à « Requises » (niveau 1)"),
                byId["M04.diagnostic-data"].Explanation,
                Diagnostic,
                [Hklm(DataCollectionPolicy, "AllowTelemetry", level)],
                c => level == 0
                    ? c with
                    {
                        Recommended = false,
                        Risk = T("Au niveau 0, les informations d'échec des mises à jour ne partent plus vers Microsoft (voir Module 3)."),
                    }
                    : c);
        }

        if (Deviates("M04.diagnostic-logs") && policies)
        {
            Add("M04.diagnostic-logs", T("Limiter les journaux et vidages mémoire facultatifs"),
                T("Garde-fou : si les données facultatives sont réactivées un jour, journaux et vidages mémoire ne partent toujours pas."),
                Diagnostic,
                [Hklm(DataCollectionPolicy, "LimitDiagnosticLogCollection", 1), Hklm(DataCollectionPolicy, "LimitDumpCollection", 1)]);
        }

        if (Deviates("M04.feedback-frequency"))
        {
            var writes = new List<SettingWrite> { Hkcu(SiufRules, "NumberOfSIUFInPeriod", 0) };
            if (policies)
            {
                writes.Add(Hklm(DataCollectionPolicy, "DoNotShowFeedbackNotifications", 1));
            }

            Add("M04.feedback-frequency", T("Ne plus demander de commentaires"), T("Windows n'affiche plus de questionnaires pour recueillir votre avis."), Diagnostic, writes);
        }

        if (Deviates("M04.tailored-experiences"))
        {
            var writes = new List<SettingWrite> { Hkcu(PrivacyUser, "TailoredExperiencesWithDiagnosticDataEnabled", 0) };
            if (policies)
            {
                writes.Add(Hkcu(CloudContentPolicy, "DisableTailoredExperiencesWithDiagnosticData", 1));
            }

            Add("M04.tailored-experiences", T("Couper les expériences personnalisées"),
                T("Microsoft ne se sert plus de vos données de diagnostic pour personnaliser astuces, publicités et recommandations."), Offers, writes);
        }

        if (Deviates("M04.advertising-id"))
        {
            var writes = new List<SettingWrite> { Hkcu(AdvertisingUser, "Enabled", 0) };
            if (policies)
            {
                writes.Add(Hklm(AdvertisingPolicy, "DisabledByGroupPolicy", 1));
            }

            Add("M04.advertising-id", T("Couper l'identifiant de publicité"),
                T("Les applications ne peuvent plus personnaliser leurs publicités d'après votre usage. Les publicités restent, sans ciblage."), Offers, writes);
        }

        AddContentDelivery("M04.settings-suggestions", T("Couper le contenu suggéré dans Paramètres"),
            ["SubscribedContent-338393Enabled", "SubscribedContent-353694Enabled", "SubscribedContent-353696Enabled"], Offers);
        AddContentDelivery("M04.tips-silent-apps", T("Couper les astuces et les installations silencieuses d'applications"),
            ["SubscribedContent-338389Enabled", "SoftLandingEnabled", "SilentInstalledAppsEnabled"], Tips);
        AddContentDelivery("M04.lockscreen-tips", T("Couper les astuces de l'écran de verrouillage"),
            ["RotatingLockScreenOverlayEnabled", "SubscribedContent-338387Enabled"], Tips);

        if (Deviates("M04.start-recommendations"))
        {
            Add("M04.start-recommendations", T("Couper les recommandations du menu Démarrer"),
                T("Le menu Démarrer ne recommande plus astuces, nouvelles applications ni sites web."), Tips,
                [Hkcu(ExplorerAdvanced, "Start_IrisRecommendations", 0)],
                c => c with { Effect = ChangeEffect.ExplorerRestart });
        }

        if (Deviates("M04.web-search"))
        {
            Add("M04.web-search", T("Couper les suggestions web de la recherche (stratégie DisableSearchBoxSuggestions)"),
                T("Pose la stratégie qui coupe les suggestions de la zone de recherche. Microsoft ne la documente que pour l'historique de l'Explorateur ; " +
                "son effet sur les résultats web est rapporté par la presse spécialisée (Tom's Hardware, Pureinfotech). L'interrupteur « Recherches sur le web » de Paramètres reste la méthode préférée."),
                Search,
                [Hkcu(ExplorerPolicy, "DisableSearchBoxSuggestions", 1)],
                c => c with
                {
                    Recommended = false,
                    Effect = ChangeEffect.ExplorerRestart,
                    Risk = T("L'Explorateur de fichiers ne propose plus l'historique de vos recherches."),
                });
        }

        if (Deviates("M04.search-highlights"))
        {
            var writes = new List<SettingWrite> { Hkcu(SearchSettingsUser, "IsDynamicSearchBoxEnabled", 0) };
            if (policies)
            {
                writes.Add(Hklm(SearchPolicy, "EnableDynamicContentInWSB", 0));
            }

            Add("M04.search-highlights", T("Couper les points forts de la recherche"),
                T("La recherche n'affiche plus d'illustrations ni de tendances chargées depuis Internet."), Search, writes,
                c => c with { Effect = ChangeEffect.ExplorerRestart });
        }

        if (Deviates("M04.activity-history"))
        {
            Add("M04.activity-history", T("Couper l'historique des activités"),
                T("Windows n'enregistre plus la trace des applications, fichiers et pages ouverts, et ne l'envoie plus.") +
                (windows.IsHomeEdition ? T(" Sur Windows Famille, l'effet de ces stratégies n'est pas garanti.") : string.Empty),
                Search,
                [Hklm(SystemPolicy, "EnableActivityFeed", 0), Hklm(SystemPolicy, "PublishUserActivities", 0), Hklm(SystemPolicy, "UploadUserActivities", 0)]);
        }

        if (Deviates("M04.delivery-optimization") && policies)
        {
            var sharesOnLanOnly = byId["M04.delivery-optimization"].Current?.StartsWith(PrivacySystemChecks.DescribeDownloadMode(1), StringComparison.Ordinal) == true;
            Add("M04.delivery-optimization", T("Télécharger les mises à jour depuis Microsoft uniquement (sans pair-à-pair)"),
                T("Votre connexion ne sert plus à envoyer des morceaux de mises à jour à d'autres PC."), Downloads,
                [Hklm(DeliveryOptimizationPolicy, "DODownloadMode", 0)],
                c => c with
                {
                    Recommended = !sharesOnLanOnly,
                    Risk = sharesOnLanOnly ? T("Avec plusieurs PC à la maison, chacun retélécharge les mises à jour depuis Internet.") : null,
                });
        }

        return changes;

        void AddContentDelivery(string id, string title, string[] names, string category)
        {
            if (Deviates(id))
            {
                Add(id, title, byId[id].Explanation, category,
                    names.Select(name => Hkcu(ContentDelivery, name, 0)),
                    c => c with { Risk = T("Valeurs non documentées par Microsoft, relevées sur Windows 11 : leur effet est vérifié après correction.") });
            }
        }
    }

    private static SettingWrite Hklm(string path, string name, int value) => new(SettingKey.Registry("HKLM", path, name), SettingValue.Dword(value));

    private static SettingWrite Hkcu(string path, string name, int value) => new(SettingKey.Registry("HKCU", path, name), SettingValue.Dword(value));
}
