using System.Text.Json;
using System.Text.Json.Serialization;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Reporting;

/// <summary>Un constat tel qu'il était lors d'un audit passé (l'état et la valeur lue, sans les explications).</summary>
/// <param name="Acknowledged">Marqué « voulu » à cet audit : <paramref name="Status"/> reste son état d'origine, pour la comparaison, mais la carte « Dernier audit » ne le signale pas.</param>
/// <param name="Enabled">Entrée de démarrage : activée ou désactivée ; <c>null</c> pour un autre constat, ou un résumé écrit avant ce champ.</param>
public sealed record SnapshotFinding(string Id, string ModuleId, string Title, FindingStatus Status, [property: JsonIgnore] string? Current = null, bool Acknowledged = false, bool? Enabled = null);

/// <summary>Résumé d'un audit gardé sur ce PC, pour le comparer aux suivants et le retrouver à l'ouverture (0.8.6).</summary>
/// <param name="Score">Score sur 100, ou <see cref="NoScore"/> pour un audit de la semaine sans droits.</param>
/// <param name="Weekly">Audit de la semaine, sans réseau : il se montre, mais ne se compare pas aux audits lancés à la main.</param>
/// <param name="MausVersion">Version de MAUS qui a fait l'audit : un contrôle ajouté par une version suivante n'est pas un changement du PC. <c>null</c> : résumé écrit avant ce champ.</param>
/// <param name="Tasks">Tâches tierces lancées à l'ouverture de session (voir <see cref="Follow.WhatChanged.KeptTasks"/>). <c>null</c> : non lues, ou résumé écrit avant ce champ.</param>
/// <param name="Drivers">Pilotes hors Microsoft (voir <see cref="Follow.WhatChanged.KeptDrivers"/>). <c>null</c> : non lus, ou résumé écrit avant ce champ. Si ce que ces deux listes gardent change un jour, changer le nom du champ : l'ancienne liste ne se comparerait plus à la nouvelle.</param>
/// <param name="OtherAccount">Fait avec un autre compte administrateur : le registre et les dossiers « de l'utilisateur » lus sont les siens, une absence n'y prouve rien.</param>
public sealed record AuditSnapshot(DateTimeOffset At, string Build, int Score, IReadOnlyList<SnapshotFinding> Findings, bool IsPartial = false, bool Weekly = false, string? MausVersion = null, IReadOnlyList<FollowedItem>? Tasks = null, IReadOnlyList<FollowedItem>? Drivers = null, bool OtherAccount = false)
{
    /// <summary>Pas de score gardé : des contrôles n'ont pas été lus, le chiffre paraîtrait meilleur que la réalité.</summary>
    public const int NoScore = -1;

    [JsonIgnore]
    public bool HasScore => Score >= 0;

    /// <summary>Audit de la semaine sans droits : des contrôles restent non lus.</summary>
    [JsonIgnore]
    public bool WithoutRights => Weekly && !HasScore;

    /// <summary>Ce que l'audit a pu lire : lancé à la main, de la semaine avec droits (sans réseau), de la semaine sans droits. Une absence ne vaut que face à un audit qui ne lit pas plus.</summary>
    [JsonIgnore]
    public int ReadingLevel => !Weekly ? 2 : HasScore ? 1 : 0;

    public static AuditSnapshot From(IReadOnlyCollection<ModuleResult> results, string build, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(results);
        return new(at, build, HealthScore.Compute(results),
            [.. results.SelectMany(r => r.Findings.Select(f => new SnapshotFinding(f.Id, r.ModuleId, f.Title, f.AcknowledgedFrom ?? f.Status, f.Current, f.AcknowledgedFrom is not null, f.Enabled)))],
            HealthScore.Explain([.. results]).IsPartial,
            MausVersion: AppVersion.Display,
            Tasks: Followed(results, Follow.WhatChanged.TasksFindingId),
            Drivers: Followed(results, Follow.WhatChanged.DriversFindingId));
    }

    private static IReadOnlyList<FollowedItem>? Followed(IReadOnlyCollection<ModuleResult> results, string findingId) =>
        results.SelectMany(r => r.Findings).FirstOrDefault(f => f.Id == findingId)?.Followed;

    /// <summary>
    /// Résumé de l'audit de la semaine ; <c>null</c> pour un score partiel (module en erreur ou en délai dépassé). Sans droits
    /// (le cas normal : la tâche n'en a pas), des contrôles restent gris : le résumé garde les constats lus, pas de chiffre.
    /// </summary>
    public static AuditSnapshot? ForWeeklyAudit(IReadOnlyCollection<ModuleResult> results, string build, DateTimeOffset at, bool elevated)
    {
        var summary = From(results, build, at);
        return summary.IsPartial ? null : summary with { Score = elevated ? summary.Score : NoScore, Weekly = true };
    }
}

