using System.Globalization;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop.Benchmark;

/// <summary>
/// Raisons qui limitent la fréquence d'une carte NVIDIA, lues par NVML : bits <c>nvmlClocksEventReason*</c> de nvml.h
/// (NVIDIA, dépôt github.com/NVIDIA/go-nvml, fichier gen/nvml/nvml.h ; descriptions reprises de ses commentaires).
/// </summary>
public static class NvidiaClockReasons
{
    /// <summary>« Les fréquences sont ajustées pour ne pas dépasser la limite de puissance » : normal à pleine charge.</summary>
    public const ulong SwPowerCap = 0x4;

    /// <summary>Fréquence divisée par 2 ou plus : température trop haute, frein de l'alimentation ou pic de puissance.</summary>
    public const ulong HwSlowdown = 0x8;

    /// <summary>Fréquence réduite pour rester sous la température maximale de fonctionnement du GPU ou de sa mémoire.</summary>
    public const ulong SwThermalSlowdown = 0x20;

    /// <summary>Fréquence divisée par 2 ou plus : température trop haute.</summary>
    public const ulong HwThermalSlowdown = 0x40;

    /// <summary>Fréquence divisée par 2 ou plus : frein demandé de l'extérieur (par exemple par l'alimentation du PC).</summary>
    public const ulong HwPowerBrakeSlowdown = 0x80;

    public static bool Thermal(ulong reasons) => (reasons & (SwThermalSlowdown | HwThermalSlowdown)) != 0;

    public static bool PowerLimit(ulong reasons) => (reasons & SwPowerCap) != 0;

    /// <summary>Freinage matériel (fréquence divisée par 2 ou plus) dont la cause n'est pas seulement la chaleur.</summary>
    public static bool HardwareBrake(ulong reasons) => (reasons & (HwSlowdown | HwPowerBrakeSlowdown)) != 0;
}

/// <summary>Une mesure des capteurs pendant le benchmark (une par seconde). Une valeur illisible reste vide.</summary>
public sealed record BenchmarkSensorSample
{
    public double? GpuTemperatureC { get; init; }

    public int? GpuClockMhz { get; init; }

    public double? GpuPowerWatts { get; init; }

    public int? GpuLoadPercent { get; init; }

    /// <summary>Raisons qui limitent la fréquence de la carte (NVIDIA seulement), voir <see cref="NvidiaClockReasons"/>.</summary>
    public ulong? GpuClockReasons { get; init; }

    /// <summary>Température où la carte ralentit pour se protéger, donnée par la carte elle-même (NVIDIA).</summary>
    public int? GpuSlowdownC { get; init; }

    /// <summary>Température interne du processeur (pilote PawnIO requis).</summary>
    public double? CpuTemperatureC { get; init; }

    /// <summary>Fréquence réelle moyenne du processeur, calculée comme le Gestionnaire des tâches.</summary>
    public double? CpuMhz { get; init; }

    /// <summary>Puissance du processeur (pilote PawnIO requis).</summary>
    public double? CpuPowerWatts { get; init; }

    /// <summary>Limite de température donnée par le processeur lui-même (Intel, pilote PawnIO requis).</summary>
    public int? CpuTjMaxC { get; init; }
}

/// <summary>Ce que les capteurs ont relevé pendant la mesure d'un test, ou de tous les tests d'un composant.</summary>
public sealed record BenchmarkSensorSummary
{
    /// <summary>Nombre de mesures (une par seconde).</summary>
    public int Samples { get; init; }

    public double? MaxTemperatureC { get; init; }

    public double? AverageClockMhz { get; init; }

    public double? AveragePowerWatts { get; init; }

    /// <summary>Limite donnée par la puce elle-même : TjMax du processeur (Intel), seuil de ralentissement de la carte (NVIDIA).</summary>
    public int? LimitTemperatureC { get; init; }

    /// <summary>Part des mesures où la carte réduisait sa fréquence à cause de la chaleur (NVIDIA), en %.</summary>
    public double? ThermalSlowdownPercent { get; init; }

    /// <summary>Part des mesures où la carte tenait sa limite de puissance (NVIDIA ; normal à pleine charge), en %.</summary>
    public double? PowerLimitPercent { get; init; }

    /// <summary>Part des mesures avec un freinage matériel, fréquence divisée par 2 ou plus (NVIDIA), en %.</summary>
    public double? HardwareBrakePercent { get; init; }

