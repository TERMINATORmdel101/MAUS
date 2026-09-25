using static Maus.Core.Localization.Texts;

namespace Maus.App;

/// <summary>Textes fixes de l'interface (XAML : <c>{x:Static app:Ui.Nom}</c>), traduits dans la langue active.</summary>
public static class Ui
{
    public static string WindowTitle => T("MAUS — Audit et corrections de Windows");

    public static string Tagline => T("MAUS audite d'abord sans rien modifier. Les corrections ne s'appliquent qu'avec votre accord, et chacune peut être annulée.");

    public static string RunAudit => T("Lancer l'audit");

    public static string TabFindings => T("Constats");

    public static string Wanted => T("C'est voulu");

    public static string WantedTip => T("Ne plus signaler ce constat tant que la situation ne change pas");

    public static string ReportAgain => T("Signaler de nouveau");

    public static string TabFixes => T("Corrections");

    public static string OneClickProfiles => T("Profils en un clic :");

    public static string YourChoices => T("Vos choix");

    public static string GameBarProfile => T("Profil Game Bar :");

    public static string LaptopPower => T("Alimentation du portable :");

    public static string CreateRestorePoint => T("Créer un point de restauration vérifié avant toute modification (recommandé)");

    public static string EnableProtection => T("Activer la protection du système sur C: si elle est désactivée (nécessaire pour le point de restauration)");

    public static string VerifyNote => T("Après les corrections, MAUS relance l'audit pour vérifier l'état réel du PC.");

    public static string SaveReport => T("Enregistrer le rapport (HTML)");

    public static string SaveReportTip => T("Une page à garder ou à imprimer : ce qui a été vu, ce qui a été changé, et comment l'annuler");

    public static string RestartExplorer => T("Redémarrer l'Explorateur");

    public static string RestartExplorerTip => T("Applique les corrections de la barre des tâches sans fermer la session");

    public static string TechnicalDetails => T("Détails techniques");

    public static string TabHistory => T("Historique");

    public static string Refresh => T("Actualiser");

    public static string RevertSession => T("Annuler cette séance");

    public static string SessionChanges => T("Corrections de cette séance");

    public static string Revert => T("Annuler");

    public static string LaptopTitle => T("MAUS — votre portable");

    public static string LaptopQuestion => T("Comment voulez-vous utiliser votre portable ?");

    public static string LaptopIntro => T("MAUS adapte ses conseils d'alimentation à votre choix. Vous pourrez en changer à tout moment dans l'onglet Corrections.");

    public static string LaptopPerformance => T("Performance sur secteur, Équilibré sur batterie (recommandé)");

    public static string LaptopPerformanceDetail => T("Plus de chaleur et de bruit quand le PC est branché ; autonomie préservée sur batterie.");

    public static string LaptopEverywhere => T("Performance partout");

    public static string LaptopEverywhereDetail => T("Le PC reste aussi rapide sur batterie, mais l'autonomie baisse nettement.");

    public static string LaptopBattery => T("Autonomie et silence");

    public static string LaptopBatteryDetail => T("Équilibré sur secteur, économie d'énergie sur batterie : plus frais, plus silencieux, un peu moins réactif.");

    public static string Later => T("Plus tard");

    public static string Validate => T("Valider");
}
