using System.Collections.ObjectModel;
using System.Windows.Media;
using Maus.App.Controls;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>Une tuile de mesure en direct : valeur, détail, mini-courbe et, si elle a un seuil, jauge de sécurité.</summary>
public sealed class MetricViewModel(string title, Brush stroke, double maximum = 100) : ObservableObject
{
    private string _value = "—";
    private string _detail = string.Empty;
    private IReadOnlyList<double> _series = [];
    private GaugeInfo? _gauge;

    public string Title { get; } = title;

    public Brush Stroke { get; } = stroke;

    public double Maximum { get; } = maximum;

    public string Value
    {
        get => _value;
        private set => SetProperty(ref _value, value);
    }

    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public IReadOnlyList<double> Series
    {
        get => _series;
        private set => SetProperty(ref _series, value);
    }

    public GaugeInfo? Gauge
    {
        get => _gauge;
        private set
        {
            if (SetProperty(ref _gauge, value))
            {
                OnPropertyChanged(nameof(HasGauge));
                OnPropertyChanged(nameof(HasSeries));
            }
        }
    }

    public bool HasGauge => Gauge is not null;

    public bool HasSeries => Gauge is null;

    public void Update(string value, string detail, IReadOnlyList<double> series, GaugeInfo? gauge = null)
    {
        Value = value;
        Detail = detail;
        Series = series;
        Gauge = gauge;
    }
}

/// <summary>« En direct » : tuiles de mesure et alarmes, rafraîchies chaque seconde tant que l'onglet est affiché.</summary>
public sealed class LiveViewModel : ObservableObject
{
    private readonly SensorHistory _history = new(90);

    public LiveViewModel()
    {
        Cpu = new(T("Processeur"), Palette.Blue);
        Memory = new(T("Mémoire vive"), Palette.Green);
        Gpu = new(T("Carte graphique"), Palette.Red);
        GpuTemperature = new(T("Température de la carte graphique"), Palette.Gold, maximum: 0);
        Disk = new(T("Disque"), Palette.Gold);
        Network = new(T("Réseau"), Palette.Blue, maximum: 0);
        Metrics = [Cpu, Memory, Gpu, GpuTemperature, Disk, Network];
    }

    public MetricViewModel Cpu { get; }

    public MetricViewModel Memory { get; }

    public MetricViewModel Gpu { get; }

    public MetricViewModel GpuTemperature { get; }

    public MetricViewModel Disk { get; }

    public MetricViewModel Network { get; }

    public IReadOnlyList<MetricViewModel> Metrics { get; }

    public ObservableCollection<string> Alarms { get; } = [];

    public IReadOnlyCollection<SensorSnapshot> Samples => _history.Samples;

    /// <summary>Dernière alarme « danger », consultée par les tests pour s'arrêter d'eux-mêmes.</summary>
    public string? DangerAlarm { get; private set; }

    public string ThermalNote => _history.Latest?.ThermalZoneC is { } c
        ? T("Zone thermique de la carte mère : {0:0} °C (indication approximative, ce n'est pas le capteur interne du processeur).", c)
        : T("La température interne du processeur ne se lit qu'avec un pilote : MAUS ne l'affiche pas plutôt que de l'inventer.");

    public void Add(SensorSnapshot snapshot)
    {
        _history.Add(snapshot);
        Cpu.Update(Percent(snapshot.CpuPercent), snapshot.CpuMhz is { } mhz ? (mhz / 1000).ToString("0.00 ", Culture) + "GHz" : string.Empty, _history.Series(s => s.CpuPercent));
        Memory.Update(snapshot.MemoryUsedBytes is { } used ? Gb(used) : "—", snapshot.MemoryTotalBytes is { } total ? "/ " + Gb(total) : string.Empty, _history.Series(s => s.MemoryPercent));

        var gpu = snapshot.Gpus.OrderByDescending(g => g.UtilizationPercent ?? 0).FirstOrDefault();
        Gpu.Update(gpu is null ? "—" : Percent(gpu.UtilizationPercent), gpu is null ? T("aucune carte lisible") : Watts(gpu) + gpu.Name, _history.Series(s => s.Gpus.Select(g => g.UtilizationPercent).Max()));
        var temperature = gpu?.TemperatureC;
        GpuTemperature.Update(
            temperature is { } t ? $"{t:0} °C" : "—",
            gpu?.FanRpm is { } rpm ? T("ventilateur {0} tr/min", rpm) : string.Empty,
            _history.Series(s => s.Gpus.Select(g => g.TemperatureC).Max()),
            temperature is { } value && gpu?.SlowdownTemperatureC is { } slow
                ? new GaugeInfo(T("Température"), 20, slow + 10, slow - 10, slow, value, T("ralentit à {0} °C", slow))
                : null);
        Disk.Update(Percent(snapshot.DiskActivePercent), T("lecture {0} · écriture {1}", Rate(snapshot.DiskReadBytesPerSecond), Rate(snapshot.DiskWriteBytesPerSecond)), _history.Series(s => s.DiskActivePercent));
        Network.Update(Rate(snapshot.NetworkBytesPerSecond), string.Empty, _history.Series(s => s.NetworkBytesPerSecond));

        var alarms = SensorAlarms.Check(snapshot);
        DangerAlarm = alarms.FirstOrDefault(a => a.Level == AlarmLevel.Danger)?.Message;
        Alarms.Clear();
        foreach (var alarm in alarms)
        {
            Alarms.Add(alarm.Message);
        }

        OnPropertyChanged(nameof(HasAlarms));
        OnPropertyChanged(nameof(ThermalNote));
    }

    public bool HasAlarms => Alarms.Count > 0;

    private static string Percent(double? value) => value is { } v ? v.ToString("0", Culture) + " %" : "—";

    private static string Gb(long bytes) => (bytes / 1073741824.0).ToString("0.0 ", Culture) + T("Go");

    private static string Watts(GpuSensor gpu) => gpu.PowerWatts is { } w ? w.ToString("0 W · ", Culture) : string.Empty;

    private static string Rate(double? bytesPerSecond) => bytesPerSecond switch
    {
        null => "—",
        >= 1_048_576 => T("{0:0.0} Mo/s", bytesPerSecond / 1_048_576),
        _ => T("{0:0} Ko/s", bytesPerSecond / 1024),
    };
}
