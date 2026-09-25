using static Maus.Core.Localization.Texts;

namespace Maus.Core.Reporting;

/// <summary>Libellés français affichés à l'utilisateur.</summary>
public static class Labels
{
    public static string Of(FindingStatus status) => status switch
    {
        FindingStatus.Ok => "Conforme",
        FindingStatus.Info => "Info",
        FindingStatus.Improvable => "Optimisation possible",
        FindingStatus.Warning => T("À surveiller"),
        FindingStatus.Problem => T("Problème"),
        _ => T("Indéterminé"),
    };

    public static string Of(Severity severity) => severity switch
    {
        Severity.Low => T("Faible"),
        Severity.Medium => "Moyen",
        Severity.High => T("Élevé"),
        Severity.Critical => "Critique",
        _ => "Info",
    };

    public static string Of(Hardware.FormFactor formFactor) => formFactor switch
    {
        Hardware.FormFactor.Desktop => "PC fixe",
        Hardware.FormFactor.Laptop => "PC portable",
        _ => T("Type de PC inconnu"),
    };

    public static string Of(Fixes.ChangeEffect effect) => effect switch
    {
        Fixes.ChangeEffect.ExplorerRestart => T("effet complet après redémarrage de l'Explorateur"),
        Fixes.ChangeEffect.SignOut => T("effet à la prochaine ouverture de session"),
        Fixes.ChangeEffect.Restart => T("effet après redémarrage du PC"),
        _ => T("effet immédiat"),
    };

    public static string Of(Fixes.ChangeStatus status) => status switch
    {
        Fixes.ChangeStatus.Applied => T("Appliqué"),
        Fixes.ChangeStatus.Skipped => T("Ignoré"),
        _ => T("Échec"),
    };

    public static string Of(Fixes.RevertStatus status) => status switch
    {
        Fixes.RevertStatus.Reverted => T("Restauré"),
        Fixes.RevertStatus.ChangedSince => T("Modifié depuis"),
        Fixes.RevertStatus.Skipped => T("Ignoré"),
        _ => T("Échec"),
    };
}
