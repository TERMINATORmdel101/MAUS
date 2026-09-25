namespace Maus.Core.Fixes;

/// <summary>Ce qu'il faut faire après la correction pour qu'elle prenne effet.</summary>
public enum ChangeEffect
{
    /// <summary>Effet immédiat.</summary>
    Immediate,

    /// <summary>Effet complet après redémarrage de l'Explorateur (ou fermeture de session).</summary>
    ExplorerRestart,

    /// <summary>Effet à la prochaine ouverture de session.</summary>
    SignOut,

    /// <summary>Effet au prochain redémarrage du PC.</summary>
    Restart,
}

/// <summary>Une écriture élémentaire : la valeur visée, ou <c>null</c> pour supprimer la valeur.</summary>
public sealed record SettingWrite(SettingKey Key, SettingValue? Value);

/// <summary>
/// Correction proposée par un module (étape Plan) : rien n'est écrit tant que l'utilisateur ne l'a pas cochée.
/// Toutes ses écritures réussissent ensemble, ou sont annulées ensemble.
/// </summary>
public sealed record PlannedChange
{
    /// <summary>Identifiant stable, en général celui du constat corrigé (par exemple « M06.taskview »).</summary>
    public required string Id { get; init; }

    public required string ModuleId { get; init; }

    /// <summary>Libellé court de l'action (« Masquer le bouton Vue des tâches »).</summary>
    public required string Title { get; init; }

    /// <summary>Ce que fait la correction, en français clair.</summary>
    public required string Description { get; init; }

    /// <summary>Ce que l'utilisateur y gagne.</summary>
    public string? Gain { get; init; }

    /// <summary>Ce qu'il peut perdre ; <c>null</c> si aucun risque connu.</summary>
    public string? Risk { get; init; }

    /// <summary>Avertissement renforcé affiché avant de cocher la case (anti-triche, instantanés supprimés…).</summary>
    public string? Warning { get; init; }

    public ChangeEffect Effect { get; init; } = ChangeEffect.Immediate;

    /// <summary>Case pré-cochée dans l'interface (valeur recommandée). L'utilisateur garde toujours le dernier mot.</summary>
    public bool Recommended { get; init; } = true;

    /// <summary>Réglage « Avancé » : jamais inclus dans un profil standard, demande une case dédiée.</summary>
    public bool Advanced { get; init; }

    public string? Category { get; init; }

    public required IReadOnlyList<SettingWrite> Writes { get; init; }

    /// <summary>Au moins une écriture vise le profil de l'utilisateur de la session.</summary>
    public bool IsUserScoped => Writes.Any(w => w.Key.IsUserScoped);
}
