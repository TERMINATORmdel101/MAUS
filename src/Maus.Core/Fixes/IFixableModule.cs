namespace Maus.Core.Fixes;

/// <summary>
/// Module capable de proposer des corrections réversibles (V0.2). Le contrat complet est Detect, Plan, Apply, Verify, Revert :
/// <list type="bullet">
/// <item><description><c>Detect</c> (<see cref="IAuditModule"/>) et <c>Plan</c> ne font que lire.</description></item>
/// <item><description><c>Apply</c>, <c>Verify</c> et <c>Revert</c> sont communs à tous les modules (<see cref="FixEngine"/>) :
/// chaque correction se décrit par des écritures élémentaires que le moteur sait journaliser, relire et restaurer.</description></item>
/// </list>
/// </summary>
public interface IFixableModule : IAuditModule
{
    /// <summary>
    /// Corrections proposées pour les constats de ce module. Ne doit jamais écrire.
    /// Un constat conforme, illisible ou non corrigeable sur ce PC (édition, matériel) ne produit aucune correction.
    /// </summary>
    IReadOnlyList<PlannedChange> Plan(AuditContext context, IReadOnlyList<Finding> findings);
}
