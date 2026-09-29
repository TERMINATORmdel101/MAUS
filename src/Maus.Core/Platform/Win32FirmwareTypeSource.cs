using System.Runtime.InteropServices;
using Maus.Core.Workshop;

namespace Maus.Core.Platform;

/// <summary>
/// Mode de démarrage (BIOS hérité ou UEFI) par <c>GetFirmwareType</c> de kernel32 (Windows 8 et suivants),
/// fonction documentée de lecture seule.
/// </summary>
public sealed partial class Win32FirmwareTypeSource : IFirmwareTypeSource
{
    public int? Read() => GetFirmwareType(out var type) ? type : null;

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFirmwareType(out int firmwareType);
}
