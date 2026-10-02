using System.Globalization;
using System.Text;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Minimum, moyenne et maximum d'une mesure sur la session (valeurs absentes ignorées).</summary>
public readonly record struct SessionStat(double? Min, double? Average, double? Max)
{
    public static SessionStat Of(IEnumerable<double?> values)
    {
        var list = values.OfType<double>().ToList();
        return list.Count == 0 ? default : new SessionStat(list.Min(), list.Average(), list.Max());
    }
}

/// <summary>Bilan d'une session : ce qui a chauffé, ce qui a limité, ce qui a manqué.</summary>
public sealed record SessionSummary(
    TimeSpan Duration,
    int Samples,
    SessionStat CpuLoad,
    SessionStat CpuMhz,
    SessionStat CpuTemperature,
    SessionStat CpuPower,
    SessionStat GpuLoad,
    SessionStat GpuTemperature,
    SessionStat GpuClock,
    SessionStat GpuPower,
    SessionStat Memory,
    IReadOnlyList<string> Findings);

/// <summary>
/// Relevé pendant une partie (ou tout autre usage) : un échantillon par seconde, puis un bilan honnête et un export CSV.
/// Rien n'est envoyé : le fichier reste sur le PC.
/// </summary>
public sealed class SessionRecording(DateTimeOffset start)
{
    private readonly List<SensorSnapshot> _samples = [];

    public DateTimeOffset Start { get; } = start;

    public int Count => _samples.Count;

    public IReadOnlyList<SensorSnapshot> Samples => _samples;

    public void Add(SensorSnapshot snapshot) => _samples.Add(snapshot);

    public TimeSpan Elapsed => _samples.Count == 0 ? TimeSpan.Zero : _samples[^1].At - Start;

    /// <param name="cpuMaxC">Limite de température du processeur selon son fabricant, si connue.</param>
    public SessionSummary Summarize(int? cpuMaxC)
    {
        static GpuSensor? MainGpu(SensorSnapshot s) => s.Gpus.OrderByDescending(g => g.UtilizationPercent ?? 0).FirstOrDefault();
        var findings = new List<string>();
        var n = _samples.Count;

        var cpuTemps = _samples.Select(s => s.CpuTemperatureC).ToList();
        var chipLimit = _samples.Select(s => s.CpuTjMaxC).FirstOrDefault(t => t is not null);
        if (cpuTemps.Any(t => t is not null) && (chipLimit ?? cpuMaxC) is { } max)
        {
            var hot = Share(cpuTemps.Select(t => t is { } v ? v >= max - 3 : (bool?)null));
            if (hot >= 10)
            {
                findings.Add(T("Le processeur a passé {0:0} % du temps à sa limite de température ({1} °C) : il a ralenti pour se protéger. Vérifiez le refroidissement (pâte thermique, ventilateurs, poussière) ; sur certains modèles récents, c'est aussi leur fonctionnement normal en pleine charge.", hot, max));
            }
        }

        var gpuSlow = _samples.Select(s => MainGpu(s) is { TemperatureC: { } t, SlowdownTemperatureC: { } limit } ? t >= limit : (bool?)null).ToList();
        if (gpuSlow.Any(v => v is not null) && Share(gpuSlow) is var slowShare and >= 10)
        {
            findings.Add(T("La carte graphique a atteint son seuil de ralentissement {0:0} % du temps : elle a baissé ses fréquences. Nettoyez-la, aérez le boîtier ou ajustez la courbe de ses ventilateurs.", slowShare));
        }

        // Goulot d'étranglement : un cœur saturé pendant que la carte graphique attend, ou l'inverse.
        var busy = _samples.Where(s => MainGpu(s)?.UtilizationPercent is not null && s.CorePercents.Count > 0).ToList();
        if (busy.Count >= 30)
        {
            var cpuBound = busy.Count(s => s.CorePercents.Max() >= 95 && MainGpu(s)!.UtilizationPercent < 85) * 100.0 / busy.Count;
            var gpuBound = busy.Count(s => MainGpu(s)!.UtilizationPercent >= 95) * 100.0 / busy.Count;
            if (cpuBound >= 30)
            {
                findings.Add(T("Limité par le processeur {0:0} % du temps : au moins un cœur était saturé pendant que la carte graphique attendait. Baisser la résolution n'aidera pas ; les réglages qui chargent le processeur (distance d'affichage, foule, physique) si. Une mémoire plus rapide aide un peu (quelques pour cent).", cpuBound));
            }
            else if (gpuBound >= 50)
            {
                findings.Add(T("Limité par la carte graphique {0:0} % du temps : c'est le cas normal en jeu. Pour plus d'images par seconde, baissez la résolution ou les réglages graphiques, ou activez DLSS / FSR / XeSS.", gpuBound));
            }
        }

        var memory = _samples.Select(s => s.MemoryPercent).ToList();
        if (memory.Any(m => m >= 90))
        {
            findings.Add(T("La mémoire vive a dépassé 90 % : Windows a dû utiliser le disque, ce qui provoque des saccades. Fermez les applications en arrière-plan (navigateur…) ou ajoutez de la mémoire."));
        }

        if (findings.Count == 0 && n > 0)
        {
            findings.Add(T("Aucun signe de surchauffe, de mémoire pleine ni de goulot d'étranglement marqué pendant cette session."));
        }

        return new SessionSummary(
            Elapsed,
            n,
            SessionStat.Of(_samples.Select(s => s.CpuPercent)),
            SessionStat.Of(_samples.Select(s => s.CpuMhz)),
            SessionStat.Of(cpuTemps),
            SessionStat.Of(_samples.Select(s => s.CpuPowerWatts)),
            SessionStat.Of(_samples.Select(s => MainGpu(s)?.UtilizationPercent)),
            SessionStat.Of(_samples.Select(s => MainGpu(s)?.TemperatureC)),
            SessionStat.Of(_samples.Select(s => (double?)MainGpu(s)?.GraphicsClockMhz)),
            SessionStat.Of(_samples.Select(s => MainGpu(s)?.PowerWatts)),
            SessionStat.Of(memory),
            findings);
    }

