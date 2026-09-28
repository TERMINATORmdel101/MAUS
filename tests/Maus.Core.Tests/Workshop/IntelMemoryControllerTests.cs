using Maus.Core.Workshop.Memory;
using Maus.Core.Workshop.Memory.PawnIo;

namespace Maus.Core.Tests.Workshop;

public class IntelMemoryControllerTests
{
    private const int CoffeeLake = 0x9E;
    private const int SkylakeDesktop = 0x5E;

    /// <summary>Fenêtres MCHBAR du module IntelMCHBAR : 32 Ko jusqu'à Comet Lake, 64 Ko Ice Lake et Rocket Lake, 128 Ko ensuite.</summary>
    private static readonly int[] MchbarWindows = [0x8000, 0x10000, 0x20000];

    private static readonly string[] Confidences = ["dual", "single"];

    /// <summary>
    /// Registres d'un i7-8700K en DDR4-3467 (rapport 13 × 133,33 MHz), 16-19-19-39, 2N, tRFC 607, deux canaux garnis,
    /// ring à 4,3 GHz (plage 0,8 à 4,3 GHz). Valeurs composées bit à bit d'après la fiche Intel 336465-001.
    /// </summary>
    private static Dictionary<int, ulong> CoffeeLakeRegisters(bool channelB = true)
    {
        var registers = new Dictionary<int, ulong>
        {
            [0x500C] = 0x0010_0010,
            [0x5010] = channelB ? 0x0010_0010UL : 0,
            [0x5918] = 13 | (8 << 8) | (43UL << 24),
        };

        foreach (var channel in new[] { 0x4000, 0x4400 })
        {
            registers[channel + 0x000] = 19 | (39 << 8) | (10 << 16) | (36UL << 24);
            registers[channel + 0x01C] = 0b01_00;
            registers[channel + 0x070] = (16 << 16) | (16 << 21);
            registers[channel + 0x23C] = 13500 | (607UL << 16);
        }

        return registers;
    }

    private static ImcFamily CoffeeLakeFamily => IntelMemoryController.FamilyFor(6, CoffeeLake)!;

    [Fact]
    public void Catalog_maps_the_owner_s_coffee_lake_to_the_skylake_controller()
    {
        Assert.Equal("skl-cml", IntelMemoryController.FamilyFor(6, CoffeeLake)?.Id);
        Assert.Equal("skl-cml", IntelMemoryController.FamilyFor(6, 0xA5)?.Id);
        Assert.Null(IntelMemoryController.FamilyFor(6, 0x55));
        Assert.Null(IntelMemoryController.FamilyFor(0x19, CoffeeLake));
    }

    [Fact]
    public void Reads_timings_clocks_and_settings_of_both_channels()
    {
        var registers = CoffeeLakeRegisters();
        var msr = new FakeMsr { [0x620] = 43 | (8 << 8), [0x621] = 43 };

        var report = IntelMemoryController.Read(CoffeeLakeFamily, 6, CoffeeLake, new FakeMchbar(registers), msr)!;

        Assert.Equal(["A", "B"], report.Channels);
        Assert.Equal("DDR4", report.MemoryType);
        Assert.Contains(report.Settings, s => s is { Key: "CommandRate", Value: "2N" });
        Assert.False(report.ChannelsDiffer);
        Assert.True(report.VerifiedOnHardware);

        int? Timing(string key) => report.Timings.Single(t => t.Key == key).Clocks;
        Assert.Equal(16, Timing("tCL"));
        Assert.Equal(19, Timing("tRCD"));
        Assert.Equal(19, Timing("tRP"));
        Assert.Equal(39, Timing("tRAS"));
        Assert.Equal(16, Timing("tCWL"));
        Assert.Equal(10, Timing("tRDPRE"));
        Assert.Equal(36, Timing("tWRPRE"));
        Assert.Equal(607, Timing("tRFC"));
        Assert.Equal(13500, Timing("tREFI"));

        Assert.Equal(1733.3, report.DclkMhz!.Value, 1);
        Assert.Equal(4300, report.RingMhz);
        Assert.Equal(800, report.RingMinMhz);
        Assert.Equal(4300, report.RingMaxMhz);
        Assert.Equal(800, report.Clocks.Single(c => c.Key == "SA").Mhz);
        Assert.Equal(350.2, report.Timings.Single(t => t.Key == "tRFC").Nanoseconds!.Value, 1);
    }

