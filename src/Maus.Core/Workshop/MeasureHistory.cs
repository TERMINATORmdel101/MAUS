using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>
/// Mesures déjà lues par l'audit (usure et température des disques, capacité de la batterie) ou par l'atelier (température
/// maximale), gardées pour une courbe sur plusieurs semaines (0.8.6). Une mesure d'audit par jour au plus.
/// </summary>
public static class MeasureHistory
{
    public const string Prefix = "measure:";
    public const string Wear = "wear";
    public const string Temperature = "temperature";
    public const string Health = "health";

    /// <summary>Erreurs non corrigées d'un disque : gardées par le carnet de santé, pas dans cet historique (voir <see cref="KeyLabel"/>).</summary>
    public const string Errors = "errors";

    public static bool IsMeasure(string kind) => kind.StartsWith(Prefix, StringComparison.Ordinal);

    private static string? KeyLabel(string key) => key switch
    {
        Wear => T("usure"),
        Temperature => T("température"),
        Health => T("capacité restante"),
        _ => null,
    };

    /// <summary>Entrées à ajouter pour cet audit : rien pour une mesure déjà notée le même jour.</summary>
    public static IReadOnlyList<BenchmarkEntry> FromAudit(IEnumerable<ModuleResult> results, IReadOnlyList<BenchmarkEntry> existing, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(existing);

        var entries = new List<BenchmarkEntry>();
        foreach (var finding in results.SelectMany(r => r.Findings).Where(f => f.Measures is not null))
        {
            foreach (var (key, value) in finding.Measures!)
            {
                if (KeyLabel(key) is not { } label)
                {
                    continue;
                }

                var kind = Prefix + finding.Id + ":" + key;
                if (!existing.Any(e => e.Kind == kind && e.At.LocalDateTime.Date == at.LocalDateTime.Date))
                {
                    entries.Add(new BenchmarkEntry(kind, value, true, at, finding.Title + " : " + label));
                }
            }
        }

        return entries;
    }

    /// <summary>Température la plus haute vue pendant cette ouverture de l'atelier.</summary>
    public static BenchmarkEntry Peak(string component, string label, double celsius, DateTimeOffset at) =>
        new(Prefix + component + ":" + Temperature, celsius, true, at, label);

    public static string Unit(string kind) => kind.EndsWith(":" + Temperature, StringComparison.Ordinal) ? "°C" : "%";
}
