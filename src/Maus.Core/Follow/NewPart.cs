using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Maus.Core.Hardware;
using Maus.Core.Modules.M14Display;
using Maus.Core.Platform;
using Maus.Core.Reporting;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Follow;

/// <summary>Les pièces que la fiche suit. Ce qui se branche et se débranche (clé USB, carte SD, disque virtuel) n'en fait pas partie.</summary>
public enum PartKind
{
    Screen,
    Memory,
    Graphics,
    Disk,
}

/// <summary>Une pièce lue : son modèle, jamais son numéro de série.</summary>
/// <param name="Key">Identité stable, indépendante de la langue de MAUS.</param>
/// <param name="Name">Nom du modèle, tel que Windows le donne (vide pour une barrette sans référence).</param>
public sealed record HardwarePart(PartKind Kind, string Key, string Name, long? SizeBytes = null);

/// <summary>Une pièce gardée sur la fiche.</summary>
/// <param name="Count">Nombre de pièces identiques (deux barrettes du même modèle).</param>
/// <param name="MissingSince">Absente d'une lecture qui a pourtant vu d'autres pièces du même genre, depuis cette date.</param>
/// <param name="MissedReadings">Lectures successives où elle manquait.</param>
public sealed record KeptPart(PartKind Kind, string Key, string Name, long? SizeBytes, int Count, DateTimeOffset FirstSeen, DateTimeOffset? MissingSince = null, int MissedReadings = 0);

public enum PartChangeKind
{
    Added,
    NoLongerSeen,
    CountChanged,
}

/// <summary>Un changement relevé : la date est celle où MAUS l'a vu, pas forcément celle du changement.</summary>
public sealed record PartChange(PartChangeKind Change, PartKind Kind, string Key, string Name, long? SizeBytes, DateTimeOffset At, int? CountBefore = null, int? CountNow = null);

/// <summary>La fiche du matériel de ce PC.</summary>
/// <param name="UnreadKinds">Genres de pièces que la dernière lecture n'a pas vus (lecture vide ou sans droits) : rien n'en est conclu.</param>
public sealed record PartsCard(DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<KeptPart> Parts, IReadOnlyList<PartChange> Changes, IReadOnlyList<PartKind> UnreadKinds)
{
    /// <summary>Pièces gardées au plus, celles qui ne sont plus vues partant les premières.</summary>
    internal const int MaxParts = 40;

    /// <summary>Changements gardés au plus, les plus récents.</summary>
    internal const int MaxChanges = 12;

    /// <summary>Une pièce absente ne compte comme telle qu'à la deuxième lecture de suite : un écran éteint revient tout seul.</summary>
    internal const int MissedToConfirm = 2;

    /// <summary>Barrettes ou disques identiques : bien au-delà de ce qu'un PC peut avoir.</summary>
    internal const int MaxCount = 1024;

    internal const int MaxNameLength = 120;

