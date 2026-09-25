using System.Runtime.InteropServices;

namespace Maus.Core.Platform;

/// <summary>Prévient Windows et l'Explorateur qu'un réglage a changé dans le registre (<c>WM_SETTINGCHANGE</c>).</summary>
public interface ISettingChangeNotifier
{
    void Broadcast();
}

public sealed partial class Win32SettingChangeNotifier : ISettingChangeNotifier
{
    private const nint HwndBroadcast = 0xFFFF;
    private const uint WmSettingChange = 0x001A;
    private const uint AbortIfHung = 0x0002;

    /// <summary>Zones annoncées : général, stratégies, thème (transparence) et barre des tâches.</summary>
    private static readonly string?[] Areas = [null, "Policy", "ImmersiveColorSet", "TraySettings"];

    public void Broadcast()
    {
        foreach (var area in Areas)
        {
            var text = area is null ? 0 : Marshal.StringToHGlobalUni(area);
            try
            {
                _ = SendMessageTimeoutW(HwndBroadcast, WmSettingChange, 0, text, AbortIfHung, 2000, out _);
            }
            finally
            {
                if (text != 0)
                {
                    Marshal.FreeHGlobal(text);
                }
            }
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SendMessageTimeoutW(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
}
