using System.Runtime.InteropServices;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Engine.Broker;

/// <summary>
/// Confirmation affichée par le moteur administrateur lui-même (0.8.4, rapport de sécurité du 29/10/2026, point 5) pour les
/// actions les plus sensibles : fermer un programme, toucher au pilote graphique, installer ou retirer PawnIO, marquer un
/// constat « voulu ». L'interface tourne sans droits : un programme malveillant du même compte pourrait s'y introduire et
/// envoyer des demandes signées. Il ne peut pas, en revanche, cliquer dans une fenêtre d'un processus administrateur :
/// Windows l'en empêche (isolation des privilèges de l'interface, UIPI, Microsoft Learn « Windows Integrity Mechanism Design »).
/// </summary>
public interface IEngineConfirmation
{
    /// <summary>Vrai seulement si l'utilisateur répond « Oui » (« Non » est le choix par défaut).</summary>
    bool Confirm(string title, string message);
}

/// <summary>Boîte de dialogue de Windows (<c>MessageBoxW</c>) affichée par le processus du moteur, au premier plan.</summary>
public sealed partial class WindowsEngineConfirmation : IEngineConfirmation
{
    private const uint YesNo = 0x4;
    private const uint IconWarning = 0x30;
    private const uint DefaultSecondButton = 0x100;
    private const uint SetForeground = 0x10000;
    private const uint TopMost = 0x40000;
    private const int Yes = 6;

    public bool Confirm(string title, string message)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);
        var text = message + Environment.NewLine + Environment.NewLine
            + T("Cette confirmation vient du moteur administrateur de MAUS : aucun autre programme ne peut y répondre à votre place. Si vous n'avez rien demandé, répondez Non.");
        try
        {
            return MessageBoxW(0, text, T("MAUS (administrateur) : {0}", title), YesNo | IconWarning | DefaultSecondButton | SetForeground | TopMost) == Yes;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint owner, string text, string caption, uint type);
}

/// <summary>Côté interface : autorise le moteur à passer au premier plan pour afficher sa confirmation.</summary>
public static partial class EngineForeground
{
    /// <summary><c>AllowSetForegroundWindow</c> (Microsoft Learn) : sans effet si l'interface n'est pas elle-même au premier plan.</summary>
    public static void Allow(int processId)
    {
        try
        {
            _ = AllowSetForegroundWindow((uint)processId);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Hors de Windows : rien à faire.
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);
}
