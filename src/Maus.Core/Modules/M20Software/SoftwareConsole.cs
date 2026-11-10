using Maus.Core.Engine.Broker;
using Maus.Core.Platform;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M20Software;

/// <summary>
/// Fenêtres de winget, ouvertes par l'interface et sans droits administrateur (0.8.6) : ce que winget installe demande lui-même
/// l'autorisation de Windows s'il en a besoin, et n'hérite jamais de celle de MAUS. Le moteur ne lance plus winget pour installer.
/// </summary>
public static class SoftwareConsole
{
    public static ToolOutcome Upgrade(IReadOnlyList<SoftwareUpdate> chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        return chosen.Any(u => u.CanTarget)
            ? Open(path => Winget.UpgradeConsoleArguments(path, chosen))
            : new ToolOutcome(false, T("Aucun logiciel de la liste n'a été choisi."));
    }

    public static ToolOutcome InstallPawnIo() => Open(PawnIo.InstallConsoleArguments);

    public static ToolOutcome UninstallPawnIo() => Open(PawnIo.UninstallConsoleArguments);

    private static ToolOutcome Open(Func<string, string> arguments) => Open(
        Environment.IsPrivilegedProcess,
        Winget.Locate(new WinRtPackageInventory(), new LocalFileSystemReader()),
        arguments,
        VisibleConsole.Run);

    internal static ToolOutcome Open(bool elevated, string? winget, Func<string, string> arguments, Func<string, bool> run)
    {
        if (elevated)
        {
            return new ToolOutcome(false, T("MAUS a été lancé en administrateur : fermez-le et rouvrez-le normalement pour cette action, afin que les installateurs n'héritent pas de ces droits."));
        }

        if (winget is null)
        {
            return new ToolOutcome(false, T("winget est absent : réinstallez « Programme d'installation d'application » depuis le Microsoft Store."));
        }

        return run(arguments(winget)) ? new ToolOutcome(true) : new ToolOutcome(false, T("La fenêtre de commande n'a pas pu s'ouvrir."));
    }
}
