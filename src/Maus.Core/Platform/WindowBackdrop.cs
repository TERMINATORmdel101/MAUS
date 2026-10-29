using System.Runtime.InteropServices;

namespace Maus.Core.Platform;

/// <summary>
/// Fond « Mica » de Windows 11 derrière une fenêtre de MAUS (0.8.4, demande du porteur « plus joli »), par
/// <c>DwmSetWindowAttribute</c> (Microsoft Learn, dwmapi.h) : <c>DWMWA_SYSTEMBACKDROP_TYPE</c> (38, Windows 11 build 22621
/// et plus) avec <c>DWMSBT_MAINWINDOW</c> (2, Mica) ou <c>DWMSBT_NONE</c> (1), et <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c> (20)
/// pour la barre de titre en thème sombre. <c>DwmExtendFrameIntoClientArea</c> (marges −1, « sheet of glass ») laisse le
/// fond passer sous le contenu. Seulement sur la fenêtre de MAUS elle-même ; aucun réglage de Windows n'est changé.
/// </summary>
public static partial class WindowBackdrop
{
    /// <summary>Premier build de Windows 11 qui accepte <c>DWMWA_SYSTEMBACKDROP_TYPE</c> (22H2, Microsoft Learn).</summary>
    public const int MinimumBuild = 22621;

    private const int SystemBackdropType = 38;
    private const int UseImmersiveDarkMode = 20;
    private const int BackdropNone = 1;
    private const int BackdropMainWindow = 2;

    /// <summary>Vrai si ce Windows accepte le fond Mica (build 22621 ou plus).</summary>
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumBuild);

    /// <summary>Pose (ou retire) le fond Mica ; faux si Windows refuse ou ne le connaît pas.</summary>
    public static bool Apply(nint window, bool mica, bool dark)
    {
        if (window == 0 || !IsSupported)
        {
            return false;
        }

        try
        {
            var darkValue = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(window, UseImmersiveDarkMode, ref darkValue, sizeof(int));
            var margins = mica ? new Margins(-1) : new Margins(0);
            _ = DwmExtendFrameIntoClientArea(window, ref margins);
            var backdrop = mica ? BackdropMainWindow : BackdropNone;
            return DwmSetWindowAttribute(window, SystemBackdropType, ref backdrop, sizeof(int)) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins(int all)
    {
        public int Left = all;
        public int Right = all;
        public int Top = all;
        public int Bottom = all;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmExtendFrameIntoClientArea(nint window, ref Margins margins);
}
