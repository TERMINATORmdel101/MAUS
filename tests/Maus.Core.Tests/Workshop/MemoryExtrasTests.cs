using System.Globalization;
using Maus.Core.Workshop;
using Maus.Core.Workshop.Memory;
using Microsoft.Win32;

namespace Maus.Core.Tests.Workshop;

public class MemoryExtrasTests
{
    private static readonly DateTimeOffset Monday = new(2026, 9, 28, 20, 0, 0, TimeSpan.FromHours(2));

    private static IntelMemoryReport Intel(int cl, int trfc, double dclk, string commandRate) => new(
        "Intel Core 6e à 10e génération", true, ["A", "B"], "DDR4",
        [new ImcSettingValue("CommandRate", commandRate)],
        [new ImcClockValue("DCLK", 13, dclk)],
        null, null,
        [
            new MemoryTiming("tCL", TimingGroup.Primary, cl),
            new MemoryTiming("tRCD", TimingGroup.Primary, 19),
            new MemoryTiming("tRP", TimingGroup.Primary, 19),
            new MemoryTiming("tRAS", TimingGroup.Primary, 39),
            new MemoryTiming("tRFC", TimingGroup.Secondary, trfc),
        ],
        [], false, []);

    private static MemoryDetailReport Report(IntelMemoryReport intel) => new([], null, null, [], intel);

    private static readonly IReadOnlyList<MemorySlotConfiguration> Slots =
        [new MemorySlotConfiguration("ChannelA-DIMM1", "3200 Series", "Patriot", 8L << 30, 3467, 3200, 1.25, "DDR4")];

    [Fact]
    public void Snapshot_keeps_clocks_settings_timings_and_modules_with_a_one_line_summary()
    {
        var snapshot = MemorySnapshot.From(Report(Intel(16, 607, 1733.3, "2N")), Slots, "1.452 V", Monday);

        Assert.Equal("DDR4-3467 · 16-19-19-39 · 2N", snapshot.Summary);
        Assert.Contains(snapshot.Values, v => v is { Key: "tCL", Value: "16" });
        Assert.Contains(snapshot.Values, v => v.Key == "Horloge mémoire" && v.Value == "1733 MHz");
        Assert.Contains(snapshot.Values, v => v.Key == "Tension mesurée" && v.Value == "1.452 V");
        Assert.Contains(snapshot.Values, v => v.Value == "Patriot 3200 Series");
    }

    [Fact]
    public void Comparison_lists_only_what_changed_after_a_bios_tweak()
    {
        var before = MemorySnapshot.From(Report(Intel(16, 607, 1733.3, "2N")), Slots, "1.452 V", Monday);
        var after = MemorySnapshot.From(Report(Intel(15, 560, 1733.3, "1N")), Slots, "1.452 V", Monday.AddHours(1));

        var differences = MemorySnapshot.Compare(before, after);

        Assert.Equal(3, differences.Count);
        Assert.Contains(new MemoryDifference("tCL", "16", "15"), differences);
        Assert.Contains(new MemoryDifference("tRFC", "607", "560"), differences);
        Assert.Contains("tCL : 16 → 15", MemorySnapshot.Describe(differences), StringComparison.Ordinal);
        Assert.StartsWith("Aucune différence", MemorySnapshot.Describe(MemorySnapshot.Compare(before, before)), StringComparison.Ordinal);
    }

