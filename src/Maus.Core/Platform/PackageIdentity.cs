using System.Runtime.InteropServices;

namespace Maus.Core.Platform;

/// <summary>MAUS tourne-t-il depuis son paquet MSIX (Microsoft Store) ?</summary>
public static partial class PackageIdentity
{
    /// <summary>Tampon trop petit : le processus a bien une identité de paquet (ERROR_INSUFFICIENT_BUFFER).</summary>
    private const int InsufficientBuffer = 122;

    /// <summary>Vrai si le processus a une identité de paquet (installé depuis le Store ou un paquet MSIX).</summary>
    public static bool IsPackaged()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var length = 0;
            return GetCurrentPackageFullName(ref length, null) == InsufficientBuffer;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetCurrentPackageFullName(ref int length, [Out] char[]? name);
}
