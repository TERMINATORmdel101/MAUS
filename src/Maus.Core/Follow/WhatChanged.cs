using System.Text.RegularExpressions;
using Maus.Core.Modules.M12Startup;
using Maus.Core.Reporting;
using Maus.Core.Workshop;

namespace Maus.Core.Follow;

/// <summary>De quoi parle un changement.</summary>
public enum ChangeFamily
{
    Windows,
    Startup,
    Task,
    Driver,
    Finding,
}

/// <summary>Ce qui s'est passé entre les deux audits.</summary>
public enum ChangeType
{
    Appeared,
    Gone,
    Changed,
}

/// <summary>Pourquoi MAUS ne peut pas conclure.</summary>
public enum NotComparableReason
{
    /// <summary>Tout un module absent de l'un des audits (non choisi, en erreur).</summary>
    ModuleNotRead,

    /// <summary>Une source de programmes au démarrage n'a pas été lue.</summary>
    SourceNotRead,

    /// <summary>L'audit ne contient pas cette information : ancienne version de MAUS, ou lecture impossible ce jour-là.</summary>
    NotKept,

    /// <summary>L'audit qui ne montre pas la chose lit moins que l'autre : son absence ne prouve rien.</summary>
    ReadsLess,

    /// <summary>Deux versions de MAUS : c'est peut-être un contrôle ajouté ou retiré.</summary>
    MausVersionChanged,

    /// <summary>Un seul des deux audits a été fait avec un autre compte administrateur : ce qui est propre à l'utilisateur n'est pas le même.</summary>
    OtherAccount,

    /// <summary>Des contrôles lus dans un audit et indéterminés dans l'autre.</summary>
    Undetermined,
}

/// <summary>
/// Un changement vu entre deux audits. Il a eu lieu entre <see cref="LastBefore"/> et <see cref="FirstAfter"/>, les deux audits
/// gardés qui l'encadrent au plus près : MAUS ne regardait pas entre les deux.
/// </summary>
public sealed record PcChange(ChangeFamily Family, ChangeType Type, string Name, DateTimeOffset LastBefore, DateTimeOffset FirstAfter)
{
    /// <summary>Valeur d'avant : build, version d'un pilote, ou <see cref="WhatChanged.StartupEnabled"/>/<see cref="WhatChanged.StartupDisabled"/>.</summary>
    public string? Before { get; init; }

    /// <summary>Valeur d'après, de même nature.</summary>
    public string? After { get; init; }

    public FindingStatus? StatusBefore { get; init; }

    public FindingStatus? StatusAfter { get; init; }

    /// <summary>Le constat qui en parle, s'il y en a un.</summary>
    public string? FindingId { get; init; }

    /// <summary>Verdict changé entre deux versions de MAUS : le contrôle a pu changer avec elle.</summary>
    public bool MausVersionChanged { get; init; }

    /// <summary>Verdict changé entre deux audits qui ne lisent pas autant l'un que l'autre : il a pu changer avec cela, sans que le PC bouge.</summary>
    public bool ReadingChanged { get; init; }

    /// <summary>Verdict changé alors qu'un seul des deux audits a été fait avec un autre compte administrateur : il peut venir de là.</summary>
    public bool OtherAccount { get; init; }
}

/// <summary>Ce que MAUS ne peut pas comparer : une réponse à part entière, pas une erreur.</summary>
/// <param name="At">L'audit concerné.</param>
/// <param name="Name">L'objet concerné ; <c>null</c> quand la note vaut pour toute la famille.</param>
/// <param name="Side">Ce que la comparaison aurait pu laisser croire.</param>
public sealed record NotComparableNote(ChangeFamily Family, NotComparableReason Reason, string? Name, DateTimeOffset At, bool WithoutRights = false, ChangeType? Side = null, int Count = 1);

/// <summary>Résultat de la comparaison de deux audits gardés.</summary>
public sealed record WhatChangedReport(AuditSnapshot Older, AuditSnapshot Newer, IReadOnlyList<PcChange> Changes, IReadOnlyList<NotComparableNote> NotComparable)
{
    public bool SameMausVersion => AuditComparison.SameMausVersion(Older, Newer);
}

/// <summary>
/// « Qu'est-ce qui a changé ? » : entre deux résumés d'audit, ce qui est apparu, a disparu ou a changé (programmes au démarrage,
/// tâches à l'ouverture de session, pilotes, build de Windows, constats). Aucune lecture de Windows : seulement les résumés gardés.
/// Une chose n'est dite « apparue » ou « disparue » que si l'audit qui ne la montre pas pouvait la voir ; sinon, c'est une note
/// « non comparable ».
/// </summary>
public static partial class WhatChanged
{
    public const string TasksFindingId = "M12.logon-tasks";
    public const string DriversFindingId = "M17.unsigned-drivers";
    public const string StartupEnabled = "enabled";
    public const string StartupDisabled = "disabled";

