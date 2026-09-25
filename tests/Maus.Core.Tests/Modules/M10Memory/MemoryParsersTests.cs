using Maus.Core.Modules.M10Memory;

namespace Maus.Core.Tests.Modules.M10Memory;

public class MemoryParsersTests
{
    [Theory]
    // Corsair : série, génération, vitesse, CAS ; ValueSelect (CMV, CMSO) = JEDEC.
    [InlineData("CMK32GX5M2B6000C36", "Corsair", 6000, 5, 36, false)]
    [InlineData("CMK16GX4M2B3200C16", "Corsair", 3200, 4, 16, false)]
    [InlineData("CMW32GX4M2E3200C16", "Corsair", 3200, 4, 16, false)]
    [InlineData("CMH32GX5M2B5600C36", "Corsair", 5600, 5, 36, false)]
    [InlineData("CMT32GX5M2X6200C36", "Corsair", 6200, 5, 36, false)]
    [InlineData("CMK16GX5M1A4800C40", "Corsair", 4800, 5, 40, true)]
    [InlineData("CMV8GX4M1A2400C16", "Corsair", 2400, 4, 16, true)]
    [InlineData("CMSO8GX4M1A2133C15", "Corsair", 2133, 4, 15, true)]
    // G.Skill.
    [InlineData("F4-3200C16-8GVKB", "G.Skill", 3200, 4, 16, false)]
    [InlineData("F4-3600C18D-32GTZN", "G.Skill", 3600, 4, 18, false)]
    [InlineData("F5-6000J3038F16GX2-TZ5N", "G.Skill", 6000, 5, 30, false)]
    [InlineData("F5-6000J3038F16G", "G.Skill", 6000, 5, 30, false)]
    [InlineData("F4-2133C15-8GNT", "G.Skill", 2133, 4, 15, true)]
    // Kingston Fury / HyperX, référence SPD, ValueRAM, Client Premier, OEM.
    [InlineData("KF560C36BBEK2-32", "Kingston", 6000, 5, 36, false)]
    [InlineData("KF436C17BB/8", "Kingston", 3600, 4, 17, false)]
    [InlineData("KF432C16BB/8", "Kingston", 3200, 4, 16, false)]
    [InlineData("KF552C40-16", "Kingston", 5200, 5, 40, false)]
    [InlineData("HX432C16FB3/8", "Kingston", 3200, 4, 16, false)]
    [InlineData("KHX3200C16D4/8GX", "Kingston", 3200, 4, 16, false)]
    [InlineData("KF3200C16D4/16GX", "Kingston", 3200, 4, 16, false)]
    [InlineData("KVR32N22S8/8", "Kingston", 3200, 4, 22, true)]
    [InlineData("KCP432NS8/8", "Kingston", 3200, 4, null, true)]
    [InlineData("ACR26D4S9S8ME-8", "Kingston", 2666, 4, null, true)]
    // Crucial / Ballistix : CP16G4DFRA32A est un module JEDEC.
    [InlineData("BL2K16G32C16U4B", "Crucial Ballistix", 3200, 4, 16, false)]
    [InlineData("BL16G36C16U4B.M16FE1", "Crucial Ballistix", 3600, 4, 16, false)]
    [InlineData("BLS8G4D240FSB.16FBD", "Crucial Ballistix", 2400, 4, null, false)]
    [InlineData("CP2K16G60C36U5B", "Crucial", 6000, 5, 36, false)]
    [InlineData("CP16G56C46U5.M8D1", "Crucial", 5600, 5, 46, true)]
    [InlineData("CT16G48C40U5.M8A1", "Crucial", 4800, 5, 40, true)]
    [InlineData("CP16G4DFRA32A.C8FE", "Crucial", 3200, 4, null, true)]
    [InlineData("CT16G4DFRA266.C8FE", "Crucial", 2666, 4, null, true)]
    [InlineData("CT8G4SFS832A.C8FE", "Crucial", 3200, 4, null, true)]
    // TeamGroup.
    [InlineData("TEAMGROUP-UD4-3200", "TeamGroup", 3200, 4, null, false)]
    [InlineData("TEAMGROUP-UD5-6000", "TeamGroup", 6000, 5, null, false)]
    [InlineData("TF3D416G3200HC16FDC01", "TeamGroup", 3200, 4, 16, false)]
    [InlineData("TED416G3200C2201", "TeamGroup", 3200, 4, 22, true)]
    // Patriot.
    [InlineData("PVS416G320C6K", "Patriot", 3200, 4, 16, false)]
    [InlineData("PVV532G600C36K", "Patriot", 6000, 5, 36, false)]
    [InlineData("PSD416G32002", "Patriot", 3200, 4, null, true)]
    [InlineData("3200 Series", "Patriot", 3200, null, null, false)]
    [InlineData("3200 C16 Series", "Patriot", 3200, null, 16, false)]
    // ADATA / XPG.
    [InlineData("AX4U320016G16A-ST41", "ADATA XPG", 3200, 4, null, false)]
    [InlineData("AX5U6000C3016G-DCLARBK", "ADATA XPG", 6000, 5, null, false)]
    [InlineData("AD4U320016G22-SGN", "ADATA", 3200, 4, null, true)]
    // Modules d'origine (JEDEC).
    [InlineData("M378A1K43CB2-CTD", "Samsung", 2666, 4, null, true)]
    [InlineData("M471A1K43DB1-CWE", "Samsung", 3200, 4, null, true)]
    [InlineData("M323R2GA3BB0-CQK", "Samsung", 4800, 5, null, true)]
    [InlineData("HMA81GU6CJR8N-VK", "SK Hynix", 2666, 4, null, true)]
    [InlineData("HMA82GS6DJR8N-XN", "SK Hynix", 3200, 4, null, true)]
    [InlineData("MTA8ATF1G64AZ-3G2E1", "Micron", 3200, 4, null, true)]
    [InlineData("MTC8C1084S1UC48BA1", "Micron", 4800, 5, null, true)]
    public void Part_numbers_are_decoded(string partNumber, string brand, int speed, int? generation, int? cas, bool jedec)
    {
        var part = MemoryPartDecoder.Decode(partNumber);

        Assert.NotNull(part);
        Assert.Equal(brand, part.Brand);
        Assert.Equal(speed, part.RatedSpeed);
        Assert.Equal(generation, part.Generation);
        Assert.Equal(cas, part.CasLatency);
        Assert.Equal(jedec, part.IsJedec);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Unknown")]
    [InlineData("99U5471-020.A00LF")]
    [InlineData("HMCG78AEBUA081N")]
    [InlineData("M378A1K43CB2-CXX")]
    [InlineData("0x0000")]
    public void Unknown_part_numbers_stay_undetermined(string? partNumber) => Assert.Null(MemoryPartDecoder.Decode(partNumber));

    [Fact]
    public void Decoding_ignores_case_and_padding() =>
        Assert.Equal(6000, MemoryPartDecoder.Decode("  f5-6000j3038f16g   ")?.RatedSpeed);

    [Theory]
    [InlineData("ChannelA-DIMM1", "BANK 1", "0A")]
    [InlineData("ChannelB-DIMM1", "BANK 3", "0B")]
    [InlineData("ChannelA-DIMM0", "BANK 0", "0A")]
    [InlineData("DIMM 0", "P0 CHANNEL A", "0A")]
    [InlineData("DIMM 1", "P0 CHANNEL B", "0B")]
    [InlineData("DIMM_A1", "BANK 0", "0A")]
    [InlineData("DIMM_B2", null, "0B")]
    [InlineData("DDR4_A2", "", "0A")]
    [InlineData("DIMMB1", null, "0B")]
    [InlineData("Controller0-ChannelA-DIMM0", "BANK 0", "0A")]
    [InlineData("Controller1-ChannelA-DIMM0", "BANK 0", "1A")]
    [InlineData("A1", "Bank0/1", "0A")]
    [InlineData("B2", null, "0B")]
    [InlineData("DIMM 0", "CHANNEL 1", "01")]
    public void Channel_is_read_from_locator_or_bank(string? locator, string? bank, string expected) =>
        Assert.Equal(expected, DimmSlotParser.ChannelOf(locator, bank));

    [Theory]
    [InlineData("DIMM 0", "BANK 0")]
    [InlineData("XMM1", null)]
    [InlineData(null, null)]
    public void Unrecognised_slot_names_give_no_channel(string? locator, string? bank) =>
        Assert.Null(DimmSlotParser.ChannelOf(locator, bank));

    [Theory]
    [InlineData(4, 1600, 3200)]
    [InlineData(4, 1333, 2666)]
    [InlineData(4, 3200, 3200)]
    [InlineData(5, 2400, 4800)]
    [InlineData(5, 6000, 6000)]
    [InlineData(null, 1600, 1600)]
    [InlineData(3, 800, 800)]
    public void Old_bios_clock_in_mhz_is_converted_to_transfers(int? generation, int raw, int expected) =>
        Assert.Equal(expected, MemorySpeeds.ToTransfersPerSecond(generation, raw));

    [Theory]
    [InlineData(4, 2133, true)]
    [InlineData(4, 2667, true)]
    [InlineData(4, 3200, true)]
    [InlineData(4, 3467, false)]
    [InlineData(4, 3600, false)]
    [InlineData(5, 4800, true)]
    [InlineData(5, 5600, true)]
    [InlineData(5, 6000, false)]
    [InlineData(3, 1600, true)]
    [InlineData(2, 800, false)]
    public void Jedec_speeds_are_recognised(int generation, int speed, bool expected) =>
        Assert.Equal(expected, MemorySpeeds.IsJedecSpeed(generation, speed));

    [Theory]
    [InlineData(26L, "DDR4", 4)]
    [InlineData(34L, "DDR5", 5)]
    [InlineData(24L, "DDR3", 3)]
    [InlineData(35L, "LPDDR5", null)]
    [InlineData(0L, null, null)]
    public void Smbios_types_are_named(long type, string? name, int? generation)
    {
        Assert.Equal(name, MemorySpeeds.TypeName(type));
        Assert.Equal(generation, MemorySpeeds.Generation(type));
    }

    [Theory]
    [InlineData(36, 3600)]
    [InlineData(26, 2666)]
    [InlineData(60, 6000)]
    [InlineData(21, 2133)]
    public void Two_digit_speed_codes_are_expanded(int code, int expected) => Assert.Equal(expected, MemorySpeeds.FromTwoDigits(code));

    [Theory]
    [InlineData("320", 3200)]
    [InlineData("266", 2666)]
    [InlineData("32", 3200)]
    public void Digit_speed_codes_are_expanded(string digits, int expected) => Assert.Equal(expected, MemorySpeeds.FromDigits(digits));
}
