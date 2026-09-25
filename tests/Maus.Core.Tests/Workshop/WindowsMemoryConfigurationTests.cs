using Maus.Core.Platform;
using Maus.Core.Workshop.Memory;

namespace Maus.Core.Tests.Workshop;

public class WindowsMemoryConfigurationTests
{
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

    private static SpdProfile Profile(ProfileKind kind, int speed, int number = 1) =>
        new(kind, number, kind == ProfileKind.Xmp ? "2.0" : null, null, speed, 2000.0 / speed, [], [], null, null, null);

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
    public void No_spd_profiles_means_no_comparison()
    {
        var slots = WindowsMemoryConfiguration.Parse([new CimRow(Dimm("DIMM1", 3467, 1250))]);

        Assert.Null(WindowsMemoryConfiguration.CompareWithSpd(slots, []));
    }
}
