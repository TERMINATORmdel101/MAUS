using System.Windows.Input;
using Maus.Core.Platform;
using System.Windows.Threading;
using Maus.Core.Diagnostics;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>
/// Compteur d'images par seconde de la fenêtre de surveillance (demande du porteur, 05/10/2026) : moyenne, 1 % et 0,1 % le
/// plus lents sur les dix dernières secondes, travail de la carte graphique, fréquence de l'écran, et ce qui limite.
/// Mesuré par PresentMon seulement quand l'utilisateur le demande ; arrêté avec la fenêtre.
/// </summary>
public sealed partial class MonitorViewModel
{
    private static readonly TimeSpan FpsWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Sans image depuis ce temps, le jeu est fermé, en pause ou réduit : le compteur repasse sur « en attente ». Windows livre
    /// les images par lots d'environ une seconde (mesuré le 07/10/2026 sur le PC du porteur) : moins de trois secondes ferait
    /// clignoter le compteur.
    /// </summary>
    private static readonly TimeSpan FpsSilence = TimeSpan.FromSeconds(3);

    // Deux fois par seconde : le chiffre en direct suit le jeu sans à-coups.
    private readonly DispatcherTimer _fpsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private PresentMonCapture? _fps;
    private int? _refreshHz;
    private string _fpsGame = string.Empty;
    private string _fpsAverage = "—";
    private string _fpsLow1 = "—";
    private string _fpsLow01 = "—";
    private string _fpsGpu = "—";
    private string _fpsAdvice = string.Empty;
    private string _fpsStatus = T("Lancez la mesure, puis votre jeu : MAUS affiche les images par seconde des dix dernières secondes.");
    private ICommand? _toggleFps;

    public bool IsMeasuringFps => _fps is not null;

    public string FpsButton => IsMeasuringFps ? T("Arrêter la mesure") : T("Mesurer les images par seconde");

    public ICommand ToggleFpsCommand => _toggleFps ??= new AsyncCommand(ToggleFpsAsync);

    public string FpsGame
    {
        get => _fpsGame;
        private set => SetProperty(ref _fpsGame, value);
    }

    public string FpsAverage
    {
        get => _fpsAverage;
        private set => SetProperty(ref _fpsAverage, value);
    }

    public string FpsLow1
    {
        get => _fpsLow1;
        private set => SetProperty(ref _fpsLow1, value);
    }

    public string FpsLow01
    {
        get => _fpsLow01;
        private set => SetProperty(ref _fpsLow01, value);
    }

    public string FpsGpu
    {
        get => _fpsGpu;
        private set => SetProperty(ref _fpsGpu, value);
    }

    public string FpsAdvice
    {
        get => _fpsAdvice;
        private set
        {
            if (SetProperty(ref _fpsAdvice, value))
            {
                OnPropertyChanged(nameof(HasFpsAdvice));
            }
        }
    }

    public bool HasFpsAdvice => FpsAdvice.Length > 0;

    public string FpsStatus
    {
        get => _fpsStatus;
        private set => SetProperty(ref _fpsStatus, value);
    }

    private async Task ToggleFpsAsync()
    {
        if (_fps is not null)
        {
            await Task.Run(StopFps);
            OnFpsStateChanged();
            FpsStatus = T("Mesure arrêtée.");
            return;
        }

        _refreshHz = DisplayRefresh.PrimaryHz();
        var capture = await Task.Run(() => PresentMonCapture.TryStart(FpsWindow + FpsWindow, "MAUS_Compteur"));
        if (capture is null)
        {
            FpsStatus = T("PresentMon est indisponible : les images par seconde ne peuvent pas être mesurées.");
            return;
        }

        _fps = capture;
        if (!_fpsTimerHooked)
        {
            _fpsTimer.Tick += (_, _) => RefreshFps();
            _fpsTimerHooked = true;
        }

        _fpsTimer.Start();
        OnFpsStateChanged();
        SyncOverlay();
        FpsStatus = _refreshHz is { } hz
            ? T("Mesure en cours. Écran principal : {0} Hz.", hz)
            : T("Mesure en cours. Fréquence de l'écran inconnue.");
    }

    private bool _fpsTimerHooked;

    private void RefreshFps()
    {
        var summary = _fps?.Log.Summarize(FpsWindow, FpsSilence);
        _overlay?.Display(summary);
        if (summary is not { } frames)
        {
            FpsGame = T("en attente d'un jeu…");
            FpsAverage = FpsLow1 = FpsLow01 = FpsGpu = "—";
            FpsAdvice = string.Empty;
            return;
        }

        FpsGame = T("{0} · moyenne sur 10 s : {1:0} images par seconde", frames.Application, frames.AverageFps);
        FpsAverage = frames.LiveFps.ToString("0", Culture);
        FpsLow1 = frames.Low1Fps.ToString("0", Culture);
        FpsLow01 = frames.Low01Fps is { } low01 ? low01.ToString("0", Culture) : "—";
        FpsGpu = frames.GpuBusyShare is { } busy ? (busy * 100).ToString("0", Culture) + " %" : "—";
        FpsAdvice = string.Join(Environment.NewLine + Environment.NewLine, FrameAdvice.Explain(frames, _refreshHz));
    }

    /// <summary>Arrête PresentMon (bouton) ; quelques secondes au plus, hors du fil de l'interface.</summary>
    private void StopFps()
    {
        _fpsTimer.Dispatcher.Invoke(_fpsTimer.Stop);
        CloseOverlay();
        if (_fps is { } capture)
        {
            _fps = null;
            using (capture)
            {
                capture.Stop();
            }

            Breadcrumbs.Add("compteur d'images par seconde arrêté");
        }
    }

    /// <summary>
    /// Fermeture de la fenêtre pendant la mesure : PresentMon est arrêté tout de suite, et sa session d'écoute refermée en
    /// arrière-plan. Attendre ici (jusqu'à huit secondes) figeait toute l'interface de MAUS.
    /// </summary>
    private void StopFpsWithoutWaiting()
    {
        _fpsTimer.Stop();
        CloseOverlay();
        if (_fps is { } capture)
        {
            _fps = null;
            capture.Halt();
            Task.Run(() =>
            {
                using (capture)
                {
                    capture.Stop();
                }

                Breadcrumbs.Add("compteur d'images par seconde arrêté (fenêtre fermée)");
            }).Forget("compteur d'images par seconde : arrêt");
        }
    }

    private void OnFpsStateChanged()
    {
        OnPropertyChanged(nameof(IsMeasuringFps));
        OnPropertyChanged(nameof(FpsButton));
    }
}
