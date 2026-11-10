using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Une ligne de l'historique de Windows : une période et la capacité de la batterie sur cette période.</summary>
/// <param name="Start">Début de la période, à l'heure locale.</param>
/// <param name="End">Fin de la période, à l'heure locale.</param>
/// <param name="DesignMwh">Capacité d'origine.</param>
/// <param name="FullChargeMwh">Capacité à pleine charge.</param>
/// <param name="BatteryChanged">Windows signale un changement de batterie à cette période.</param>
public sealed record BatteryCapacityPoint(DateTime Start, DateTime End, int DesignMwh, int FullChargeMwh, bool BatteryChanged)
{
    public int Percent => (int)Math.Round(100.0 * FullChargeMwh / DesignMwh, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Capacité passée de la batterie, lue dans <c>powercfg /batteryreport /output … /xml</c>. Le début est celui des données que
/// Windows garde, jamais l'achat. Aucun seuil de remplacement, aucune valeur de garantie : MAUS rapporte, sans juger.
/// </summary>
/// <param name="Points">Périodes ayant une capacité, dans l'ordre ; vide si le rapport n'en contient aucune (PC sans batterie).</param>
public sealed record BatteryHistory(IReadOnlyList<BatteryCapacityPoint> Points)
{
    /// <summary>Nombre de valeurs de la courbe : assez pour voir une tendance sur une petite carte.</summary>
    internal const int CurveLength = 48;

    /// <summary>Début réel des données, ou <c>null</c> sans données.</summary>
    public DateTime? Since => Points.Count == 0 ? null : Points[0].Start;

    /// <summary>
    /// Courbe à intervalles de temps égaux, en % de la capacité d'origine. Windows donne des semaines puis des jours : point par
    /// point, les derniers jours prendraient autant de place que des mois. Entre deux relevés, la dernière valeur connue.
    /// </summary>
    public IReadOnlyList<int> Curve => CurveIndexes().Select(index => Points[index].Percent).ToList();

    /// <summary>Position, dans <see cref="Curve"/>, de chaque changement de batterie signalé par Windows.</summary>
    public IReadOnlyList<int> CurveChanges
    {
        get
        {
            var indexes = CurveIndexes();
            return Points.Select((point, index) => (point, index))
                .Where(p => p.index > 0 && p.point.BatteryChanged)
                .Select(p => indexes.FindIndex(i => i >= p.index))
                .Where(position => position > 0)
                .Distinct()
                .ToList();
        }
    }

    /// <summary>Le relevé en français (ou dans la langue active), du début réel des données au dernier relevé.</summary>
    public IReadOnlyList<string> Describe()
    {
        if (Points.Count == 0)
        {
            return [T("Ce rapport de Windows ne contient aucune capacité de batterie.")];
        }

        var first = Points[0];
        var last = Points[^1];
        var lines = new List<string>
        {
            T("Données depuis le {0} : c'est le début de ce que Windows garde, pas la date d'achat.", first.Start.ToString("d", Culture)),
            T("Au début : {0} % de la capacité d'origine ({1} / {2} mWh).", first.Percent, Mwh(first.FullChargeMwh), Mwh(first.DesignMwh)),
        };
        if (Points.Count > 1)
        {
            lines.Add(T("Dernier relevé, le {0} : {1} % de la capacité d'origine ({2} / {3} mWh).", last.End.ToString("d", Culture), last.Percent, Mwh(last.FullChargeMwh), Mwh(last.DesignMwh)));
        }

        lines.AddRange(Points.Where(point => point.BatteryChanged).Select(point =>
            T("Windows signale un changement de batterie vers le {0} : avant et après ne se comparent pas.", point.Start.ToString("d", Culture))));
        lines.Add(T("Ce relevé ne dit pas s'il faut changer la batterie (aucun seuil général n'est publié) et n'est pas une preuve pour une garantie."));
        return lines;
    }

    private static string Mwh(int value) => value.ToString("N0", Culture);

    /// <summary>Pour chaque valeur de la courbe, la dernière période commencée à cette date.</summary>
    private List<int> CurveIndexes()
    {
        var indexes = new List<int>();
        if (Points.Count < 2)
        {
            return indexes;
        }

        var start = Points[0].Start;
        var step = (Points[^1].Start - start) / (CurveLength - 1);
        var current = 0;
        for (var position = 0; position < CurveLength; position++)
        {
            var date = position == CurveLength - 1 ? Points[^1].Start : start + (step * position);
            while (current + 1 < Points.Count && Points[current + 1].Start <= date)
            {
                current++;
            }

            indexes.Add(current);
        }

        return indexes;
    }
}

/// <summary>
/// Lecture stricte du rapport XML de Windows. Microsoft documente l'option <c>/xml</c>, pas le contenu du fichier : les noms lus
/// ici sont ceux observés dans un vrai fichier de Windows 11. Le moindre écart donne « non lu » (<c>null</c>), jamais une valeur
/// devinée. Seules la version, la date du rapport, l'unité et l'historique sont lus : ni le nom du PC ni le numéro de série.
/// </summary>
public static class BatteryHistoryReader
{
    private const string Namespace = "http://schemas.microsoft.com/battery/2012";
    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss";

    /// <summary>Taille maximale admise : un vrai rapport fait quelques dizaines de Ko.</summary>
    internal const long MaxBytes = 2 * 1024 * 1024;

    private const int MaxEntries = 5000;

    /// <summary>Plafond très large, pour écarter une valeur absurde (1 kWh, dix fois une batterie de portable).</summary>
    private const int MaxCapacityMwh = 1_000_000;

    /// <summary>Lit le rapport ; <c>null</c> = non lu.</summary>
    public static BatteryHistory? Parse(Stream report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxBytes,
        };
        try
        {
            using var reader = XmlReader.Create(report, settings);
            return Read(XDocument.Load(reader).Root);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Fichier changé entre l'écriture et la lecture, date hors limites… : « non lu », jamais une erreur inattendue.
            return null;
        }
    }

    private static BatteryHistory? Read(XElement? root)
    {
        XNamespace ns = Namespace;
        if (root is null || root.Name != ns + "BatteryReport"
            || Single(root, ns + "ReportInformation") is not { } information
            || Single(root, ns + "Batteries") is not { } batteries
            || Single(root, ns + "History") is not { } history
            || Text(Single(information, ns + "ReportVersion"))?.Split('.')[0] != "1"
            || !TryDate(Text(Single(information, ns + "LocalScanTime")), out var scan))
        {
            return null;
        }

        // Unité : une batterie en capacité « relative » ne parle pas en mWh. Élément absent : rien ne le dit, donc rien à refuser.
        if (batteries.Elements(ns + "Battery").SelectMany(b => b.Elements(ns + "RelativeCapacity")).Any(relative => Text(relative) != "0"))
        {
            return null;
        }

        var entries = history.Elements().ToList();
        if (entries.Count > MaxEntries || entries.Any(entry => entry.Name != ns + "HistoryEntry"))
        {
            return null;
        }

        var points = new List<BatteryCapacityPoint>();
        DateTime? previous = null;
        var changed = false;
        foreach (var entry in entries)
        {
            if (!TryDate((string?)entry.Attribute("LocalStartDate"), out var start)
                || !TryDate((string?)entry.Attribute("LocalEndDate"), out var end)
                || !TryCapacity((string?)entry.Attribute("DesignCapacity"), out var design)
                || !TryCapacity((string?)entry.Attribute("FullChargeCapacity"), out var full)
                || (string?)entry.Attribute("BatteryChanged") is not ("0" or "1")
                || end < start || end - scan > TimeSpan.FromDays(1) || start <= previous
                || (design > 0 && full > design * 2L))
            {
                return null;
            }

            previous = start;
            changed |= (string?)entry.Attribute("BatteryChanged") == "1";

            // Windows écrit 0 pour une période sans capacité connue (vu sur un PC sans batterie) : elle n'est pas un relevé.
            if (design > 0 && full > 0)
            {
                points.Add(new BatteryCapacityPoint(start, end, design, full, changed));
                changed = false;
            }
        }

        return new BatteryHistory(points);
    }

    /// <summary>L'élément, s'il est là une seule fois.</summary>
    private static XElement? Single(XElement parent, XName name)
    {
        var found = parent.Elements(name).Take(2).ToList();
        return found.Count == 1 ? found[0] : null;
    }

    /// <summary>Texte d'un élément qui n'en contient pas d'autres ; sinon <c>null</c> (un élément emboîté n'est jamais lu).</summary>
    private static string? Text(XElement? element) => element is null || element.HasElements ? null : element.Value;

    private static bool TryDate(string? text, out DateTime date) =>
        DateTime.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static bool TryCapacity(string? text, out int capacity) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out capacity) && capacity <= MaxCapacityMwh;
}