/// <summary>Sens d'un changement entre deux audits.</summary>
public enum AuditChangeKind
{
    Worse,
    Better,
    New,
    Gone,

    /// <summary>
    /// Présent dans un seul des deux audits, sans qu'on puisse conclure : MAUS a changé de version (c'est peut-être un contrôle
    /// ajouté ou retiré), ou l'audit qui ne le montre pas lit moins que l'autre. Ni une aggravation ni un progrès.
    /// </summary>
    NotComparable,
}

/// <summary>Un constat dont l'état a changé entre deux audits.</summary>
public sealed record AuditChange(AuditChangeKind Kind, string Title, string ModuleId, FindingStatus? Before, FindingStatus? After, string? CurrentBefore = null, string? CurrentAfter = null, string? FindingId = null);

/// <summary>
/// Ce qui a changé entre deux audits : seulement les changements d'état, car une valeur lue (température, espace libre) bouge
/// tout le temps. Un module absent de l'un des deux audits (non choisi, en erreur) n'est pas comparé : rien n'y a été lu, ce
/// n'est ni une amélioration ni une aggravation. Un état indéterminé ne se compare pas non plus. Un constat présent d'un seul
/// côté n'est dit « nouveau » ou « disparu » que si la même version de MAUS a fait les deux audits, avec le même compte, et que
/// l'audit qui ne le montre pas lit au moins autant que l'autre : sinon, <see cref="AuditChangeKind.NotComparable"/>.
/// </summary>
public static class AuditComparison
{
    /// <summary>Même version de MAUS, connue des deux côtés : un résumé écrit avant ce champ ne prouve rien.</summary>
    public static bool SameMausVersion(AuditSnapshot first, AuditSnapshot second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return first.MausVersion is { } version && string.Equals(version, second.MausVersion, StringComparison.Ordinal);
    }

    public static IReadOnlyList<AuditChange> Compare(AuditSnapshot older, AuditSnapshot newer)
    {
        ArgumentNullException.ThrowIfNull(older);
        ArgumentNullException.ThrowIfNull(newer);

        var before = older.Findings.DistinctBy(f => f.Id, StringComparer.Ordinal).ToDictionary(f => f.Id, StringComparer.Ordinal);
        var after = newer.Findings.DistinctBy(f => f.Id, StringComparer.Ordinal).ToDictionary(f => f.Id, StringComparer.Ordinal);
        var modulesBefore = older.Findings.Select(f => f.ModuleId).ToHashSet(StringComparer.Ordinal);
        var modulesAfter = newer.Findings.Select(f => f.ModuleId).ToHashSet(StringComparer.Ordinal);
        var sameMaus = SameMausVersion(older, newer) && older.OtherAccount == newer.OtherAccount;
        var canSayNew = sameMaus && older.ReadingLevel >= newer.ReadingLevel;
        var canSayGone = sameMaus && newer.ReadingLevel >= older.ReadingLevel;
        var changes = new List<AuditChange>();
        foreach (var (id, now) in after)
        {
            if (before.TryGetValue(id, out var then))
            {
                // Conforme et Info ne demandent rien : passer de l'un à l'autre n'est ni une aggravation ni un progrès.
                if (now.Status != FindingStatus.Unknown && then.Status != FindingStatus.Unknown && now.Status.Rank() != then.Status.Rank()
                    && (NeedsAction(now.Status) || NeedsAction(then.Status)))
                {
                    changes.Add(new(now.Status.Rank() > then.Status.Rank() ? AuditChangeKind.Worse : AuditChangeKind.Better, now.Title, now.ModuleId, then.Status, now.Status, then.Current, now.Current, id));
                }
            }
            else if (modulesBefore.Contains(now.ModuleId) && NeedsAction(now.Status))
            {
                changes.Add(new(canSayNew ? AuditChangeKind.New : AuditChangeKind.NotComparable, now.Title, now.ModuleId, null, now.Status, null, now.Current, id));
            }
        }

        foreach (var (id, then) in before)
        {
            if (!after.ContainsKey(id) && modulesAfter.Contains(then.ModuleId) && NeedsAction(then.Status))
            {
                changes.Add(new(canSayGone ? AuditChangeKind.Gone : AuditChangeKind.NotComparable, then.Title, then.ModuleId, then.Status, null, then.Current, null, id));
            }
        }

        return [.. changes.OrderBy(c => c.Kind is AuditChangeKind.Worse or AuditChangeKind.New ? 0 : 1)
            .ThenByDescending(c => (c.After ?? c.Before)!.Value.Rank())
            .ThenBy(c => c.Title, StringComparer.CurrentCulture)];
    }

