using System.Runtime.InteropServices;
using Maus.Core.Platform;
using Microsoft.Win32.SafeHandles;

namespace Maus.Core.Modules.M01Audit;

internal enum SignatureStatus
{
    Signed,
    Unsigned,
    Invalid,
    Unknown,
}

/// <summary>Vérification de la signature Authenticode d'un exécutable, sans interface ni accès réseau.</summary>
internal interface ISignatureVerifier
{
    SignatureStatus Verify(string path);
}

internal sealed record ScheduledTaskInfo(string Name, bool Enabled);

/// <summary>Lecture des tâches planifiées d'un dossier du Planificateur de tâches.</summary>
internal interface IScheduledTaskReader
{
    /// <exception cref="MausAccessDeniedException">Dossier protégé.</exception>
    /// <exception cref="DataSourceUnavailableException">Planificateur ou dossier indisponible.</exception>
    IReadOnlyList<ScheduledTaskInfo> GetTasks(string folderPath);
}

/// <summary>Appel de <c>WinVerifyTrust</c> (stratégie Authenticode) en mode silencieux, sans contrôle de révocation en ligne.</summary>
internal sealed partial class WinTrustSignatureVerifier : ISignatureVerifier
{
    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint ChoiceFile = 1;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;
    private const uint RevocationCheckNone = 0x10;
    private const uint CacheOnlyUrlRetrieval = 0x1000;
    private const int TrustNoSignature = unchecked((int)0x800B0100);
    private const int TrustBadDigest = unchecked((int)0x80096010);
    private const int TrustCertSignature = unchecked((int)0x80096004);
    private const int TrustExplicitDistrust = unchecked((int)0x800B0111);
    private const int CertUntrustedRoot = unchecked((int)0x800B0109);
    private const int CertRevoked = unchecked((int)0x800B010C);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    /// <summary>Sous-système des catalogues du système d'exploitation (DRIVER_ACTION_VERIFY).</summary>
    private static readonly Guid DriverActionVerify = new("F750E6C3-38EE-11d1-85E5-00C04FC295EE");

    public SignatureStatus Verify(string path)
    {
        var pathPointer = Marshal.StringToHGlobalUni(path);
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = pathPointer,
            };
            Marshal.StructureToPtr(fileInfo, filePointer, fDeleteOld: false);

            var data = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = UiNone,
                RevocationChecks = RevokeNone,
                UnionChoice = ChoiceFile,
                File = filePointer,
                StateAction = StateActionVerify,
                ProviderFlags = RevocationCheckNone | CacheOnlyUrlRetrieval,
            };
            var action = GenericVerifyV2;
            var result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            data.StateAction = StateActionClose;
            _ = WinVerifyTrust(new IntPtr(-1), ref action, ref data);

            return result switch
            {
                0 => SignatureStatus.Signed,

                // Les programmes de Windows sont signés par catalogue, sans signature intégrée au fichier.
                TrustNoSignature => IsCatalogSigned(path) switch
                {
                    true => SignatureStatus.Signed,
                    false => SignatureStatus.Unsigned,
                    null => SignatureStatus.Unknown,
                },
                TrustBadDigest or TrustExplicitDistrust or CertUntrustedRoot or TrustCertSignature or CertRevoked => SignatureStatus.Invalid,

                // Certificat expiré, fichier illisible ou format inconnu : on ne conclut pas.
                _ => SignatureStatus.Unknown,
            };
        }
        finally
        {
            Marshal.FreeHGlobal(filePointer);
            Marshal.FreeHGlobal(pathPointer);
        }
    }

    /// <summary>
    /// Cherche l'empreinte du fichier dans les catalogues de signatures installés (SHA-256 puis SHA-1).
    /// <c>null</c> si le fichier ne peut pas être ouvert en lecture.
    /// </summary>
    private static unsafe bool? IsCatalogSigned(string path)
    {
        const int maxHashSize = 64;
        byte* hash = stackalloc byte[maxHashSize];
        foreach (var algorithm in new[] { "SHA256", null })
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var subsystem = DriverActionVerify;
                if (!CryptCATAdminAcquireContext2(out var admin, ref subsystem, algorithm, IntPtr.Zero, 0))
                {
                    continue;
                }

                try
                {
                    uint size = 0;
                    _ = CryptCATAdminCalcHashFromFileHandle2(admin, stream.SafeFileHandle, ref size, null, 0);
                    if (size is 0 or > maxHashSize)
                    {
                        continue;
                    }

                    if (!CryptCATAdminCalcHashFromFileHandle2(admin, stream.SafeFileHandle, ref size, hash, 0))
                    {
                        continue;
                    }

                    var catalog = CryptCATAdminEnumCatalogFromHash(admin, hash, size, 0, IntPtr.Zero);
                    if (catalog != IntPtr.Zero)
                    {
                        _ = CryptCATAdminReleaseCatalogContext(admin, catalog, 0);
                        return true;
                    }
                }
                finally
                {
                    _ = CryptCATAdminReleaseContext(admin, 0);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return false;
    }

    [LibraryImport("wintrust.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminAcquireContext2(out IntPtr admin, ref Guid subsystem, string? hashAlgorithm, IntPtr strongHashPolicy, uint flags);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CryptCATAdminCalcHashFromFileHandle2(IntPtr admin, SafeFileHandle file, ref uint hashSize, byte* hash, uint flags);

    [LibraryImport("wintrust.dll")]
    private static unsafe partial IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr admin, byte* hash, uint hashSize, uint flags, IntPtr previousCatalog);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminReleaseCatalogContext(IntPtr admin, IntPtr catalog, uint flags);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminReleaseContext(IntPtr admin, uint flags);

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}

/// <summary>Lecture par l'objet COM <c>Schedule.Service</c> (méthodes de lecture uniquement : Connect, GetFolder, GetTasks).</summary>
internal sealed class ComScheduledTaskReader : IScheduledTaskReader
{
    private const int TaskEnumHidden = 1;

    public IReadOnlyList<ScheduledTaskInfo> GetTasks(string folderPath)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new DataSourceUnavailableException("Planificateur de tâches indisponible.");
        object? service = null;
        try
        {
            service = Activator.CreateInstance(type)
                ?? throw new DataSourceUnavailableException("Planificateur de tâches indisponible.");
            dynamic scheduler = service;
            scheduler.Connect();
            dynamic folder = scheduler.GetFolder(folderPath);
            dynamic tasks = folder.GetTasks(TaskEnumHidden);
            int count = tasks.Count;
            var result = new List<ScheduledTaskInfo>(count);
            for (var i = 1; i <= count; i++)
            {
                dynamic task = tasks[i];
                result.Add(new ScheduledTaskInfo((string)task.Name, (bool)task.Enabled));
            }

            return result;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new MausAccessDeniedException("Lecture des tâches planifiées refusée.", ex);
        }
        catch (FileNotFoundException ex)
        {
            throw new DataSourceUnavailableException("Dossier de tâches planifiées absent.", ex);
        }
        catch (COMException ex)
        {
            throw new DataSourceUnavailableException("Planificateur de tâches illisible.", ex);
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex)
        {
            throw new DataSourceUnavailableException("Planificateur de tâches illisible.", ex);
        }
        finally
        {
            if (service is not null && Marshal.IsComObject(service))
            {
                Marshal.FinalReleaseComObject(service);
            }
        }
    }
}
