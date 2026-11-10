using System.Runtime.InteropServices;

namespace Maus.Core.Platform;

/// <summary>Mode de gestion de l'alimentation actif (API documentées <c>PowerGetActiveScheme</c> et <c>PowerSetActiveScheme</c>).</summary>
public interface IPowerSchemeAccessor
{
    Guid? GetActiveScheme();

    /// <returns>Faux si Windows refuse (mode absent de ce PC, droits insuffisants).</returns>
    bool SetActiveScheme(Guid scheme);
}

public sealed partial class Win32PowerSchemeAccessor : IPowerSchemeAccessor
{
    public Guid? GetActiveScheme() => PowerSchemeNative.ReadActive();

    public bool SetActiveScheme(Guid scheme) => PowerSetActiveScheme(0, in scheme) == 0;

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSetActiveScheme(nint userRootPowerKey, in Guid schemeGuid);
}

/// <summary>Lecture du mode actif, commune au module d'audit et au moteur de corrections. L'écriture reste dans <see cref="Win32PowerSchemeAccessor"/>.</summary>
internal static partial class PowerSchemeNative
{
    public static Guid? ReadActive()
    {
        if (PowerGetActiveScheme(0, out var pointer) != 0 || pointer == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