    /// <summary>
    /// Met la fiche à jour d'après une lecture. Un genre de pièces absent de la lecture n'a pas été lu : il reste tel quel. La
    /// toute première lecture d'un genre crée sa fiche sans rien annoncer. <c>null</c> : aucune fiche et rien de lu.
    /// </summary>
    public static PartsCard? Apply(PartsCard? card, IEnumerable<HardwarePart> reading, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var read = reading
            .Where(p => !string.IsNullOrWhiteSpace(p.Key))
            .Select(p => p with { Key = Clip(p.Key), Name = Clip(p.Name) })
            .GroupBy(p => (p.Kind, p.Key))
            .ToList();
        if (card is null && read.Count == 0)
        {
            return null;
        }

        var parts = new List<KeptPart>();
        var changes = new List<PartChange>(card?.Changes ?? []);
        var unread = new List<PartKind>();
        foreach (var kind in Enum.GetValues<PartKind>())
        {
            var now = read.Where(g => g.Key.Kind == kind).ToList();
            var kept = (card?.Parts ?? []).Where(p => p.Kind == kind).ToList();
            if (now.Count == 0)
            {
                unread.Add(kind);
                parts.AddRange(kept);
                continue;
            }

            foreach (var group in now)
            {
                parts.Add(Seen(kept.Find(p => p.Key == group.Key.Key), group.First(), group.Count(), kept.Count == 0, changes, at));
            }

            parts.AddRange(kept.Where(p => now.TrueForAll(g => g.Key.Key != p.Key)).Select(p => Missed(p, changes, at)));
        }

        // Dans le fichier, l'entrée la plus récente est celle qui reste : une horloge reculée ne doit pas la faire perdre.
        var updated = card is not null && at <= card.UpdatedAt ? card.UpdatedAt.AddTicks(1) : at;
        return new PartsCard(
            card?.CreatedAt ?? at,
            updated,
            [.. parts.OrderBy(p => p.MissingSince is null ? 0 : 1).ThenByDescending(p => p.MissingSince).Take(MaxParts)],
            [.. changes.OrderBy(c => c.At).TakeLast(MaxChanges)],
            unread);
    }

    private static KeptPart Seen(KeptPart? known, HardwarePart part, int count, bool firstOfItsKind, List<PartChange> changes, DateTimeOffset at)
    {
        if (known is null)
        {
            if (!firstOfItsKind)
            {
                changes.Add(new(PartChangeKind.Added, part.Kind, part.Key, part.Name, part.SizeBytes, at));
            }

            return new KeptPart(part.Kind, part.Key, part.Name, part.SizeBytes, count, at);
        }

        // Revenue : elle était éteinte ou débranchée, ce n'est plus un changement.
        changes.RemoveAll(c => c.Change == PartChangeKind.NoLongerSeen && c.Kind == part.Kind && c.Key == part.Key);
        if (known.Count != count)
        {
            // Retour au nombre d'avant (un des deux écrans identiques était éteint) : l'annonce précédente tombe.
            var undone = changes.FindLastIndex(c => c.Change == PartChangeKind.CountChanged && c.Kind == part.Kind && c.Key == part.Key);
            if (undone >= 0 && changes[undone].CountBefore == count)
            {
                changes.RemoveAt(undone);
            }
            else
            {
                changes.Add(new(PartChangeKind.CountChanged, part.Kind, part.Key, part.Name, part.SizeBytes, at, known.Count, count));
            }
        }

        return known with { Name = part.Name, SizeBytes = part.SizeBytes, Count = count, MissingSince = null, MissedReadings = 0 };
    }

    private static KeptPart Missed(KeptPart part, List<PartChange> changes, DateTimeOffset at)
    {
        var missing = part with { MissingSince = part.MissingSince ?? at, MissedReadings = Math.Min(part.MissedReadings + 1, MissedToConfirm) };
        if (part.MissedReadings == MissedToConfirm - 1)
        {
            changes.Add(new(PartChangeKind.NoLongerSeen, part.Kind, part.Key, part.Name, part.SizeBytes, missing.MissingSince!.Value));
        }

        return missing;
    }

    private static string Clip(string? value)
    {
        return StoredValues.Text(value?.Trim(), MaxNameLength);
    }
}

