using Maus.Core.Platform;
using Maus.Core.Workshop.Memory;

namespace Maus.Core.Tests.Workshop;

public class WindowsMemoryConfigurationTests
{
    [Fact]
    public void A_chip_read_only_at_its_start_says_so_and_proves_nothing_about_the_speed()
    {
        var image = new SpdImage(0, MemoryDetailsTests.Ddr5Kit()[..SpdDecoder.FirstPageBytes], FirstPageOnly: true);

        var module = SpdDecoder.Decode(image)!;

        Assert.True(module.FirstPageOnly);
        Assert.Equal(0, module.CapacityBytes);
        Assert.True(module.ChecksumOk);
        Assert.Equal(ProfileKind.Jedec, Assert.Single(module.Profiles).Kind);
        Assert.Equal("capacité inconnue", module.CapacityText);
        Assert.DoesNotContain("rang", module.Organization, StringComparison.Ordinal);
        var slot = new MemorySlotConfiguration("DIMM 1", null, null, 1L << 34, 6000, 1.35, "DDR5");
        Assert.Null(WindowsMemoryConfiguration.CompareWithSpd([slot], [module]));
        Assert.Null(SpdDecoder.Decode(image with { FirstPageOnly = false }));
    }

    [Fact]
    public void A_repeated_slot_name_gets_its_bank_label()
    {
        var a = Dimm("DIMM 1", 3200, 0);
        a["BankLabel"] = "P0 CHANNEL A";
        var b = Dimm("DIMM 1", 3200, 0);
        b["BankLabel"] = "P0 CHANNEL B";
        var c = Dimm("DIMM 2", 3200, 0);
        c["BankLabel"] = "P0 CHANNEL C";

        var slots = WindowsMemoryConfiguration.Parse([new CimRow(a), new CimRow(b), new CimRow(c)]);

        Assert.Equal(["P0 CHANNEL A / DIMM 1", "P0 CHANNEL B / DIMM 1", "DIMM 2"], slots.Select(s => s.Slot));
    }

    private static Dictionary<string, object?> Dimm(string locator, long configured, long voltageMv) => new()
    {
        ["DeviceLocator"] = locator,
        ["PartNumber"] = "3200 Series          ",
        ["Manufacturer"] = "Patriot",
        ["Capacity"] = 8L * 1024 * 1024 * 1024,
        ["Speed"] = 3467L,
        ["ConfiguredClockSpeed"] = configured,
        ["ConfiguredVoltage"] = voltageMv,
        ["SMBIOSMemoryType"] = 26L,
    };

    [Fact]
    public void A_module_with_only_its_standard_profile_is_never_called_custom()
    {
        MemorySlotConfiguration[] slots = [new("A1", null, null, 8L << 30, 2133, null, "DDR4")];

        var text = WindowsMemoryConfiguration.CompareWithSpd(slots, [Module(Profile(ProfileKind.Jedec, 2133))]);

        Assert.Contains("profil standard", text, StringComparison.Ordinal);
        Assert.DoesNotContain("personnalisé", text, StringComparison.Ordinal);
    }

    private static SpdProfile Profile(ProfileKind kind, int speed, int number = 1) =>
        new(kind, number, kind == ProfileKind.Xmp ? "2.0" : null, null, speed, 2000.0 / speed, [], null, null, null);

    private static SpdModule Module(params SpdProfile[] profiles) => new()
    {
        Slot = 0,
        MemoryType = "DDR4",
        ModuleType = "UDIMM",
        Profiles = profiles,
    };

    [Fact]
    public void Reads_applied_speed_and_voltage_from_windows()
    {
        var cim = new FakeCim().Answer(WindowsMemoryConfiguration.Query, Dimm("ChannelA-DIMM1", 3467, 1250), Dimm("ChannelB-DIMM1", 3467, 1250));

        var slots = WindowsMemoryConfiguration.Read(cim);

        Assert.Equal(2, slots.Count);
        Assert.Equal("ChannelA-DIMM1", slots[0].Slot);
        Assert.Equal("3200 Series", slots[0].PartNumber);
        Assert.Equal(3467, slots[0].ConfiguredMts);
        Assert.Equal(1.25, slots[0].ConfiguredVolts);
        Assert.Equal("DDR4", slots[0].MemoryType);
        Assert.Contains("3467 MT/s", WindowsMemoryConfiguration.Describe(slots));
        Assert.Contains("1.250 V", WindowsMemoryConfiguration.Describe(slots));
    }

