using System.Globalization;
using System.Text;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Statistiques d'une mesure pendant un relevé.</summary>
public sealed record MonitorStat(string Component, string Name, MonitorKind Kind, double Min, double Average, double Max);

/// <summary>
/// Relevé de la fenêtre de surveillance (pendant un test de stabilité, par exemple) : chaque échantillon est gardé pour le
/// bilan et l'export CSV. Il ne relit que les mesures déjà prises par l'atelier. Un relevé peut durer des heures : les noms
/// ne sont gardés qu'une fois, dans une table des colonnes, et chaque échantillon n'a que ses valeurs.
/// </summary>
public sealed class MonitorRecording(DateTimeOffset start)
{
    // Une colonne par mesure, numérotée à sa première apparition, et un tableau de valeurs par échantillon : un long relevé
    // pèse bien moins qu'une liste d'objets, et l'export n'a plus à chercher chaque mesure dans chaque échantillon.
    private readonly Dictionary<(string Component, string Name, MonitorKind Kind), int> _numbers = [];
    private readonly List<(string Component, string Name, MonitorKind Kind)> _columns = [];
    private readonly List<(DateTimeOffset At, double[] Values)> _samples = [];

    public DateTimeOffset Start { get; } = start;

    public int Count => _samples.Count;

    public TimeSpan Elapsed => _samples.Count == 0 ? TimeSpan.Zero : _samples[^1].At - Start;

    public void Add(DateTimeOffset at, IReadOnlyList<MonitorRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows)
        {
            var key = (row.Component, row.Name, row.Kind);
            if (!_numbers.ContainsKey(key))
            {
                _numbers[key] = _columns.Count;
                _columns.Add(key);
            }
        }

        var values = new double[_columns.Count];
        Array.Fill(values, double.NaN);
        foreach (var row in rows)
        {
            // Deux mesures de même nom dans un échantillon : la colonne garde la première.
            ref var value = ref values[_numbers[(row.Component, row.Name, row.Kind)]];
            if (double.IsNaN(value))
            {
                value = row.Value;
            }
        }

        _samples.Add((at, values));
    }

    /// <summary>Valeur d'une colonne dans un échantillon ; NaN si elle n'y était pas (capteur absent, ou apparu plus tard).</summary>
    private static double ValueAt(double[] values, int column) => column < values.Length ? values[column] : double.NaN;

    /// <summary>Minimum, moyenne et maximum de chaque mesure sur tout le relevé.</summary>
    public IReadOnlyList<MonitorStat> Stats()
    {
        var stats = new List<MonitorStat>(_columns.Count);
        for (var column = 0; column < _columns.Count; column++)
        {
            var (min, max, sum, count) = (double.MaxValue, double.MinValue, 0.0, 0);
            foreach (var (_, values) in _samples)
            {
                var value = ValueAt(values, column);
                if (!double.IsNaN(value))
                {
                    (min, max, sum, count) = (Math.Min(min, value), Math.Max(max, value), sum + value, count + 1);
                }
            }

            if (count > 0)
            {
                var (component, name, kind) = _columns[column];
                stats.Add(new MonitorStat(component, name, kind, min, sum / count, max));
            }
        }

        return stats;
    }

    /// <summary>
    /// Bilan en quelques lignes : durée, températures et consommations maximales par composant, fréquences, et verdict sur
    /// les erreurs relevées pendant le relevé (erreurs WHEA et erreurs de Windows depuis son début).
    /// </summary>
    public string Summary(int hardwareErrors, int pciExpressErrors, int windowsErrors, bool incomplete = false)
    {
        var text = new StringBuilder();
        text.AppendLine(T("Relevé de {0:hh\\:mm\\:ss}, {1} mesures.", Elapsed, Count));
        foreach (var component in Stats().GroupBy(s => s.Component))
        {
            var parts = new List<string>();
            foreach (var stat in component.Where(s => s.Kind == MonitorKind.Temperature).OrderByDescending(s => s.Max).Take(1))
            {
                parts.Add(T("température max. {0} °C ({1})", stat.Max.ToString("0", CultureInfo.CurrentCulture), stat.Name));
            }

            foreach (var stat in component.Where(s => s.Kind == MonitorKind.Power).OrderByDescending(s => s.Max).Take(1))
            {
                parts.Add(T("consommation max. {0} W", stat.Max.ToString("0", CultureInfo.CurrentCulture)));
            }

            foreach (var stat in component.Where(s => s.Kind == MonitorKind.Clock).OrderByDescending(s => s.Max).Take(1))
            {
                parts.Add(T("fréquence {0} à {1} MHz", stat.Min.ToString("0", CultureInfo.CurrentCulture), stat.Max.ToString("0", CultureInfo.CurrentCulture)));
            }

            if (parts.Count > 0)
            {
                text.AppendLine(CultureInfo.CurrentCulture, $"• {component.Key} : {string.Join(" · ", parts)}");
            }
        }

        text.Append(hardwareErrors == 0
            ? incomplete
                ? T("Journaux de Windows illisibles : les erreurs matérielles de ce relevé sont inconnues.")
                : T("Aucune erreur matérielle (WHEA) pendant le relevé : bon signe de stabilité.")
            : T("{0} erreur(s) matérielle(s) (WHEA) pendant le relevé, dont {1} du bus PCI Express : le PC n'est pas stable dans ces conditions (réglage trop poussé, température, alimentation ou matériel à vérifier).", hardwareErrors, pciExpressErrors));
        if (windowsErrors > 0)
        {
            text.AppendLine();
            text.Append(T("{0} erreur(s) de Windows pendant le relevé : voyez la liste des événements pour savoir si elles ont un lien avec le test.", windowsErrors));
        }

        return text.ToString();
    }

    /// <summary>
    /// Export CSV (séparateur de liste et décimales de <paramref name="culture"/>), une colonne par mesure, une ligne par échantillon.
    /// L'application passe <see cref="Localization.Texts.RegionalCulture"/> (format régional de Windows), jamais la culture de
    /// la langue de MAUS.
    /// </summary>
    public string ToCsv(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var separator = Reporting.CsvText.Separator(culture);
        static string Unit(MonitorKind kind) => kind switch
        {
            MonitorKind.Temperature => "°C",
            MonitorKind.Power => "W",
            MonitorKind.Clock => "MHz",
            _ => "%",
        };

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(separator, new[] { Reporting.CsvText.Cell(T("Heure")) }.Concat(_columns.Select(c => Reporting.CsvText.Cell($"{c.Component} · {c.Name} ({Unit(c.Kind)})")))));
        foreach (var (at, values) in _samples)
        {
            csv.Append(at.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            for (var column = 0; column < _columns.Count; column++)
            {
                csv.Append(separator);
                if (ValueAt(values, column) is var value && !double.IsNaN(value))
                {
                    csv.Append(value.ToString("0.#", culture));
                }
            }

            csv.AppendLine();
        }

        return csv.ToString();
    }
}
