using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Maus.Core.Platform;

/// <summary>Ramène au premier plan la fenêtre de MAUS déjà ouverte, quand on relance MAUS.</summary>
public static partial class SingleInstance
{
    private const int RestoreCommand = 9;

    public static void BringExistingToFront()
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
                return;
            }
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