    /// <summary>
    /// CSV avec le séparateur de liste et la virgule décimale de <paramref name="culture"/>, prêt pour Excel ou LibreOffice.
    /// L'application passe <see cref="Localization.Texts.RegionalCulture"/> (format régional de Windows), jamais la culture de
    /// la langue de MAUS : Windows en anglais avec le format français attend « ; » et « 12,5 ». Les en-têtes sont dans la langue de MAUS.
    /// </summary>
    public string ToCsv(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var separator = culture.TextInfo.ListSeparator;
        string F(double? value) => value?.ToString("0.#", culture) ?? string.Empty;
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(separator,
            T("Heure"), T("Processeur %"), T("Cœur le plus chargé %"), T("Processeur MHz"), T("Processeur °C"), T("Processeur W"),
            T("Carte graphique %"), T("Carte graphique °C"), T("Carte graphique MHz"), T("Carte graphique W"), T("Mémoire %")));
        foreach (var s in _samples)
        {
            var gpu = s.Gpus.OrderByDescending(g => g.UtilizationPercent ?? 0).FirstOrDefault();
            csv.AppendLine(string.Join(separator,
                s.At.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                F(s.CpuPercent), F(s.CorePercents.Count > 0 ? s.CorePercents.Max() : null), F(s.CpuMhz), F(s.CpuTemperatureC), F(s.CpuPowerWatts),
                F(gpu?.UtilizationPercent), F(gpu?.TemperatureC), F(gpu?.GraphicsClockMhz), F(gpu?.PowerWatts), F(s.MemoryPercent)));
        }

        return csv.ToString();
    }

    private static double Share(IEnumerable<bool?> values)
    {
        var known = values.OfType<bool>().ToList();
        return known.Count == 0 ? 0 : known.Count(v => v) * 100.0 / known.Count;
    }
}
