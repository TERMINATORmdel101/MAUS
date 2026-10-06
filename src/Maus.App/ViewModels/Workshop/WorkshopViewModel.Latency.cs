using System.Windows.Input;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>
/// Atelier, onglet Tests : latence des pilotes (craquements audio, micro-saccades). Écoute la trace du noyau de Windows
/// pendant quelques secondes, en lecture seule, et nomme les pilotes qui ont gardé le processeur le plus longtemps d'affilée.
/// </summary>
public sealed partial class WorkshopViewModel
{
    private readonly EtwDriverLatencyTracer _latencyTracer = new();
    private bool _isLatencyRunning;
    private string _latencyStatus = T("Laissez tourner ce qui crée le problème (musique, jeu, appel vidéo), puis lancez la mesure.");
    private TestOption<TimeSpan>? _latencyDuration;
    private ICommand? _startLatency;

    /// <summary>Durées proposées (créées avec l'atelier, donc dans la langue choisie).</summary>
    public IReadOnlyList<TestOption<TimeSpan>> LatencyDurations { get; } =
    [
        new(T("10 secondes"), TimeSpan.FromSeconds(10)),
        new(T("30 secondes (recommandé)"), TimeSpan.FromSeconds(30)),
        new(T("1 minute"), TimeSpan.FromMinutes(1)),
    ];

    public TestOption<TimeSpan> LatencyDuration
    {
        get => _latencyDuration ?? LatencyDurations[1];
        set => SetProperty(ref _latencyDuration, value);
    }

    public bool IsLatencyRunning
    {
        get => _isLatencyRunning;
        private set
        {
            if (SetProperty(ref _isLatencyRunning, value))
            {
                OnPropertyChanged(nameof(IsNotLatencyRunning));
            }
        }
    }

    public bool IsNotLatencyRunning => !IsLatencyRunning;

    public string LatencyStatus
    {
        get => _latencyStatus;
        private set => SetProperty(ref _latencyStatus, value);
    }

    public ICommand StartLatencyCommand => _startLatency ??= new AsyncCommand(RunLatencyAsync);

    private async Task RunLatencyAsync()
    {
        if (IsLatencyRunning)
        {
            return;
        }

        IsLatencyRunning = true;
        var duration = LatencyDuration.Value;
        LatencyStatus = T("Mesure en cours ({0:0} s)… continuez d'utiliser le PC normalement.", duration.TotalSeconds);
        try
        {
            var result = await _latencyTracer.MeasureAsync(duration, CancellationToken.None);
            LatencyStatus = result.Describe()
                + Environment.NewLine + Environment.NewLine
                + T("Un pilote qui garde le processeur longtemps d'affilée peut retarder le son et les images (craquements, micro-saccades). Si un nom revient en tête quand le problème se produit, cherchez une mise à jour de ce pilote (fabricant de la carte mère, de la carte son, de la carte réseau ou de la carte graphique).");
        }
        catch (InvalidOperationException ex)
        {
            LatencyStatus = ex.Message;
        }
        finally
        {
            IsLatencyRunning = false;
        }
    }
}
