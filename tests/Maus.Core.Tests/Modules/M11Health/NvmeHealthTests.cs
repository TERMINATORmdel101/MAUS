using System.Buffers.Binary;
using Maus.Core.Modules.M11Health;

namespace Maus.Core.Tests.Modules.M11Health;

/// <summary>Disposition du journal : structure NVME_HEALTH_INFO_LOG de Microsoft Learn (nvme.h).</summary>
public class NvmeHealthTests
{
    /// <summary>Valeurs relevées sur le SSD du porteur le 05/10/2026 (Sandisk Optimus GX Pro 8100).</summary>
    private static byte[] Log()
    {
        var data = new byte[NvmeHealthLog.Size];
        data[0] = 0x00;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(1), 304); // 31 °C
        data[3] = 100;
        data[4] = 10;
        data[5] = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(48), 2_000_000); // données écrites
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(128), 37); // heures
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(144), 27); // coupures
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(160), 0); // erreurs
        return data;
    }

    [Fact]
    public void Health_log_fields_are_read_at_their_documented_offsets()
    {
        var log = NvmeHealthLog.Parse(Log())!;

        Assert.Equal(31, log.TemperatureCelsius);
        Assert.Equal(100, log.AvailableSpare);
        Assert.Equal(10, log.AvailableSpareThreshold);
        Assert.Equal(37UL, log.PowerOnHours);
        Assert.Equal(27UL, log.UnsafeShutdowns);
        Assert.Equal(1.024, log.TerabytesWritten, 3);
        Assert.False(log.ReadOnly);
    }

    [Fact]
    public void Critical_warning_bits_are_decoded()
    {
        var data = Log();
        data[0] = 0x1F;

        var log = NvmeHealthLog.Parse(data)!;

        Assert.True(log.SpareBelowThreshold && log.TemperatureAlert && log.ReliabilityDegraded && log.ReadOnly && log.BackupDeviceFailed);
    }

    [Fact]
    public void Short_or_invalid_answers_give_nothing()
    {
        Assert.Null(NvmeHealthLog.Parse(new byte[100]));

        var answer = new byte[8 + 40 + NvmeHealthLog.Size];
        Assert.Null(WindowsNvmeHealthReader.FromDescriptor(answer));
    }

    [Fact]
    public void Windows_answer_is_located_from_its_descriptor()
    {
        var answer = new byte[8 + 40 + NvmeHealthLog.Size];
        BinaryPrimitives.WriteInt32LittleEndian(answer, 48);
        BinaryPrimitives.WriteInt32LittleEndian(answer.AsSpan(4), 48);
        BinaryPrimitives.WriteInt32LittleEndian(answer.AsSpan(8 + 16), 40);
        BinaryPrimitives.WriteInt32LittleEndian(answer.AsSpan(8 + 20), NvmeHealthLog.Size);
        Log().CopyTo(answer, 48);

        Assert.Equal(37UL, WindowsNvmeHealthReader.FromDescriptor(answer)?.PowerOnHours);
    }
}
