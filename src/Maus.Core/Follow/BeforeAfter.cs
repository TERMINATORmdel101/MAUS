using Maus.Core.Fixes;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Follow;

/// <summary>Ce que la comparaison permet de dire d'une séance.</summary>
public enum BeforeAfterVerdict
{
    /// <summary>Aucune correction de la séance ne peut toucher au démarrage : aucun chiffre.</summary>
    NotMeasurable,

    /// <summary>Trop peu de démarrages d'un côté ou de l'autre : aucun chiffre.</summary>
    NotEnough,

    /// <summary>Les deux plages se recouvrent.</summary>
    NoVisibleEffect,

    /// <summary>Plages séparées, démarrages plus courts après la séance.</summary>
    Shorter,

    /// <summary>Plages séparées, démarrages plus longs après la séance.</summary>
    Longer,
}

/// <summary>Une séance de corrections et ce que les démarrages mesurés en disent.</summary>
/// <param name="Before">Médiane et plage avant la séance ; vide sans comparaison.</param>
/// <param name="After">Médiane et plage après la séance ; vide sans comparaison.</param>
/// <param name="Caution">Plages séparées : ce qui d'autre peut expliquer l'écart.</param>
/// <param name="Note">Séance annulée, ou corrections de la séance que cette mesure ne concerne pas.</param>
public sealed record BeforeAfterRow(string Title, BeforeAfterVerdict Verdict, string? Before, string? After, string Conclusion, string? Caution, string? Note)
{
    public override string ToString() => Title;

    public bool HasFigures => Before is not null;

    public bool HasCaution => Caution is not null;

    public bool HasNote => Note is not null;
}

/// <summary>Les séances montrées, et le nombre de séances plus anciennes qui ne le sont pas.</summary>
public sealed record BeforeAfterSummary(IReadOnlyList<BeforeAfterRow> Rows, int Older);

/// <summary>
/// Avant et après, en vrai : les démarrages mesurés par Windows (carnet de santé) de part et d'autre d'une séance du journal.
/// La séance n'est jamais présentée comme la cause : si les plages se recouvrent, rien ne se voit, et cela se dit tel quel.
/// </summary>
public static class BeforeAfter
{
    /// <summary>
    /// Démarrages demandés de chaque côté. C'est un choix de MAUS, pas une norme publiée : la médiane de cinq valeurs ne bouge pas à
    /// cause de deux démarrages hors du commun (une grosse mise à jour, par exemple). Ce nombre évite de conclure sur presque rien,
    /// il ne garantit pas que la comparaison soit juste.
    /// </summary>
    public const int MinimumBootsPerSide = 5;

    /// <summary>
    /// Démarrages gardés de chaque côté, les plus proches de la séance. Choix de MAUS aussi : une plage ne peut que s'élargir avec le
    /// nombre de valeurs, et des mois d'historique finiraient par recouvrir n'importe quel écart.
    /// </summary>
    private const int BootsPerSide = 30;

    private const int SessionsShown = 10;

    private const int TitlesShown = 3;

    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services\";

    /// <summary>
    /// Liste fermée, d'après ce qui est écrit et non d'après un titre traduit : ce qui se lance à l'ouverture de session (module 12),
    /// le démarrage rapide, le type de démarrage d'un service. Agir « au prochain redémarrage » ne suffit pas à y entrer.
    /// </summary>
    public static bool CanTouchBoot(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.Equals(entry.ModuleId, "M12", StringComparison.Ordinal))
        {
            return true;
        }