    [Fact]
    public void Reference_bit_switches_the_memory_clock_to_100_mhz()
    {
        var registers = CoffeeLakeRegisters();
        registers[0x5918] = 18 | (1 << 7) | (8 << 8) | (43UL << 24);

        var report = IntelMemoryController.Read(CoffeeLakeFamily, 6, CoffeeLake, new FakeMchbar(registers), null)!;

        Assert.Equal(1800, report.DclkMhz);
    }

    [Fact]
    public void Empty_channel_is_skipped_and_no_channel_means_no_report()
    {
        var single = IntelMemoryController.Read(CoffeeLakeFamily, 6, CoffeeLake, new FakeMchbar(CoffeeLakeRegisters(channelB: false)), null)!;
        Assert.Equal(["A"], single.Channels);

        var none = CoffeeLakeRegisters(channelB: false);
        none[0x500C] = 0;
        Assert.Null(IntelMemoryController.Read(CoffeeLakeFamily, 6, CoffeeLake, new FakeMchbar(none), null));
    }

    [Fact]
    public void Different_channels_are_flagged_and_all_kept()
    {
        var registers = CoffeeLakeRegisters();
        registers[0x4470] = (17 << 16) | (16 << 21);

        var report = IntelMemoryController.Read(CoffeeLakeFamily, 6, CoffeeLake, new FakeMchbar(registers), null)!;

        Assert.True(report.ChannelsDiffer);
        Assert.Equal(16, report.Timings.Single(t => t.Key == "tCL").Clocks);
        Assert.Equal(17, report.PerChannel[1].Timings.Single(t => t.Key == "tCL").Clocks);
    }

    [Fact]
    public void Ring_msr_is_only_read_on_models_listed_by_linux()
    {
        var msr = new FakeMsr { [0x620] = 43 | (8 << 8), [0x621] = 43 };

        var report = IntelMemoryController.Read(CoffeeLakeFamily, 6, SkylakeDesktop, new FakeMchbar(CoffeeLakeRegisters()), msr)!;

        Assert.Null(report.RingMaxMhz);
        Assert.Empty(msr.Reads);
    }

