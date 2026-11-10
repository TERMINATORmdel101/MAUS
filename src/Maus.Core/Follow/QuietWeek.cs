using Maus.Core.Engine.Broker;
using Maus.Core.Platform;
using Maus.Core.Reporting;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Follow;

/// <summary>Ce que la carte peut dire honnêtement.</summary>
public enum QuietWeekState
{
    /// <summary>Audit de la semaine désactivé.</summary>
    Off,

    /// <summary>État de la tâche non lu, et aucun résumé à montrer.</summary>
    Unread,

    /// <summary>La tâche tourne pour un autre compte : ses résumés sont dans le profil de cet autre compte.</summary>
    OtherAccount,

    /// <summary>Activé, aucun résumé lisible pour l'instant.</summary>
    Waiting,

    /// <summary>Aucun audit depuis la dernière visite.</summary>
    NoneSince,

    /// <summary>Un seul audit et rien avant lui : impossible de dire ce qui est nouveau.</summary>
    NotComparable,

    /// <summary>Audits comparés, rien de nouveau.</summary>
    Quiet,

    /// <summary>Audits comparés, au moins un constat apparu ou aggravé.</summary>
    News,
}

/// <summary>Un constat apparu ou aggravé entre deux audits de la semaine, daté de l'audit qui l'a vu le premier.</summary>
public sealed record QuietWeekPoint(string Title, DateTimeOffset At, bool Worse)
{
    public override string ToString() => Title;

    public string Text => Worse
        ? T("Aggravé le {0:d} : {1}", At.LocalDateTime, T(Title))
        : T("Apparu le {0:d} : {1}", At.LocalDateTime, T(Title));
}

/// <param name="Audits">Audits de la semaine gardés depuis la dernière visite (tous ceux gardés à la première visite).</param>
public sealed record QuietWeekSummary(QuietWeekState State, string Headline, int Audits, IReadOnlyList<QuietWeekPoint> Points, IReadOnlyList<string> Notes)
{
    /// <summary>Lignes de constats à afficher : les premières seulement, la suite est comptée dans <see cref="Notes"/>.</summary>
    public const int ShownPoints = 10;

    /// <summary>Des audits ont vraiment été montrés : la visite peut servir de repère à la suivante.</summary>
    public bool ShowsAudits => State is QuietWeekState.NoneSince or QuietWeekState.NotComparable or QuietWeekState.Quiet or QuietWeekState.News;
}

