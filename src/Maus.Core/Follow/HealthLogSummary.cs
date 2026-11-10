using static Maus.Core.Localization.Texts;

namespace Maus.Core.Follow;

/// <summary>Une mesure suivie : sa valeur actuelle, sa première valeur et sa courbe, comparées à ce PC seulement.</summary>
/// <param name="Range">Médiane et plage, pour une valeur qui varie d'un jour à l'autre.</param>
/// <param name="Unread">Le dernier audit n'a pas lu cette valeur : celle qui est montrée est plus ancienne.</param>
public sealed record HealthLogRow(string Label, string Current, string? First, string? Range, string Variation, string? Unread, IReadOnlyList<double> Values)
{
    public override string ToString() => Label;

    public bool HasFirst => First is not null;

    public bool HasRange => Range is not null;

    public bool HasUnread => Unread is not null;

    /// <summary>Une courbe demande deux points.</summary>
    public bool HasCurve => Values.Count >= 2;
}

/// <summary>Ce que montre le carnet : les mesures, ce qui reste vide et pourquoi, et sa dernière ligne.</summary>
public sealed record HealthLogSummary(IReadOnlyList<HealthLogRow> Rows, IReadOnlyList<string> Notes, string LastLine)
{
    private const int BootsInRange = 30;
    private const int PointsOnCurve = 60;

    /// <summary>Plus petit écart de ces valeurs, données en nombres entiers : en dessous, la mesure ne sait rien dire.</summary>
    private const double SmallestStep = 1;

    private readonly record struct Point(DateTimeOffset At, double Value);

    /// <param name="hasBattery">Ce PC a une batterie : sans cela, ou tant que MAUS ne le sait pas, rien n'est dit de ce qui ne le concerne pas.</param>
    public static HealthLogSummary Build(IReadOnlyList<HealthLogEntry> entries, bool hasBattery = false)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var audits = entries.Where(e => e.Cooling is null).OrderBy(e => e.At).ToList();
        var coolings = entries.Where(e => e.Cooling is not null).OrderBy(e => e.At).ToList();
        var last = audits.LastOrDefault();
        var rows = new List<HealthLogRow>();
        var notes = new List<string>();
        var unread = new List<string>();

        AddDisks(audits, last, rows, notes, unread);
        AddBatteries(audits, last, rows, notes, unread, hasBattery);
        AddBoots(audits, last, rows, notes, unread);

        // Une seule phrase pour tout ce que l'audit sans droits n'a pas pu lire : le conseil est le même.
        if (unread.Count > 0)
        {
            notes.Add(T("Non lu sans droits administrateur : {0}. Lancez un audit avec « Toutes les fonctionnalités » pour les noter.", string.Join(", ", unread)));
        }

        AddCooling(coolings, rows, notes);

