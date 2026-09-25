namespace Maus.Core.Workshop;

/// <summary>Mesures d'une carte graphique à un instant donné.</summary>
public sealed record GpuSensor(
    string Name,
    double? UtilizationPercent,
    double? TemperatureC,
    int? FanRpm,
    int? FanPercent,
    double? PowerPercent,
    double? PowerWatts,
    int? GraphicsClockMhz,
    long? MemoryUsedBytes,
    int? SlowdownTemperatureC);

/// <summary>Toutes les mesures d'un échantillon (une par seconde dans l'atelier).</summary>
public sealed record SensorSnapshot
{
    public required DateTimeOffset At { get; init; }

    public double? CpuPercent { get; init; }

    public IReadOnlyList<double> CorePercents { get; init; } = [];

    /// <summary>Fréquence réelle moyenne (fréquence nominale × % de performance), comme le Gestionnaire des tâches.</summary>
    public double? CpuMhz { get; init; }

    public long? MemoryUsedBytes { get; init; }

    public long? MemoryTotalBytes { get; init; }

    public double? DiskActivePercent { get; init; }

    public double? DiskReadBytesPerSecond { get; init; }

    public double? DiskWriteBytesPerSecond { get; init; }

    public double? NetworkBytesPerSecond { get; init; }

    /// <summary>Température la plus haute des zones thermiques ACPI : approximative, ce n'est pas le capteur interne du processeur.</summary>
    public double? ThermalZoneC { get; init; }

    public IReadOnlyList<GpuSensor> Gpus { get; init; } = [];

    /// <summary>Température interne du processeur (pilote PawnIO requis), sinon <c>null</c>.</summary>
    public double? CpuTemperatureC { get; init; }

    /// <summary>Limite de température donnée par le processeur lui-même (Intel, pilote PawnIO requis).</summary>
    public int? CpuTjMaxC { get; init; }

    /// <summary>Puissance du processeur en watts (pilote PawnIO requis).</summary>
    public double? CpuPowerWatts { get; init; }

    /// <summary>Tension des cœurs en volts (pilote PawnIO requis).</summary>
    public double? CpuVoltage { get; init; }

    /// <summary>Toutes les mesures lues par le pilote (vide sans PawnIO).</summary>
    public IReadOnlyList<HardwareReading> Readings { get; init; } = [];

    public double? MemoryPercent => MemoryUsedBytes is { } used && MemoryTotalBytes is > 0 ? 100.0 * used / MemoryTotalBytes.Value : null;
}

/// <summary>Source de mesures en direct ; <see cref="Sample"/> est appelée à intervalle régulier.</summary>
public interface ISensorSource : IDisposable
{
    SensorSnapshot Sample();
}

/// <summary>Historique glissant des mesures, pour les graphiques (le plus ancien est oublié).</summary>
public sealed class SensorHistory(int capacity = 120)
{
    private readonly Queue<SensorSnapshot> _samples = new();

    public int Capacity { get; } = capacity;

    public IReadOnlyCollection<SensorSnapshot> Samples => _samples;

    public SensorSnapshot? Latest { get; private set; }

    public void Add(SensorSnapshot snapshot)
    {
        _samples.Enqueue(snapshot);
        while (_samples.Count > Capacity)
        {
            _samples.Dequeue();
        }

        Latest = snapshot;
    }

    /// <summary>Série d'une mesure (valeurs absentes ignorées).</summary>
    public IReadOnlyList<double> Series(Func<SensorSnapshot, double?> selector) =>
        _samples.Select(selector).OfType<double>().ToList();

    public double? Max(Func<SensorSnapshot, double?> selector) => Series(selector) is { Count: > 0 } s ? s.Max() : null;
}

public enum AlarmLevel
{
    Attention,
    Danger,
}

public sealed record SensorAlarm(string Id, AlarmLevel Level, string Message);

/// <summary>Alarmes en direct : comparaison de chaque mesure à son seuil de sécurité.</summary>
public static class SensorAlarms
{
    /// <param name="snapshot">Mesures.</param>
    /// <param name="cpuMaxC">Température maximale du processeur selon son fabricant (catalogue des seuils), si connue.</param>
    public static IReadOnlyList<SensorAlarm> Check(SensorSnapshot snapshot, int? cpuMaxC = null)
    {
        var alarms = new List<SensorAlarm>();
        // Limite : celle que donne le processeur, sinon celle du catalogue (modèles vérifiés) ; inconnue = pas d'alarme inventée
        // (le processeur se protège de toute façon en ralentissant).
        if (snapshot.CpuTemperatureC is { } cpu && (snapshot.CpuTjMaxC ?? cpuMaxC) is { } max)
        {
            // À sa limite, le processeur ralentit de lui-même pour se protéger ; certains modèles récents y montent
            // volontairement en pleine charge. Danger seulement au-delà (protection qui n'agit pas, ou sonde en défaut).
            if (cpu >= max + 5)
            {
                alarms.Add(new("cpu-hot", AlarmLevel.Danger,
                    Localization.Texts.T("Le processeur dépasse sa limite ({0:0} °C pour {1} °C au maximum) : arrêtez la charge et vérifiez le refroidissement.", cpu, max)));
            }
            else if (cpu >= max - 3)
            {
                alarms.Add(new("cpu-warm", AlarmLevel.Attention,
                    Localization.Texts.T("Le processeur est à sa limite de température ({0:0} °C sur {1} °C) : il ralentit pour se protéger. Normal en pleine charge pour certains modèles récents ; au repos, vérifiez le refroidissement.", cpu, max)));
            }
        }

        foreach (var gpu in snapshot.Gpus)
        {
            if (gpu.TemperatureC is not { } temperature)
            {
                continue;
            }

            if (gpu.SlowdownTemperatureC is { } slowdown && temperature >= slowdown)
            {
                alarms.Add(new($"gpu-hot:{gpu.Name}", AlarmLevel.Danger,
                    Localization.Texts.T("{0} a atteint son seuil de ralentissement ({1:0} °C) : elle baisse ses fréquences pour se protéger.", gpu.Name, temperature)));
            }
            else if (gpu.SlowdownTemperatureC is { } near && temperature >= near - 5)
            {
                alarms.Add(new($"gpu-warm:{gpu.Name}", AlarmLevel.Attention,
                    Localization.Texts.T("{0} approche de son seuil de ralentissement ({1:0} °C sur {2} °C).", gpu.Name, temperature, near)));
            }
            else if (gpu.SlowdownTemperatureC is null && temperature >= 90)
            {
                alarms.Add(new($"gpu-hot:{gpu.Name}", AlarmLevel.Attention,
                    Localization.Texts.T("{0} est très chaude ({1:0} °C).", gpu.Name, temperature)));
            }
        }

        if (snapshot.ThermalZoneC is >= 95)
        {
            alarms.Add(new("thermal-zone", AlarmLevel.Danger,
                Localization.Texts.T("La zone thermique de la carte mère indique {0:0} °C : le PC surchauffe.", snapshot.ThermalZoneC)));
        }

        if (snapshot.MemoryPercent is >= 95)
        {
            alarms.Add(new("memory-full", AlarmLevel.Attention,
                Localization.Texts.T("La mémoire vive est presque pleine ({0:0} %) : Windows utilise le disque et ralentit.", snapshot.MemoryPercent)));
        }

        return alarms;
    }
}