    [Fact]
    public void Store_keeps_the_latest_snapshots_newest_first()
    {
        var directory = Path.Combine(Path.GetTempPath(), "maus-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MemorySnapshotStore(directory);
            for (var i = 0; i < MemorySnapshotStore.Keep + 3; i++)
            {
                store.Save(MemorySnapshot.From(Report(Intel(14 + (i % 3), 600, 1733.3, "2N")), Slots, null, Monday.AddMinutes(i)));
            }

            File.WriteAllText(Path.Combine(directory, "fiche-99999999-999999.json"), "{ abîmé");
            var list = store.List();

            Assert.Equal(MemorySnapshotStore.Keep, list.Count);
            Assert.Equal(Monday.AddMinutes(MemorySnapshotStore.Keep + 2), list[0].At);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Calibration_offers_only_anonymous_inputs_matching_the_bios_voltage()
    {
        IReadOnlyList<HardwareReading> readings =
        [
            new("Nuvoton NCT6797D", ReadingGroup.Motherboard, "Vcore", ReadingKind.Voltage, 1.44),
            new("Nuvoton NCT6797D", ReadingGroup.Motherboard, "Voltage #5", ReadingKind.Voltage, 0.726),
            new("Nuvoton NCT6797D", ReadingGroup.Motherboard, "Voltage #7", ReadingKind.Voltage, 1.12),
            new("Nuvoton NCT6797D", ReadingGroup.Motherboard, "Voltage #11", ReadingKind.Voltage, 1.448),
        ];

        var candidates = DramVoltageCalibrations.Candidates(readings, 1.45);

        Assert.Equal(["Voltage #11", "Voltage #5"], candidates.Select(c => c.Reading.Name).Order(StringComparer.Ordinal));
        Assert.Equal(2, candidates.Single(c => c.Reading.Name == "Voltage #5").Factor);
        Assert.Empty(DramVoltageCalibrations.Candidates(readings, 5));

        var calibration = new DramVoltageCalibration("Micro-Star International Co., Ltd. MPG Z390 GAMING PRO CARBON (MS-7B17)", "Nuvoton NCT6797D", "Voltage #5", 2, 1.45, Monday);
        Assert.Equal(1.452, DramVoltageCalibrations.Apply(readings, calibration, calibration.Board)!.Value, 3);
        Assert.Null(DramVoltageCalibrations.Apply(readings, calibration, "ASUS PRIME Z390-A"));
    }

    [Fact]
    public void Board_name_comes_from_the_bios_registry_key()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, @"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardManufacturer", "Micro-Star International Co., Ltd.")
            .Set(RegistryHive.LocalMachine, @"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardProduct", "MPG Z390 GAMING PRO CARBON (MS-7B17)");

        Assert.Equal("Micro-Star International Co., Ltd. MPG Z390 GAMING PRO CARBON (MS-7B17)", DramVoltageCalibrations.Board(registry));
        Assert.Null(DramVoltageCalibrations.Board(new FakeRegistry()));
    }

    [Fact]
    public void Recording_summarizes_extremes_and_errors_and_exports_csv()
    {
        var recording = new MonitorRecording(Monday);
        MonitorRow Row(string name, MonitorKind kind, double value) => new("Intel Core i7-8700K", name, kind, value, value, value);
        recording.Add(Monday.AddSeconds(1), [Row("CPU Package", MonitorKind.Temperature, 60), Row("CPU Package", MonitorKind.Power, 90)]);
        recording.Add(Monday.AddSeconds(2), [Row("CPU Package", MonitorKind.Temperature, 78), Row("CPU Package", MonitorKind.Power, 120)]);

        var stat = recording.Stats().Single(s => s.Kind == MonitorKind.Temperature);
        Assert.Equal((60.0, 69.0, 78.0), (stat.Min, stat.Average, stat.Max));

        var clean = recording.Summary(0, 0, 0);
        Assert.Contains("température max. 78 °C", clean, StringComparison.Ordinal);
        Assert.Contains("Aucune erreur matérielle", clean, StringComparison.Ordinal);
        Assert.Contains("dont 1 du bus PCI Express", recording.Summary(2, 1, 0), StringComparison.Ordinal);

        var csv = recording.ToCsv(CultureInfo.GetCultureInfo("fr-FR")).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, csv.Length);
        Assert.Contains("Intel Core i7-8700K · CPU Package (°C)", csv[0], StringComparison.Ordinal);
        Assert.EndsWith("120", csv[2], StringComparison.Ordinal);
    }
}
