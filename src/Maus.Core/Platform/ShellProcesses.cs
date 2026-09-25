using System.Diagnostics;
using Microsoft.Win32;

namespace Maus.Core.Platform;

/// <summary>Processus de l'Explorateur (barre des tâches, Bureau) de la session courante.</summary>
public interface IShellProcesses
{
    /// <summary>Windows relance-t-il seul l'Explorateur quand il s'arrête (<c>AutoRestartShell</c>, activé par défaut) ?</summary>
    bool AutoRestartEnabled();

    int Count();

    /// <summary>Arrête l'Explorateur de cette session ; Windows le relance lui-même, avec les droits de l'utilisateur.</summary>
    void StopShell();
}

public sealed class WindowsShellProcesses(IRegistryReader registry) : IShellProcesses
{
    public bool AutoRestartEnabled()
    {
        try
        {
            return registry.GetDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "AutoRestartShell") != 0;
        }
        catch (MausAccessDeniedException)
        {
            return true;
        }
    }

    public int Count()
    {
        var processes = SessionExplorers();
        try
        {
            return processes.Count(p => !p.HasExited);
        }
        finally
        {
            processes.ForEach(p => p.Dispose());
        }
    }

    public void StopShell()
    {
        var processes = SessionExplorers();
        try
        {
            foreach (var process in processes)
            {
                // MAUS ne relance jamais l'Explorateur lui-même : il reviendrait avec les droits administrateur.
                process.Kill();
                process.WaitForExit(5000);
            }
        }
        finally
        {
            processes.ForEach(p => p.Dispose());
        }
    }

    private static List<Process> SessionExplorers()
    {
        var session = Process.GetCurrentProcess().SessionId;
        var all = Process.GetProcessesByName("explorer");
        var mine = new List<Process>();
        foreach (var process in all)
        {
            if (process.SessionId == session)
            {
                mine.Add(process);
            }
            else
            {
                process.Dispose();
            }
        }

        return mine;
    }
}