    [Theory]
    [InlineData(1866L, 3200L, 1866, 3200)] // Vraie DDR4-1866 sur une barrette 3200 : déjà en MT/s.
    [InlineData(1066L, 1600L, 2132, 3200)] // Vieux BIOS : les deux en MHz.
    [InlineData(3200L, 3200L, 3200, 3200)]
    public void Every_reader_gets_the_same_speeds_from_a_ddr4_row(long configured, long rated, int expectedConfigured, int expectedRated)
    {
        var row = new CimRow(new Dictionary<string, object?> { ["ConfiguredClockSpeed"] = configured, ["Speed"] = rated, ["SMBIOSMemoryType"] = 26L });

        Assert.Equal((expectedConfigured, expectedRated), Maus.Core.Modules.M10Memory.MemorySpeeds.FromRow(row, 4));
        Assert.Equal(expectedConfigured, WindowsMemoryConfiguration.Parse([row])[0].ConfiguredMts);
    }

    [Fact]
    public void A_filler_value_from_the_bios_is_not_a_name()
    {
        Assert.Null(Maus.Core.Hardware.HardwareText.Clean(" To Be Filled By O.E.M.\0 "));
        Assert.Null(Maus.Core.Hardware.HardwareText.Clean("Undefined"));
        Assert.Null(Maus.Core.Hardware.HardwareText.Clean("   "));
        Assert.Equal("Patriot", Maus.Core.Hardware.HardwareText.Clean(" Patriot\0"));
    }

    [Fact]
    public void Unknown_voltage_is_not_shown_as_zero()
    {
        var slots = WindowsMemoryConfiguration.Parse([new CimRow(Dimm("DIMM1", 3200, 0))]);

        Assert.Null(slots[0].ConfiguredVolts);
        Assert.DoesNotContain("0.000 V", WindowsMemoryConfiguration.Describe(slots));
    }

    [Fact]
    public void Unreadable_wmi_gives_an_empty_list_not_an_error()
    {
        var cim = new FakeCim().Throw(WindowsMemoryConfiguration.Query, new MausAccessDeniedException("refusé"));

        Assert.Empty(WindowsMemoryConfiguration.Read(cim));
    }

    [Fact]
    public void Speed_above_every_profile_is_a_manual_overclock()
    {
        var slots = WindowsMemoryConfiguration.Parse([new CimRow(Dimm("DIMM1", 3467, 1250))]);

        var text = WindowsMemoryConfiguration.CompareWithSpd(slots, [Module(Profile(ProfileKind.Jedec, 2133), Profile(ProfileKind.Xmp, 3200))]);

        Assert.Contains("réglée à la main", text);
    }

    [Fact]
    public void Speed_matching_an_xmp_profile_means_the_profile_is_enabled()
    {
        var slots = WindowsMemoryConfiguration.Parse([new CimRow(Dimm("DIMM1", 3200, 1350))]);

        var text = WindowsMemoryConfiguration.CompareWithSpd(slots, [Module(Profile(ProfileKind.Jedec, 2133), Profile(ProfileKind.Xmp, 3200))]);

        Assert.Contains("probablement activé", text);
    }

    [Fact]
    public void Jedec_speed_with_a_faster_profile_means_xmp_is_off()
    {
        var slots = WindowsMemoryConfiguration.Parse([new CimRow(Dimm("DIMM1", 2133, 1200))]);

        var text = WindowsMemoryConfiguration.CompareWithSpd(slots, [Module(Profile(ProfileKind.Jedec, 2133), Profile(ProfileKind.Xmp, 3200))]);

        Assert.Contains("n'est pas activé", text);
    }

    [Fact]
    public void Profile_summary_shows_speed_primary_timings_and_voltage()
    {
        MemoryTiming Primary(string key, int clocks) => new(key, TimingGroup.Primary, clocks);
        var profile = new SpdProfile(ProfileKind.Xmp, 1, "2.0", null, 3200, 0.625,
            [Primary("tCL", 16), Primary("tRCD", 18), Primary("tRP", 18), Primary("tRAS", 36)], 1.35, null, null);

        Assert.Equal("3200 MT/s · 16-18-18-36 · 1.35 V", MemoryDetails.ProfileSummary(profile));
    }

    [Fact]
    public void Spd_chip_is_matched_by_part_number_then_by_order()
    {
        var first = Module(Profile(ProfileKind.Xmp, 3200)) with { Slot = 2, PartNumber = "PVS416G320C6" };
        var second = Module(Profile(ProfileKind.Xmp, 3600)) with { Slot = 0, PartNumber = "F4-3600C16-8GTZN" };

        Assert.Same(second, MemoryDetails.MatchModule([first, second], "F4-3600C16-8GTZN  ", 0));
        Assert.Same(second, MemoryDetails.MatchModule([first, second], "3200 Series", 0));
        Assert.Same(first, MemoryDetails.MatchModule([first, second], null, 1));
        Assert.Null(MemoryDetails.MatchModule([first], null, 3));
    }

    [Fact]
    public void No_spd_profiles_means_no_comparison()
    {
        var slots = WindowsMemoryConfiguration.Parse([new CimRow(Dimm("DIMM1", 3467, 1250))]);

        Assert.Null(WindowsMemoryConfiguration.CompareWithSpd(slots, []));
    }
}
