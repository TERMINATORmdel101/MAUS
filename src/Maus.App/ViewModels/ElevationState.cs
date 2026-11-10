using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Étape de la demande de droits administrateur.</summary>
public enum ElevationStage
{
    None,

    /// <summary>La fenêtre d'autorisation de Windows est ouverte.</summary>
    Asking,

    /// <summary>Autorisation accordée : le moteur démarre.</summary>
    Starting,
}

/// <summary>
/// Mode limité ou toutes les fonctionnalités, pour toute l'interface (demande du porteur, 29/10/2026) : en mode limité, les
/// options qui ont besoin des droits administrateur sont grisées (liées à <see cref="IsFull"/>), et un bandeau propose
/// « Toutes les fonctionnalités ». La demande d'autorisation de Windows ne vient que de ce bouton (ou de la fenêtre
/// d'ouverture, des capteurs avancés, de « Remettre en place » sur l'accueil) : c'est l'utilisateur qui la déclenche. Quand le moteur s'arrête (15 minutes sans demande), tout redevient gris.
/// </summary>
public sealed class ElevationState : ObservableObject
{
    /// <summary>Dernier état connu, relu à chaque démarrage ou arrêt du moteur.</summary>
    private bool _full = EngineHost.IsElevated;

    private ElevationStage _stage;

    /// <summary>Le moteur s'est arrêté seul (inactivité) : le bandeau dit pourquoi les options sont de nouveau grises.</summary>
    private bool _closedAlone;

    /// <summary>L'utilisateur a demandé de refermer : ce n'est pas un arrêt pour inactivité.</summary>
    private bool _closeAsked;

    private string _fullText = FullBannerText();

    private ElevationState()
    {
        EngineHost.ElevationChanged += (_, _) => Refresh();
        EngineHost.StageChanged += (_, stage) => OnInterface(() =>
        {
            _stage = stage;
            NotifyStage();
        });
        EnableAllCommand = new AsyncCommand(async () =>
        {
            if (EngineHost.OnDemand is { } onDemand)
            {
                await onDemand.EnsureElevatedAsync(CancellationToken.None);
            }
        });
        CloseRightsCommand = new AsyncCommand(async () =>
        {
            if (EngineHost.OnDemand is not { } onDemand)
            {
                return;
            }

            _closeAsked = true;
            var closed = await onDemand.CloseRightsAsync(CancellationToken.None);
            _closeAsked = closed;
            FullText = closed
                ? FullBannerText()
                : T("Une opération est en cours : les droits restent ouverts jusqu'à sa fin. Ils se referment ensuite seuls, ou d'ici.");
        });
    }

    public static ElevationState Current { get; } = new();

    /// <summary>Toutes les fonctionnalités : moteur administrateur en marche, ou MAUS lancé en administrateur.</summary>
    public bool IsFull => _full;

    /// <summary>Bandeau « Mode limité » : MAUS ouvert sans droits et moteur administrateur arrêté.</summary>
    public bool ShowLimitedBanner => !IsFull && EngineHost.OnDemand is not null;

    /// <summary>Bande discrète : les droits sont ouverts par le moteur, et ce bouton les referme. MAUS lancé en administrateur n'a rien à refermer.</summary>
    public bool ShowFullBanner => IsFull && EngineHost.OnDemand is not null;

    public string FullText
    {
        get => _fullText;
        private set => SetProperty(ref _fullText, value);
    }

    public ICommand CloseRightsCommand { get; }

    /// <summary>Demande de droits en cours : le bandeau le montre dès le clic.</summary>
    public bool IsEnabling => _stage != ElevationStage.None;

    public bool CanEnable => !IsEnabling;

    /// <summary>Texte du bandeau, selon l'étape de la demande.</summary>
    public string LimitedText => _stage switch
    {
        ElevationStage.Asking => T("Windows demande votre autorisation…"),
        ElevationStage.Starting => T("Autorisation accordée : MAUS active toutes les fonctionnalités…"),
        _ when _closedAlone => WithClosedNote(Ui.LimitedModeBanner),
        _ => Ui.LimitedModeBanner,
    };

    internal static string WithClosedNote(string banner) => banner + " " + T("Les droits se sont refermés après 15 minutes sans utilisation.");

    private static string FullBannerText() => T("Droits administrateur ouverts. Ils se referment seuls après 15 minutes sans utilisation (la page En direct ouverte compte comme une utilisation).");

    /// <summary>Lance le moteur administrateur (demande d'autorisation de Windows) ; un refus laisse MAUS en mode limité.</summary>
    public ICommand EnableAllCommand { get; }

    private void Refresh() => OnInterface(() =>
    {
        var wasFull = _full;
        _full = EngineHost.IsElevated;
        _stage = ElevationStage.None;
        _closedAlone = _full ? false : _closedAlone || (wasFull && !_closeAsked && EngineHost.LastEngineExitCode == 0);
        _closeAsked = false;
        FullText = FullBannerText();
        OnPropertyChanged(nameof(IsFull));
        OnPropertyChanged(nameof(ShowLimitedBanner));
        OnPropertyChanged(nameof(ShowFullBanner));
        NotifyStage();
    });

    private void NotifyStage()
    {
        OnPropertyChanged(nameof(IsEnabling));
        OnPropertyChanged(nameof(CanEnable));
        OnPropertyChanged(nameof(LimitedText));
    }

    /// <summary>Avant tout le reste : c'est ce que l'utilisateur attend de voir.</summary>
    private static void OnInterface(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(action, DispatcherPriority.Send);
        }
        else
        {
            action();
        }
    }
}
