using System.Runtime.InteropServices;

namespace Maus.Core.Platform;

/// <summary>
/// Réglages Windows de la fenêtre de superposition (compteur d'images par seconde au-dessus du jeu). MAUS ne s'injecte
/// jamais dans le jeu (les anti-triche le détectent) : c'est une fenêtre ordinaire, transparente aux clics et au premier
/// plan. Elle s'affiche en fenêtré, en plein écran fenêtré, et en plein écran exclusif quand Windows le transforme en
/// plein écran fenêtré (« optimisations du plein écran », DirectX Developer Blog, « Demystifying Fullscreen Optimizations »).
/// Styles étendus et SetWindowPos : Microsoft Learn (« Extended Window Styles », « SetWindowPos »).
/// </summary>
public static partial class OverlayWindowStyle
{
    private const int ExStyleIndex = -20;
    private const int Layered = 0x00080000;
    private const int Transparent = 0x00000020;
    private const int ToolWindow = 0x00000080;
    private const int NoActivate = 0x08000000;
    private const nint TopMost = -1;
    private const uint NoSize = 0x0001;
    private const uint NoMove = 0x0002;
    private const uint NoActivateFlag = 0x0010;

    /// <summary>Les clics traversent la fenêtre, elle ne prend jamais le focus du jeu et n'apparaît pas dans Alt+Tab.</summary>
    /// <returns>Faux si Windows a refusé (la fenêtre reste alors une fenêtre ordinaire au premier plan).</returns>
    public static bool MakeClickThrough(nint window)
    {
        var style = GetWindowLong(window, ExStyleIndex);
        return SetWindowLong(window, ExStyleIndex, style | Layered | Transparent | ToolWindow | NoActivate) != 0 || style == 0;
    }

    /// <summary>Remet la fenêtre au premier plan sans l'activer (un jeu qui passe en plein écran peut la recouvrir).</summary>
    public static void KeepOnTop(nint window) =>
        SetWindowPos(window, TopMost, 0, 0, 0, 0, NoMove | NoSize | NoActivateFlag);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(nint window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static partial int SetWindowLong(nint window, int index, int value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
