using Maus.App.Appearance;

namespace Maus.App.ViewModels;

/// <summary>
/// Mode technicien (0.8.6, Paramètres) pour toute l'interface : les zones « Détails techniques » s'ouvrent d'elles-mêmes.
/// Lié en XAML comme <see cref="ElevationState"/> : <c>Source={x:Static vm:DisplayMode.Current}, Path=IsTechnician</c>.
/// </summary>
public sealed class DisplayMode : ObservableObject
{
    private bool _technician = AppearanceManager.Current.TechnicianMode;

    private DisplayMode()
    {
        AppearanceManager.Changed += (_, _) =>
        {
            _technician = AppearanceManager.Current.TechnicianMode;
            OnPropertyChanged(nameof(IsTechnician));
        };
    }

    public static DisplayMode Current { get; } = new();

    public bool IsTechnician => _technician;

}
