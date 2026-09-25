namespace Maus.Core.Fixes;

/// <summary>Choix de l'utilisateur pour la séance de corrections.</summary>
public sealed record ApplyOptions
{
    /// <summary>Créer un point de restauration vérifié avant la première écriture (recommandé, pré-coché).</summary>
    public bool CreateRestorePoint { get; init; } = true;

    /// <summary>Accord de l'utilisateur pour activer la protection du système si elle est coupée.</summary>
    public bool EnableProtectionIfNeeded { get; init; }

    /// <summary>Accord explicite pour continuer si le point de restauration échoue (le journal permet quand même d'annuler).</summary>
    public bool ProceedWithoutRestorePoint { get; init; }

    public string RestorePointDescription { get; init; } = "MAUS : avant corrections";
}

public enum ChangeStatus
{
    /// <summary>Écrit et relu.</summary>
    Applied,

    /// <summary>Rien à faire (déjà en place) ou non autorisé dans ce contexte.</summary>
    Skipped,

    /// <summary>Refusé par Windows ou non conforme à la relecture : valeur d'origine remise.</summary>
    Failed,
}

public sealed record ChangeOutcome(string ChangeId, string Title, ChangeStatus Status, string Message, ChangeEffect Effect = ChangeEffect.Immediate);

public sealed record ApplyResult(
    JournalSession? Session,
    IReadOnlyList<ChangeOutcome> Changes,
    RestorePointOutcome? RestorePoint,
    string? BlockedReason = null)
{
    public bool Blocked => BlockedReason is not null;

    public int AppliedCount => Changes.Count(c => c.Status == ChangeStatus.Applied);

    /// <summary>Action la plus lourde demandée par les corrections appliquées (redémarrage, déconnexion…).</summary>
    public ChangeEffect RequiredEffect => Changes
        .Where(c => c.Status == ChangeStatus.Applied)
        .Select(c => c.Effect)
        .DefaultIfEmpty(ChangeEffect.Immediate)
        .Max();

    public static ApplyResult Block(string reason, RestorePointOutcome? restorePoint = null) => new(null, [], restorePoint, reason);
}

public enum RevertStatus
{
    Reverted,

    /// <summary>La valeur a changé depuis la correction : laissée telle quelle.</summary>
    ChangedSince,

    Failed,

    /// <summary>Non autorisé dans ce contexte (compte élevé différent…).</summary>
    Skipped,
}

public sealed record RevertOutcome(string ChangeId, string Title, string Setting, RevertStatus Status, string Message);

public sealed record RevertResult(JournalSession? Session, IReadOnlyList<RevertOutcome> Entries, string? Error = null)
{
    public bool Completed => Error is null && Entries.All(e => e.Status == RevertStatus.Reverted);
}
