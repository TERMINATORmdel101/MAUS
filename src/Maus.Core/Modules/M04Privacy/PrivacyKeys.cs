namespace Maus.Core.Modules.M04Privacy;

/// <summary>Clés de registre, requêtes WMI et catégories du Module 4, partagées avec les tests.</summary>
internal static class PrivacyKeys
{
    // Catégories affichées dans l'interface.
    public const string Diagnostic = "Données de diagnostic";
    public const string Offers = "Recommandations et offres";
    public const string Tips = "Publicités et astuces";
    public const string Search = "Recherche et historique";
    public const string Ai = "IA : Copilot et Recall";
    public const string Downloads = "Téléchargements";

    // HKLM : stratégie et état effectif des données de diagnostic.
    public const string DataCollectionPolicy = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";
    public const string DataCollectionState = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection";

    // Expériences personnalisées : stratégie utilisateur (Pro et plus) et interrupteur de Paramètres.
    public const string CloudContentPolicy = @"Software\Policies\Microsoft\Windows\CloudContent";
    public const string PrivacyUser = @"Software\Microsoft\Windows\CurrentVersion\Privacy";

    // Identifiant de publicité : stratégie (HKLM) et interrupteur (HKCU).
    public const string AdvertisingPolicy = @"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo";
    public const string AdvertisingUser = @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo";

    // Historique des activités (HKLM).
    public const string SystemPolicy = @"SOFTWARE\Policies\Microsoft\Windows\System";

    // Fréquence des commentaires (HKCU).
    public const string SiufRules = @"Software\Microsoft\Siuf\Rules";

    // Suggestions, astuces et installations silencieuses (HKCU, valeurs non documentées par Microsoft).
    public const string ContentDelivery = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";

    // Menu Démarrer et Explorateur.
    public const string ExplorerAdvanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    public const string ExplorerPolicy = @"Software\Policies\Microsoft\Windows\Explorer";

    // Recherche.
    public const string SearchPolicy = @"SOFTWARE\Policies\Microsoft\Windows\Windows Search";
    public const string SearchUser = @"Software\Microsoft\Windows\CurrentVersion\Search";
    public const string SearchSettingsUser = @"Software\Microsoft\Windows\CurrentVersion\SearchSettings";

    // Recall (stratégies machine et utilisateur).
    public const string WindowsAiPolicy = @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI";

    // Rapports d'erreurs Windows.
    public const string ErrorReportingPolicy = @"SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting";
    public const string ErrorReportingSettings = @"SOFTWARE\Microsoft\Windows\Windows Error Reporting";

    // Optimisation de la distribution : stratégie puis configuration locale (repli si WMI est indisponible).
    public const string DeliveryOptimizationPolicy = @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization";
    public const string DeliveryOptimizationConfig = @"SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config";

    // Requêtes WMI en lecture seule.
    public const string DiagTrackQuery = "SELECT Name, StartMode, State FROM Win32_Service WHERE Name = 'DiagTrack'";
    public const string TaskSchedulerScope = @"root\Microsoft\Windows\TaskScheduler";
    public const string CeipTasksQuery = @"SELECT TaskName, TaskPath, State FROM MSFT_ScheduledTask WHERE TaskPath = '\\Microsoft\\Windows\\Customer Experience Improvement Program\\'";
    public const string DeliveryOptimizationScope = @"root\Microsoft\Windows\DeliveryOptimization";
    public const string DeliveryOptimizationQuery = "SELECT DownloadMode, DownloadModeProvider FROM MSFT_DeliveryOptimizationConfig";
    public const string RecallQuery = "SELECT Name, InstallState FROM Win32_OptionalFeature WHERE Name = 'Recall'";

    /// <summary>Nom exact du paquet de l'application Copilot. <c>Microsoft.MicrosoftOfficeHub</c> (Microsoft 365 Copilot) n'est jamais visé.</summary>
    public const string CopilotPackage = "Microsoft.Copilot";
}
