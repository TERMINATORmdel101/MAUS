using System.Runtime.InteropServices;

namespace Maus.Core.Platform;

/// <summary>
/// Privilèges du jeton du processus, activés le temps d'une opération puis désactivés (0.8.6). Un administrateur élevé
/// possède SeTakeOwnershipPrivilege et SeRestorePrivilege, mais désactivés : sans eux, un dossier créé par un autre compte
/// avec des droits hostiles ne peut pas être repris (<see cref="Fixes.WindowsDirectoryProtector"/>). Sans droits
/// administrateur, rien n'est activé et l'opération qui suit échoue comme avant : jamais d'exception ici.
/// </summary>
internal static partial class TokenPrivileges
{
    /// <summary>Résultat d'une demande sur un privilège du jeton.</summary>
    internal enum Adjustment
    {
        Done,
        UnknownPrivilege,
        NotGranted,
    }

    public const string TakeOwnership = "SeTakeOwnershipPrivilege";

    public const string Restore = "SeRestorePrivilege";

    /// <summary>Depuis Windows 11 24H2, nécessaire pour que Windows donne les adresses des pilotes chargés.</summary>
    public const string Debug = "SeDebugPrivilege";

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    internal const uint SePrivilegeEnabled = 0x0002;
    private const int ErrorNotAllAssigned = 1300;

    /// <summary>Active ce qu'il peut ; <see cref="Scope.Enabled"/> dit quoi. À libérer par <c>using</c> : tout est désactivé.</summary>
    public static Scope Enable(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var scope = new Scope();
        if (!OperatingSystem.IsWindows() || !OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            return scope;
        }

        scope.Token = token;
        foreach (var name in names)
        {
            if (Adjust(token, name, SePrivilegeEnabled) == Adjustment.Done)
            {
                scope.EnabledList.Add(name);
            }
        }

        return scope;
    }

    /// <summary>Change un privilège sur ce jeton, celui du processus ou la copie propre à un fil.</summary>
    internal static Adjustment Adjust(nint token, string name, uint attributes)
    {
        if (!LookupPrivilegeValueW(null, name, out var luid))
        {
            return Adjustment.UnknownPrivilege;
        }

        var state = new PrivilegeState { PrivilegeCount = 1, Luid = luid, Attributes = attributes };
        // AdjustTokenPrivileges réussit même quand le privilège n'est pas dans le jeton : c'est le dernier code d'erreur qui le dit.
        return AdjustTokenPrivileges(token, false, ref state, 0, 0, 0) && Marshal.GetLastPInvokeError() != ErrorNotAllAssigned ? Adjustment.Done : Adjustment.NotGranted;
    }

    public sealed class Scope : IDisposable
    {
        internal nint Token { get; set; }

        internal List<string> EnabledList { get; } = [];

        /// <summary>Privilèges effectivement activés (vide sans droits administrateur).</summary>
        public IReadOnlyList<string> Enabled => EnabledList;

        public void Dispose()
        {
            if (Token == 0)
            {
                return;
            }

            foreach (var name in EnabledList)
            {
                _ = Adjust(Token, name, 0);
            }

            _ = CloseHandle(Token);
            Token = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrivilegeState
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValueW(string? systemName, string name, out Luid luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(
        nint token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref PrivilegeState newState,
        uint bufferLength,
        nint previousState,
        nint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);
}
