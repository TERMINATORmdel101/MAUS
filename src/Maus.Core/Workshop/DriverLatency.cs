using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Temps passé par les routines urgentes (DPC et interruptions) d'un pilote pendant la mesure.</summary>
/// <param name="Driver">Fichier du pilote (« nvlddmkm.sys »), ou adresse inconnue.</param>
/// <param name="MaxMicroseconds">Plus longue exécution d'affilée.</param>
/// <param name="TotalMicroseconds">Temps total occupé pendant la mesure.</param>
public sealed record DriverLatencyLine(string Driver, int Count, double MaxMicroseconds, double TotalMicroseconds);

/// <summary>Résultat d'une mesure de latence des pilotes.</summary>
public sealed record DriverLatencyResult(TimeSpan Duration, long Events, IReadOnlyList<DriverLatencyLine> Drivers, long LostEvents)
{
    /// <summary>Résumé en clair : les pilotes qui ont gardé le processeur le plus longtemps d'affilée, sans seuil inventé.</summary>
    public string Describe(int top = 5)
    {
        if (Drivers.Count == 0)
        {
            return T("Aucune routine de pilote mesurée pendant {0:0} s.", Duration.TotalSeconds);
        }

        var lines = new List<string>
        {
            T("{0} exécutions de routines urgentes de pilotes mesurées en {1:0} s. Les plus longues d'affilée :", Events, Duration.TotalSeconds),
        };
        lines.AddRange(Drivers.Take(top).Select(d =>
            T("• {0} : jusqu'à {1:0} µs d'affilée ({2} fois, {3:0.0} ms au total)", d.Driver, d.MaxMicroseconds, d.Count, d.TotalMicroseconds / 1000)));
        if (LostEvents > 0)
        {
            lines.Add(T("{0} événements perdus pendant la mesure : les valeurs réelles peuvent être un peu plus hautes.", LostEvents));
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Regroupe les exécutions de DPC et d'interruptions par pilote. Les adresses des routines (événements PerfInfo de la trace
/// du noyau, Microsoft Learn « DPC class » et « ISR class ») sont rapprochées des adresses de chargement des pilotes.
/// </summary>
public sealed class DriverLatencyAggregator
{
    private readonly (ulong Base, string Name)[] _drivers;
    private readonly Dictionary<string, (int Count, double Max, double Total)> _byDriver = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private long _events;

    /// <param name="drivers">Adresse de chargement et nom de chaque pilote chargé.</param>
    public DriverLatencyAggregator(IEnumerable<(ulong Base, string Name)> drivers)
    {
        _drivers = drivers.Where(d => d.Base != 0).OrderBy(d => d.Base).ToArray();
    }

    /// <summary>Pilote qui contient l'adresse : celui dont l'adresse de chargement est la plus haute en dessous d'elle.</summary>
    public string DriverOf(ulong routine)
    {
        var low = 0;
        var high = _drivers.Length - 1;
        var found = -1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (_drivers[mid].Base <= routine)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found < 0 ? T("adresse inconnue") : _drivers[found].Name;
    }

    /// <summary>Ajoute une exécution (durée en microsecondes).</summary>
    public void Add(ulong routine, double microseconds)
    {
        if (microseconds < 0 || !double.IsFinite(microseconds))
        {
            return;
        }

        var driver = DriverOf(routine);
        lock (_gate)
        {
            _events++;
            var (count, max, total) = _byDriver.GetValueOrDefault(driver);
            _byDriver[driver] = (count + 1, Math.Max(max, microseconds), total + microseconds);
        }
    }

    public DriverLatencyResult Result(TimeSpan duration, long lostEvents)
    {
        lock (_gate)
        {
            return new DriverLatencyResult(
                duration,
                _events,
                _byDriver.Select(d => new DriverLatencyLine(d.Key, d.Value.Count, d.Value.Max, d.Value.Total)).OrderByDescending(d => d.MaxMicroseconds).ToList(),
                lostEvents);
        }
    }
}

/// <summary>Mesure de la latence des pilotes (droits administrateur requis : trace du noyau).</summary>
public interface IDriverLatencyTracer
{
    /// <exception cref="InvalidOperationException">Trace impossible (droits, trace du noyau déjà utilisée par un autre outil).</exception>
    Task<DriverLatencyResult> MeasureAsync(TimeSpan duration, CancellationToken cancellationToken);
}