    private static bool NeedsAction(FindingStatus status) => status is FindingStatus.Problem or FindingStatus.Warning or FindingStatus.Improvable;
}

/// <summary>Les derniers audits, dans le profil de l'utilisateur : rien n'est envoyé, et rien n'y sert à écrire dans Windows.</summary>
public sealed class AuditSnapshotStore(string path, Action<TimeSpan>? wait = null)
{
    private const int Keep = 12;

    private const int LongestText = 500;

    /// <summary>Constats lus au plus dans un résumé : bien plus que ce qu'un audit produit, un fichier retouché ne fige pas la page.</summary>
    private const int MostFindings = 2000;

    /// <summary>Audits de la semaine gardés, en plus : ils ne doivent pas pousser dehors les audits à comparer.</summary>
    public const int KeepWeekly = 4;

    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly JsonHistoryFile<AuditSnapshot> _file = new(
        path,
        Options,
        Keep,
        s => s.At,
        _ => T("Les audits gardés ({0}) sont momentanément illisibles : ils ne sont pas réécrits, pour ne rien perdre.", path),
        wait,

        // JSON valide mais vidé de son contenu : traité comme un fichier illisible.
        list => !list.Exists(s => s?.Findings is null || s.Findings.Any(f => f?.Id is null || f.ModuleId is null || !Enum.IsDefined(f.Status))),

        // Les audits de la semaine sont comptés à part : ils ne poussent pas dehors les audits à comparer.
        all => all.Where(s => !s.Weekly).TakeLast(Keep).Concat(all.Where(s => s.Weekly).TakeLast(KeepWeekly)).OrderBy(s => s.At),
        Clean);

    public static AuditSnapshotStore CreateDefault() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MAUS", "audit-snapshots.json"));

    /// <summary>Audits gardés, du plus ancien au plus récent ; vide si le fichier est absent ou illisible.</summary>
    public IReadOnlyList<AuditSnapshot> Load() => _file.Load();

    /// <exception cref="IOException">Fichier momentanément illisible (laissé intact) ou écriture impossible.</exception>
    /// <exception cref="UnauthorizedAccessException">Écriture refusée.</exception>
    public IReadOnlyList<AuditSnapshot> Add(AuditSnapshot snapshot) => _file.Add(snapshot);

    /// <summary>
    /// Fichier modifiable par un programme du même compte. Un audit à une date impossible n'est pas montré : daté de 2090, il resterait
    /// « le dernier audit » pour toujours. Un score impossible n'est pas un score, et seul un audit de la semaine peut ne pas en avoir ;
    /// un titre vidé reprend l'identifiant du constat.
    /// </summary>
    private static AuditSnapshot? Clean(AuditSnapshot snapshot) => !StoredValues.IsPastDate(snapshot.At) || (!snapshot.Weekly && snapshot.Score is < 0 or > 100) ? null : snapshot with
    {
        Build = StoredValues.Text(snapshot.Build, LongestText),
        Score = snapshot.Score is >= 0 and <= 100 ? snapshot.Score : AuditSnapshot.NoScore,
        Findings = [.. snapshot.Findings.Take(MostFindings).Select(f => f with { Title = StoredValues.Text(f.Title ?? f.Id, LongestText) })],
        MausVersion = snapshot.MausVersion is null ? null : StoredValues.Text(snapshot.MausVersion, LongestText),
        Tasks = CleanItems(snapshot.Tasks),
        Drivers = CleanItems(snapshot.Drivers),
    };

    /// <summary>Une liste plus longue que ce que MAUS garde vaut « non lue », comme à l'écriture : tronquée, elle ferait croire à des disparitions.</summary>
    private static List<FollowedItem>? CleanItems(IReadOnlyList<FollowedItem>? items) =>
        items is { Count: > Follow.WhatChanged.MaxKept } ? null : items?.Where(i => !string.IsNullOrWhiteSpace(i?.Name))
            .Select(i => new FollowedItem(StoredValues.Text(i.Name, LongestText), i.Version is null ? null : StoredValues.Text(i.Version, LongestText)))
            .ToList();
}

/// <summary>Carte « Avant une mise à jour » : due quand Windows Update propose quelque chose en attente.</summary>
public static class UpdateReadiness
{
    public const string PendingId = "M03.pending-updates";
    public const string OptionalId = "M03.optional-updates";
    public const string CountMeasure = "count";

    public static bool IsDue(IEnumerable<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return findings.Any(f =>
            (f.Id == PendingId && f.Status is FindingStatus.Improvable or FindingStatus.Warning or FindingStatus.Problem)
            || (f.Id == OptionalId && f.Measures?.GetValueOrDefault(CountMeasure) > 0));
    }
}
