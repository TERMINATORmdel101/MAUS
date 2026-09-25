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
    public static IReadOnlyList<SensorAlarm> Check(SensorSnapshot snapshot)
    {
        var alarms = new List<SensorAlarm>();
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
