using System.Runtime.InteropServices;

namespace Maus.Core.Platform;

/// <summary><c>DEVMODEW</c> (220 octets), seuls les champs d'affichage sont nommés (Microsoft Learn, « DEVMODEW »).</summary>
[StructLayout(LayoutKind.Explicit, Size = NativeSize)]
internal struct DevModeW
{
    public const int NativeSize = 220;

    [FieldOffset(68)]
    public ushort Size;

    [FieldOffset(72)]
    public uint Fields;

    [FieldOffset(172)]
    public uint PelsWidth;

    [FieldOffset(176)]
    public uint PelsHeight;

    [FieldOffset(180)]
    public uint DisplayFlags;

    [FieldOffset(184)]
    public uint DisplayFrequency;
}

/// <summary>
/// Fréquence d'actualisation de l'écran principal, en lecture seule : <c>EnumDisplaySettingsExW</c> avec
/// ENUM_CURRENT_SETTINGS, champ <c>dmDisplayFrequency</c> de DEVMODEW (Microsoft Learn, « EnumDisplaySettingsExW » et
/// « DEVMODEW »). Les valeurs 0 et 1 signifient « valeur par défaut du matériel » : la fréquence est alors inconnue.
/// La seule déclaration de cette fonction de lecture dans MAUS est ici.
/// </summary>
public static partial class DisplayRefresh
{
    internal const uint EnumCurrentSettings = unchecked((uint)-1);

    public static int? PrimaryHz()
    {
        var mode = new DevModeW { Size = DevModeW.NativeSize };
        try
        {
            if (EnumDisplaySettingsExW(null, EnumCurrentSettings, ref mode, 0) == 0)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }

        return mode.DisplayFrequency > 1 ? (int)mode.DisplayFrequency : null;
    }

    /// <param name="deviceName">Source de l'écran (<c>\\.\DISPLAY1</c>), ou <c>null</c> pour l'écran principal.</param>
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int EnumDisplaySettingsExW(string? deviceName, uint modeNum, ref DevModeW devMode, uint flags);
}
