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
        var others = OtherInterfaces();
        foreach (var process in others)
        {
            process.Dispose();
        }

        return others.Count;
    }

    /// <summary>Ramène devant la fenêtre de l'autre MAUS ; <c>false</c> s'il n'en a aucune (MAUS bloqué, resté sans fenêtre).</summary>
    public static bool BringExistingToFront()
    {
        var found = false;
        foreach (var process in OtherInterfaces())
        {
            using (process)
            {
                var window = MainWindowOf(process);
                if (found || window == 0)
                {
                    continue;
                }

                if (IsIconic(window))
                {
                    ShowWindow(window, RestoreCommand);
                }

                SetForegroundWindow(window);
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// Ferme les autres MAUS restés sans fenêtre (à la demande de l'utilisateur) et attend leur fin, 10 secondes au plus.
    /// Un MAUS qui a une fenêtre n'est jamais fermé ici, ni l'outil en ligne de commande « maus » (une correction ou une
    /// annulation peut y être en cours, dans une console qui n'appartient pas à son processus).
    /// </summary>
    /// <returns>Le nombre de MAUS fermés.</returns>
    public static int CloseWindowlessOthers()
    {
        var closed = 0;
        foreach (var process in OtherInterfaces())
        {
            using (process)
            {
                if (MainWindowOf(process) != 0)
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

    /// <summary>
    /// Vrai si le processus <paramref name="id"/> / <paramref name="name"/> est un autre MAUS avec interface. Windows ne
    /// distingue pas les majuscules dans les noms de programmes : chercher « MAUS » renvoie aussi « maus », l'outil en ligne
    /// de commande, que seule une comparaison exacte écarte.
    /// </summary>
    internal static bool IsOtherInterface(int id, string name, int currentId, string currentName) =>
        id != currentId && string.Equals(name, currentName, StringComparison.Ordinal);

    /// <summary>Les autres MAUS avec interface ; à libérer par l'appelant.</summary>
    private static List<Process> OtherInterfaces()
    {
        using var current = Process.GetCurrentProcess();
        var others = new List<Process>();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            if (IsOtherInterface(process.Id, process.ProcessName, current.Id, current.ProcessName))
            {
                others.Add(process);
            }
            else
            {
                process.Dispose();
            }
        }

        return others;
    }

    /// <summary>Fenêtre principale du processus, ou 0 s'il n'en a pas (ou s'il vient de se terminer).</summary>
    private static nint MainWindowOf(Process process)
    {
        try
        {
            return process.MainWindowHandle;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
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
