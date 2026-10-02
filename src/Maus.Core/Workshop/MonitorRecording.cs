using System.Globalization;
using System.Text;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Statistiques d'une mesure pendant un relevé.</summary>
public sealed record MonitorStat(string Component, string Name, MonitorKind Kind, double Min, double Average, double Max);

/// <summary>
/// Relevé de la fenêtre de surveillance (pendant un test de stabilité, par exemple) : chaque échantillon est gardé pour le
/// bilan et l'export CSV. Il ne relit que les mesures déjà prises par l'atelier.
/// </summary>
public sealed class MonitorRecording(DateTimeOffset start)
{
    private readonly List<(DateTimeOffset At, IReadOnlyList<MonitorRow> Rows)> _samples = [];

    public DateTimeOffset Start { get; } = start;

    public int Count => _samples.Count;

    public TimeSpan Elapsed => _samples.Count == 0 ? TimeSpan.Zero : _samples[^1].At - Start;

    public void Add(DateTimeOffset at, IReadOnlyList<MonitorRow> rows) => _samples.Add((at, rows));

    /// <summary>Minimum, moyenne et maximum de chaque mesure sur tout le relevé.</summary>
    public IReadOnlyList<MonitorStat> Stats() => _samples
        .SelectMany(s => s.Rows)
        .GroupBy(r => (r.Component, r.Name, r.Kind))
        .Select(g => new MonitorStat(g.Key.Component, g.Key.Name, g.Key.Kind, g.Min(r => r.Value), g.Average(r => r.Value), g.Max(r => r.Value)))
        .ToList();

    /// <summary>
    /// Bilan en quelques lignes : durée, températures et consommations maximales par composant, fréquences, et verdict sur
    /// les erreurs relevées pendant le relevé (erreurs WHEA et erreurs de Windows depuis son début).
    /// </summary>
    public string Summary(int hardwareErrors, int pciExpressErrors, int windowsErrors)
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
            ? T("Aucune erreur matérielle (WHEA) pendant le relevé : bon signe de stabilité.")
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
        var separator = culture.TextInfo.ListSeparator;
        var columns = _samples.SelectMany(s => s.Rows).Select(r => (r.Component, r.Name, r.Kind)).Distinct().ToList();
        static string Unit(MonitorKind kind) => kind switch
        {
            MonitorKind.Temperature => "°C",
            MonitorKind.Power => "W",
            MonitorKind.Clock => "MHz",
            _ => "%",
        };

        // Guillemets autour d'un nom qui contient le séparateur régional (il peut avoir été personnalisé), « ; », « , » ou « " ».
        string Cell(string text) => text.Contains('"', StringComparison.Ordinal) || text.Contains(';', StringComparison.Ordinal) || text.Contains(',', StringComparison.Ordinal)
            || (separator.Length > 0 && text.Contains(separator, StringComparison.Ordinal))
            ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : text;

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(separator, new[] { T("Heure") }.Concat(columns.Select(c => Cell($"{c.Component} · {c.Name} ({Unit(c.Kind)})")))));
        foreach (var (at, rows) in _samples)
        {
            var values = columns.Select(c => rows.FirstOrDefault(r => r.Component == c.Component && r.Name == c.Name && r.Kind == c.Kind) is { } row
                ? row.Value.ToString("0.#", culture)
                : string.Empty);
            csv.AppendLine(string.Join(separator, new[] { at.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture) }.Concat(values)));
        }

        return csv.ToString();
    }
}