/// <summary>
/// « Rien de nouveau cette semaine » : le bilan des audits de la semaine depuis la dernière visite, d'après les résumés gardés.
/// Ils ne se comparent qu'entre eux, à droits égaux, jamais aux audits lancés à la main ; un état non lu n'est ni bon ni mauvais.
/// </summary>
public static class QuietWeek
{
    /// <param name="kept">Résumés gardés, audits lancés à la main compris (ignorés ici).</param>
    /// <param name="lastVisit">Dernière fois que cette carte a été vue ; <c>null</c> à la première visite. Une date à venir ou impossible est ignorée.</param>
    /// <param name="schedule">État de la tâche ; <c>null</c> s'il n'a pas pu être lu.</param>
    public static QuietWeekSummary Build(IEnumerable<AuditSnapshot> kept, DateTimeOffset? lastVisit, ScheduleState? schedule, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(kept);
        if (schedule is { Day: null })
        {
            return Without(QuietWeekState.Off, T("L'audit de la semaine n'est pas activé : s'il l'était, cette carte dirait ce qui a changé d'une semaine à l'autre."));
        }

        if (schedule is { OfAnotherAccount: true })
        {
            return Without(QuietWeekState.OtherAccount, T("L'audit de la semaine de ce PC tourne pour un autre compte : ses résumés ne sont pas gardés ici."));
        }

        var weekly = kept.Where(s => s.Weekly && s.At <= now).OrderBy(s => s.At).ToList();
        if (weekly.Count == 0)
        {
            return schedule is null
                ? Without(QuietWeekState.Unread, T("L'état de l'audit de la semaine n'a pas pu être lu pour l'instant : cette carte ne peut rien en dire."))
                : Without(QuietWeekState.Waiting, T("L'audit de la semaine est activé, et aucun résumé lisible n'est gardé pour l'instant. Le prochain apparaîtra ici, après son passage."));
        }

        var since = lastVisit is { } visited && visited <= now && StoredValues.IsPossibleDate(visited) ? lastVisit : null;
        var recent = since is { } date ? weekly.FindAll(s => s.At > date) : weekly;
        var latest = weekly[^1];
        if (recent.Count == 0)
        {
            return Without(QuietWeekState.NoneSince, T("Aucun audit de la semaine depuis votre dernière visite du {0:d}. Le dernier date du {1:d}.", since!.Value.LocalDateTime, latest.At.LocalDateTime));
        }

        // Sans droits, un audit lit moins : le comparer à un audit avec droits ferait croire à des changements.
        var older = since is { } visit ? weekly.FindLast(s => s.At <= visit && s.HasScore == latest.HasScore) : null;
        var chain = recent.Where(s => s.HasScore == latest.HasScore).ToList();
        if (older is not null)
        {
            chain.Insert(0, older);
        }

        var count = CountText(recent.Count, since is not null, since is not null && recent.Count == weekly.Count && weekly.Count >= AuditSnapshotStore.KeepWeekly);
        if (chain.Count < 2)
        {
            return new(QuietWeekState.NotComparable, $"{count}, {T("pas encore de point de comparaison.")}", recent.Count, [], [LastAudit(latest)]);
        }

        var points = Compare(chain);
        var notes = new List<string> { LastAudit(latest) };
        if (older is null)
        {
            notes.Add(T("La comparaison part de l'audit du {0:d}, le plus ancien gardé : MAUS en garde {1} au plus.", chain[0].At.LocalDateTime, AuditSnapshotStore.KeepWeekly));
        }

        if (!latest.HasScore)
        {
            notes.Add(T("Sans droits administrateur, l'audit de la semaine lit moins qu'un audit complet : ce qu'il n'a pas pu lire n'est pas compté, ni en bien ni en mal."));
        }

        if (points.Count > QuietWeekSummary.ShownPoints)
        {
            notes.Add(Plural(points.Count - QuietWeekSummary.ShownPoints, T("Et {0} autre."), T("Et {0} autres.")));
        }

        // Un contrôle ajouté par une nouvelle version de MAUS n'est pas un changement du PC : AuditComparison ne le compte pas.
        if (chain.Zip(chain.Skip(1)).Any(pair => !AuditComparison.SameMausVersion(pair.First, pair.Second)))
        {
            notes.Add(T("MAUS a changé de version entre ces audits : ce qu'un nouveau contrôle a pu faire apparaître n'est pas compté."));
        }

        notes.Add(T("Seuls les audits allés au bout sont gardés et comptés."));
        return new(points.Count == 0 ? QuietWeekState.Quiet : QuietWeekState.News, $"{count}, {NewsText(points.Count)}{ScopeText(latest, recent.Count)}.", recent.Count, [.. points.Take(QuietWeekSummary.ShownPoints)], notes);
    }

    /// <summary>D'audit en audit : ce qui est apparu puis reparti entre deux visites est dit aussi, à sa date. Un constat marqué « voulu » n'est pas nouveau.</summary>
    private static List<QuietWeekPoint> Compare(List<AuditSnapshot> chain)
    {
        var seen = new HashSet<(string, string)>();
        var points = new List<QuietWeekPoint>();
        for (var i = 1; i < chain.Count; i++)
        {
            var notWanted = chain[i] with { Findings = [.. chain[i].Findings.Where(f => !f.Acknowledged)] };
            foreach (var change in AuditComparison.Compare(chain[i - 1], notWanted))
            {
                if (change.Kind is AuditChangeKind.New or AuditChangeKind.Worse && seen.Add((change.ModuleId, change.Title)))
                {
                    points.Add(new(change.Title, chain[i].At, change.Kind == AuditChangeKind.Worse));
                }
            }
        }

        return points;
    }

    private static QuietWeekSummary Without(QuietWeekState state, string headline) => new(state, headline, 0, [], []);

    private static string LastAudit(AuditSnapshot latest) => T("Dernier audit le {0:d}.", latest.At.LocalDateTime);

    private static string CountText(int audits, bool hasVisit, bool atLeast)
    {
        if (!hasVisit)
        {
            return audits == 1 ? T("1 audit de la semaine gardé") : T("{0} audits de la semaine gardés", audits);
        }

        if (atLeast)
        {
            return T("Au moins {0} audits depuis votre dernière visite", audits);
        }

        return audits == 1 ? T("1 audit depuis votre dernière visite") : T("{0} audits depuis votre dernière visite", audits);
    }

    private static string NewsText(int points) => points switch
    {
        0 => T("rien de nouveau"),
        1 => T("1 constat nouveau ou aggravé"),
        _ => T("{0} constats nouveaux ou aggravés", points),
    };

    private static string ScopeText(AuditSnapshot latest, int audits)
    {
        if (latest.HasScore)
        {
            return string.Empty;
        }

        return " " + (audits == 1 ? T("sur ce qu'il a pu lire") : T("sur ce qu'ils ont pu lire"));
    }
}