    /// <summary>Résumé des mesures d'un test pour un composant (« gpu » ou « cpu ») ; <c>null</c> si rien n'était lisible.</summary>
    public static BenchmarkSensorSummary? Summarize(IReadOnlyList<BenchmarkSensorSample> samples, string device)
    {
        var gpu = device == "gpu";
        var temperatures = Values(samples, s => gpu ? s.GpuTemperatureC : s.CpuTemperatureC);
        var clocks = Values(samples, s => gpu ? s.GpuClockMhz : s.CpuMhz);
        var powers = Values(samples, s => gpu ? s.GpuPowerWatts : s.CpuPowerWatts);
        if (temperatures.Count == 0 && clocks.Count == 0 && powers.Count == 0)
        {
            return null;
        }

        var limits = samples.Select(s => gpu ? s.GpuSlowdownC : s.CpuTjMaxC).OfType<int>().ToList();
        List<ulong> reasons = gpu ? [.. samples.Select(s => s.GpuClockReasons).OfType<ulong>()] : [];
        double? Share(Func<ulong, bool> test) => reasons.Count > 0 ? Math.Round(100.0 * reasons.Count(test) / reasons.Count, 1) : null;
        return new BenchmarkSensorSummary
        {
            Samples = samples.Count,
            MaxTemperatureC = temperatures.Count > 0 ? Math.Round(temperatures.Max(), 1) : null,
            AverageClockMhz = clocks.Count > 0 ? Math.Round(clocks.Average()) : null,
            AveragePowerWatts = powers.Count > 0 ? Math.Round(powers.Average(), 1) : null,
            LimitTemperatureC = limits.Count > 0 ? limits[^1] : null,
            ThermalSlowdownPercent = Share(NvidiaClockReasons.Thermal),
            PowerLimitPercent = Share(NvidiaClockReasons.PowerLimit),
            HardwareBrakePercent = Share(NvidiaClockReasons.HardwareBrake),
        };
    }

    /// <summary>Résumé d'un composant à partir de ses tests : maximum des maxima, moyennes pondérées par le nombre de mesures.</summary>
    public static BenchmarkSensorSummary? Merge(IEnumerable<BenchmarkSensorSummary?> parts)
    {
        var list = parts.OfType<BenchmarkSensorSummary>().Where(p => p.Samples > 0).ToList();
        if (list.Count == 0)
        {
            return null;
        }

        double? Average(Func<BenchmarkSensorSummary, double?> pick, int digits)
        {
            var with = list.Where(p => pick(p) is not null).ToList();
            var weight = with.Sum(p => p.Samples);
            return weight > 0 ? Math.Round(with.Sum(p => pick(p)!.Value * p.Samples) / weight, digits) : null;
        }

        return new BenchmarkSensorSummary
        {
            Samples = list.Sum(p => p.Samples),
            MaxTemperatureC = list.Max(p => p.MaxTemperatureC),
            AverageClockMhz = Average(p => p.AverageClockMhz, 0),
            AveragePowerWatts = Average(p => p.AveragePowerWatts, 1),
            LimitTemperatureC = list.LastOrDefault(p => p.LimitTemperatureC is not null)?.LimitTemperatureC,
            ThermalSlowdownPercent = Average(p => p.ThermalSlowdownPercent, 1),
            PowerLimitPercent = Average(p => p.PowerLimitPercent, 1),
            HardwareBrakePercent = Average(p => p.HardwareBrakePercent, 1),
        };
    }

    private static List<double> Values(IReadOnlyList<BenchmarkSensorSample> samples, Func<BenchmarkSensorSample, double?> pick) =>
        [.. samples.Select(pick).OfType<double>()];
}

