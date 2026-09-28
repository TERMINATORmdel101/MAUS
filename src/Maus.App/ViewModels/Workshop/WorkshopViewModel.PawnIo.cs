using System.Windows.Input;
using Maus.Core.Modules.M20Software;
using Maus.Core.Platform;
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

    public ICommand InstallPawnIoCommand => _installPawnIo ??= new AsyncCommand(() => Run(() => RunPawnIoConsole(install: true)));

    public ICommand UninstallPawnIoCommand => _uninstallPawnIo ??= new AsyncCommand(() => Run(() => RunPawnIoConsole(install: false)));

    /// <summary>
    /// Mesures sans pilote, complétées par PawnIO s'il est installé et que MAUS a les droits administrateur. Une erreur du
    /// pilote ne bloque jamais les autres mesures.
    /// </summary>
    private ISensorSource CreateSensors()
    {
        ISensorSource basic = new WindowsSensorSource();
        var registry = new WindowsRegistryReader();
        var (installed, version) = PawnIo.State(registry);
        Live.CpuMaxC = CpuName(registry) is { } name ? SafetyLimits.Load().ForCpu(name)?.MaxC : null;
        IsPawnIoInstalled = installed;
        if (!installed)
        {
            PawnIoStatus = T("Sans pilote, MAUS ne lit ni la vraie température du processeur, ni sa tension, ni les sondes et ventilateurs de la carte mère. "
                + "PawnIO est un pilote libre et signé, utilisé par LibreHardwareMonitor, FanControl et OpenRGB : il permet de les lire. "
                + "Il s'installe et se retire en un clic (winget, fenêtre visible). Certains anti-triche de jeux peuvent refuser les pilotes "
                + "d'accès au matériel : en cas de souci, retirez-le.");
            return basic;
        }

        if (!ProcessElevation.IsElevated())
        {
            PawnIoStatus = T("PawnIO {0} est installé, mais il faut lancer MAUS en administrateur pour lire les capteurs.", version ?? "?");
            return basic;
        }

        // Le pilote s'ouvre en arrière-plan : les mesures sans pilote s'affichent tout de suite.
        PawnIoStatus = T("PawnIO {0} installé : température, tension et puissance du processeur, sondes et ventilateurs de la carte mère lus par LibreHardwareMonitor (bibliothèque libre, MPL-2.0).", version ?? "?");
        return new CombinedSensorSource(basic, () => new LhmAdvancedSensors());
    }

    /// <summary>Après chaque mesure : signale un pilote qui n'a pas pu s'ouvrir ou qui ne répond plus.</summary>
    private void CheckAdvancedSensors()
    {
        if (_sensors is not CombinedSensorSource combined)
        {
            return;
        }

        if (combined.Failure is { } failure)
        {
            PawnIoStatus = T("PawnIO est installé mais n'a pas pu être utilisé : {0}", failure);
        }
        else if (combined.IsStalled)
        {
            PawnIoStatus = T("Le pilote PawnIO ne répond plus depuis quelques secondes : les mesures sans pilote continuent. Un autre logiciel de surveillance l'occupe peut-être.");
        }
    }

    private static string? CpuName(WindowsRegistryReader registry)
    {
        try
        {
            return registry.GetValue(Microsoft.Win32.RegistryHive.LocalMachine, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") as string;
        }
        catch (MausAccessDeniedException)
        {
            return null;
        }
    }

    private void RunPawnIoConsole(bool install)
    {
        var title = install ? T("Installer le pilote PawnIO ?") : T("Retirer le pilote PawnIO ?");
        var message = install
            ? T("MAUS va ouvrir une fenêtre de commande qui installe PawnIO avec winget, l'outil de Microsoft (paquet « namazso.PawnIO »). "
                + "PawnIO est un pilote libre et signé qui donne accès aux capteurs du processeur et de la carte mère ; il reste installé "
                + "jusqu'à ce que vous le retiriez (bouton « Retirer PawnIO », ou Paramètres > Applications). Relancez MAUS après l'installation.")
            : T("MAUS va ouvrir une fenêtre de commande qui désinstalle PawnIO avec winget. Les autres logiciels qui l'utilisent "
                + "(FanControl, LibreHardwareMonitor, OpenRGB) perdront aussi l'accès aux capteurs. Relancez MAUS ensuite.");
        if (!_confirm(title, message + Environment.NewLine + Environment.NewLine + T("Continuer ?")))
        {
            return;
        }

        var winget = Winget.Locate(new WinRtPackageInventory(), new LocalFileSystemReader());
        if (winget is null)
        {
            PawnIoStatus = T("winget est absent : réinstallez « Programme d'installation d'application » depuis le Microsoft Store.");
            return;
        }

        if (!ShellLauncher.RunConsole(install ? PawnIo.InstallConsoleArguments(winget) : PawnIo.UninstallConsoleArguments(winget)))
        {
            PawnIoStatus = T("La fenêtre de commande n'a pas pu s'ouvrir.");
        }
    }
}
