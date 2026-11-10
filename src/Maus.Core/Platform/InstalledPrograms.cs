using Microsoft.Win32;

namespace Maus.Core.Platform;

/// <summary>Programme inscrit dans la liste « Désinstaller » de Windows.</summary>
public sealed record InstalledProgram(string Name, string? Version, string? Publisher);

/// <summary>Liste « Désinstaller » de Windows : HKLM 64 bits, HKLM 32 bits et HKCU.</summary>
public static class InstalledPrograms
{
    public const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string Wow64UninstallPath = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly (RegistryHive Hive, string Path)[] Sources =
    [
        (RegistryHive.LocalMachine, UninstallPath),
        (RegistryHive.LocalMachine, Wow64UninstallPath),
        (RegistryHive.CurrentUser, UninstallPath),
    ];

    /// <summary>
    /// Les programmes qui portent un nom, dans l'ordre des sources. Une entrée illisible est ignorée : les autres restent utiles.
    /// <paramref name="readable"/> est faux si les trois sources ont été refusées.
    /// </summary>
    public static IReadOnlyList<InstalledProgram> Read(IRegistryReader registry, out bool readable)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var programs = new List<InstalledProgram>();
        var denied = 0;
        foreach (var (hive, path) in Sources)
        {
            IReadOnlyList<string> keys;
            try
            {
                keys = registry.GetSubKeyNames(hive, path);
            }
            catch (MausAccessDeniedException)
            {
                denied++;
                continue;
            }

            foreach (var key in keys)
            {
                var keyPath = $@"{path}\{key}";
                try
                {
                    if (registry.GetString(hive, keyPath, "DisplayName") is { Length: > 0 } name)
                    {
                        programs.Add(new InstalledProgram(name, registry.GetString(hive, keyPath, "DisplayVersion"), registry.GetString(hive, keyPath, "Publisher")));
                    }
                }
                catch (MausAccessDeniedException)
                {
                    // Une clé illisible n'empêche pas de lire les autres.
                }
            }
        }

        readable = denied < Sources.Length;
        return programs;
    }
}