    [Fact]
    public void Reads_an_ivy_bridge_controller_with_its_own_clock_registers()
    {
        var registers = new Dictionary<int, ulong>
        {
            [0x5004] = 0x0808,
            [0x5008] = 0,
            [0x4000] = 9 | (9 << 4) | (9 << 8) | (7 << 12) | (24UL << 16),
            [0x4004] = 5 | (5 << 4) | (4 << 8) | (5 << 12) | (24 << 16) | (10 << 24) | (2UL << 30),
            [0x4298] = 6240 | (208UL << 16),
            [0x42A4] = 512 | (256 << 16) | (4UL << 28),
            [0x5E04] = 8,
            [0x5E00] = 0,
        };
        var family = IntelMemoryController.FamilyFor(6, 0x3A)!;

        var report = IntelMemoryController.Read(family, 6, 0x3A, new FakeMchbar(registers), null)!;

        Assert.Equal("snb-ivb", family.Id);
        Assert.Equal(["A"], report.Channels);
        Assert.Equal("DDR3", report.MemoryType);
        Assert.Contains(report.Settings, s => s is { Key: "CommandRate", Value: "2N" });
        int? Timing(string key) => report.Timings.Single(t => t.Key == key).Clocks;
        Assert.Equal((9, 9, 9, 24), (Timing("tCL"), Timing("tRCD"), Timing("tRP"), Timing("tRAS")));
        Assert.Equal((7, 5, 5, 4, 24, 10), (Timing("tCWL"), Timing("tRRD"), Timing("tWTR"), Timing("tCKE"), Timing("tFAW"), Timing("tWR")));
        Assert.Equal((208, 6240, 12, 512, 256), (Timing("tRFC"), Timing("tREFI"), Timing("tMOD"), Timing("tXSDLL"), Timing("tZQOPER")));
        Assert.Equal(1066.7, report.DclkMhz!.Value, 1);

        registers[0x5E00] = 1 << 8;
        registers[0x5E04] = 9;
        Assert.Equal(900, IntelMemoryController.Read(family, 6, 0x3A, new FakeMchbar(registers), null)!.DclkMhz!.Value, 3);

        registers[0x5E04] = 13;
        Assert.Null(IntelMemoryController.Read(family, 6, 0x3A, new FakeMchbar(registers), null)!.DclkMhz);

        // Génération pas encore comparée à CPU-Z sur un vrai processeur : la fiche le dit.
        var details = MemoryDetails.Read(new NoSpd(), null, null, null, new IntelControllerAccess(6, 0x3A, () => new FakeMchbar(registers), () => null));
        Assert.Contains(details.Notes, n => n.Contains("pas encore comparée à CPU-Z", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0x3C, "hsw-bdw")]
    [InlineData(0x47, "hsw-bdw")]
    [InlineData(0x7E, "icl-rkl")]
    [InlineData(0xA7, "icl-rkl")]
    [InlineData(0x8C, "tgl")]
    [InlineData(0x97, "adl-rpl")]
    [InlineData(0xB7, "adl-rpl")]
    [InlineData(0xBF, "adl-rpl")]
    [InlineData(0xAA, "mtl-arl-lnl")]
    [InlineData(0xC6, "mtl-arl-lnl")]
    [InlineData(0xBD, "mtl-arl-lnl")]
    public void Catalog_covers_every_core_generation_from_haswell(int model, string family)
    {
        Assert.Equal(family, IntelMemoryController.FamilyFor(6, model)?.Id);
        Assert.False(IntelMemoryController.FamilyFor(6, model)!.VerifiedOnHardware);
    }

    /// <summary>
    /// Alder Lake en DDR5-6000 : contrôleur 0 garni (deux sous-canaux), contrôleur 1 absent (registres tout à un),
    /// 30-36-36-76, gear 2, 2N. Valeurs composées bit à bit d'après la fiche Intel 655259.
    /// </summary>
    private static Dictionary<int, ulong> AlderLakeRegisters(int gear = 1)
    {
        var registers = new Dictionary<int, ulong>
        {
            [0xD800] = 1,
            [0xD80C] = 0x20,
            [0xD810] = 0x20,
            [0x1D80C] = 0xFFFF_FFFF,
            [0x1D810] = 0xFFFF_FFFF,
            [0x5918] = (30 << 2) | (1 << 10) | (48UL << 24),
            [0x5E04] = 30 | (1 << 8) | ((ulong)gear << 12),
        };

        foreach (var channel in new[] { 0xE000, 0xE800 })
        {
            registers[channel + 0x000] = 36 | (12 << 13) | (90UL << 32) | (76UL << 42) | (36UL << 51);
            registers[channel + 0x070] = (30 << 16) | (28UL << 24);
            registers[channel + 0x088] = 0b01_000;
            registers[channel + 0x008] = 32 | (8 << 9) | (4 << 15);
            registers[channel + 0x43C] = 3900 | (884UL << 18);
        }

        return registers;
    }

    [Fact]
    public void Reads_alder_lake_ddr5_with_gear_and_skips_the_absent_controller()
    {
        var family = IntelMemoryController.FamilyFor(6, 0x97)!;

        var report = IntelMemoryController.Read(family, 6, 0x97, new FakeMchbar(AlderLakeRegisters()), null)!;

        Assert.Equal(["MC0-A", "MC0-B"], report.Channels);
        Assert.Equal("DDR5", report.MemoryType);
        Assert.Contains(report.Settings, s => s is { Key: "CommandRate", Value: "2N" });
        Assert.Contains(report.Settings, s => s is { Key: "Gear", Value: "Gear 2" });
        Assert.Equal(3000, report.DclkMhz!.Value, 3);
        Assert.Equal(4800, report.RingMhz);
        int? Timing(string key) => report.Timings.Single(t => t.Key == key).Clocks;
        Assert.Equal((30, 36, 36, 76), (Timing("tCL"), Timing("tRCD"), Timing("tRP"), Timing("tRAS")));
        Assert.Equal((28, 12, 90), (Timing("tCWL"), Timing("tRDPRE"), Timing("tWRPRE")));
        Assert.Equal((32, 8, 4), (Timing("tFAW"), Timing("tRRD_L"), Timing("tRRD_S")));
        Assert.Equal((884, 3900), (Timing("tRFC"), Timing("tREFI")));
        Assert.Equal(294.7, report.Timings.Single(t => t.Key == "tRFC").Nanoseconds!.Value, 1);

        Assert.Equal(6000, IntelMemoryController.Read(family, 6, 0x97, new FakeMchbar(AlderLakeRegisters(gear: 2)), null)!.DclkMhz!.Value, 3);
        Assert.Null(IntelMemoryController.Read(family, 6, 0x97, new FakeMchbar(AlderLakeRegisters(gear: 3)), null)!.DclkMhz);
    }

    [Fact]
    public void Tiger_lake_reads_its_second_controller_and_64_bit_timing_register()
    {
        var registers = new Dictionary<int, ulong>
        {
            [0x5000] = 0,
            [0x500C] = 0x10,
            [0x5010] = 0,
            [0x1500C] = 0x10,
            [0x15010] = 0,
            [0x5E04] = 32 | (1 << 8),
        };
        foreach (var channel in new[] { 0x4000, 0x14000 })
        {
            registers[channel + 0x000] = 22 | (52UL << 33) | (22UL << 41);
            registers[channel + 0x070] = 22 << 16;
        }

        var family = IntelMemoryController.FamilyFor(6, 0x8C)!;
        var report = IntelMemoryController.Read(family, 6, 0x8C, new FakeMchbar(registers), null)!;

        Assert.Equal(["MC0-A", "MC1-A"], report.Channels);
        Assert.Equal("DDR4", report.MemoryType);
        Assert.Contains(report.Settings, s => s is { Key: "Gear", Value: "Gear 1" });
        Assert.Equal(1600, report.DclkMhz);
        int? Timing(string key) => report.Timings.Single(t => t.Key == key).Clocks;
        Assert.Equal((22, 22, 22, 52), (Timing("tCL"), Timing("tRCD"), Timing("tRP"), Timing("tRAS")));
    }

    [Fact]
    public void Meteor_lake_clock_is_shown_in_gear_2_only_and_lunar_lake_gets_timings_without_clock()
    {
        var registers = new Dictionary<int, ulong>
        {
            [0xD80C] = 0x20,
            [0xD810] = 0,
            [0x1D80C] = 0xFFFF_FFFF,
            [0x1D810] = 0xFFFF_FFFF,
            [0x13D10] = 84,
            [0xE000] = 40 | (90UL << 45),
            [0xE138] = 40UL << 22,
            [0xE070] = 40 << 16,
            [0xE088] = 1 << 3,
        };
        var family = IntelMemoryController.FamilyFor(6, 0xAA)!;

        var meteor = IntelMemoryController.Read(family, 6, 0xAA, new FakeMchbar(registers), null)!;
        Assert.Equal(2800, meteor.DclkMhz!.Value, 1);
        Assert.Contains(meteor.Settings, s => s is { Key: "CommandRate", Value: "2N" });
        Assert.Contains(meteor.Settings, s => s is { Key: "Gear", Value: "Gear 2" });
        int? Timing(string key) => meteor.Timings.Single(t => t.Key == key).Clocks;
        Assert.Equal((40, 40, 40, 90), (Timing("tCL"), Timing("tRCD"), Timing("tRP"), Timing("tRAS")));

        registers[0x13D10] = 84 | (1 << 8);
        Assert.Null(IntelMemoryController.Read(family, 6, 0xAA, new FakeMchbar(registers), null)!.DclkMhz);

        var lunar = IntelMemoryController.Read(family, 6, 0xBD, new FakeMchbar(registers), null)!;
        Assert.Null(lunar.DclkMhz);
        Assert.Empty(lunar.Settings);
        Assert.Equal(40, lunar.Timings.Single(t => t.Key == "tCL").Clocks);
    }

    [Fact]
    public void Haswell_gets_timings_without_the_disputed_clock_and_command_rate_only_where_documented()
    {
        var registers = new Dictionary<int, ulong>
        {
            [0x5000] = 0,
            [0x5004] = 0x10,
            [0x5008] = 0x10,
            [0x4000] = 9 | (9 << 5) | (24 << 10),
            [0x4004] = 5 | (24 << 4) | (2UL << 30),
            [0x4014] = 9 | (7 << 5),
            [0x4298] = 6240 | (208UL << 16),
        };
        registers[0x4400] = registers[0x4000];
        registers[0x4404] = registers[0x4004];
        registers[0x4414] = registers[0x4014];
        registers[0x4698] = registers[0x4298];
        var family = IntelMemoryController.FamilyFor(6, 0x3C)!;

        var haswell = IntelMemoryController.Read(family, 6, 0x3C, new FakeMchbar(registers), null)!;
        Assert.Equal("DDR3", haswell.MemoryType);
        Assert.Contains(haswell.Settings, s => s is { Key: "CommandRate", Value: "2N" });
        Assert.Null(haswell.DclkMhz);
        int? Timing(string key) => haswell.Timings.Single(t => t.Key == key).Clocks;
        Assert.Equal((9, 9, 9, 24, 7), (Timing("tCL"), Timing("tRCD"), Timing("tRP"), Timing("tRAS"), Timing("tCWL")));
        Assert.Equal((208, 6240), (Timing("tRFC"), Timing("tREFI")));
        Assert.DoesNotContain(haswell.Timings, t => t.Key is "tRRD" or "tRDPRE");

        var broadwell = IntelMemoryController.Read(family, 6, 0x47, new FakeMchbar(registers), null)!;
        Assert.DoesNotContain(broadwell.Settings, s => s.Key == "CommandRate");

        var details = MemoryDetails.Read(new NoSpd(), null, null, null, new IntelControllerAccess(6, 0x3C, () => new FakeMchbar(registers), () => null));
        Assert.Contains(details.Notes, n => n.StartsWith("Horloge mémoire réelle non affichée", StringComparison.Ordinal));
    }

    [Fact]
    public void Turnaround_timings_are_described_in_plain_words()
    {
        Assert.Equal("Délai entre deux lectures successives, dans le même groupe de banques.", MemoryDetails.Describe("tRDRD_sg"));
        Assert.Equal("Délai pour passer d'une écriture à une lecture, sur une autre barrette.", MemoryDetails.Describe("tWRRD_dd"));
        Assert.Null(MemoryDetails.Describe("tRDRD_xx"));
        foreach (var key in IntelMemoryController.Families.SelectMany(f => f.Fields).Select(f => f.Key).Distinct())
        {
            Assert.True(MemoryDetails.Describe(key) is not null || key is "tZQOPER" or "tMOD", $"{key} sans description");
        }
    }

    [Fact]
    public void Extract_reads_documented_bit_ranges()
    {
        Assert.Equal(0x3UL, IntelMemoryController.Extract(0b1100, "3:2"));
        Assert.Equal(1UL, IntelMemoryController.Extract(0x80, "7"));
        Assert.Equal(0xABUL, IntelMemoryController.Extract(0xAB00_0000_0000_0000, "63:56"));
        Assert.Equal(ulong.MaxValue, IntelMemoryController.Extract(ulong.MaxValue, "63:0"));
    }

    [Fact]
    public void Every_catalog_entry_is_sourced_and_readable_by_the_pawnio_module()
    {
        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in IntelMemoryController.Families)
        {
            Assert.NotEmpty(family.Sources);
            Assert.NotEmpty(family.CpuModels);
            Assert.NotEmpty(family.Channels);
            Assert.Contains(family.MchbarWindowBytes, MchbarWindows);
            foreach (var model in family.CpuModels)
            {
                Assert.True(models.Add(model), $"{family.Id} : modèle {model} déjà dans une autre famille");
            }

            void InWindow(int offset, int size, string what)
            {
                Assert.True(size is 4 or 8, $"{family.Id} {what} : taille {size}");
                Assert.True(offset % size == 0, $"{family.Id} {what} : 0x{offset:X} non aligné sur {size}");
                Assert.True(offset >= 0 && offset + size <= family.MchbarWindowBytes, $"{family.Id} {what} : 0x{offset:X} hors de la fenêtre");
            }

            void ValidBits(string bits, int size, string what)
            {
                var parts = bits.Split(':').Select(int.Parse).ToArray();
                Assert.True(parts[0] < size * 8 && parts[^1] >= 0 && parts[0] >= parts[^1], $"{family.Id} {what} : bits {bits}");
            }

            foreach (var channel in family.Channels)
            {
                var channelBase = IntelMemoryController.Hex(channel.Base);
                if (channel.PresenceOffset is { } presence)
                {
                    InWindow(IntelMemoryController.Hex(presence), 4, $"présence {channel.Name}");
                }

                foreach (var field in family.Fields)
                {
                    Assert.NotEmpty(field.Sources);
                    Assert.Contains(field.Confidence, Confidences);
                    InWindow(channelBase + IntelMemoryController.Hex(field.Offset), field.Size, $"{field.Key} canal {channel.Name}");
                    ValidBits(field.Bits, field.Size, field.Key);
                }

                foreach (var setting in family.Settings.Where(s => s.PerChannel))
                {
                    InWindow(channelBase + IntelMemoryController.Hex(setting.Offset), setting.Size, $"{setting.Key} canal {channel.Name}");
                }
            }

            foreach (var setting in family.Settings)
            {
                Assert.NotEmpty(setting.Sources);
                Assert.NotEmpty(setting.Values);
                ValidBits(setting.Bits, setting.Size, setting.Key);
                if (!setting.PerChannel)
                {
                    InWindow(IntelMemoryController.Hex(setting.Offset), setting.Size, setting.Key);
                }
            }

            foreach (var clock in family.Clocks)
            {
                Assert.NotEmpty(clock.Sources);
                Assert.True(clock.MultiplierMhz is not null || clock.ReferenceMhz.Count > 0, $"{family.Id} {clock.Key} : pas de référence");
                InWindow(IntelMemoryController.Hex(clock.Offset), clock.Size, clock.Key);
                ValidBits(clock.Bits, clock.Size, clock.Key);
                if (clock.ReferenceOffset is { } referenceOffset)
                {
                    InWindow(IntelMemoryController.Hex(referenceOffset), 4, clock.Key + " référence");
                }

                if (clock.GearBits is not null)
                {
                    Assert.NotEmpty(clock.GearFactors);
                    InWindow(IntelMemoryController.Hex(clock.GearOffset ?? clock.Offset), 4, clock.Key + " gear");
                }

                Assert.All(clock.CpuModels, m => Assert.Contains(m, family.CpuModels));
            }

            Assert.All(family.Settings.SelectMany(s => s.CpuModels), m => Assert.Contains(m, family.CpuModels));
            Assert.All(family.UncoreMsrModels, m => Assert.Contains(m, family.CpuModels));
            foreach (var field in family.Fields)
            {
                Assert.True(field.Confidence == "single" || field.Sources.Count >= 2, $"{family.Id} {field.Key} : « dual » avec une seule source");
            }
        }
    }

