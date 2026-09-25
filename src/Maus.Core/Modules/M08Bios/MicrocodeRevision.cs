using System.Buffers.Binary;

namespace Maus.Core.Modules.M08Bios;

/// <summary>
/// Décodage de <c>Update Revision</c> (REG_BINARY) sous <c>HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0</c>.
/// Windows 11 récent stocke 4 octets little-endian (<c>F0 00 00 00</c> = 0xF0, <c>2F 01 00 00</c> = 0x12F).
/// Les versions plus anciennes stockent 8 octets : la copie du registre MSR 0x8B, dont la révision occupe
/// le mot de 32 bits supérieur (<c>00 00 00 00 1E 00 00 00</c> = 0x1E).
/// </summary>
internal static class MicrocodeRevision
{
    public static uint? Parse(byte[]? value)
    {
        if (value is null || value.Length < 4)
        {
            return null;
        }

        if (value.Length >= 8)
        {
            var high = BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(4, 4));
            var low = BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(0, 4));
            var revision = high != 0 ? high : low;
            return revision != 0 ? revision : null;
        }

        var dword = BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(0, 4));
        return dword != 0 ? dword : null;
    }

    public static string Format(uint revision) => $"0x{revision:X}";
}
