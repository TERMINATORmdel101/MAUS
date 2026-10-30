using System.Windows.Input;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>Atelier, En direct : capteurs avancés par le pilote PawnIO (installation et retrait à la demande).</summary>
public sealed partial class WorkshopViewModel
{
    private ICommand? _installPawnIo;
    private ICommand? _uninstallPawnIo;
    private string _pawnIoStatus = string.Empty;
    private bool _pawnIoInstalled;

    public string PawnIoStatus
    {
        get => _pawnIoStatus;
        private set => SetProperty(ref _pawnIoStatus, value);
    }

    public bool IsPawnIoInstalled
    {
        get => _pawnIoInstalled;
        private set
        {
            if (SetProperty(ref _pawnIoInstalled, value))
            {
                OnPropertyChanged(nameof(IsPawnIoMissing));
            }
        }
    }

    public bool IsPawnIoMissing => !IsPawnIoInstalled;

    private ICommand? _enableAdvancedSensors;
    private bool _canEnableAdvancedSensors;

    /// <summary>PawnIO installé, moteur administrateur pas encore lancé : le bouton « Activer les capteurs avancés » s'affiche.</summary>
    public bool CanEnableAdvancedSensors
    {
        get => _canEnableAdvancedSensors;
        private set => SetProperty(ref _canEnableAdvancedSensors, value);
    }

    /// <summary>Lance le moteur administrateur (demande d'autorisation de Windows) : les mesures suivantes passent par PawnIO.</summary>
    public ICommand EnableAdvancedSensorsCommand => _enableAdvancedSensors ??= new AsyncCommand(async () =>
    {
        if (EngineHost.OnDemand is { } onDemand)
        {
            await onDemand.EnsureElevatedAsync(CancellationToken.None);
            CanEnableAdvancedSensors = false;
        }
    });

    public ICommand InstallPawnIoCommand => _installPawnIo ??= new AsyncCommand(() => RunPawnIoConsoleAsync(install: true));

    public ICommand UninstallPawnIoCommand => _uninstallPawnIo ??= new AsyncCommand(() => RunPawnIoConsoleAsync(install: false));

    /// <summary>
    /// Mesures lues par le moteur : sans pilote, complétées par PawnIO s'il est installé (le moteur a les droits
    /// administrateur). Une erreur du pilote ne bloque jamais les autres mesures.
    /// </summary>
    private ISensorSource CreateSensors() => new Maus.Core.Engine.Broker.EngineSensorSource(EngineHost.Current);

    /// <summary>Après chaque mesure : état du pilote PawnIO tel que le moteur le voit.</summary>
    private void CheckAdvancedSensors()
    {
        if (_sensors is not Maus.Core.Engine.Broker.EngineSensorSource { State: { } state })
        {
            return;
        }

        Live.CpuMaxC = state.CpuMaxC;
        IsPawnIoInstalled = state.PawnIoInstalled;
        CanEnableAdvancedSensors = state.PawnIoInstalled && !state.EngineElevated && EngineHost.OnDemand is not null;
        PawnIoStatus = !state.PawnIoInstalled
            ? T("Sans pilote, MAUS ne lit ni la vraie température du processeur, ni sa tension, ni les sondes et ventilateurs de la carte mère. "
                + "PawnIO est un pilote libre et signé, utilisé par LibreHardwareMonitor, FanControl et OpenRGB : il permet de les lire. "
                + "Il s'installe et se retire en un clic (winget, fenêtre visible). Certains anti-triche de jeux peuvent refuser les pilotes "
                + "d'accès au matériel : en cas de souci, retirez-le.")
            : !state.EngineElevated
                ? T("PawnIO {0} est installé. La vraie température du processeur, sa tension et les sondes de la carte mère se lisent avec les droits administrateur : bouton « Activer les capteurs avancés » (Windows demande votre autorisation). Ces mesures servent aussi à arrêter les tests de stabilité en cas de surchauffe.", state.PawnIoVersion ?? "?")
                : state.Failure is { } failure
                    ? T("PawnIO est installé mais n'a pas pu être utilisé : {0}", failure)
                    : state.IsStalled
                        ? T("Le pilote PawnIO ne répond plus depuis quelques secondes : les mesures sans pilote continuent. Un autre logiciel de surveillance l'occupe peut-être.")
                        : T("PawnIO {0} installé : température, tension et puissance du processeur, sondes et ventilateurs de la carte mère lus par LibreHardwareMonitor (bibliothèque libre, MPL-2.0).", state.PawnIoVersion ?? "?");
    }

    private async Task RunPawnIoConsoleAsync(bool install)
    {
        var title = install ? T("Installer le pilote PawnIO ?") : T("Retirer le pilote PawnIO ?");
        var message = install
            ? T("MAUS va ouvrir une fenêtre de commande qui installe PawnIO avec winget, l'outil de Microsoft (paquet « namazso.PawnIO »). "
                + "PawnIO est un pilote libre et signé qui donne accès aux capteurs du processeur et de la carte mère ; il reste installé "
                + "jusqu'à ce que vous le retiriez (bouton « Retirer PawnIO », ou Paramètres > Applications). Relancez MAUS après l'installation.")
            : T("MAUS va ouvrir une fenêtre de commande qui désinstalle PawnIO avec winget. Les autres logiciels qui l'utilisent "
                + "(FanControl, LibreHardwareMonitor, OpenRGB) perdront aussi l'accès aux capteurs. Relancez MAUS ensuite.");
        if (!_confirm(title, message + Environment.NewLine + Environment.NewLine + Maus.Core.Legal.Disclaimer.OperationReminder + Environment.NewLine + Environment.NewLine + T("Continuer ?")))
        {
            return;
        }

        // Attente asynchrone : le moteur affiche sa propre confirmation pendant ce temps, la fenêtre de MAUS reste vivante.
        var outcome = await EngineHost.Current.OpenConsoleAsync(new Maus.Core.Engine.Broker.ConsoleRequest(
            install ? Maus.Core.Engine.Broker.ConsoleKind.PawnIoInstall : Maus.Core.Engine.Broker.ConsoleKind.PawnIoUninstall), CancellationToken.None);
        if (!outcome.Opened)
        {
            PawnIoStatus = outcome.Message ?? T("La fenêtre de commande n'a pas pu s'ouvrir.");
        }
    }
}
