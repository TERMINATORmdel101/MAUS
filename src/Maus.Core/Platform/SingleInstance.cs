using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Maus.Core.Platform;

/// <summary>Ramène au premier plan la fenêtre de MAUS déjà ouverte, quand on relance MAUS.</summary>
public static partial class SingleInstance
{
    private const int RestoreCommand = 9;

    /// <summary>Autres processus « MAUS » (l'interface ; l'outil en ligne de commande s'appelle « maus » et n'en fait pas partie).</summary>
    public static int OtherInterfaceProcesses()
    {
        using var current = Process.GetCurrentProcess();
        var others = 0;
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                if (process.Id != current.Id && string.Equals(process.ProcessName, current.ProcessName, StringComparison.Ordinal))
                {
                    others++;
                }
            }
        }

        return others;
    }

    /// <summary>Ramène devant la fenêtre de l'autre MAUS ; <c>false</c> s'il n'en a aucune (MAUS bloqué, resté sans fenêtre).</summary>
    public static bool BringExistingToFront()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                if (process.Id == current.Id || process.MainWindowHandle == 0)
                {
                    continue;
                }

                if (IsIconic(process.MainWindowHandle))
                {
                    ShowWindow(process.MainWindowHandle, RestoreCommand);
                }

                SetForegroundWindow(process.MainWindowHandle);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Ferme les autres MAUS restés sans fenêtre (à la demande de l'utilisateur) et attend leur fin, 10 secondes au plus.
    /// Un MAUS qui a une fenêtre n'est jamais fermé ici.
    /// </summary>
    /// <returns>Le nombre de MAUS fermés.</returns>
    public static int CloseWindowlessOthers()
    {
        using var current = Process.GetCurrentProcess();
        var closed = 0;
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                if (process.Id == current.Id || process.MainWindowHandle != 0)
                {
                    continue;
                }

                try
                {
                    process.Kill();
                    process.WaitForExit(TimeSpan.FromSeconds(10));
                    closed++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Déjà terminé, ou refusé : l'utilisateur le fermera depuis le Gestionnaire des tâches.
                }
            }
        }

        return closed;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);
}
