namespace Maus.Core.Reporting;

/// <summary>Libellés français affichés à l'utilisateur.</summary>
public static class Labels
{
    public static string Of(FindingStatus status) => status switch
    {
        FindingStatus.Ok => "Conforme",
        FindingStatus.Info => "Info",
        FindingStatus.Improvable => "Optimisation possible",
        FindingStatus.Warning => "À surveiller",
        FindingStatus.Problem => "Problème",
        _ => "Indéterminé",
    };

    public static string Of(Severity severity) => severity switch
    {
        Severity.Low => "Faible",
        Severity.Medium => "Moyen",
        Severity.High => "Élevé",
        Severity.Critical => "Critique",
        _ => "Info",
    };

    public static string Of(Hardware.FormFactor formFactor) => formFactor switch
    {
        Hardware.FormFactor.Desktop => "PC fixe",
        Hardware.FormFactor.Laptop => "PC portable",
        _ => "Type de PC inconnu",
    };

    public static string Of(Fixes.ChangeEffect effect) => effect switch
    {
        Fixes.ChangeEffect.ExplorerRestart => "effet complet après redémarrage de l'Explorateur",
        Fixes.ChangeEffect.SignOut => "effet à la prochaine ouverture de session",
        Fixes.ChangeEffect.Restart => "effet après redémarrage du PC",
        _ => "effet immédiat",
    };

    public static string Of(Fixes.ChangeStatus status) => status switch
    {
        Fixes.ChangeStatus.Applied => "Appliqué",
        Fixes.ChangeStatus.Skipped => "Ignoré",
        _ => "Échec",
    };

    public static string Of(Fixes.RevertStatus status) => status switch
    {
        Fixes.RevertStatus.Reverted => "Restauré",
        Fixes.RevertStatus.ChangedSince => "Modifié depuis",
        Fixes.RevertStatus.Skipped => "Ignoré",
        _ => "Échec",
    };
}
