using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop.Benchmark;

/// <summary>
/// Arrêt de sécurité du benchmark (demande du 12/10/2026 : ne pas compter seulement sur les protections du matériel). Mêmes
/// seuils que les alarmes « danger » des tests de stabilité (<see cref="SensorAlarms"/>), lus dans les puces elles-mêmes,
/// jamais inventés (principe 7) :
/// <list type="bullet">
/// <item>carte graphique à son seuil de ralentissement (NVML, donné par la carte) ;</item>
/// <item>processeur plus de 5 °C au-delà de sa limite (TjMax, donnée par le processeur) : la protection n'agit pas, ou la
/// sonde est en défaut.</item>
/// </list>
/// Il faut deux mesures de suite au-delà du seuil (une valeur isolée peut être un pic de lecture).
/// </summary>
public sealed class BenchmarkSafetyStop
{
    /// <summary>Mesures de suite au-delà du seuil avant l'arrêt.</summary>
    public const int Strikes = 2;

    private BenchmarkSensorSample? _last;
    private int _strikes;

    /// <summary>Raison de l'arrêt, ou <c>null</c> pour continuer. Une même mesure relue ne compte qu'une fois.</summary>
    public string? Check(BenchmarkSensorSample? sample)
    {
        if (sample is null || ReferenceEquals(sample, _last))
        {
            return _strikes >= Strikes ? Danger(_last) : null;
        }

        _last = sample;
        var reason = Danger(sample);
        _strikes = reason is null ? 0 : _strikes + 1;
        return _strikes >= Strikes ? reason : null;
    }

    /// <summary>Seuil dépassé dans cette mesure, ou <c>null</c>.</summary>
    public static string? Danger(BenchmarkSensorSample? sample)
    {
        if (sample is null)
        {
            return null;
        }

        if (sample.GpuTemperatureC is { } gpu && sample.GpuSlowdownC is { } slowdown && gpu >= slowdown)
        {
            return T("La carte graphique a atteint son seuil de ralentissement ({0:0} °C pour {1} °C) : benchmark arrêté par sécurité. Vérifiez la ventilation du boîtier et la poussière dans la carte avant de recommencer.", gpu, slowdown);
        }

        if (sample.CpuTemperatureC is { } cpu && sample.CpuTjMaxC is { } max && cpu >= max + 5)
        {
            return T("Le processeur dépasse sa limite ({0:0} °C pour {1} °C au maximum) : benchmark arrêté par sécurité. Vérifiez le refroidissement avant de recommencer.", cpu, max);
        }

        return null;
    }
}