/// <summary>La fiche, dans le profil de l'utilisateur : rien n'est envoyé, et rien n'y sert à écrire dans Windows.</summary>
public sealed class PartsCardStore(string path, Action<TimeSpan>? wait = null)
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly JsonHistoryFile<PartsCard> _file = new(
        path,
        Options,
        1,
        c => c.UpdatedAt,
        _ => T("La fiche du matériel ({0}) est momentanément illisible : elle n'est pas réécrite, pour ne rien perdre.", path),
        wait,
        list => list.TrueForAll(IsWhole),
        clean: Clean);

    public static PartsCardStore CreateDefault() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MAUS", "hardware-card.json"));

    /// <summary>La fiche ; <c>null</c> si le fichier est absent, abîmé ou momentanément illisible.</summary>
    public PartsCard? Load() => TryLoad(out var card) ? card : null;

    /// <summary>Faux si le fichier existe mais n'a pas pu être relu (illisible ou abîmé) : l'absence de fiche n'y prouve rien.</summary>
    public bool TryLoad(out PartsCard? card)
    {
        var read = _file.TryLoad(out var cards);
        card = cards.Count > 0 ? cards[^1] : null;
        return read;
    }

    /// <summary>
    /// Compare une lecture à la fiche et la garde. Un fichier abîmé est copié à côté et la fiche repart de cette lecture, sans rien annoncer.
    /// </summary>
    /// <exception cref="IOException">Fichier momentanément illisible (laissé intact) ou écriture impossible.</exception>
    /// <exception cref="UnauthorizedAccessException">Écriture refusée.</exception>
    public PartsCard? Update(IEnumerable<HardwarePart> reading, DateTimeOffset at)
    {
        var card = PartsCard.Apply(Load(), reading, at);
        return card is null ? null : _file.Add(card)[^1];
    }

    /// <summary>JSON valide, mais vidé ou hors de ce que MAUS écrit : traité comme abîmé plutôt que d'afficher du faux.</summary>
    private static bool IsWhole(PartsCard? card) =>
        card is { Parts: not null, Changes: not null, UnreadKinds: not null }
        && StoredValues.IsPossibleDate(card.CreatedAt) && StoredValues.IsPossibleDate(card.UpdatedAt)
        && card.Parts.Count <= PartsCard.MaxParts && card.Changes.Count <= PartsCard.MaxChanges
        && card.Parts.All(IsWhole)
        && card.Changes.All(IsWhole)
        && card.UnreadKinds.All(Enum.IsDefined);

    private static bool IsWhole(KeptPart? part) =>
        part is { Key: not null, Name: not null, Count: > 0 and <= PartsCard.MaxCount, MissedReadings: >= 0 and <= PartsCard.MissedToConfirm, SizeBytes: null or >= 0 }
        && Enum.IsDefined(part.Kind)
        && StoredValues.IsPossibleDate(part.FirstSeen)
        && (part.MissingSince is null || StoredValues.IsPossibleDate(part.MissingSince.Value));

    private static bool IsWhole(PartChange? change) =>
        change is { Key: not null, Name: not null, SizeBytes: null or >= 0 }
        && Enum.IsDefined(change.Kind) && Enum.IsDefined(change.Change)
        && StoredValues.IsPossibleDate(change.At)

        // Sans ses deux nombres, le changement se lirait « 0 au lieu de 0 ».
        && (change.Change != PartChangeKind.CountChanged || change is { CountBefore: > 0, CountNow: > 0 });

    /// <summary>Ce que le panneau peut montrer : des noms courts et lisibles, aucun changement daté d'un jour à venir.</summary>
    private static PartsCard Clean(PartsCard card) => card with
    {
        Parts = [.. card.Parts.Select(p => p with { Key = StoredValues.Text(p.Key, PartsCard.MaxNameLength), Name = StoredValues.Text(p.Name, PartsCard.MaxNameLength) })],
        Changes =
        [
            .. card.Changes.Where(c => StoredValues.IsPastDate(c.At))
                .Select(c => c with { Key = StoredValues.Text(c.Key, PartsCard.MaxNameLength), Name = StoredValues.Text(c.Name, PartsCard.MaxNameLength) }),
        ],
    };
}

/// <summary>
/// La lecture de la fiche, sans droits et sans pilote : modèles uniquement, aucun numéro de série n'est demandé à Windows. Une lecture
/// qui échoue rend simplement moins de pièces.
/// </summary>
public static partial class PartsReader
{
    /// <summary>Lecture légère, à faire hors du fil de la fenêtre.</summary>
    public static IReadOnlyList<HardwarePart> ReadLight() => Read(new WmiCimReader(), new Win32DisplayConfigReader());

