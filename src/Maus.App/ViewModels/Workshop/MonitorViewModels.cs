using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Maus.App.Controls;
using Maus.Core.Platform;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>Une mesure de la fenêtre de surveillance : actuelle, minimale, maximale.</summary>
public sealed class MonitorRowViewModel(string name, MonitorKind kind) : ObservableObject
{
    private string _value = "—";
    private string _min = "—";
    private string _max = "—";

    public string Name { get; } = name;

    public MonitorKind Kind { get; } = kind;

    public string Value
    {
        get => _value;
        private set => SetProperty(ref _value, value);
    }

    public string Min
    {
        get => _min;
        private set => SetProperty(ref _min, value);
    }

    public string Max
    {
        get => _max;
        private set => SetProperty(ref _max, value);
    }

    internal void Show(MonitorRow row)
    {
        Value = Format(row.Value);
        Min = Format(row.Min);
        Max = Format(row.Max);
    }

    private string Format(double value) => Kind switch
    {
        MonitorKind.Temperature => value.ToString("0", CultureInfo.CurrentCulture) + " °C",
        MonitorKind.Power => value.ToString(value < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " W",
        MonitorKind.Clock => value.ToString("0", CultureInfo.CurrentCulture) + " MHz",
        _ => value.ToString("0", CultureInfo.CurrentCulture) + " %",
    };
}

/// <summary>Un composant (processeur, carte graphique, mémoire, carte mère) et ses mesures.</summary>
public sealed class MonitorGroupViewModel(string component)
{
    public string Component { get; } = component;

    public ObservableCollection<MonitorRowViewModel> Rows { get; } = [];
}

/// <summary>Un événement relevé : heure, source, identifiant, message.</summary>
public sealed class WatchedEventViewModel(WatchedEvent e)
{
    public string Time { get; } = e.Time.ToString("T", CultureInfo.CurrentCulture);

    public string Source { get; } = $"{e.Provider} ({e.Id})";

    public string Message { get; } = FirstLine(e.Message) ?? T("Message illisible (journal {0}).", e.Log);

    public Brush Accent { get; } = e.Hardware ? Palette.Red : Palette.Gold;

    private static string? FirstLine(string? message) =>
        message?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}

/// <summary>
/// Fenêtre de surveillance, façon HWMonitor simplifié : relit les mesures de l'atelier (aucune mesure en double), garde les
/// extrêmes, et relève toutes les 5 secondes les erreurs matérielles (WHEA, dont PCI Express) et de Windows depuis son ouverture.
/// </summary>
public sealed class MonitorViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan ErrorPeriod = TimeSpan.FromSeconds(5);

    private readonly WorkshopViewModel _workshop;
    private readonly MonitorTracker _tracker = new();
    private readonly IEventLogReader _logs = new WindowsEventLogReader();
    private readonly DispatcherTimer _errorTimer = new() { Interval = ErrorPeriod };
    private readonly Dictionary<(string Component, string Name, MonitorKind Kind), MonitorRowViewModel> _rows = [];
    private DateTime _since = DateTime.Now;
    private bool _readingErrors;
    private string _hardwareErrors = "—";
    private string _hardwareDetail = string.Empty;
    private Brush _hardwareBrush = Views.ScoreToBrushConverter.ThemeText();
    private string _windowsErrors = "—";
    private Brush _windowsBrush = Views.ScoreToBrushConverter.ThemeText();
    private string _status = string.Empty;
    private string _errorsStatus = string.Empty;

    public MonitorViewModel(WorkshopViewModel workshop)
    {
        _workshop = workshop;
        _workshop.Sampled += OnSampled;
        _errorTimer.Tick += async (_, _) => await ReadErrorsAsync();
        ResetCommand = new AsyncCommand(() =>
        {
            _tracker.Reset();
            _since = DateTime.Now;
            OnPropertyChanged(nameof(Since));
            return ReadErrorsAsync();
        });
        UpdateStatus();
    }

    public ObservableCollection<MonitorGroupViewModel> Groups { get; } = [];

