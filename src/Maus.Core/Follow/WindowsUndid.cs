using Maus.Core.Fixes;
using Maus.Core.Reporting;

namespace Maus.Core.Follow;

/// <summary>Ce que le dernier audit dit d'une correction encore appliquée d'après le journal.</summary>
public enum FixHoldState
{
    /// <summary>Le dernier audit la voit en place.</summary>
    InPlace,

    /// <summary>Le dernier audit ne la voit plus en place : on ne sait pas qui l'a remise (ou si elle a pris), on ne désigne personne.</summary>
    NotInPlace,

    /// <summary>Le dernier audit ne permet pas de le dire : jamais présenté comme sain.</summary>
    NotChecked,
}

/// <summary>Pourquoi une correction n'a pas pu être contrôlée.</summary>
public enum NotCheckedReason
{
    None,

    /// <summary>Aucun audit gardé sur ce PC.</summary>
    NoAudit,

    /// <summary>Corrigée après le dernier audit gardé : l'audit de vérification a échoué ou n'a pas eu lieu.</summary>
    AppliedSinceAudit,

    /// <summary>Le module n'était pas dans le dernier audit (non choisi, ou en erreur).</summary>
    ModuleNotRead,

    /// <summary>Le dernier audit a vu ce réglage comme indéterminé.</summary>
    ValueNotRead,

    /// <summary>Le résumé gardé ne dit rien de cette correction, et il n'y a pas de plan depuis l'ouverture : il faut un nouvel audit.</summary>
    NeedsNewAudit,

    /// <summary>Le réglage est dans le profil d'un autre compte : l'audit de ce compte-ci ne le lit pas.</summary>
    OtherAccount,

    /// <summary>Le dernier audit a été fait avec un autre compte administrateur : il n'a pas lu le profil de ce compte-ci.</summary>
    AuditOfOtherAccount,
}

/// <summary>Une correction encore appliquée, et ce que le dernier audit en dit.</summary>
/// <param name="EffectNote">Note gardée au moment de la correction (« au prochain redémarrage », « sans effet sur votre édition »).</param>
public sealed record FixHold(string ChangeId, string Title, DateTimeOffset AppliedOn, FixHoldState State, NotCheckedReason Reason = NotCheckedReason.None, string? EffectNote = null);

/// <summary>Les corrections encore appliquées, avec la date et la version de Windows du dernier contrôle.</summary>
public sealed record FixHoldReport(IReadOnlyList<FixHold> Fixes, DateTimeOffset? CheckedAt, string? AuditBuild, string? CurrentBuild)
{
    /// <summary>Les deux versions sont connues : sinon, aucune comparaison n'est possible, et rien n'est affirmé.</summary>
    public bool KnowsBothBuilds => !string.IsNullOrWhiteSpace(AuditBuild) && !string.IsNullOrWhiteSpace(CurrentBuild);

    public bool BuildChanged => KnowsBothBuilds && !string.Equals(AuditBuild, CurrentBuild, StringComparison.Ordinal);

    public int Count(FixHoldState state) => Fixes.Count(f => f.State == state);
}

/// <summary>
/// « Windows a-t-il défait mes réglages ? » : compare le journal au dernier audit gardé, sans rien relire dans Windows et sans
/// rien relancer. Seul l'état se déduit ici : qui a remis la valeur (Windows, un programme, l'utilisateur), personne ne le sait.
/// </summary>
public static class WindowsUndid
{
    /// <summary>Le dernier audit lancé à la main : comme ailleurs, l'audit de la semaine ne sert pas de base de comparaison.</summary>
    public static AuditSnapshot? LastAudit(IEnumerable<AuditSnapshot> kept)
    {
        ArgumentNullException.ThrowIfNull(kept);
        return kept.Where(s => !s.Weekly).MaxBy(s => s.At);
    }

