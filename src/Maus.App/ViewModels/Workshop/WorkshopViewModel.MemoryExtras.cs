using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Maus.Core.Fixes;
using Maus.Core.Platform;
using Maus.Core.Preferences;
using Maus.Core.Workshop;
using Maus.Core.Workshop.Memory;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>Une entrée de la carte mère proposée pour l'étalonnage de la tension de la mémoire.</summary>
public sealed class VoltageCandidateViewModel(DramVoltageCandidate candidate)
{
    public DramVoltageCandidate Candidate { get; } = candidate;

    public string Label { get; } = candidate.Factor == 1
        ? T("{0} : {1} V", candidate.Reading.Name, candidate.Volts.ToString("0.000", CultureInfo.InvariantCulture))
        : T("{0} × {1} : {2} V (mesure brute {3} V)", candidate.Reading.Name, candidate.Factor.ToString("0", CultureInfo.InvariantCulture),
            candidate.Volts.ToString("0.000", CultureInfo.InvariantCulture), candidate.Reading.Value.ToString("0.000", CultureInfo.InvariantCulture));
}

/// <summary>
/// Onglet Mémoire, suite : tension mesurée par la carte mère (ou étalonnée par l'utilisateur) et comparaison avec une fiche
/// enregistrée (avant / après un réglage dans le BIOS).
/// </summary>
public sealed partial class WorkshopViewModel
{
    private readonly MemorySnapshotStore _snapshots = MemorySnapshotStore.CreateDefault();
    private string? _measuredVoltage;
    private string? _board;
    private IReadOnlyList<HardwareReading> _boardReadings = [];
    private bool _canCalibrate;
    private string _calibrationInput = string.Empty;
    private string _calibrationStatus = string.Empty;
    private VoltageCandidateViewModel? _selectedCandidate;
    private MemorySnapshot? _currentSnapshot;
    private ChoiceOption<MemorySnapshot>? _comparisonChoice;
    private string _memoryComparison = string.Empty;
    private ICommand? _findCandidates;
    private ICommand? _useCandidate;
    private ICommand? _forgetCalibration;

    /// <summary>Étalonnage proposé : pilote présent, mais la carte mère ne nomme pas l'entrée de la tension de la mémoire.</summary>
    public bool CanCalibrateVoltage
    {
        get => _canCalibrate;
        private set => SetProperty(ref _canCalibrate, value);
    }

    /// <summary>Tension réglée dans le BIOS, tapée par l'utilisateur (« 1,45 »).</summary>
    public string CalibrationInput
    {
        get => _calibrationInput;
        set => SetProperty(ref _calibrationInput, value ?? string.Empty);
    }

    public string CalibrationStatus
    {
        get => _calibrationStatus;
        private set => SetProperty(ref _calibrationStatus, value);
    }

    public ObservableCollection<VoltageCandidateViewModel> VoltageCandidates { get; } = [];

    public bool HasVoltageCandidates => VoltageCandidates.Count > 0;

    public VoltageCandidateViewModel? SelectedCandidate
    {
        get => _selectedCandidate;
        set => SetProperty(ref _selectedCandidate, value);
    }

    public bool HasCalibration => _preferences.Load().DramVoltage is not null;

    public ICommand FindCandidatesCommand => _findCandidates ??= new AsyncCommand(FindCandidatesAsync);

    public ICommand UseCandidateCommand => _useCandidate ??= new AsyncCommand(UseCandidateAsync);

    public ICommand ForgetCalibrationCommand => _forgetCalibration ??= new AsyncCommand(ForgetCalibrationAsync);

    /// <summary>Fiches mémoire enregistrées, à comparer avec la lecture actuelle.</summary>
    public ObservableCollection<ChoiceOption<MemorySnapshot>> ComparisonOptions { get; } = [];

    public bool HasComparisonOptions => ComparisonOptions.Count > 0;

    public ChoiceOption<MemorySnapshot>? ComparisonChoice
    {
        get => _comparisonChoice;
        set
        {
            if (SetProperty(ref _comparisonChoice, value))
            {
                MemoryComparison = value is not null && _currentSnapshot is { } current
                    ? T("Depuis le {0:g} :", value.Value.At.LocalDateTime) + Environment.NewLine + MemorySnapshot.Describe(MemorySnapshot.Compare(value.Value, current))
                    : string.Empty;
            }
        }
    }

    public string MemoryComparison
    {
        get => _memoryComparison;
        private set
        {
            if (SetProperty(ref _memoryComparison, value))
            {
                OnPropertyChanged(nameof(HasMemoryComparison));
            }
        }
    }

    public bool HasMemoryComparison => MemoryComparison.Length > 0;

