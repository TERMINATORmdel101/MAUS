using System.Runtime.InteropServices;

namespace Maus.Core.Platform;

/// <summary>Lecture des paramètres système de la session (<c>SystemParametersInfoW</c> en mode GET uniquement).</summary>
public interface ISystemParametersReader
{
    /// <summary>Valeur booléenne d'un code SPI_GET*, ou <c>null</c> si l'appel échoue.</summary>
    bool? GetBool(uint action);

    /// <summary>Animation de réduction et d'agrandissement des fenêtres (<c>SPI_GETANIMATION</c>).</summary>
    bool? GetMinimizeAnimation();
}

public static class SpiGet
{
    public const uint DragFullWindows = 0x0026;
    public const uint FontSmoothing = 0x004A;
    public const uint Animation = 0x0048;
    public const uint ComboBoxAnimation = 0x1004;
    public const uint ListBoxSmoothScrolling = 0x1006;
    public const uint MenuAnimation = 0x1002;
    public const uint SelectionFade = 0x1014;
    public const uint TooltipAnimation = 0x1016;
    public const uint CursorShadow = 0x101A;
    public const uint DropShadow = 0x1024;
    public const uint ClientAreaAnimation = 0x1042;
}

public sealed partial class Win32SystemParametersReader : ISystemParametersReader
{
    public bool? GetBool(uint action)
    {
        var value = 0;
        return SystemParametersInfoW(action, 0, ref value, 0) ? value != 0 : null;
    }

    public bool? GetMinimizeAnimation()
    {
        var info = new SpiAnimation.AnimationInfo { Size = (uint)Marshal.SizeOf<SpiAnimation.AnimationInfo>() };
        return SpiAnimation.SystemParametersInfoW(SpiGet.Animation, info.Size, ref info, 0) ? info.MinAnimate != 0 : null;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoW(uint action, uint param, ref int value, uint winIni);
}

/// <summary>Structure <c>ANIMATIONINFO</c> et déclaration de <c>SystemParametersInfoW</c> qui la prend, communes à la lecture et à l'écriture.</summary>
internal static partial class SpiAnimation
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct AnimationInfo
    {
        public uint Size;
        public int MinAnimate;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SystemParametersInfoW(uint action, uint param, ref AnimationInfo value, uint winIni);
}
