namespace Maus.Core;

/// <summary>
/// Étape « Detect » du contrat des modules : lecture seule, aucune écriture sur le système.
/// Les étapes Plan, Apply, Verify et Revert arrivent en V0.2.
/// </summary>
public interface IAuditModule
{
    /// <summary>Numéro du module dans la fiche technique, par exemple « M05 ».</summary>
    string Id { get; }

    string Title { get; }

    /// <summary>Ordre d'affichage et d'exécution.</summary>
    int Order { get; }

    /// <summary>Durée maximale de la détection avant abandon.</summary>
    TimeSpan Timeout => TimeSpan.FromSeconds(60);

    /// <summary>Lit l'état du PC et renvoie les constats. Ne doit jamais modifier le système.</summary>
    Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken);
}