/// <summary>Phrases du bilan des capteurs, partagées par le benchmark et la page Benchmark de MAUS.</summary>
public static class BenchmarkSensorText
{
    /// <summary>« 76 °C au plus · 1 880 MHz en moyenne · 245 W en moyenne » : seulement ce qui a été lu.</summary>
    public static string? Describe(BenchmarkSensorSummary? summary, string device)
    {
        if (summary is null)
        {
            return null;
        }

        var culture = Culture;
        var parts = new List<string>();
        if (summary.MaxTemperatureC is { } temperature)
        {
            parts.Add(T("{0} °C au plus", temperature.ToString("0", culture)));
        }

        if (summary.AverageClockMhz is { } clock)
        {
            parts.Add(device == "cpu" && clock >= 1000
                ? T("{0} GHz en moyenne", (clock / 1000).ToString("0.00", culture))
                : T("{0} MHz en moyenne", clock.ToString("0", culture)));
        }

        if (summary.AveragePowerWatts is { } watts)
        {
            parts.Add(T("{0} W en moyenne", watts.ToString("0", culture)));
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    /// <summary>Mesure en direct pour l'écran du benchmark : « 72 °C · 1 905 MHz · 243 W ».</summary>
    public static string? Live(BenchmarkSensorSample? sample, string device)
    {
        if (sample is null)
        {
            return null;
        }

        var culture = Culture;
        var gpu = device == "gpu";
        var parts = new List<string>();
        if ((gpu ? sample.GpuTemperatureC : sample.CpuTemperatureC) is { } temperature)
        {
            parts.Add(temperature.ToString("0", culture) + " °C");
        }

        if (gpu && sample.GpuClockMhz is { } mhz)
        {
            parts.Add(mhz.ToString(culture) + " MHz");
        }
        else if (!gpu && sample.CpuMhz is { } cpuMhz)
        {
            parts.Add((cpuMhz / 1000).ToString("0.00", culture) + " GHz");
        }

        if ((gpu ? sample.GpuPowerWatts : sample.CpuPowerWatts) is { } watts)
        {
            parts.Add(watts.ToString("0", culture) + " W");
        }

        return parts.Count > 0 ? string.Join("  ·  ", parts) : null;
    }

    /// <summary>
    /// Avertissement, seulement d'après ce que la puce signale elle-même : raisons de NVML pour la carte, limite TjMax
    /// lue dans le processeur (au-delà, le processeur réduit sa fréquence pour se protéger : Intel, manuel du développeur
    /// IA-32 et Intel 64, volume 3B, « Thermal Monitoring and Protection »). <c>null</c> si rien n'est signalé.
    /// </summary>
    public static string? Warning(BenchmarkSensorSummary? summary, string device)
    {
        if (summary is null)
        {
            return null;
        }

        var culture = Culture;
        if (device == "gpu")
        {
            if (summary.ThermalSlowdownPercent is > 0 and var thermal)
            {
                return T("La carte graphique a baissé sa fréquence à cause de la chaleur pendant {0} % de la mesure : son score en pâtit. Vérifiez la poussière, les ventilateurs et l'aération du boîtier.", thermal.ToString("0.#", culture));
            }

            return summary.HardwareBrakePercent is > 0 and var brake
                ? T("La carte graphique a été freinée par sa protection matérielle (fréquence divisée par deux ou plus) pendant {0} % de la mesure : chaleur, alimentation ou pic de puissance.", brake.ToString("0.#", culture))
                : null;
        }

        return summary.MaxTemperatureC is { } max && summary.LimitTemperatureC is { } limit && max >= limit
            ? T("Le processeur a atteint sa limite de température ({0} °C, donnée par la puce) : il ralentit alors pour se protéger, et son score en pâtit. Vérifiez son refroidissement.", limit)
            : null;
    }
}

/// <summary>Lecture d'une mesure des capteurs (Windows en vrai, un faux dans les tests).</summary>
public interface IBenchmarkSensorReader : IDisposable
{
    BenchmarkSensorSample Read();
}

/// <summary>
/// Relève les capteurs une fois par seconde, sur un fil à part, pendant tout le benchmark : la fréquence, la température
/// et la puissance expliquent souvent un score (une carte qui chauffe baisse sa fréquence). Le fil a une priorité un peu
/// plus haute que les calculs pour mesurer aussi pendant les tests du processeur ; son travail (quelques millisecondes
/// par seconde) ne pèse pas sur les scores.
/// </summary>
public sealed class BenchmarkSensorSampler : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<BenchmarkSensorSample> _segment = [];
    private readonly ManualResetEventSlim _stop = new();
    private readonly Thread _thread;
    private readonly TimeSpan _interval;
    private BenchmarkSensorSample? _latest;
    private bool _recording;

    public BenchmarkSensorSampler(Func<IBenchmarkSensorReader> open, TimeSpan? interval = null)
    {
        _interval = interval ?? TimeSpan.FromSeconds(1);
        _thread = new Thread(() => Loop(open)) { IsBackground = true, Name = "MAUS benchmark sensors", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Capteurs de ce PC pour la carte <paramref name="gpuName"/> (nom donné par DirectX).</summary>
    public static BenchmarkSensorSampler ForThisPc(string gpuName) => new(() => new WindowsBenchmarkSensors(gpuName));

    /// <summary>Dernière mesure (affichée en direct), ou <c>null</c> avant la première.</summary>
    public BenchmarkSensorSample? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    /// <summary>Début de la mesure d'un test : les mesures suivantes lui sont attribuées.</summary>
    public void BeginSegment()
    {
        lock (_gate)
        {
            _segment.Clear();
            _recording = true;
        }
    }

    /// <summary>Fin de la mesure d'un test : résumé de ses mesures pour le composant testé.</summary>
    public BenchmarkSensorSummary? EndSegment(string device)
    {
        lock (_gate)
        {
            _recording = false;
            return BenchmarkSensorSummary.Summarize([.. _segment], device);
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(10));
        _stop.Dispose();
    }

    private void Loop(Func<IBenchmarkSensorReader> open)
    {
        IBenchmarkSensorReader? reader = null;
        try
        {
            reader = open();
            do
            {
                BenchmarkSensorSample sample;
                try
                {
                    sample = reader.Read();
                }
                catch (Exception)
                {
                    // Un capteur en panne ne doit jamais arrêter le benchmark : la mesure manque, c'est tout.
                    continue;
                }

                lock (_gate)
                {
                    _latest = sample;
                    if (_recording)
                    {
                        _segment.Add(sample);
                    }
                }
            }
            while (!_stop.Wait(_interval));
        }
        catch (Exception)
        {
            // Capteurs impossibles à ouvrir : le benchmark continue sans eux.
        }
        finally
        {
            reader?.Dispose();
        }
    }
}

/// <summary>
/// Capteurs de Windows, en lecture seule : NVML pour les cartes NVIDIA (avec les raisons qui limitent leur fréquence),
/// D3DKMT comme le Gestionnaire des tâches pour la température des autres cartes quand leur pilote la publie, compteurs
/// de Windows pour la fréquence réelle du processeur, PawnIO (s'il est installé et MAUS administrateur) pour sa
/// température et sa puissance.
/// </summary>
internal sealed class WindowsBenchmarkSensors : IBenchmarkSensorReader
{
    private readonly string _gpuName;
    private readonly PdhQuery _pdh = new();
    private readonly nint _performance;
    private readonly nint _frequency;
    private readonly WindowsNvmlSource _nvml = new();
    private readonly CombinedSensorSource? _driver;

    public WindowsBenchmarkSensors(string gpuName)
    {
        _gpuName = gpuName;
        _performance = _pdh.Add(@"\Processor Information(_Total)\% Processor Performance");
        _frequency = _pdh.Add(@"\Processor Information(_Total)\Processor Frequency");
        _pdh.Collect();
        if (PawnIo.State(new WindowsRegistryReader()).Installed && ProcessElevation.IsElevated())
        {
            // Le pilote s'ouvre en arrière-plan (quelques secondes) : les autres mesures n'attendent pas.
            _driver = new CombinedSensorSource(new NoSensors(), () => new LhmAdvancedSensors());
        }
    }

    public BenchmarkSensorSample Read()
    {
        _pdh.Collect();
        var performance = PdhQuery.Value(_performance);
        var nominal = PdhQuery.Value(_frequency);
        var nvidia = Nvidia();
        double? otherTemperature = nvidia is null ? GpuPerformance.Read().FirstOrDefault(g => SameGpu(g.Name, _gpuName))?.TemperatureC : null;
        var driver = _driver?.Sample();
        return new BenchmarkSensorSample
        {
            GpuTemperatureC = nvidia?.TemperatureC ?? otherTemperature,
            GpuClockMhz = nvidia?.GraphicsClockMhz,
            GpuPowerWatts = nvidia?.PowerWatts,
            GpuLoadPercent = nvidia?.UtilizationPercent,
            GpuClockReasons = nvidia?.ClockEventReasons,
            GpuSlowdownC = nvidia?.SlowdownTemperatureC,
            CpuMhz = performance is { } p && nominal is { } n && n > 0 ? n * p / 100 : null,
            CpuTemperatureC = driver?.CpuTemperatureC,
            CpuPowerWatts = driver?.CpuPowerWatts,
            CpuTjMaxC = driver?.CpuTjMaxC,
        };
    }

    public void Dispose()
    {
        _driver?.Dispose();
        _pdh.Dispose();
    }

    private NvidiaGpuState? Nvidia()
    {
        try
        {
            return _nvml.Read()?.FirstOrDefault(g => SameGpu(g.Name, _gpuName));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Même carte : noms égaux sans la marque (DirectX, NVML et le registre ne l'écrivent pas toujours).</summary>
    internal static bool SameGpu(string a, string b)
    {
        static string Clean(string name) => name.Replace("NVIDIA ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        var x = Clean(a);
        var y = Clean(b);
        return x.Length > 0 && y.Length > 0
            && (x.Contains(y, StringComparison.OrdinalIgnoreCase) || y.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Aucune mesure de base : seules celles du pilote PawnIO sont ajoutées par <see cref="CombinedSensorSource"/>.</summary>
    private sealed class NoSensors : ISensorSource
    {
        public SensorSnapshot Sample() => new() { At = DateTimeOffset.Now };

        public void Dispose()
        {
        }
    }
}