        var key = entry.Key;
        return key.Kind == SettingKind.Registry
            && (string.Equals(key.Name, "HiberbootEnabled", StringComparison.OrdinalIgnoreCase)
                || (string.Equals(key.Name, "Start", StringComparison.OrdinalIgnoreCase)
                    && key.Path?.StartsWith(ServicesPath, StringComparison.OrdinalIgnoreCase) == true));
    }

    /// <summary>Séances où quelque chose a été écrit, de la plus récente à la plus ancienne. Une séance sans aucune écriture n'a rien à comparer.</summary>
    public static BeforeAfterSummary Build(IEnumerable<JournalSession> sessions, IEnumerable<DatedBootTime> boots)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(boots);

        var written = sessions
            .Where(s => s.Entries.Exists(WasWritten))
            .OrderByDescending(s => s.CreatedAt)
            .ToList();
        var measured = written.Where(s => s.Entries.Exists(e => WasWritten(e) && CanTouchBoot(e))).ToList();
        var ordered = boots.OrderBy(b => b.At).ToList();
        var rows = written.Take(SessionsShown).Select(s => Row(s, ordered, measured)).ToList();
        return new BeforeAfterSummary(rows, written.Count - rows.Count);
    }

    /// <summary>Une écriture ratée ou interrompue n'a rien changé de sûr : elle ne compte pas comme une correction faite.</summary>
    private static bool WasWritten(JournalEntry entry) => entry.State is EntryState.Applied or EntryState.Reverted or EntryState.RevertSkipped;

    private static BeforeAfterRow Row(JournalSession session, List<DatedBootTime> boots, List<JournalSession> measured)
    {
        var applied = session.Entries.Where(WasWritten).ToList();
        var touching = applied.Where(CanTouchBoot).ToList();
        if (touching.Count == 0)
        {
            var count = applied.DistinctBy(e => e.ChangeId).Count();
            return new BeforeAfterRow(
                Title(session, applied),
                BeforeAfterVerdict.NotMeasurable,
                null,
                null,
                count == 1 ? T("Cette correction ne se mesure pas ainsi.") : T("Ces corrections ne se mesurent pas ainsi."),
                null,
                null);
        }

        var putBack = touching.TrueForAll(e => e.State == EntryState.Reverted);
        DateTimeOffset? end = putBack ? PutBackAt(session, touching) : null;
        var before = boots.Where(b => b.At < session.CreatedAt).TakeLast(BootsPerSide).ToList();
        var after = boots.Where(b => b.At > session.CreatedAt && (!putBack || b.At <= end)).Take(BootsPerSide).ToList();
        var note = Note(applied, touching, putBack, end);
        var title = Title(session, touching);

        if (before.Count < MinimumBootsPerSide || after.Count < MinimumBootsPerSide)
        {
            var conclusion = putBack
                ? T("Pas assez de démarrages entre la séance et son annulation : {0} avant, {1} après, il en faut au moins {2} de chaque côté.", before.Count, after.Count, MinimumBootsPerSide)
                : T("Pas encore assez de démarrages : {0} avant la séance, {1} après, il en faut au moins {2} de chaque côté.", before.Count, after.Count, MinimumBootsPerSide);
            return new BeforeAfterRow(title, BeforeAfterVerdict.NotEnough, null, null, conclusion, null, note);
        }

        var was = before.Select(b => b.Seconds).ToList();
        var now = after.Select(b => b.Seconds).ToList();
        var beforeLine = T("Avant : {0} s en général, de {1} à {2} s, sur {3} démarrages", Number(HealthLogSummary.Median(was)), Number(was.Min()), Number(was.Max()), was.Count);
        var afterLine = T("Après : {0} s en général, de {1} à {2} s, sur {3} démarrages", Number(HealthLogSummary.Median(now)), Number(now.Min()), Number(now.Max()), now.Count);
        if (was.Max() >= now.Min() && now.Max() >= was.Min())
        {
            return new BeforeAfterRow(title, BeforeAfterVerdict.NoVisibleEffect, beforeLine, afterLine, T("Aucun effet visible sur le temps de démarrage : les deux séries se chevauchent."), null, note);
        }

        var (from, to) = (before[0].At, after[^1].At);
        var caution = measured.Exists(s => !ReferenceEquals(s, session) && s.CreatedAt >= from && s.CreatedAt <= to)
            ? T("Autre chose peut expliquer cet écart : une autre séance de corrections du démarrage dans la même période, une mise à jour de Windows, un pilote ou un programme installé.")
            : T("Autre chose peut expliquer cet écart : une mise à jour de Windows, un pilote, un programme installé ou le démarrage rapide.");
        return now.Max() < was.Min()
            ? new BeforeAfterRow(title, BeforeAfterVerdict.Shorter, beforeLine, afterLine, T("Démarrages plus courts après la séance : les deux séries ne se chevauchent pas. Un lien avec cette correction est probable, pas certain."), caution, note)
            : new BeforeAfterRow(title, BeforeAfterVerdict.Longer, beforeLine, afterLine, T("Démarrages plus longs après la séance : les deux séries ne se chevauchent pas. Un lien avec cette correction est probable, pas certain."), caution, note);
    }

    /// <summary>Tout est remis : après cette date, les démarrages ne disent plus rien de la correction. Date absente : aucun démarrage ne compte après la séance.</summary>
    private static DateTimeOffset PutBackAt(JournalSession session, List<JournalEntry> touching) =>
        touching.Max(e => e.RevertedAt) ?? session.RevertedAt ?? session.CreatedAt;

    private static string? Note(List<JournalEntry> applied, List<JournalEntry> touching, bool putBack, DateTimeOffset? end)
    {
        var measuredIds = touching.Select(e => e.ChangeId).ToHashSet(StringComparer.Ordinal);
        var others = applied.Where(e => !measuredIds.Contains(e.ChangeId)).DistinctBy(e => e.ChangeId).Count();
        var parts = new List<string>();
        if (putBack && end is { } at)
        {
            parts.Add(T("Annulée le {0} : seuls les démarrages d'avant l'annulation comptent.", Day(at)));
        }

        if (others > 0)
        {
            parts.Add(Plural(others, T("{0} autre correction de cette séance ne se mesure pas ainsi."), T("{0} autres corrections de cette séance ne se mesurent pas ainsi.")));
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static string Title(JournalSession session, List<JournalEntry> entries)
    {
        var titles = entries.DistinctBy(e => e.ChangeId).Select(e => e.ChangeTitle).ToList();
        var shown = string.Join(", ", titles.Take(TitlesShown));
        var named = titles.Count > TitlesShown ? T("{0} et {1}", shown, Plural(titles.Count - TitlesShown, T("{0} autre"), T("{0} autres"))) : shown;
        return T("Séance du {0} : {1}", Day(session.CreatedAt), named);
    }

    private static string Number(double value) => value.ToString("0.#", Culture);

    private static string Day(DateTimeOffset at) => at.LocalDateTime.ToString("d", Culture);
}