        var lastLine = entries.Count == 0 ? string.Empty : LastLineText(entries.Count, entries.Max(e => e.At));
        return new HealthLogSummary(rows, notes, lastLine);
    }

    private static string LastLineText(int count, DateTimeOffset at) => T("{0}, la dernière le {1}.", Plural(count, T("{0} ligne dans le carnet"), T("{0} lignes dans le carnet")), Day(at));

    private static void AddDisks(List<HealthLogEntry> audits, HealthLogEntry? last, List<HealthLogRow> rows, List<string> notes, List<string> unread)
    {
        var groups = audits
            .SelectMany(e => (e.Disks ?? []).Select(d => (e.At, Disk: d)))
            .GroupBy(x => (x.Disk.Id, x.Disk.Name, x.Disk.Source))
            .OrderBy(g => g.Key.Id, StringComparer.Ordinal)
            .ToList();
        foreach (var group in groups)
        {
            var (id, name, source) = group.Key;
            var inLast = last?.Disks?.FirstOrDefault(d => d.Id == id && d.Name == name && d.Source == source);
            var wear = group.Where(x => x.Disk.WearPercent is not null).Select(x => new Point(x.At, x.Disk.WearPercent!.Value)).ToList();
            if (wear.Count > 0)
            {
                rows.Add(MeasureRow(T("Usure du disque {0}", name), wear, Percent, Points, inLast?.WearPercent is not null, last));
            }

            var errors = group.Where(x => x.Disk.Errors is not null).Select(x => new Point(x.At, x.Disk.Errors!.Value)).ToList();
            if (errors.Count > 0)
            {
                var label = source == HealthLog.SourceNvme ? T("Erreurs de données non corrigées du disque {0}", name) : T("Erreurs de lecture non corrigées du disque {0}", name);
                rows.Add(ErrorsRow(label, errors, inLast?.Errors is not null, last));
            }
        }

        if (groups.Count == 0 && last is not null)
        {
            if (last.Elevated)
            {
                notes.Add(T("Usure et erreurs des disques : non lues. Fréquent pour un disque USB ou derrière un contrôleur RAID (plusieurs disques en un)."));
            }
            else
            {
                unread.Add(T("usure et erreurs des disques"));
            }
        }
    }

    private static void AddBatteries(List<HealthLogEntry> audits, HealthLogEntry? last, List<HealthLogRow> rows, List<string> notes, List<string> unread, bool hasBattery)
    {
        var groups = audits
            .SelectMany(e => (e.Batteries ?? []).Select(b => (e.At, Battery: b)))
            .GroupBy(x => x.Battery.Id)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        foreach (var group in groups)
        {
            var label = groups.Count == 1 ? T("Capacité de la batterie") : T("Capacité de la batterie {0}", group.Key);
            var read = last?.Batteries?.Any(b => b.Id == group.Key) == true;
            rows.Add(MeasureRow(label, [.. group.Select(x => new Point(x.At, x.Battery.CapacityPercent))], Percent, Points, read, last));
        }

        if (groups.Count == 0 && last is not null && hasBattery)
        {
            if (last.Elevated)
            {
                notes.Add(T("Batterie : aucune capacité lue. Windows ne donne pas sa capacité."));
            }
            else
            {
                unread.Add(T("capacité de la batterie"));
            }
        }
    }

    private static void AddBoots(List<HealthLogEntry> audits, HealthLogEntry? last, List<HealthLogRow> rows, List<string> notes, List<string> unread)
    {
        var boots = HealthLog.BootTimes(audits);
        if (boots.Count > 0)
        {
            rows.Add(BootRow(boots, last));
        }
        else if (last is not null)
        {
            if (last.Elevated)
            {
                notes.Add(T("Temps de démarrage : aucune mesure de Windows ces 60 derniers jours. Avec le démarrage rapide actif, Windows en enregistre rarement."));
            }
            else
            {
                unread.Add(T("temps de démarrage"));
            }
        }
    }

    private static void AddCooling(List<HealthLogEntry> coolings, List<HealthLogRow> rows, List<string> notes)
    {
        if (coolings.Count == 0)
        {
            notes.Add(T("Chauffe : pas encore de test. Le test de refroidissement est dans l'Atelier, onglet Tests."));
            return;
        }

        var latest = coolings[^1];
        var points = coolings.Select(e => new Point(e.At, e.Cooling!.RiseC)).ToList();
        var measure = latest.Cooling!;
        var current = measure switch
        {
            { IdleC: { } idle, PeakC: { } peak } => T("Actuel : {0} °C au-dessus du repos ({1} °C, pic à {2} °C), le {3}", Number(measure.RiseC), Number(idle), Number(peak), Day(latest.At)),
            { DatedByFile: true } => T("Actuel : {0} °C au-dessus du repos, le {1} (date du fichier, ancien test)", Number(measure.RiseC), Day(latest.At)),
            _ => T("Actuel : {0} °C au-dessus du repos, le {1}", Number(measure.RiseC), Day(latest.At)),
        };
        var first = coolings.Count < 2 ? null : FirstCooling(coolings[0]);
        var variation = Variation(points, Degrees) + " " + T("La température de la pièce compte aussi.");
        rows.Add(new HealthLogRow(T("Chauffe au test de refroidissement"), current, first, null, variation, null, Curve(points)));
    }

    private static string FirstCooling(HealthLogEntry entry) => entry.Cooling!.DatedByFile
        ? T("Première valeur : {0} °C, le {1} (date du fichier, ancien test)", Number(entry.Cooling.RiseC), Day(entry.At))
        : T("Première valeur : {0} °C, le {1}", Number(entry.Cooling.RiseC), Day(entry.At));

    private static HealthLogRow MeasureRow(string label, List<Point> points, Func<double, string> value, Func<double, string> gap, bool readAtLast, HealthLogEntry? last) => new(
        label,
        T("Actuel : {0}, le {1}", value(points[^1].Value), Day(points[^1].At)),
        points.Count < 2 ? null : T("Première valeur : {0}, le {1}", value(points[0].Value), Day(points[0].At)),
        null,
        Variation(points, gap),
        readAtLast ? null : UnreadText(last, points[^1].At),
        Curve(points));

    /// <summary>Le constat du disque dit « surveillez si ce nombre augmente » : ici on le surveille. Toute erreur compte, il n'y a pas de petite variation.</summary>
    private static HealthLogRow ErrorsRow(string label, List<Point> points, bool readAtLast, HealthLogEntry? last)
    {
        var (first, latest) = (points[0], points[^1]);
        var gap = latest.Value - first.Value;
        string variation;
        if (points.Count < 2)
        {
            variation = OnePoint();
        }
        else if (gap > 0)
        {
            variation = T("{0} de plus depuis le {1}. Ce nombre a augmenté : gardez une sauvegarde à jour.", Number(gap), Day(first.At));
        }
        else if (gap < 0)
        {
            variation = T("{0} de moins depuis le {1} : compteur remis à zéro, probablement.", Number(-gap), Day(first.At));
        }
        else
        {
            variation = T("Aucun changement depuis le {0}.", Day(first.At));
        }

        return new HealthLogRow(
            label,
            T("Actuel : {0}, le {1}", Number(latest.Value), Day(latest.At)),
            points.Count < 2 ? null : T("Première valeur : {0}, le {1}", Number(first.Value), Day(first.At)),
            null,
            variation,
            readAtLast ? null : UnreadText(last, latest.At),
            Curve(points));
    }

    /// <summary>
    /// Le temps de démarrage varie d'un jour à l'autre : la médiane et la plage plutôt qu'un chiffre seul. La première moitié des
    /// démarrages sert de référence : c'est ce PC qui dit ce qui est ordinaire pour lui, pas un seuil.
    /// </summary>
    private static HealthLogRow BootRow(IReadOnlyList<DatedBootTime> boots, HealthLogEntry? last)
    {
        var latest = boots[^1];
        var recent = boots.TakeLast(BootsInRange).Select(b => b.Seconds).ToList();
        return new HealthLogRow(
            T("Temps de démarrage"),
            T("Dernier démarrage : {0} s, le {1}", Number(latest.Seconds), Day(latest.At)),
            boots.Count < 2 ? null : T("Premier mesuré : {0} s, le {1}", Number(boots[0].Seconds), Day(boots[0].At)),
            recent.Count < 2 ? null : T("En général {0} s, de {1} à {2} s, sur les {3} derniers démarrages", Number(Median(recent)), Number(recent.Min()), Number(recent.Max()), recent.Count),
            BootVariation(boots),
            last?.Boots is { Count: > 0 } || last is null ? null : UnreadText(last, null),
            [.. boots.TakeLast(PointsOnCurve).Select(b => b.Seconds)]);
    }

    private static string BootVariation(IReadOnlyList<DatedBootTime> boots)
    {
        if (boots.Count < 4)
        {
            return Plural(boots.Count, T("{0} démarrage mesuré : c'est encore peu pour comparer, cette durée varie d'un jour à l'autre."), T("{0} démarrages mesurés : c'est encore peu pour comparer, cette durée varie d'un jour à l'autre."));
        }

        var middle = boots.Count / 2;
        var before = boots.Take(middle).Select(b => b.Seconds).ToList();
        var after = boots.Skip(middle).Select(b => b.Seconds).ToList();
        var (was, now, low, high) = (Median(before), Median(after), before.Min(), before.Max());
        return now >= low && now <= high
            ? T("Ces derniers temps, en général {0} s, contre {1} s au début : cela reste dans l'écart du début ({2} à {3} s). C'est probablement la variation ordinaire d'un jour à l'autre.", Number(now), Number(was), Number(low), Number(high))
            : T("Ces derniers temps, en général {0} s, contre {1} s au début : hors de l'écart du début ({2} à {3} s). MAUS constate ce changement, sans en connaître la cause.", Number(now), Number(was), Number(low), Number(high));
    }

    /// <summary>
    /// Une variation est petite si elle ne dépasse pas le plus petit écart mesurable, ou si la valeur reste dans ce que ce PC a déjà
    /// montré : aucun seuil de santé là-dedans.
    /// </summary>
    private static string Variation(List<Point> points, Func<double, string> gap)
    {
        if (points.Count < 2)
        {
            return OnePoint();
        }

        var (first, latest) = (points[0], points[^1]);
        var change = latest.Value - first.Value;
        if (change == 0)
        {
            return T("Aucun changement depuis le {0}.", Day(first.At));
        }

        var earlier = points.SkipLast(1).Select(p => p.Value).ToList();
        var seenBefore = earlier.Count >= 2 && latest.Value >= earlier.Min() && latest.Value <= earlier.Max();
        return Math.Abs(change) <= SmallestStep || seenBefore
            ? T("Petite variation : {0} depuis le {1}, à ne pas surinterpréter.", gap(change), Day(first.At))
            : T("{0} depuis le {1}.", gap(change), Day(first.At));
    }

    private static string OnePoint() => T("Une seule mesure pour l'instant : la comparaison viendra avec les suivantes.");

    private static string UnreadText(HealthLogEntry? last, DateTimeOffset? seenAt)
    {
        var at = last is null ? string.Empty : Day(last.At);
        return (seenAt, last?.Elevated == true) switch
        {
            ({ } seen, true) => T("Non lu au dernier audit, le {0} : cette valeur date du {1}.", at, Day(seen)),
            ({ } seen, false) => T("Non lu sans droits administrateur au dernier audit, le {0} : cette valeur date du {1}.", at, Day(seen)),
            (null, true) => T("Non lu au dernier audit, le {0}.", at),
            (null, false) => T("Non lu sans droits administrateur au dernier audit, le {0}.", at),
        };
    }

    private static List<double> Curve(List<Point> points) => [.. points.TakeLast(PointsOnCurve).Select(p => p.Value)];

    /// <summary>Médiane ; pas un nombre pour une liste vide, que personne n'affiche : chaque appelant vérifie d'abord qu'il a assez de valeurs.</summary>
    internal static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }

        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static string Percent(double value) => T("{0} %", Number(value));

    private static string Points(double change) => Math.Abs(change) <= 1 ? T("{0} point", Signed(change)) : T("{0} points", Signed(change));

    private static string Degrees(double change) => T("{0} °C", Signed(change));

    private static string Signed(double change) => (change > 0 ? "+" : string.Empty) + Number(change);

    private static string Number(double value) => value.ToString("0.#", Culture);

    private static string Day(DateTimeOffset at) => at.LocalDateTime.ToString("d", Culture);
}
