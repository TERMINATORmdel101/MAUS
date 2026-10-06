using System.Runtime.InteropServices;

namespace Maus.Core.Platform;

/// <summary>
/// Fréquence de rafraîchissement de l'écran principal, en lecture seule : <c>EnumDisplaySettingsW</c> avec
/// ENUM_CURRENT_SETTINGS, champ <c>dmDisplayFrequency</c> de DEVMODEW (Microsoft Learn, « EnumDisplaySettingsW » et
/// « DEVMODEW »). Les valeurs 0 et 1 signifient « valeur par défaut du matériel » : la fréquence est alors inconnue.
/// </summary>
public static partial class DisplayRefresh
{
    private const int EnumCurrentSettings = -1;
    private const int DevModeSize = 220;
    private const int SizeOffset = 68;
    private const int FrequencyOffset = 184;

    public static int? PrimaryHz()
    {
        var mode = new byte[DevModeSize];
        BitConverter.TryWriteBytes(mode.AsSpan(SizeOffset), (ushort)DevModeSize);
        try
        {
            if (!EnumDisplaySettings(null, EnumCurrentSettings, mode))
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }

        var hz = BitConverter.ToInt32(mode, FrequencyOffset);
        return hz > 1 ? hz : null;
    }

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplaySettings(string? device, int mode, [In, Out] byte[] devMode);
}
