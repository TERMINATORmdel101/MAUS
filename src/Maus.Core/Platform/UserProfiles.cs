using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Maus.Core.Platform;

/// <summary>Profils des comptes de ce PC, d'après la liste de Windows (une clé par SID).</summary>
public static class UserProfiles
{
    internal const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    /// <summary>Dossiers des profils de tous les comptes sauf <paramref name="ownSid"/> ; vide si la liste est illisible.</summary>
    public static IReadOnlyList<string> OfOthers(IRegistryReader registry, string? ownSid)
    {
        ArgumentNullException.ThrowIfNull(registry);
        try
        {
            return registry.GetSubKeyNames(RegistryHive.LocalMachine, ProfileListKey)
                // « S-1-5-21-…-1001.bak » : ancienne copie du même profil, créée par Windows après une erreur de chargement.
                .Where(sid => ownSid is null || !(sid.Equals(ownSid, StringComparison.OrdinalIgnoreCase) || sid.Equals(ownSid + ".bak", StringComparison.OrdinalIgnoreCase)))
                .Select(sid => registry.GetString(RegistryHive.LocalMachine, ProfileListKey + @"\" + sid, "ProfileImagePath"))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.TrimEndingDirectorySeparator(Environment.ExpandEnvironmentVariables(path!)))
                .ToList();
        }
        catch (MausAccessDeniedException)
        {
            return [];
        }
    }

    /// <summary>
    /// Les dossiers donnés et, en plus, leur chemin réel : un dossier demandé est comparé sous sa forme réelle, un profil
    /// déplacé par un lien doit l'être aussi. Un chemin réseau n'est jamais ouvert (connexion sortante).
    /// </summary>
    public static string[] WithRealPaths(IReadOnlyCollection<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        return [.. folders, .. folders.Where(folder => !FinalPath.IsRemote(folder)).Select(FinalPath.Of).OfType<string>()];
    }
}

/// <summary>Chemin réel d'un dossier, liens et noms courts résolus par Windows.</summary>
public static partial class FinalPath
{
    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;

    /// <summary>Chemin réseau ou de périphérique, sous toutes ses écritures : y toucher ouvrirait une connexion sortante.</summary>
    public static bool IsRemote(string path) =>
        path.Length >= 2 && path[0] is '\\' or '/' && path[1] is '\\' or '/';

    /// <summary>Lecteur réseau, ou chemin UNC. Un lecteur illisible n'est pas compté : rien ne prouve alors qu'il est distant.</summary>
    public static bool IsOnNetworkDrive(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (IsRemote(path))
        {
            return true;
        }

        try
        {
            return Path.GetPathRoot(path) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Le chemin réel ; <c>null</c> s'il ne peut pas être ouvert.</summary>
    public static string? Of(string path)
    {
        using var handle = CreateFileW(path, 0, ShareAll, 0, OpenExisting, BackupSemantics, 0);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new char[32768];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
        {
            return null;
        }

        var final = new string(buffer, 0, (int)length);
        return final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + final[8..]
            : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..]
            : final;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint size, uint flags);
}
