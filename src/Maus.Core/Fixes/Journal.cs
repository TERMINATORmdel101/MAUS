namespace Maus.Core.Fixes;

/// <summary>État d'une écriture journalisée.</summary>
public enum EntryState
{
    /// <summary>Valeur d'origine enregistrée, écriture pas encore faite (ou interrompue).</summary>
    Pending,

    /// <summary>Écriture faite et relue.</summary>
    Applied,

    /// <summary>Écriture refusée ou non conforme à la relecture : valeur d'origine remise.</summary>
    Failed,

    /// <summary>Valeur d'origine restaurée par « Annuler ».</summary>
    Reverted,

    /// <summary>Retour arrière non fait : la valeur a changé depuis (par l'utilisateur ou Windows).</summary>
    RevertSkipped,

    /// <summary>
    /// Correction en échec dont le retour arrière a lui aussi échoué (ou n'a pas pu être relu) : la valeur modifiée est
    /// peut-être encore en place. Jamais rangée avec les échecs ordinaires : « Annuler » reste proposé et réessaie.
    /// </summary>
    RollbackFailed,
}

public static class EntryStateExtensions
{
    /// <summary>Une valeur que « Annuler » doit encore traiter : appliquée, écriture interrompue, ou retour arrière raté.</summary>
    public static bool IsOutstanding(this EntryState state) => state is EntryState.Applied or EntryState.Pending or EntryState.RollbackFailed;
}

/// <summary>Une valeur modifiée : avant, après, et ce qui lui est arrivé.</summary>
public sealed class JournalEntry
{
    public required string ChangeId { get; init; }

    public required string ModuleId { get; init; }

    public required string ChangeTitle { get; init; }

    public required SettingKey Key { get; init; }

    /// <summary>Valeur d'origine ; <c>null</c> si elle était absente.</summary>
    public SettingValue? Before { get; init; }

    /// <summary>La clé de registre existait-elle avant ? Sinon, le retour arrière la supprime si elle est vide.</summary>
    public bool ContainerExisted { get; init; } = true;

    /// <summary>Valeur écrite par MAUS ; <c>null</c> pour une suppression.</summary>
    public SettingValue? After { get; init; }

    public EntryState State { get; set; } = EntryState.Pending;

    public DateTimeOffset? AppliedAt { get; set; }

    public DateTimeOffset? RevertedAt { get; set; }

    public string? Error { get; set; }

    /// <summary>Effet constaté par le nouvel audit (par exemple « sans effet sur votre édition »).</summary>
    public string? EffectNote { get; set; }
}

/// <summary>Point de restauration créé avant une série de corrections.</summary>
public sealed record RestorePointInfo(long SequenceNumber, string Description, DateTimeOffset? CreatedAt);

/// <summary>Une séance de corrections : tout ce qui a été modifié ensemble, et qu'« Annuler » défait ensemble.</summary>
public sealed class JournalSession
{
    public required string Id { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public string MausVersion { get; init; } = typeof(JournalSession).Assembly.GetName().Version?.ToString(3) ?? "?";

    public int WindowsBuild { get; init; }

    public RestorePointInfo? RestorePoint { get; set; }

    /// <summary>Pourquoi aucun point de restauration n'a été créé, le cas échéant.</summary>
    public string? RestorePointNote { get; set; }

    public List<JournalEntry> Entries { get; init; } = [];

    public DateTimeOffset? RevertedAt { get; set; }

    /// <summary>
    /// Au moins une valeur est encore appliquée, une écriture a été interrompue (MAUS fermé pendant la correction), ou un
    /// retour arrière a échoué : « Annuler » a peut-être quelque chose à faire.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasFailedRollback => Entries.Any(e => e.State == EntryState.RollbackFailed);

    /// <summary>Voir <see cref="EntryStateExtensions.IsOutstanding"/>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanRevert => Entries.Any(e => e.State.IsOutstanding());

    /// <summary>Identifiant lisible et triable : date, heure, puis suffixe aléatoire.</summary>
    public static string NewId(DateTimeOffset now) =>
        now.ToUniversalTime().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
}