    public ObservableCollection<WatchedEventViewModel> Events { get; } = [];

    public ICommand ResetCommand { get; }

    public string Since => T("Depuis {0:T}", _since);

    public string HardwareErrors
    {
        get => _hardwareErrors;
        private set => SetProperty(ref _hardwareErrors, value);
    }

    public string HardwareDetail
    {
        get => _hardwareDetail;
        private set => SetProperty(ref _hardwareDetail, value);
    }

    public Brush HardwareBrush
    {
        get => _hardwareBrush;
        private set => SetProperty(ref _hardwareBrush, value);
    }

    public string WindowsErrors
    {
        get => _windowsErrors;
        private set => SetProperty(ref _windowsErrors, value);
    }

    public Brush WindowsBrush
    {
        get => _windowsBrush;
        private set => SetProperty(ref _windowsBrush, value);
    }

    public bool HasEvents => Events.Count > 0;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string ErrorsStatus
    {
        get => _errorsStatus;
        private set => SetProperty(ref _errorsStatus, value);
    }

    /// <summary>Démarre la surveillance (fenêtre affichée).</summary>
    public async Task StartAsync()
    {
        _workshop.IsMonitorOpen = true;
        _errorTimer.Start();
        await ReadErrorsAsync();
    }

    public void Dispose()
    {
        _errorTimer.Stop();
        _workshop.Sampled -= OnSampled;
        _workshop.IsMonitorOpen = false;
    }

    private void OnSampled(object? sender, SensorSnapshot snapshot)
    {
        foreach (var row in _tracker.Update(snapshot))
        {
            var key = (row.Component, row.Name, row.Kind);
            if (!_rows.TryGetValue(key, out var model))
            {
                model = new MonitorRowViewModel(row.Name, row.Kind);
                _rows[key] = model;
                var group = Groups.FirstOrDefault(g => g.Component == row.Component);
                if (group is null)
                {
                    group = new MonitorGroupViewModel(row.Component);
                    Groups.Add(group);
                }

                // Comme HWMonitor : températures, puis consommations, fréquences et charges.
                var index = group.Rows.TakeWhile(r => r.Kind <= model.Kind).Count();
                group.Rows.Insert(index, model);
            }

            model.Show(row);
        }

        UpdateStatus(snapshot);
    }

    private void UpdateStatus(SensorSnapshot? snapshot = null)
    {
        var interval = _workshop.RefreshInterval.TotalSeconds.ToString("0.#", CultureInfo.CurrentCulture);
        Status = snapshot is { Readings.Count: 0 }
            ? T("Mesures toutes les {0} s. Sans le pilote PawnIO (ou sans les droits administrateur), la température et la consommation du processeur ne sont pas lisibles.", interval)
            : T("Mesures toutes les {0} s (vitesse réglable dans les paramètres).", interval);
    }

    private async Task ReadErrorsAsync()
    {
        if (_readingErrors)
        {
            return;
        }

        _readingErrors = true;
        try
        {
            var since = _since;
            var result = await Task.Run(() => ErrorWatch.Read(_logs, since));
            HardwareErrors = result.Hardware.ToString(CultureInfo.CurrentCulture);
            HardwareDetail = T("dont PCI Express : {0}", result.PciExpress);
            HardwareBrush = result.Hardware > 0 ? Palette.Red : Palette.Green;
            WindowsErrors = result.Windows.ToString(CultureInfo.CurrentCulture);
            WindowsBrush = result.Windows > 0 ? Palette.Gold : Palette.Green;
            ErrorsStatus = result.Incomplete ? T("Un journal de Windows n'a pas pu être lu : les compteurs peuvent être incomplets.") : string.Empty;

            Events.Clear();
            foreach (var e in result.Events.Take(ErrorWatch.MaxEvents))
            {
                Events.Add(new WatchedEventViewModel(e));
            }

            OnPropertyChanged(nameof(HasEvents));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ErrorsStatus = T("Lecture des journaux impossible : {0}", ex.Message);
        }
        finally
        {
            _readingErrors = false;
        }
    }
}