    /// <summary>
    /// Tension de la mémoire mesurée par la puce de surveillance de la carte mère (pilote PawnIO, lecture seule) : capteur
    /// nommé par LibreHardwareMonitor, sinon entrée étalonnée par l'utilisateur pour cette carte. Jamais devinée.
    /// </summary>
    private async Task<string> ReadDramVoltageAsync()
    {
        _measuredVoltage = null;
        try
        {
            _board = DramVoltageCalibrations.Board(new WindowsRegistryReader());
            _boardReadings = await Task.Run(() =>
            {
                using var sensors = new LhmAdvancedSensors();
                return sensors.Read();
            }).WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CanCalibrateVoltage = false;
            return T("Tension mesurée illisible : {0}", ex.Message);
        }

        if (AdvancedReadings.DramVoltage(_boardReadings) is { } dram)
        {
            CanCalibrateVoltage = false;
            _measuredVoltage = Volts(dram.Value);
            return T("Tension de la mémoire mesurée par la carte mère (capteur « {0} ») : {1} V.", dram.Name, dram.Value.ToString("0.000", CultureInfo.InvariantCulture));
        }

        CanCalibrateVoltage = true;
        if (_preferences.Load().DramVoltage is { } calibration && DramVoltageCalibrations.Apply(_boardReadings, calibration, _board) is { } volts)
        {
            _measuredVoltage = Volts(volts);
            CalibrationStatus = T("Étalonnée par vous le {0:d} : entrée « {1} »{2}, pour {3} V réglés dans le BIOS.",
                calibration.At.LocalDateTime, calibration.Sensor, calibration.Factor == 1 ? string.Empty : T(" × {0}", calibration.Factor.ToString("0", CultureInfo.InvariantCulture)),
                calibration.SetVolts.ToString("0.00", CultureInfo.InvariantCulture));
            return T("Tension de la mémoire mesurée par la carte mère (entrée étalonnée par vous) : {0} V.", volts.ToString("0.000", CultureInfo.InvariantCulture));
        }

        return T("Tension réellement appliquée : la puce de surveillance de cette carte mère ne dit pas à MAUS laquelle de ses entrées porte la tension de la mémoire (modèle non décrit par LibreHardwareMonitor), et MAUS ne la devine pas. Vous pouvez l'étalonner ci-dessous avec la tension que vous avez réglée dans le BIOS.");
    }

    private Task FindCandidatesAsync()
    {
        VoltageCandidates.Clear();
        SelectedCandidate = null;
        OnPropertyChanged(nameof(HasVoltageCandidates));
        var text = CalibrationInput.Trim().Replace(',', '.').TrimEnd('V', 'v').Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var set) || set is < 0.8 or > 2.2)
        {
            CalibrationStatus = T("Tapez la tension réglée dans le BIOS, par exemple 1,45.");
            return Task.CompletedTask;
        }

        foreach (var candidate in DramVoltageCalibrations.Candidates(_boardReadings, set))
        {
            VoltageCandidates.Add(new VoltageCandidateViewModel(candidate));
        }

        SelectedCandidate = VoltageCandidates.FirstOrDefault();
        OnPropertyChanged(nameof(HasVoltageCandidates));
        CalibrationStatus = VoltageCandidates.Count switch
        {
            0 => T("Aucune entrée de la carte mère ne mesure {0} V (à 3 % près) : MAUS ne peut pas étalonner la tension de la mémoire sur cette carte.", set.ToString("0.00", CultureInfo.InvariantCulture)),
            1 => T("Une entrée correspond. Vérifiez qu'elle bouge avec la tension de la mémoire si vous la changez dans le BIOS, puis choisissez « Utiliser cette entrée »."),
            _ => T("{0} entrées correspondent : choisissez celle de la mémoire. En cas de doute, changez un peu la tension dans le BIOS et regardez laquelle suit.", VoltageCandidates.Count),
        };
        return Task.CompletedTask;
    }

    private async Task UseCandidateAsync()
    {
        if (SelectedCandidate?.Candidate is not { } candidate || _board is null)
        {
            return;
        }

        var set = double.Parse(CalibrationInput.Trim().Replace(',', '.').TrimEnd('V', 'v').Trim(), CultureInfo.InvariantCulture);
        var calibration = new DramVoltageCalibration(_board, candidate.Reading.Hardware, candidate.Reading.Name, candidate.Factor, set, DateTimeOffset.Now);
        await SaveCalibrationAsync(calibration);
        VoltageCandidates.Clear();
        OnPropertyChanged(nameof(HasVoltageCandidates));
        _dramVoltage = await ReadDramVoltageAsync();
        ShowMemoryConfiguration(null);
    }

    private async Task ForgetCalibrationAsync()
    {
        await SaveCalibrationAsync(null);
        CalibrationStatus = string.Empty;
        _dramVoltage = await ReadDramVoltageAsync();
        ShowMemoryConfiguration(null);
    }

    private async Task SaveCalibrationAsync(DramVoltageCalibration? calibration)
    {
        try
        {
            await Task.Run(() => _preferences.Update(p => p with { DramVoltage = calibration }));
        }
        catch (Exception ex) when (ex is JournalUnsafeException or System.IO.IOException or UnauthorizedAccessException)
        {
            CalibrationStatus = T("Votre choix n'a pas pu être enregistré : {0}", ex.Message);
        }

        OnPropertyChanged(nameof(HasCalibration));
    }

    /// <summary>Enregistre la fiche de cette lecture et propose les fiches précédentes pour comparaison.</summary>
    private async Task SaveSnapshotAsync(MemoryDetailReport report)
    {
        var snapshot = MemorySnapshot.From(report, _memorySlots, _measuredVoltage, DateTimeOffset.Now);
        _currentSnapshot = snapshot;
        IReadOnlyList<MemorySnapshot> previous = [];
        try
        {
            previous = await Task.Run(() =>
            {
                var earlier = _snapshots.List();
                _snapshots.Save(snapshot);
                return earlier;
            });
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // Fiche non enregistrée : la comparaison reste possible avec les précédentes.
        }

        ComparisonOptions.Clear();
        foreach (var earlier in previous)
        {
            ComparisonOptions.Add(new ChoiceOption<MemorySnapshot>(T("{0:g} · {1}", earlier.At.LocalDateTime, earlier.Summary), earlier));
        }

        OnPropertyChanged(nameof(HasComparisonOptions));
        ComparisonChoice = ComparisonOptions.FirstOrDefault();
    }
}