    /// <summary>Lecture d'après la fiche du PC de l'Atelier : seuls les écrans restent à lire.</summary>
    public static IReadOnlyList<HardwarePart> FromInventory(HardwareInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return Build(inventory.Memory, inventory.Gpus.Select(g => g.Info), inventory.Disks, Screens(new Win32DisplayConfigReader()));
    }

    internal static IReadOnlyList<HardwarePart> Read(ICimReader cim, IDisplayConfigReader display) => Build(
        Safe(() => HardwareInventoryReader.ReadMemory(cim)),
        Safe(() => HardwareProfileBuilder.ReadGpus(cim)),
        Safe(() => HardwareInventoryReader.ReadDisks(cim)),
        Screens(display));

    internal static IReadOnlyList<HardwarePart> Build(IEnumerable<MemoryModuleInfo> memory, IEnumerable<GpuInfo> gpus, IEnumerable<DiskIdentity> disks, IEnumerable<HardwarePart> screens)
    {
        var parts = new List<HardwarePart>(screens);
        parts.AddRange(memory.Where(m => m.CapacityBytes > 0).Select(Memory));

        // Une carte sur le pilote de base de Windows n'est pas lue sous son vrai nom : ce n'est pas une nouvelle carte, et la vraie n'a pas disparu.
        var cards = gpus.ToList();
        if (!cards.Exists(g => string.Equals(g.InfFilename, "display.inf", StringComparison.OrdinalIgnoreCase) || g.Name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
            || g.Name.Contains("de base Microsoft", StringComparison.OrdinalIgnoreCase) || g.Name == Localization.Texts.T("GPU inconnu")))
        {
            parts.AddRange(cards.Select(g => new HardwarePart(PartKind.Graphics, GraphicsKey(g), g.Name.Trim())));
        }

        parts.AddRange(disks.Where(d => !d.ComesAndGoes && d.Model != "?").Select(d => new HardwarePart(PartKind.Disk, Keyed(d.Model, d.SizeBytes), d.Model, d.SizeBytes)));
        return parts;
    }

