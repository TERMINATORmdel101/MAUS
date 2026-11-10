using System.Collections.ObjectModel;
using System.Globalization;
using Maus.Core.Workshop;
using Maus.Core.Workshop.Memory;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>
/// Onglet Mémoire, suite : tension mesurée par la carte mère, quand elle la décrit, et comparaison avec une fiche enregistrée
/// (avant / après un réglage dans le BIOS).
/// </summary>
public sealed partial class WorkshopViewModel
{
    private readonly MemorySnapshotStore _snapshots = MemorySnapshotStore.CreateDefault();
    private string? _measuredVoltage;
    private MemorySnapshot? _currentSnapshot;
    private ChoiceOption<MemorySnapshot>? _comparisonChoice;
    private string _memoryComparison = string.Empty;

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
    /// nommé par LibreHardwareMonitor. Jamais devinée.
    /// </summary>
    private async Task<string> ReadDramVoltageAsync()
    {
        _measuredVoltage = null;
        IReadOnlyList<HardwareReading> boardReadings;
        try
        {
            boardReadings = await ReadBoardSensorsAsync().WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return T("Tension mesurée illisible : {0}", Maus.Core.Diagnostics.ErrorText.For(ex));
        }

        if (AdvancedReadings.DramVoltage(boardReadings) is { } dram)
        {
            _measuredVoltage = Volts(dram.Value);
            return T("Tension de la mémoire mesurée par la carte mère (capteur « {0} ») : {1} V.", dram.Name, dram.Value.ToString("0.000", CultureInfo.InvariantCulture));
        }

        return T("Tension réellement appliquée : non lisible sur cette carte mère (MAUS ne devine pas la bonne mesure).");
    }

    /// <summary>
    /// Capteurs de la carte mère lus par le moteur (PawnIO) : le pilote s'ouvre en arrière-plan, les premières mesures peuvent
    /// arriver sans eux, d'où quelques essais.
    /// </summary>
    private static async Task<IReadOnlyList<HardwareReading>> ReadBoardSensorsAsync()
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var reading = await EngineHost.Current.SampleSensorsAsync(CancellationToken.None);
            if (reading.Snapshot.Readings is { Count: > 0 } readings)
            {
                return readings;
            }

            if (reading.State.Failure is { } failure)
            {
                throw new InvalidOperationException(failure);
            }

            await Task.Delay(500);
        }

        return [];
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
                try
                {
                    _snapshots.Save(snapshot);
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                {
                    // Fiche non enregistrée : la comparaison avec les précédentes, déjà lues, reste possible.
                    Maus.Core.Diagnostics.Breadcrumbs.Add("fiche mémoire non enregistrée (" + ex.GetType().Name + ")");
                }

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
