using static Maus.Core.Localization.Texts;

namespace Maus.Core.Reporting;

/// <summary>Libellés français affichés à l'utilisateur.</summary>
public static class Labels
{
    public static string Of(FindingStatus status) => status switch
    {
        FindingStatus.Ok => T("Conforme"),
        FindingStatus.Info => T("Info"),
        FindingStatus.Improvable => T("Optimisation possible"),
        FindingStatus.Warning => T("À surveiller"),
        FindingStatus.Problem => T("Problème"),
        _ => T("Indéterminé"),
    };

    /// <summary>État d'une écriture du journal, en quelques mots.</summary>
    public static string Of(Fixes.EntryState state) => state switch
    {
        Fixes.EntryState.Applied => T("en place"),
        Fixes.EntryState.Reverted => T("annulée"),
        Fixes.EntryState.RevertSkipped => T("modifiée depuis"),
        Fixes.EntryState.Failed => T("échec, origine remise"),
        Fixes.EntryState.Pending => T("interrompue : peut-être appliquée, « Annuler » vérifie"),
        Fixes.EntryState.RollbackFailed => T("retour arrière raté"),
        _ => state.ToString(),
    };

    public static string Of(Hardware.FormFactor formFactor) => formFactor switch
    {
        Hardware.FormFactor.Desktop => T("PC fixe"),
        Hardware.FormFactor.Laptop => T("PC portable"),
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
        Fixes.RevertStatus.Skipped => T("Déjà en place"),
        Fixes.RevertStatus.WrongAccount => T("Ignoré"),
        _ => T("Échec"),
    };
}
