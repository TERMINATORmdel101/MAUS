using System.Runtime.InteropServices;

namespace Maus.Core.Platform;

/// <summary>Écriture des paramètres système de la session (<c>SystemParametersInfoW</c> en mode SET), réservée aux corrections.</summary>
public interface ISystemParametersWriter
{
    /// <summary>
    /// Écrit un booléen SPI_SET* dans le profil et prévient les applications (<c>SPIF_UPDATEINIFILE | SPIF_SENDCHANGE</c>).
    /// <paramref name="useUiParam"/> : la valeur passe par <c>uiParam</c> (glisser les fenêtres, lissage des polices) au lieu de <c>pvParam</c>.
    /// </summary>
    /// <returns>Faux si Windows refuse l'appel.</returns>
    bool SetBool(uint setAction, bool value, bool useUiParam);

    /// <summary>Animation de réduction et d'agrandissement des fenêtres (<c>SPI_SETANIMATION</c>).</summary>
    bool SetMinimizeAnimation(bool value);
}

public static class SpiSet
{
    public const uint DragFullWindows = 0x0025;
    public const uint FontSmoothing = 0x004B;
    public const uint Animation = 0x0049;
    public const uint ComboBoxAnimation = 0x1005;
    public const uint ListBoxSmoothScrolling = 0x1007;
    public const uint MenuAnimation = 0x1003;
    public const uint SelectionFade = 0x1015;
    public const uint TooltipAnimation = 0x1017;
    public const uint CursorShadow = 0x101B;
    public const uint DropShadow = 0x1025;
    public const uint ClientAreaAnimation = 0x1043;
}

public sealed partial class Win32SystemParametersWriter : ISystemParametersWriter
{
    private const uint UpdateIniFile = 0x01;
    private const uint SendChange = 0x02;

    public bool SetBool(uint setAction, bool value, bool useUiParam)
    {
        var flag = value ? 1u : 0u;
        return useUiParam
            ? SystemParametersInfoW(setAction, flag, 0, UpdateIniFile | SendChange)
            : SystemParametersInfoW(setAction, 0, (nint)flag, UpdateIniFile | SendChange);
    }

    public bool SetMinimizeAnimation(bool value)
    {
        var info = new AnimationInfo { Size = (uint)Marshal.SizeOf<AnimationInfo>(), MinAnimate = value ? 1 : 0 };
        return SystemParametersInfoW(SpiSet.Animation, info.Size, ref info, UpdateIniFile | SendChange);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AnimationInfo
    {
        public uint Size;
        public int MinAnimate;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoW(uint action, uint param, nint value, uint winIni);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoW(uint action, uint param, ref AnimationInfo value, uint winIni);
}
