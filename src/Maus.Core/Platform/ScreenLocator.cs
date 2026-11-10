using System.Runtime.InteropServices;

namespace Maus.Core.Platform;

/// <summary>Un point de l'écran virtuel tombe-t-il sur un écran réel ? Le rectangle qui englobe tous les écrans a des trous.</summary>
public static partial class ScreenLocator
{
    private const int MonitorDefaultToNull = 0;

    /// <param name="x">Abscisse, en pixels réels.</param>
    /// <param name="y">Ordonnée, en pixels réels.</param>
    public static bool IsOnAScreen(int x, int y) => MonitorFromPoint(new Point(x, y), MonitorDefaultToNull) != nint.Zero;

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(Point point, int flags);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Point(int X, int Y);
}
