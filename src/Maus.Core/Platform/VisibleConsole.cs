using System.Diagnostics;

namespace Maus.Core.Platform;

/// <summary>
/// Fenêtre de commande visible (cmd.exe de System32), avec les droits du processus qui l'ouvre : DISM, SFC, pnputil et
/// winget s'y déroulent sous les yeux de l'utilisateur. Les arguments sont toujours construits par MAUS lui-même.
/// </summary>
public static class VisibleConsole
{
    /// <summary>Renvoie faux si la fenêtre n'a pas pu s'ouvrir.</summary>
    public static bool Run(string arguments)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(Path.Combine(system, "cmd.exe"), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = system,
            });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}
