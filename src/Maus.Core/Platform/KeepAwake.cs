using System.Runtime.InteropServices;

namespace Maus.Core.Platform;

/// <summary>
/// Empêche la mise en veille de Windows pendant un test (sinon un test de plusieurs heures s'arrêterait avec la veille
/// programmée). <c>SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)</c> : l'état reste en vigueur jusqu'au prochain
/// appel avec <c>ES_CONTINUOUS</c> (documentation Microsoft). L'écran, lui, peut s'éteindre.
/// </summary>
public static partial class KeepAwake
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    /// <summary>Demande à Windows de rester éveillé ; <c>false</c> si l'appel a échoué.</summary>
    public static bool Begin() => OperatingSystem.IsWindows() && SetThreadExecutionState(EsContinuous | EsSystemRequired) != 0;

    /// <summary>Rend la main à Windows : la veille programmée reprend.</summary>
    public static void End()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = SetThreadExecutionState(EsContinuous);
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);
}