    /// <param name="sessions">Séances du journal.</param>
    /// <param name="lastAudit">Dernier audit gardé, ou <c>null</c>.</param>
    /// <param name="proposedAgain">Corrections que le plan de cet audit propose ; <c>null</c> si aucun plan n'est connu pour lui (audit d'une ouverture précédente).</param>
    /// <param name="accountSid">Compte de cette session Windows : le journal est commun à tout le PC.</param>
    /// <param name="currentBuild">Version actuelle de Windows, ou <c>null</c> si elle n'a pas pu être lue.</param>
    public static FixHoldReport Compare(IReadOnlyList<JournalSession> sessions, AuditSnapshot? lastAudit, IReadOnlySet<string>? proposedAgain, string? accountSid, string? currentBuild)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var fixes = sessions
            .SelectMany(s => s.Entries.Where(e => e.State == EntryState.Applied).Select(e => new Written(s, e)))
            .GroupBy(w => w.Entry.ChangeId, StringComparer.Ordinal)
            .Select(g => Judge([.. g], lastAudit, proposedAgain, accountSid))
            .OrderBy(f => f.State switch { FixHoldState.NotInPlace => 0, FixHoldState.NotChecked => 1, _ => 2 })
            .ThenBy(f => f.Title, StringComparer.CurrentCulture)
            .ToList();
        return new(fixes, lastAudit?.At, lastAudit?.Build, currentBuild);
    }

    private static FixHold Judge(List<Written> written, AuditSnapshot? lastAudit, IReadOnlySet<string>? proposedAgain, string? accountSid)
    {
        // Faite par ce compte et par un autre : seule la sienne se lit ici.
        var own = written.Where(w => !w.Session.IsInAnotherProfile(w.Entry, accountSid)).ToList();
        var kept = own.Count > 0 ? own : written;
        var latest = kept.MaxBy(w => w.AppliedOn)!;
        var note = kept.Where(w => ReferenceEquals(w.Session, latest.Session)).Select(w => w.Entry.EffectNote).FirstOrDefault(n => n is not null);
        var (state, reason) = own.Count == 0
            ? (FixHoldState.NotChecked, NotCheckedReason.OtherAccount)
            : StateOf(latest, lastAudit, proposedAgain);
        return new(latest.Entry.ChangeId, latest.Entry.ChangeTitle, latest.AppliedOn, state, reason, note);
    }

    private static (FixHoldState State, NotCheckedReason Reason) StateOf(Written latest, AuditSnapshot? lastAudit, IReadOnlySet<string>? proposedAgain)
    {
        if (lastAudit is null)
        {
            return (FixHoldState.NotChecked, NotCheckedReason.NoAudit);
        }

        if (latest.AppliedOn > lastAudit.At)
        {
            return (FixHoldState.NotChecked, NotCheckedReason.AppliedSinceAudit);
        }

        // Un réglage du profil se juge d'après le profil de ce compte, jamais d'après celui de l'administrateur qui a validé.
        if (lastAudit.OtherAccount && latest.Entry.Key.IsUserScoped)
        {
            return (FixHoldState.NotChecked, NotCheckedReason.AuditOfOtherAccount);
        }

        var id = latest.Entry.ChangeId;
        if (proposedAgain?.Contains(id) == true)
        {
            return (FixHoldState.NotInPlace, NotCheckedReason.None);
        }

        // Module absent de cet audit : rien n'y a été lu, ce n'est pas « en place ».
        if (!lastAudit.Findings.Any(f => string.Equals(f.ModuleId, latest.Entry.ModuleId, StringComparison.Ordinal)))
        {
            return (FixHoldState.NotChecked, NotCheckedReason.ModuleNotRead);
        }

        // Une correction porte le plus souvent l'identifiant de son constat (voir FixVerification).
        if (lastAudit.Findings.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal)) is { } finding)
        {
            return finding.Status switch
            {
                FindingStatus.Unknown => (FixHoldState.NotChecked, NotCheckedReason.ValueNotRead),
                FindingStatus.Ok or FindingStatus.Info => (FixHoldState.InPlace, NotCheckedReason.None),
                _ => (FixHoldState.NotInPlace, NotCheckedReason.None),
            };
        }

        // Sans constat du même nom, seul le plan répond : module lu, et correction non proposée de nouveau.
        return proposedAgain is null
            ? (FixHoldState.NotChecked, NotCheckedReason.NeedsNewAudit)
            : (FixHoldState.InPlace, NotCheckedReason.None);
    }

    private sealed record Written(JournalSession Session, JournalEntry Entry)
    {
        public DateTimeOffset AppliedOn => Entry.AppliedAt ?? Session.CreatedAt;
    }
}
