using System.Runtime.InteropServices;

namespace Maus.Core.Platform;

/// <summary>
/// Détecte une élévation avec un autre compte administrateur : MAUS écrirait alors les réglages HKCU et SPI
/// dans le profil de ce compte, pas dans celui de la personne assise devant l'écran.
/// </summary>
public static partial class SessionUser
{
    private const int UserName = 5;
    private const int DomainName = 7;
    private const uint CurrentSession = uint.MaxValue;

    /// <summary>Vrai si le compte du processus diffère de l'utilisateur de la session ; faux si c'est le même ou si c'est illisible.</summary>
    public static bool IsElevatedAsAnotherUser()
    {
        var sessionUser = Query(UserName);
        var sessionDomain = Query(DomainName);
        if (string.IsNullOrEmpty(sessionUser))
        {
            return false;
        }

        return !string.Equals(sessionUser, Environment.UserName, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(sessionDomain) && !string.Equals(sessionDomain, Environment.UserDomainName, StringComparison.OrdinalIgnoreCase));
    }

    private static string? Query(int infoClass)
    {
        if (!WTSQuerySessionInformationW(0, CurrentSession, infoClass, out var buffer, out _) || buffer == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformationW(nint server, uint sessionId, int infoClass, out nint buffer, out uint bytes);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(nint memory);
}
