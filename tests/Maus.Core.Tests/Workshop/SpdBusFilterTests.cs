using Maus.Core.Workshop.Memory.PawnIo;

namespace Maus.Core.Tests.Workshop;

/// <summary>Garde-fou du bus des barrettes : MAUS lit, et n'écrit que le choix de page des puces SPD.</summary>
public class SpdBusFilterTests
{
    private const long Read = 1;
    private const long Write = 0;
    private const long Quick = 0;
    private const long ByteData = 2;
    private const long ProcCall = 4;

    private static bool Allowed(long address, long direction, long command, long protocol, long data = 0) =>
        ReadOnlySpdModule.IsAllowedTransfer([address, direction, command, protocol, data], 5);

    [Fact]
    public void Reads_from_any_spd_address_are_allowed()
    {
        Assert.True(Allowed(0x50, Read, 0x12, ByteData));
        Assert.True(Allowed(0x57, Read, 0xFF, ByteData));
    }

    [Fact]
    public void Only_spd_page_selection_can_be_written()
    {
        Assert.True(Allowed(0x36, Write, 0, Quick));
        Assert.True(Allowed(0x37, Write, 0, ByteData));
        Assert.True(Allowed(0x51, Write, 0x0B, ByteData, 1));
    }

    [Fact]
    public void Writes_to_spd_contents_pmic_or_other_devices_are_refused()
    {
        Assert.False(Allowed(0x50, Write, 0x20, ByteData, 0xFF));
        Assert.False(Allowed(0x48, Write, 0x00, ByteData, 0x10));
        Assert.False(Allowed(0x51, Write, 0x0C, ByteData, 1));
        Assert.False(Allowed(0x36, Read, 0, ProcCall));
        Assert.False(ReadOnlySpdModule.IsAllowedTransfer([0x50, Write], 2));
    }
}
