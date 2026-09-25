using System.Runtime.InteropServices;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M08Bios;

/// <summary>Lecture d'une variable du firmware UEFI (par exemple <c>dbDefault</c>). Aucune écriture n'est possible par cette interface.</summary>
internal interface IFirmwareVariableReader
{
    /// <summary>Contenu brut de la variable, ou <c>null</c> si le firmware ne la publie pas.</summary>
    /// <exception cref="MausAccessDeniedException">Privilège absent : droits administrateur requis.</exception>
    /// <exception cref="DataSourceUnavailableException">Firmware sans UEFI ou lecture impossible.</exception>
    byte[]? Read(string name, string vendorGuid);
}

internal static class FirmwareVariables
{
    /// <summary>Espace de noms EFI_GLOBAL_VARIABLE, qui contient <c>dbDefault</c>, <c>KEKDefault</c> et <c>PKDefault</c>.</summary>
    public const string GlobalVariableGuid = "{8BE4DF61-93CA-11D2-AA0D-00E098032B8C}";

    public const string DbDefault = "dbDefault";
}

/// <summary>
/// Lecture par <c>GetFirmwareEnvironmentVariableExW</c>. Le privilège SeSystemEnvironmentPrivilege est activé
/// uniquement sur une copie du jeton, propre au thread (<c>ImpersonateSelf</c>), abandonnée juste après la lecture.
/// </summary>
internal sealed unsafe partial class WindowsFirmwareVariableReader : IFirmwareVariableReader
{
    private const int SecurityImpersonation = 2;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x0002;
    private const int ErrorInvalidFunction = 1;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorEnvVarNotFound = 203;
    private const int ErrorNotAllAssigned = 1300;
    private const int ErrorPrivilegeNotHeld = 1314;

    public byte[]? Read(string name, string vendorGuid)
    {
        if (!ImpersonateSelf(SecurityImpersonation))
        {
            throw new DataSourceUnavailableException(T("Lecture de la variable {0} impossible (erreur {1}).", name, Marshal.GetLastPInvokeError()));
        }

        try
        {
            EnableSystemEnvironmentPrivilege();
            return ReadVariable(name, vendorGuid);
        }
        finally
        {
            _ = RevertToSelf();
        }
    }

    private static void EnableSystemEnvironmentPrivilege()
    {
        if (!OpenThreadToken(GetCurrentThread(), TokenAdjustPrivileges | TokenQuery, true, out var token))
        {
            throw new MausAccessDeniedException(T("Jeton de sécurité inaccessible."));
        }

        try
        {
            if (!LookupPrivilegeValueW(null, "SeSystemEnvironmentPrivilege", out var luid))
            {
                throw new DataSourceUnavailableException(T("Privilège SeSystemEnvironmentPrivilege inconnu."));
            }

            var privileges = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            var adjusted = AdjustTokenPrivileges(token, false, ref privileges, 0, 0, 0);
            if (!adjusted || Marshal.GetLastPInvokeError() == ErrorNotAllAssigned)
            {
                throw new MausAccessDeniedException(T("Privilège SeSystemEnvironmentPrivilege absent : droits administrateur requis."));
            }
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    private static byte[]? ReadVariable(string name, string vendorGuid)
    {
        var size = 16 * 1024;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = new byte[size];
            uint length;
            fixed (byte* pointer = buffer)
            {
                length = GetFirmwareEnvironmentVariableExW(name, vendorGuid, pointer, (uint)buffer.Length, out _);
            }

            if (length > 0)
            {
                return buffer[..(int)length];
            }

            switch (Marshal.GetLastPInvokeError())
            {
                case 0:
                    return [];
                case ErrorEnvVarNotFound:
                    return null;
                case ErrorInsufficientBuffer:
                    size *= 4;
                    continue;
                case ErrorPrivilegeNotHeld:
                    throw new MausAccessDeniedException(T("Lecture de la variable {0} refusée.", name));
                case ErrorInvalidFunction:
                    throw new DataSourceUnavailableException(T("Firmware sans UEFI : variables inaccessibles."));
                case var error:
                    throw new DataSourceUnavailableException(T("Lecture de la variable {0} impossible (erreur {1}).", name, error));
            }
        }

        throw new DataSourceUnavailableException(T("Variable {0} trop volumineuse.", name));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ImpersonateSelf(int impersonationLevel);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RevertToSelf();

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenThreadToken(nint thread, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool openAsSelf, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValueW(string? systemName, string name, out Luid luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(
        nint token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        nint previousState,
        nint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFirmwareEnvironmentVariableExW(string name, string guid, byte* buffer, uint size, out uint attributes);
}
