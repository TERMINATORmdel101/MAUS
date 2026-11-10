using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop.Benchmark;

/// <summary>
/// Arrêt de sécurité du benchmark (demande du 12/10/2026 : ne pas compter seulement sur les protections du matériel), avec les
/// mêmes seuils que le reste de MAUS (<see cref="ThermalRules"/>) : carte graphique à son seuil de ralentissement (NVML, donné par
/// la carte, jamais inventé : principe 7), processeur au-delà de sa limite (la sienne, sinon celle du catalogue). Une limite donnée
/// par le processeur (Intel) n'est jamais dépassée à la lecture (<see cref="CpuTemperatureWatch.CappedAtLimit"/>) : seuls les
/// processeurs à limite du catalogue peuvent arrêter le benchmark. Il faut deux mesures de suite au-delà du seuil (une valeur
/// isolée peut être un pic de lecture). Comme pour les tests de stabilité (<see cref="ThermalWatchdog"/>), une température lue avec
/// sa limite puis perdue depuis <see cref="ThermalWatchdog.Grace"/> arrête aussi le benchmark : plus rien ne surveille alors la
/// chaleur. Une température jamais lue n'arrête rien (pas de PawnIO, carte sans seuil connu).
/// </summary>
public sealed class BenchmarkSafetyStop
{
    /// <summary>Mesures de suite au-delà du seuil avant l'arrêt.</summary>
    public const int Strikes = 2;

    private readonly Func<TimeSpan> _clock;
    private BenchmarkSensorSample? _last;
    private int _strikes;
    private TimeSpan? _gpuSeenAt;
    private TimeSpan? _cpuSeenAt;

    public BenchmarkSafetyStop(Func<TimeSpan>? clock = null)
    {
        _clock = clock ?? (static () => TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    /// <summary>Raison de l'arrêt, ou <c>null</c> pour continuer. Une même mesure relue ne compte qu'une fois.</summary>
    public string? Check(BenchmarkSensorSample? sample)
    {
        if (sample is not null && !ReferenceEquals(sample, _last))
        {
            _last = sample;
            _strikes = Danger(sample) is null ? 0 : _strikes + 1;
            var now = _clock();
            if (sample.GpuTemperatureC is not null && sample.GpuSlowdownC is not null)
            {
                _gpuSeenAt = now;
            }

            if (sample.CpuTemperatureC is not null && ThermalRules.CpuLimit(sample.CpuTjMaxC, sample.CpuCatalogLimitC) is not null)
            {
                _cpuSeenAt = now;
            }
        }

        return (_strikes >= Strikes ? Danger(_last) : null) ?? Lost();
    }

    /// <summary>Température lue avec sa limite, et qui ne l'est plus depuis le délai de grâce ; <c>null</c> sinon.</summary>
    private string? Lost()
    {
        var now = _clock();
        if (_gpuSeenAt is { } gpu && now - gpu > ThermalWatchdog.Grace)
        {
            return T("La température de la carte graphique n'est plus lue depuis {0:0} s : le benchmark s'est arrêté par prudence, car sans elle sa protection contre la surchauffe ne peut plus jouer. Relancez le benchmark ; les scores déjà mesurés sont gardés.", (now - gpu).TotalSeconds);
        }

        return _cpuSeenAt is { } cpu && now - cpu > ThermalWatchdog.Grace
            ? T("La température du processeur n'est plus lue depuis {0:0} s : le benchmark s'est arrêté par prudence, car sans elle sa protection contre la surchauffe ne peut plus jouer. Le pilote PawnIO ne répond peut-être plus (un autre logiciel de surveillance du matériel l'occupe parfois) : fermez les autres logiciels de surveillance, puis relancez le benchmark.", (now - cpu).TotalSeconds)
            : null;
    }

    /// <summary>Seuil dépassé dans cette mesure, ou <c>null</c>.</summary>
    public static string? Danger(BenchmarkSensorSample? sample)
    {
        if (sample is null)
        {
            return null;
        }

        if (sample.GpuTemperatureC is { } gpu && sample.GpuSlowdownC is { } slowdown && ThermalRules.GpuDanger(gpu, slowdown))
        {
            return T("La carte graphique a atteint son seuil de ralentissement ({0:0} °C pour {1} °C) : benchmark arrêté par sécurité. Vérifiez la ventilation du boîtier et la poussière dans la carte avant de recommencer.", gpu, slowdown);
        }

        if (sample.CpuTemperatureC is { } cpu && ThermalRules.CpuLimit(sample.CpuTjMaxC, sample.CpuCatalogLimitC) is { } limit && ThermalRules.CpuDanger(cpu, limit))
        {
            return T("Le processeur dépasse sa limite ({0:0} °C pour {1} °C au maximum) : benchmark arrêté par sécurité. Vérifiez son refroidissement avant de recommencer.", cpu, limit);
        }

        return null;
    }
}