    [Fact]
    public void Memory_details_show_intel_timings_and_flag_only_unverified_maps()
    {
        var intel = new IntelControllerAccess(6, CoffeeLake, () => new FakeMchbar(CoffeeLakeRegisters()), () => new FakeMsr { [0x620] = 43 | (8 << 8), [0x621] = 43 });

        var report = MemoryDetails.Read(new NoSpd(), null, null, null, intel);

        Assert.NotNull(report.Intel);
        Assert.DoesNotContain(report.Notes, n => n.Contains("pas encore comparée à CPU-Z", StringComparison.Ordinal));
        Assert.Contains(report.Notes, n => n.Contains("tFAW", StringComparison.Ordinal));
        var text = MemoryDetails.ToText(report);
        Assert.Contains("Horloge mémoire 1733 MHz (3467 MT/s)", text, StringComparison.Ordinal);
        Assert.Contains("DDR4-3467", text, StringComparison.Ordinal);
        Assert.Contains("commande 2N", text, StringComparison.Ordinal);
        Assert.Contains("tRFC 607 (350.2 ns)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_intel_model_and_driver_failure_become_notes()
    {
        var unknown = MemoryDetails.Read(new NoSpd(), null, null, null, new IntelControllerAccess(6, 0x55, () => throw new InvalidOperationException(), () => null));
        Assert.Null(unknown.Intel);
        Assert.Contains(unknown.Notes, n => n.Contains("modèle 55h", StringComparison.Ordinal));

        var refused = MemoryDetails.Read(new NoSpd(), null, null, null, new IntelControllerAccess(6, CoffeeLake, () => throw new PawnIoException("Module PawnIO refusé", 50), () => null));
        Assert.Null(refused.Intel);
        Assert.Contains(refused.Notes, n => n.Contains("Module PawnIO refusé", StringComparison.Ordinal));
    }

    private sealed class FakeMchbar(Dictionary<int, ulong> registers) : IMchbarReader
    {
        public uint ReadDword(int offset) => (uint)registers.GetValueOrDefault(offset);

        public ulong ReadQword(int offset) => registers.GetValueOrDefault(offset);
    }

    private sealed class FakeMsr : IMsrReader
    {
        private readonly Dictionary<uint, ulong> _values = [];

        public List<uint> Reads { get; } = [];

        public ulong this[uint msr]
        {
            get => _values[msr];
            set => _values[msr] = value;
        }

        public ulong? Read(uint msr)
        {
            Reads.Add(msr);
            return _values.TryGetValue(msr, out var value) ? value : null;
        }
    }

    private sealed class NoSpd : ISpdSource
    {
        public IReadOnlyList<SpdImage> ReadAll() => [];
    }
}
