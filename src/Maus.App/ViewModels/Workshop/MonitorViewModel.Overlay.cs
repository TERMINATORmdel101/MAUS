using System.Windows.Threading;
using Maus.App.Appearance;
using Maus.App.Views;
using Maus.Core.Diagnostics;
using Maus.Core.Preferences;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>
/// Compteur au-dessus du jeu (demande du porteur, 06/10/2026) : affiché pendant la mesure des images par seconde si
/// l'utilisateur le veut ; taille, transparence et coin réglables, gardés dans ses choix.
/// </summary>
public sealed partial class MonitorViewModel
{
    private readonly DispatcherTimer _overlaySave = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private FpsOverlayWindow? _overlay;
    private bool _overlaySaveHooked;
    private bool _overlayEnabled = AppearanceManager.Current.OverlayEnabled;
    private double _overlayScale = AppearanceManager.Current.OverlayScaleUsed;
    private double _overlayOpacity = AppearanceManager.Current.OverlayOpacityUsed;
    private ChoiceOption<OverlayCorner>? _overlayCorner;

    /// <summary>Coins proposés (créés avec la fenêtre, donc dans la langue choisie).</summary>
    public IReadOnlyList<ChoiceOption<OverlayCorner>> OverlayCorners { get; } =
    [
        new(T("En haut à gauche"), OverlayCorner.TopLeft),
        new(T("En haut à droite"), OverlayCorner.TopRight),
        new(T("En bas à gauche"), OverlayCorner.BottomLeft),
        new(T("En bas à droite"), OverlayCorner.BottomRight),
    ];

    public bool OverlayEnabled
    {
        get => _overlayEnabled;
        set
        {
            if (SetProperty(ref _overlayEnabled, value))
            {
                SyncOverlay();
                SaveOverlaySoon();
            }
        }
    }

    /// <summary>Taille du compteur : de 0,6 à 2,5 fois la taille normale.</summary>
    public double OverlayScale
    {
        get => _overlayScale;
        set
        {
            if (SetProperty(ref _overlayScale, Math.Clamp(value, 0.6, 2.5)))
            {
                OnPropertyChanged(nameof(OverlayScaleText));
                ApplyOverlay();
                SaveOverlaySoon();
            }
        }
    }

    /// <summary>Opacité du compteur : de 20 % (très transparent) à 100 % (opaque).</summary>
    public double OverlayOpacity
    {
        get => _overlayOpacity;
        set
        {
            if (SetProperty(ref _overlayOpacity, Math.Clamp(value, 0.2, 1)))
            {
                OnPropertyChanged(nameof(OverlayOpacityText));
                ApplyOverlay();
                SaveOverlaySoon();
            }
        }
    }

    public string OverlayScaleText => T("{0:0} %", OverlayScale * 100);

    public string OverlayOpacityText => T("{0:0} %", OverlayOpacity * 100);

    public ChoiceOption<OverlayCorner> OverlayCornerChoice
    {
        get => _overlayCorner ?? OverlayCorners.FirstOrDefault(c => c.Value == AppearanceManager.Current.OverlayCorner) ?? OverlayCorners[0];
        set
        {
            if (value is not null && SetProperty(ref _overlayCorner, value))
            {
                ApplyOverlay();
                SaveOverlaySoon();
            }
        }
    }

    /// <summary>Affiche ou retire le compteur selon la mesure en cours et le choix de l'utilisateur.</summary>
    private void SyncOverlay()
    {
        if (IsMeasuringFps && OverlayEnabled)
        {
            if (_overlay is null)
            {
                _overlay = new FpsOverlayWindow();
                _overlay.Show();
                ApplyOverlay();
                Breadcrumbs.Add("compteur au-dessus du jeu affiché");
            }
        }
        else
        {
            CloseOverlay();
        }
    }

    private void ApplyOverlay() => _overlay?.Apply(OverlayScale, OverlayOpacity, OverlayCornerChoice.Value);

    private void CloseOverlay()
    {
        if (_overlay is { } overlay)
        {
            _overlay = null;
            overlay.Dispatcher.Invoke(overlay.Close);
        }
    }

    /// <summary>Enregistre les réglages un peu après le dernier mouvement du curseur (pas à chaque pixel).</summary>
    private void SaveOverlaySoon()
    {
        if (!_overlaySaveHooked)
        {
            _overlaySave.Tick += (_, _) =>
            {
                _overlaySave.Stop();
                var (enabled, scale, opacity, corner) = (OverlayEnabled, OverlayScale, OverlayOpacity, OverlayCornerChoice.Value);
                AppearanceManager.Apply(AppearanceManager.Current with { OverlayEnabled = enabled, OverlayScale = scale, OverlayOpacity = opacity, OverlayCorner = corner });
                Task.Run(() =>
                {
                    try
                    {
                        FilePreferencesStore.CreateDefault().Update(p => p with { OverlayEnabled = enabled, OverlayScale = scale, OverlayOpacity = opacity, OverlayCorner = corner });
                    }
                    catch (Exception ex) when (ex is Maus.Core.Fixes.JournalUnsafeException or System.IO.IOException or UnauthorizedAccessException)
                    {
                        // Le réglage vaut pour cette ouverture de MAUS.
                    }
                }).Forget("compteur au-dessus du jeu : enregistrement des réglages");
            };
            _overlaySaveHooked = true;
        }

        _overlaySave.Stop();
        _overlaySave.Start();
    }
}
