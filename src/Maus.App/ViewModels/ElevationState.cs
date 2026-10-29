using System.Windows;
using System.Windows.Input;

namespace Maus.App.ViewModels;

/// <summary>
/// Mode limité ou toutes les fonctionnalités, pour toute l'interface (demande du porteur, 29/10/2026) : en mode limité, les
/// options qui ont besoin des droits administrateur sont grisées (liées à <see cref="IsFull"/>), et un bandeau propose
/// « Toutes les fonctionnalités ». La demande d'autorisation de Windows ne vient que de ce bouton (ou de la fenêtre
/// d'ouverture) : c'est l'utilisateur qui la déclenche. Quand le moteur s'arrête (15 minutes sans demande), tout redevient gris.
/// </summary>
public sealed class ElevationState : ObservableObject
{
    /// <summary>Dernier état connu, relu à chaque démarrage ou arrêt du moteur.</summary>
    private bool _full = EngineHost.IsElevated;

    private ElevationState()
    {
        EngineHost.ElevationChanged += (_, _) => Refresh();
        EnableAllCommand = new AsyncCommand(async () =>
        {
            if (EngineHost.OnDemand is { } onDemand)
            {
                await onDemand.EnsureElevatedAsync(CancellationToken.None);
            }
        });
    }

    public static ElevationState Current { get; } = new();

    /// <summary>Toutes les fonctionnalités : moteur administrateur en marche, ou MAUS lancé en administrateur.</summary>
    public bool IsFull => _full;

    /// <summary>Bandeau « Mode limité » : MAUS ouvert sans droits et moteur administrateur arrêté.</summary>
    public bool ShowLimitedBanner => !IsFull && EngineHost.OnDemand is not null;

    /// <summary>Lance le moteur administrateur (demande d'autorisation de Windows) ; un refus laisse MAUS en mode limité.</summary>
    public ICommand EnableAllCommand { get; }

    private void Refresh()
    {
        void Notify()
        {
            _full = EngineHost.IsElevated;
            OnPropertyChanged(nameof(IsFull));
            OnPropertyChanged(nameof(ShowLimitedBanner));
        }

        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(Notify);
        }
        else
        {
            Notify();
        }
    }
}