    private static HardwarePart Memory(MemoryModuleInfo module)
    {
        var name = string.Join(' ', new[] { module.Manufacturer, module.PartNumber }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new HardwarePart(PartKind.Memory, Keyed(name, module.CapacityBytes), name, module.CapacityBytes);
    }

    /// <summary>Le pilote peut changer le nom affiché d'une carte : son identifiant PCI (modèle, pas exemplaire) la reconnaît quand même.</summary>
    private static string GraphicsKey(GpuInfo gpu) =>
        PciModel().Match(gpu.PnpDeviceId) is { Success: true } match ? match.Value.ToUpperInvariant() : Keyed(gpu.Name, null);

    private static string Keyed(string name, long? size) =>
        name.Trim().ToUpperInvariant() + (size is { } bytes ? "|" + bytes.ToString(CultureInfo.InvariantCulture) : string.Empty);

    private static List<HardwarePart> Screens(IDisplayConfigReader display) =>
        [.. Safe(display.ReadActivePaths)
            .Where(p => !string.IsNullOrWhiteSpace(p.MonitorName) && p.Output is not (OutputTechnology.IndirectWired or OutputTechnology.IndirectVirtual or OutputTechnology.Miracast))
            .Select(p => new HardwarePart(PartKind.Screen, Keyed(p.MonitorName, null), p.MonitorName.Trim()))];

    private static IReadOnlyList<T> Safe<T>(Func<IReadOnlyList<T>?> read)
    {
        try
        {
            return read() ?? [];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Diagnostics.Breadcrumbs.Add("fiche du matériel : lecture impossible (" + ex.GetType().Name + ")");
            return [];
        }
    }

    [GeneratedRegex("VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PciModel();
}

/// <summary>Un constat existant, tel qu'un audit l'a laissé : <c>Current</c> et <c>Expected</c> manquent pour un audit gardé.</summary>
public sealed record PartCheck(string Id, string ModuleId, string Title, FindingStatus Status, string? Current = null, string? Expected = null)
{
    public static IEnumerable<PartCheck> From(IEnumerable<ModuleResult> results) =>
        results.SelectMany(r => r.Findings.Select(f => new PartCheck(f.Id, r.ModuleId, f.Title, f.Status, f.Current, f.Expected)));

    public static IEnumerable<PartCheck> From(AuditSnapshot snapshot) =>
        snapshot.Findings.Select(f => new PartCheck(f.Id, f.ModuleId, f.Title, f.Status));
}

/// <summary>Un changement tel que le panneau le montre.</summary>
/// <param name="Guide">Ce que MAUS sait vérifier pour cette pièce, et ce qu'il ne fait pas. Vide pour une pièce qui n'est plus vue.</param>
/// <param name="Checks">Les constats de l'audit fait depuis le changement. Vide s'il n'y en a pas encore.</param>
/// <param name="NeedsAudit">Aucun audit n'a été fait depuis : un audit dira ce qu'il en est.</param>
public sealed record PartChangeView(PartKind Kind, string Title, string When, string Guide, IReadOnlyList<string> Checks, bool NeedsAudit, string? ModuleId);

/// <summary>Ce que MAUS peut dire d'un changement : seulement ce que disent des constats existants, sans en inventer et sans désigner de coupable.</summary>
public static class NewPartView
{
    public static string KindName(PartKind kind) => kind switch
    {
        PartKind.Screen => T("écrans"),
        PartKind.Memory => T("mémoire"),
        PartKind.Graphics => T("carte graphique"),
        _ => T("disques"),
    };

    /// <summary>Le module dont les constats parlent de ce genre de pièce.</summary>
    public static string ModuleOf(PartKind kind) => kind switch
    {
        PartKind.Screen => "M14",
        PartKind.Memory => "M10",
        PartKind.Graphics => "M09",
        _ => "M11",
    };

    /// <param name="checksAt">Date du dernier audit connu avec les constats qui vont avec ; plus ancien que le changement, il ne dit rien de la nouvelle pièce.</param>
    public static IReadOnlyList<PartChangeView> Build(PartsCard? card, IReadOnlyList<PartCheck> checks, DateTimeOffset? checksAt) =>
        card is null
            ? []
            : [.. card.Changes.OrderByDescending(c => c.At).Select(c => Describe(c, checksAt is { } at && at >= c.At ? checks : null))];

    private static PartChangeView Describe(PartChange change, IReadOnlyList<PartCheck>? checks)
    {
        var name = Named(change);
        if (change.Change == PartChangeKind.NoLongerSeen)
        {
            return new(change.Kind, NoLongerSeenTitle(change.Kind, name),
                T("Plus vu depuis le {0:d}. Débranché, éteint ou retiré : MAUS ne sait pas lequel, et ce n'est pas un problème en soi.", change.At.ToLocalTime()),
                string.Empty, [], false, null);
        }

        var title = change.Change == PartChangeKind.Added
            ? AddedTitle(change.Kind, name)
            : T("{0} : {1} au lieu de {2}", name, change.CountNow ?? 0, change.CountBefore ?? 0);
        var lines = checks is null ? [] : Select(change.Kind, change.Name, checks).Select(Line).ToList();
        return new(change.Kind, title, T("Relevé le {0:d} : le changement a probablement eu lieu peu avant.", change.At.ToLocalTime()),
            Guide(change.Kind), lines, checks is null, ModuleOf(change.Kind));
    }

    private static string Named(PartChange change)
    {
        var size = change.SizeBytes is > 0 and var bytes ? Math.Round(bytes / (1024d * 1024 * 1024)).ToString("0", CultureInfo.CurrentCulture) : null;
        return change.Name.Length == 0 ? T("barrette de {0} Go", size ?? "?") : size is null ? change.Name : T("{0} ({1} Go)", change.Name, size);
    }

    private static string AddedTitle(PartKind kind, string name) => kind switch
    {
        PartKind.Screen => T("Nouvel écran : {0}", name),
        PartKind.Memory => T("Nouvelle mémoire : {0}", name),
        PartKind.Graphics => T("Nouvelle carte graphique : {0}", name),
        _ => T("Nouveau disque : {0}", name),
    };

    private static string NoLongerSeenTitle(PartKind kind, string name) => kind switch
    {
        PartKind.Screen => T("Écran plus vu : {0}", name),
        PartKind.Memory => T("Mémoire plus vue : {0}", name),
        PartKind.Graphics => T("Carte graphique plus vue : {0}", name),
        _ => T("Disque plus vu : {0}", name),
    };

    private static string Guide(PartKind kind) => kind switch
    {
        PartKind.Screen => T("MAUS vérifie si l'écran tourne à la plus haute fréquence qu'il propose : un écran neuf reste souvent à 60 Hz. Le réglage se change dans Paramètres > Système > Écran."),
        PartKind.Memory => T("MAUS compare la vitesse de la mémoire à celle qu'annonce sa référence, et vérifie le double canal. Le profil XMP ou EXPO se règle dans le BIOS du PC : MAUS ne le fait pas."),
        PartKind.Graphics => T("MAUS vérifie le pilote de cette carte. Le benchmark mesure ce qu'elle donne sur ce PC : il se lance quand vous le décidez."),
        _ => T("MAUS lit la santé et l'usure de ce disque, si Windows les donne, et vérifie le TRIM (entretien du SSD par Windows)."),
    };

    /// <summary>Les constats de cette pièce : ceux du bon module, et pour ce qui existe en plusieurs exemplaires, ceux qui la nomment.</summary>
    internal static IEnumerable<PartCheck> Select(PartKind kind, string name, IReadOnlyList<PartCheck> checks)
    {
        bool Named(PartCheck c) => name.Length > 0 && c.Title.Contains(name, StringComparison.OrdinalIgnoreCase);
        switch (kind)
        {
            case PartKind.Screen:
                var refresh = checks.Where(c => c.Id.StartsWith("M14.refresh.", StringComparison.Ordinal)).ToList();
                return refresh.Exists(Named) ? refresh.Where(Named) : refresh;
            case PartKind.Memory:
                return checks.Where(c => c.Id is "M10.xmp" or "M10.dual-channel" or "M10.mixed-kit");
            case PartKind.Graphics:
                return checks.Where(c => (c.Id.StartsWith("M09.driver.", StringComparison.Ordinal) && Named(c)) || (c.Id == "M09.basic-display-driver" && NeedsAction(c)));
            default:
                return checks.Where(c => (c.Id.StartsWith("M11.disk-", StringComparison.Ordinal) && Named(c)) || c.Id == "M11.trim");
        }
    }

    private static bool NeedsAction(PartCheck check) => check.Status is FindingStatus.Improvable or FindingStatus.Warning or FindingStatus.Problem;

    private static string Line(PartCheck check)
    {
        if (check.Status == FindingStatus.Unknown)
        {
            return T("{0} : non lu à cet audit (sans droits administrateur ou donnée indisponible).", check.Title);
        }

        if (!NeedsAction(check))
        {
            return check.Current is { Length: > 0 } read ? T("{0} : rien à signaler ({1}).", check.Title, read) : T("{0} : rien à signaler.", check.Title);
        }

        return check.Current is { Length: > 0 } current
            ? check.Expected is { Length: > 0 } expected ? T("À voir : {0} ({1} ; attendu : {2}).", check.Title, current, expected) : T("À voir : {0} ({1}).", check.Title, current)
            : T("À voir : {0}.", check.Title);
    }
}
