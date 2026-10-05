using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Maus.Core.Modules.M11Health;

/// <summary>
/// Journal de santé SMART d'un SSD NVMe (page de journal 02h), tel que le disque le déclare lui-même. Disposition :
/// structure <c>NVME_HEALTH_INFO_LOG</c> de Microsoft Learn (nvme.h) ; les seuils (réserve, avertissements critiques)
/// sont ceux du disque, aucun n'est inventé par MAUS.
/// </summary>
/// <param name="CriticalWarning">Bits d'avertissement critique : 0 réserve sous le seuil, 1 température, 2 fiabilité dégradée,
/// 3 lecture seule, 4 sauvegarde de la mémoire volatile en panne.</param>
/// <param name="AvailableSpare">Réserve restante, en pourcentage normalisé (0 à 100).</param>
/// <param name="AvailableSpareThreshold">Seuil de réserve fixé par le fabricant.</param>
/// <param name="PercentageUsed">Estimation du fabricant de l'endurance consommée (peut dépasser 100 ; 255 au plus).</param>
/// <param name="DataUnitsWritten">Données écrites, en milliers d'unités de 512 octets.</param>
public sealed record NvmeHealthLog(
    byte CriticalWarning,
    int? TemperatureCelsius,
    int AvailableSpare,
    int AvailableSpareThreshold,
    int PercentageUsed,
    ulong DataUnitsWritten,
    ulong PowerOnHours,
    ulong UnsafeShutdowns,
    ulong MediaErrors)
{
    /// <summary>Taille du journal (Microsoft Learn, NVME_HEALTH_INFO_LOG).</summary>
    public const int Size = 512;

    public bool SpareBelowThreshold => (CriticalWarning & 0x01) != 0;

    public bool TemperatureAlert => (CriticalWarning & 0x02) != 0;

    public bool ReliabilityDegraded => (CriticalWarning & 0x04) != 0;

    public bool ReadOnly => (CriticalWarning & 0x08) != 0;

    public bool BackupDeviceFailed => (CriticalWarning & 0x10) != 0;

    /// <summary>Octets écrits : unités de 512 octets, comptées par milliers (Microsoft Learn, champ DataUnitWritten).</summary>
    public double TerabytesWritten => DataUnitsWritten * 1000d * 512 / 1e12;

    /// <summary>Décode les 512 octets du journal ; <c>null</c> s'ils sont trop courts.</summary>
    public static NvmeHealthLog? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
        {
            return null;
        }

        // Température composite en kelvins ; 0 = non fournie.
        var kelvin = BinaryPrimitives.ReadUInt16LittleEndian(data[1..]);
        return new NvmeHealthLog(
            data[0],
            kelvin == 0 ? null : kelvin - 273,
            data[3],
            data[4],
            data[5],
            Low64(data, 48),
            Low64(data, 128),
            Low64(data, 144),
            Low64(data, 160));
    }

    /// <summary>Compteurs de 16 octets : les 8 octets bas suffisent (au-delà de 2⁶⁴, la valeur est plafonnée).</summary>
    private static ulong Low64(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 8, 8)) == 0
            ? BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8))
            : ulong.MaxValue;
}

/// <summary>Lecture du journal de santé d'un disque NVMe (numéro de disque physique de Windows).</summary>
internal interface INvmeHealthReader
{
    /// <returns>Le journal, ou <c>null</c> si le disque ne le fournit pas (pilote du fabricant, adaptateur USB, RAID).</returns>
    NvmeHealthLog? Read(int diskNumber);
}

/// <summary>
/// Requête <c>IOCTL_STORAGE_QUERY_PROPERTY</c> « StorageDeviceProtocolSpecificProperty » (Microsoft Learn, « Working with
/// NVMe drives »). Le disque est ouvert avec un accès nul : aucune donnée lue ni écrite, seulement une demande
/// d'information (FILE_ANY_ACCESS), possible sans droits administrateur (vérifié sur le PC du porteur le 05/10/2026).
/// </summary>
internal sealed partial class WindowsNvmeHealthReader : INvmeHealthReader
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageDeviceProtocolSpecificProperty = 50;
    private const int ProtocolTypeNvme = 3;
    private const int NvmeDataTypeLogPage = 2;
    private const int NvmeLogPageHealthInfo = 2;
    private const int QueryHeader = 8;
    private const int ProtocolSpecificDataSize = 40;
    private const uint ShareReadWrite = 3;
    private const uint OpenExisting = 3;

    public NvmeHealthLog? Read(int diskNumber)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var handle = CreateFile(@"\\.\PhysicalDrive" + diskNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), 0, ShareReadWrite, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new byte[QueryHeader + ProtocolSpecificDataSize + NvmeHealthLog.Size];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0), StorageDeviceProtocolSpecificProperty);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), ProtocolTypeNvme);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12), NvmeDataTypeLogPage);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), NvmeLogPageHealthInfo);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(24), ProtocolSpecificDataSize);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(28), NvmeHealthLog.Size);
        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, buffer, buffer.Length, buffer, buffer.Length, out _, 0))
        {
            return null;
        }

        return FromDescriptor(buffer);
    }

    /// <summary>
    /// Réponse : STORAGE_PROTOCOL_DATA_DESCRIPTOR (version et taille = 48), puis les données à l'emplacement annoncé,
    /// compté depuis le début de STORAGE_PROTOCOL_SPECIFIC_DATA.
    /// </summary>
    internal static NvmeHealthLog? FromDescriptor(ReadOnlySpan<byte> buffer)
    {
        const int Descriptor = QueryHeader + ProtocolSpecificDataSize;
        if (buffer.Length < Descriptor
            || BinaryPrimitives.ReadInt32LittleEndian(buffer) != Descriptor
            || BinaryPrimitives.ReadInt32LittleEndian(buffer[4..]) != Descriptor)
        {
            return null;
        }

        var offset = BinaryPrimitives.ReadInt32LittleEndian(buffer[(QueryHeader + 16)..]);
        var length = BinaryPrimitives.ReadInt32LittleEndian(buffer[(QueryHeader + 20)..]);
        var start = QueryHeader + offset;
        return offset < ProtocolSpecificDataSize || length < NvmeHealthLog.Size || start + NvmeHealthLog.Size > buffer.Length
            ? null
            : NvmeHealthLog.Parse(buffer.Slice(start, NvmeHealthLog.Size));
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, nint overlapped);
}
