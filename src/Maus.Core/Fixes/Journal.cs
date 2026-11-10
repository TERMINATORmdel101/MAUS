using static Maus.Core.Localization.Texts;

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
    public static bool IsOutstanding(this EntryState state) => state is EntryState.Applied or EntryState.RevertSkipped or EntryState.Pending or EntryState.RollbackFailed;
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

    /// <summary>SID du compte qui a fait la séance. Absent d'une séance écrite avant la 0.8.6 : elle s'annule comme avant.</summary>
    public string? UserSid { get; init; }

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

    /// <summary>
    /// Séance faite par un autre compte du PC : le journal est commun à tout le PC. Séance sans compte noté, ou compte actuel
    /// illisible : on ne peut pas le savoir, faux.
    /// </summary>
    public bool IsFromAnotherAccount(string? currentSid) =>
        !string.IsNullOrEmpty(UserSid) && !string.IsNullOrEmpty(currentSid) && !string.Equals(UserSid, currentSid, StringComparison.OrdinalIgnoreCase);

    /// <summary>Cette valeur est dans le profil d'un autre compte : seul ce compte peut la remettre.</summary>
    public bool IsInAnotherProfile(JournalEntry entry, string? currentSid)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Key.IsUserScoped && IsFromAnotherAccount(currentSid);
    }

    /// <summary>« Annuler » a quelque chose à faire pour ce compte (voir <see cref="CanRevert"/> et <see cref="IsInAnotherProfile"/>).</summary>
    public bool CanRevertAs(string? currentSid) => Entries.Any(e => e.State.IsOutstanding() && !IsInAnotherProfile(e, currentSid));

    /// <summary>Dit avant d'annuler la séance d'un autre compte.</summary>
    public static string AnotherAccountNotice =>
        T("Cette séance a été faite par un autre compte de ce PC. Seuls les réglages communs à tout le PC seront remis : ceux de son profil ne peuvent être annulés que depuis ce compte.");

    /// <summary>État de la séance, tel qu'il s'affiche dans l'historique et dans <c>maus --journal</c>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string StateLabel
    {
        get
        {
            if (HasFailedRollback)
            {
                return T("à reprendre : un retour arrière a échoué");
            }

            if (RevertedAt is not null)
            {
                return T("annulée");
            }

            return CanRevert ? T("active") : T("rien à annuler");
        }
    }

    /// <summary>Identifiant lisible et triable : date, heure, puis suffixe aléatoire.</summary>
    public static string NewId(DateTimeOffset now) =>
        now.ToUniversalTime().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
}