    private const string StartupModule = "M12";
    private const string SourcePrefix = "M12.source-";

    /// <summary>Au-delà, la liste n'est pas gardée : mieux vaut « non comparable » qu'une liste tronquée qui ferait croire à des disparitions.</summary>
    internal const int MaxKept = 500;

    /// <summary>Tâches gardées dans le résumé : leur nom seul, sans le SID que certaines portent à la fin (tâches par compte de OneDrive).</summary>
    public static IReadOnlyList<FollowedItem>? KeptTasks(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var kept = names
            .Select(n => Sid().Replace(n, "…"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => new FollowedItem(n))
            .ToList();
        return kept.Count > MaxKept ? null : kept;
    }

    /// <summary>
    /// Pilotes gardés dans le résumé : nom et version, sans l'identifiant du périphérique (il peut contenir un numéro de série).
    /// Ceux dont l'éditeur est Microsoft restent dehors : leur version suit le build de Windows, déjà montré, et une clé USB
    /// débranchée ne doit pas passer pour un pilote disparu.
    /// </summary>
    public static IReadOnlyList<FollowedItem>? KeptDrivers(IEnumerable<DriverEntry> drivers)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        var kept = drivers
            .Where(d => d.Provider?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) != true)
            .Select(d => new FollowedItem(d.Device, d.Version))
            .Distinct()
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToList();
        return kept.Count > MaxKept ? null : kept;
    }

    /// <param name="kept">Tous les audits gardés : ceux qui sont entre les deux resserrent la date de chaque changement.</param>
    public static WhatChangedReport Compare(IReadOnlyList<AuditSnapshot> kept, AuditSnapshot older, AuditSnapshot newer)
    {
        ArgumentNullException.ThrowIfNull(kept);
        ArgumentNullException.ThrowIfNull(older);
        ArgumentNullException.ThrowIfNull(newer);

        var pass = new Pass(older, newer, [.. kept.Where(s => s.At > older.At && s.At < newer.At).OrderBy(s => s.At)]);
        pass.CompareWindows();
        pass.CompareItems(ChangeFamily.Startup, StartupView);
        pass.NoteUnreadSources();
        pass.CompareItems(ChangeFamily.Task, s => ListView(s.Tasks, TasksFindingId));
        pass.CompareItems(ChangeFamily.Driver, s => ListView(s.Drivers, null));
        pass.CompareFindings();
        return pass.Report();
    }

    /// <summary>Source d'une entrée de démarrage, ou <c>null</c> pour un autre constat (« M12.boot-time », « M12.source-… »).</summary>
    private static string? SourceOf(string findingId) =>
        findingId.StartsWith(StartupModule + ".", StringComparison.Ordinal)
            ? StartupAppsModule.EntrySourceIds.FirstOrDefault(source => findingId.StartsWith($"{StartupModule}.{source}-", StringComparison.Ordinal))
            : null;

    private static View? StartupView(AuditSnapshot snapshot)
    {
        if (!snapshot.Findings.Any(f => f.ModuleId == StartupModule))
        {
            return null;
        }

        var items = snapshot.Findings
            .Where(f => SourceOf(f.Id) is not null)
            .DistinctBy(f => f.Id, StringComparer.Ordinal)
            .ToDictionary(
                f => f.Id,
                f => new Item(f.Title ?? f.Id, f.Enabled switch { true => StartupEnabled, false => StartupDisabled, null => null }, f.Id),
                StringComparer.Ordinal);
        var unread = snapshot.Findings.Where(f => f.Id.StartsWith(SourcePrefix, StringComparison.Ordinal)).Select(f => f.Id[SourcePrefix.Length..]).ToHashSet(StringComparer.Ordinal);
        var seen = items.Keys.Select(SourceOf).ToHashSet(StringComparer.Ordinal);
        return new View(items, key => !unread.Contains(SourceOf(key)!), key => seen.Contains(SourceOf(key)));
    }

    private static View? ListView(IReadOnlyList<FollowedItem>? list, string? findingId)
    {
        if (list is null)
        {
            return null;
        }

        // Fichier retouché : une ligne sans nom ne compte pas.
        var items = list
            .Where(i => !string.IsNullOrWhiteSpace(i?.Name))
            .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => new Item(g.First().Name, Versions(g), findingId), StringComparer.OrdinalIgnoreCase);
        return new View(items);
    }

    /// <summary>Plusieurs appareils peuvent porter le même nom : toutes leurs versions, dans un ordre stable.</summary>
    private static string? Versions(IEnumerable<FollowedItem> sameName)
    {
        var versions = sameName.Select(i => i.Version).OfType<string>().Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return versions.Count == 0 ? null : string.Join(" ; ", versions);
    }

    [GeneratedRegex(@"S-1-5-21-[\d-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Sid();

    /// <summary>Un objet vu dans un audit, sous une clé stable.</summary>
    private sealed record Item(string Name, string? Value, string? FindingId);

    /// <summary>Ce qu'un audit dit d'une famille d'objets.</summary>
    /// <param name="reads">La source de cet objet a-t-elle été lue dans cet audit ?</param>
    /// <param name="saw">Cet audit a-t-il vu d'autres objets de cette source ? Sinon, face à une autre version de MAUS, la source est peut-être nouvelle.</param>
    private sealed class View(Dictionary<string, Item> items, Func<string, bool>? reads = null, Func<string, bool>? saw = null)
    {
        public Dictionary<string, Item> Items => items;

        public bool Reads(string key) => reads?.Invoke(key) ?? true;

        public bool Saw(string key) => saw?.Invoke(key) ?? true;
    }

    private sealed class Pass(AuditSnapshot older, AuditSnapshot newer, List<AuditSnapshot> between)
    {
        private readonly List<PcChange> _changes = [];
        private readonly List<NotComparableNote> _notes = [];
        private readonly bool _sameMaus = AuditComparison.SameMausVersion(older, newer);
        private readonly bool _sameAccount = older.OtherAccount == newer.OtherAccount;
        private readonly int _highestLevel = Math.Max(older.ReadingLevel, newer.ReadingLevel);

        public WhatChangedReport Report() => new(
            older,
            newer,
            [.. _changes.OrderByDescending(c => c.FirstAfter).ThenBy(c => c.Family).ThenBy(c => c.Name, StringComparer.CurrentCulture)],
            _notes);

        public void CompareWindows()
        {
            if (string.IsNullOrEmpty(older.Build) || string.IsNullOrEmpty(newer.Build) || string.Equals(older.Build, newer.Build, StringComparison.Ordinal))
            {
                return;
            }

            var (lastBefore, firstAfter) = Window(s => string.IsNullOrEmpty(s.Build) ? null : string.Equals(s.Build, newer.Build, StringComparison.Ordinal));
            _changes.Add(new(ChangeFamily.Windows, ChangeType.Changed, "Windows", lastBefore, firstAfter) { Before = older.Build, After = newer.Build });
        }

        public void CompareItems(ChangeFamily family, Func<AuditSnapshot, View?> read)
        {
            var before = read(older);
            var after = read(newer);
            if (before is null || after is null)
            {
                NoteIfMissing(family, before, older);
                NoteIfMissing(family, after, newer);
                return;
            }

            var views = new Dictionary<AuditSnapshot, View?>(ReferenceEqualityComparer.Instance);
            foreach (var snapshot in between)
            {
                views[snapshot] = read(snapshot);
            }

            foreach (var (key, then) in before.Items)
            {
                if (after.Items.TryGetValue(key, out var now))
                {
                    if (then.Value is not null && now.Value is not null && !string.Equals(then.Value, now.Value, StringComparison.Ordinal))
                    {
                        // Ce qui est propre à l'utilisateur n'est pas le même d'un compte à l'autre ; les pilotes sont ceux de la machine.
                        if (!_sameAccount && family != ChangeFamily.Driver)
                        {
                            _notes.Add(new(family, NotComparableReason.OtherAccount, now.Name, AccountAudit.At, Side: ChangeType.Changed));
                        }
                        else
                        {
                            AddItem(family, ChangeType.Changed, key, now, then.Value, now.Value, views, now);
                        }
                    }
                }
                else if (after.Reads(key))
                {
                    if (!_sameAccount && family != ChangeFamily.Driver)
                    {
                        _notes.Add(new(family, NotComparableReason.OtherAccount, then.Name, AccountAudit.At, Side: ChangeType.Gone));
                    }
                    else if (newer.ReadingLevel < older.ReadingLevel)
                    {
                        _notes.Add(new(family, NotComparableReason.ReadsLess, then.Name, newer.At, newer.WithoutRights, ChangeType.Gone));
                    }
                    else if (!_sameMaus && !after.Saw(key))
                    {
                        _notes.Add(new(family, NotComparableReason.MausVersionChanged, then.Name, newer.At, Side: ChangeType.Gone));
                    }
                    else
                    {
                        AddItem(family, ChangeType.Gone, key, then, then.Value, null, views, null);
                    }
                }
            }

            foreach (var (key, now) in after.Items)
            {
                if (before.Items.ContainsKey(key) || !before.Reads(key))
                {
                    continue;
                }

                if (!_sameAccount && family != ChangeFamily.Driver)
                {
                    _notes.Add(new(family, NotComparableReason.OtherAccount, now.Name, AccountAudit.At, Side: ChangeType.Appeared));
                }
                else if (older.ReadingLevel < newer.ReadingLevel)
                {
                    _notes.Add(new(family, NotComparableReason.ReadsLess, now.Name, older.At, older.WithoutRights, ChangeType.Appeared));
                }
                else if (!_sameMaus && !before.Saw(key))
                {
                    _notes.Add(new(family, NotComparableReason.MausVersionChanged, now.Name, newer.At, Side: ChangeType.Appeared));
                }
                else
                {
                    AddItem(family, ChangeType.Appeared, key, now, null, now.Value, views, now);
                }
            }
        }

        /// <summary>Une source de programmes au démarrage non lue : ses programmes ne sont pas comparés, et il faut le dire.</summary>
        public void NoteUnreadSources()
        {
            foreach (var snapshot in new[] { older, newer })
            {
                foreach (var source in snapshot.Findings.Where(f => f.Id.StartsWith(SourcePrefix, StringComparison.Ordinal)).DistinctBy(f => f.Id, StringComparer.Ordinal))
                {
                    _notes.Add(new(ChangeFamily.Startup, NotComparableReason.SourceNotRead, source.Title ?? source.Id, snapshot.At, snapshot.WithoutRights));
                }
            }
        }

        public void CompareFindings()
        {
            NoteMissingModules(older, newer);
            NoteMissingModules(newer, older);
            NoteUndetermined();

            var indexes = between.Select(s => (Snapshot: s, Findings: Index(s), Modules: s.Findings.Select(f => f.ModuleId).ToHashSet(StringComparer.Ordinal))).ToList();
            foreach (var change in AuditComparison.Compare(older, newer))
            {
                var id = change.FindingId!;
                var name = change.Title ?? id;

                // Une entrée de démarrage apparue ou disparue est déjà montrée comme telle ; seul un changement de verdict s'ajoute.
                if (SourceOf(id) is not null && change.Kind is not (AuditChangeKind.Worse or AuditChangeKind.Better))
                {
                    continue;
                }

                if (change.Kind == AuditChangeKind.NotComparable)
                {
                    _notes.Add(NoteFor(change, name));
                    continue;
                }

                var (lastBefore, firstAfter) = Window(s =>
                {
                    var (_, findings, modules) = indexes.First(x => ReferenceEquals(x.Snapshot, s));
                    if (findings.TryGetValue(id, out var seen))
                    {
                        return seen.Status == FindingStatus.Unknown ? null : change.After is { } final && seen.Status.Rank() == final.Rank();
                    }

                    var canSayAbsent = modules.Contains(change.ModuleId) && s.ReadingLevel >= _highestLevel && s.OtherAccount == older.OtherAccount && _sameAccount
                        && AuditComparison.SameMausVersion(s, older) && AuditComparison.SameMausVersion(s, newer);
                    return canSayAbsent ? change.After is null : null;
                });
                var verdict = change.Kind is AuditChangeKind.Worse or AuditChangeKind.Better;
                _changes.Add(new(
                    ChangeFamily.Finding,
                    change.Kind switch { AuditChangeKind.New => ChangeType.Appeared, AuditChangeKind.Gone => ChangeType.Gone, _ => ChangeType.Changed },
                    name,
                    lastBefore,
                    firstAfter)
                {
                    StatusBefore = change.Before,
                    StatusAfter = change.After,
                    FindingId = id,
                    MausVersionChanged = !_sameMaus,
                    ReadingChanged = verdict && older.ReadingLevel != newer.ReadingLevel,
                    OtherAccount = verdict && !_sameAccount,
                });
            }
        }

        private NotComparableNote NoteFor(AuditChange change, string name)
        {
            var side = change.After is null ? ChangeType.Gone : ChangeType.Appeared;
            if (!_sameAccount)
            {
                return new(ChangeFamily.Finding, NotComparableReason.OtherAccount, name, AccountAudit.At, Side: side);
            }

            if (!_sameMaus)
            {
                return new(ChangeFamily.Finding, NotComparableReason.MausVersionChanged, name, newer.At, Side: side);
            }

            var lessRead = side == ChangeType.Gone ? newer : older;
            return new(ChangeFamily.Finding, NotComparableReason.ReadsLess, name, lessRead.At, lessRead.WithoutRights, side);
        }

        /// <summary>Celui des deux audits fait avec un autre compte.</summary>
        private AuditSnapshot AccountAudit => older.OtherAccount ? older : newer;

        private static Dictionary<string, SnapshotFinding> Index(AuditSnapshot snapshot) =>
            snapshot.Findings.DistinctBy(f => f.Id, StringComparer.Ordinal).ToDictionary(f => f.Id, StringComparer.Ordinal);

        private void NoteIfMissing(ChangeFamily family, View? view, AuditSnapshot snapshot)
        {
            if (view is null)
            {
                _notes.Add(new(family, NotComparableReason.NotKept, null, snapshot.At, snapshot.WithoutRights));
            }
        }

        private void NoteMissingModules(AuditSnapshot reader, AuditSnapshot other)
        {
            var read = reader.Findings.Select(f => f.ModuleId).ToHashSet(StringComparer.Ordinal);
            var missing = other.Findings.Select(f => f.ModuleId).Where(m => !read.Contains(m)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
            {
                _notes.Add(new(ChangeFamily.Finding, NotComparableReason.ModuleNotRead, string.Join(", ", missing), reader.At, reader.WithoutRights, Count: missing.Count));
            }
        }

        private void NoteUndetermined()
        {
            var before = Index(older);
            var after = Index(newer);
            var unreadBefore = 0;
            var unreadAfter = 0;
            foreach (var (id, now) in after)
            {
                if (before.TryGetValue(id, out var then) && (now.Status == FindingStatus.Unknown) != (then.Status == FindingStatus.Unknown))
                {
                    if (now.Status == FindingStatus.Unknown)
                    {
                        unreadAfter++;
                    }
                    else
                    {
                        unreadBefore++;
                    }
                }
            }

            if (unreadBefore > 0)
            {
                _notes.Add(new(ChangeFamily.Finding, NotComparableReason.Undetermined, null, older.At, older.WithoutRights, Count: unreadBefore));
            }

            if (unreadAfter > 0)
            {
                _notes.Add(new(ChangeFamily.Finding, NotComparableReason.Undetermined, null, newer.At, newer.WithoutRights, Count: unreadAfter));
            }
        }

        private void AddItem(ChangeFamily family, ChangeType type, string key, Item shown, string? before, string? after, Dictionary<AuditSnapshot, View?> views, Item? final)
        {
            var (lastBefore, firstAfter) = Window(s =>
            {
                if (views[s] is not { } view)
                {
                    return null;
                }

                if (view.Items.TryGetValue(key, out var seen))
                {
                    if (type != ChangeType.Changed)
                    {
                        return final is not null;
                    }

                    return seen.Value is null ? null : string.Equals(seen.Value, final!.Value, StringComparison.Ordinal);
                }

                var canSayAbsent = view.Reads(key) && s.ReadingLevel >= _highestLevel && (family == ChangeFamily.Driver || (_sameAccount && s.OtherAccount == older.OtherAccount))
                    && ((AuditComparison.SameMausVersion(s, older) && AuditComparison.SameMausVersion(s, newer)) || view.Saw(key));
                return canSayAbsent ? final is null : null;
            });
            _changes.Add(new(family, type, shown.Name, lastBefore, firstAfter) { Before = before, After = after, FindingId = shown.FindingId });
        }

        /// <summary>
        /// Resserre la date : remonte depuis le dernier audit tant que l'état est déjà l'état final. <paramref name="isFinal"/> rend
        /// <c>null</c> quand un audit intermédiaire ne permet pas de conclure : il est sauté, jamais pris pour une preuve.
        /// </summary>
        private (DateTimeOffset LastBefore, DateTimeOffset FirstAfter) Window(Func<AuditSnapshot, bool?> isFinal)
        {
            var firstAfter = newer.At;
            for (var i = between.Count - 1; i >= 0; i--)
            {
                switch (isFinal(between[i]))
                {
                    case true:
                        firstAfter = between[i].At;
                        break;
                    case false:
                        return (between[i].At, firstAfter);
                }
            }

            return (older.At, firstAfter);
        }
    }
}
