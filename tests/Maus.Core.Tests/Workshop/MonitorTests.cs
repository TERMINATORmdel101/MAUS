using Maus.Core.Workshop;

namespace Maus.Core.Tests.Workshop;

public class MonitorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 14, 0, 0, TimeSpan.FromHours(2));

    private static SensorSnapshot Basic(double cpuMhz, double load, double? gpuTemp = 55) => new()
    {
        At = Now,
        CpuPercent = load,
        CpuMhz = cpuMhz,
        ThermalZoneC = 40,
        MemoryUsedBytes = 8L << 30,
        MemoryTotalBytes = 16L << 30,
        Gpus = [new GpuSensor("NVIDIA GeForce RTX 3070", 30, gpuTemp, null, null, null, 120, 1905, null, null)],
    };

    [Fact]
    public void Tracks_current_min_and_max_per_component()
    {
        var tracker = new MonitorTracker();
        tracker.Update(Basic(4300, 20, gpuTemp: 55));
        tracker.Update(Basic(800, 90, gpuTemp: 71));
        var rows = tracker.Update(Basic(3700, 50, gpuTemp: 60));

        var clock = rows.Single(r => r.Name == "Fréquence moyenne");
        Assert.Equal((3700.0, 800.0, 4300.0), (clock.Value, clock.Min, clock.Max));
        var gpu = rows.Single(r => r is { Component: "NVIDIA GeForce RTX 3070", Kind: MonitorKind.Temperature });
        Assert.Equal((60.0, 55.0, 71.0), (gpu.Value, gpu.Min, gpu.Max));
        Assert.Contains(rows, r => r is { Name: "Consommation", Kind: MonitorKind.Power, Value: 120 });
        Assert.Contains(rows, r => r is { Component: "Mémoire vive", Kind: MonitorKind.Load, Value: 50 });

        tracker.Reset();
        var fresh = tracker.Update(Basic(4000, 10)).Single(r => r.Name == "Fréquence moyenne");
        Assert.Equal((4000.0, 4000.0), (fresh.Min, fresh.Max));
    }

    [Fact]
    public void Driver_readings_replace_the_approximate_cpu_summaries()
    {
        var snapshot = Basic(4300, 20) with
        {
            Readings =
            [
                new HardwareReading("Intel Core i7-8700K", ReadingGroup.Cpu, "CPU Package", ReadingKind.Temperature, 64),
                new HardwareReading("Intel Core i7-8700K", ReadingGroup.Cpu, "CPU Package", ReadingKind.Power, 88.5),
                new HardwareReading("Intel Core i7-8700K", ReadingGroup.Cpu, "Core #1", ReadingKind.Clock, 4700),
                new HardwareReading("Intel Core i7-8700K", ReadingGroup.Cpu, "CPU Total", ReadingKind.Load, 20),
                new HardwareReading("Intel Core i7-8700K", ReadingGroup.Cpu, "Vcore", ReadingKind.Voltage, 1.3),
                new HardwareReading("ASUS PRIME Z370-A", ReadingGroup.Motherboard, "Motherboard", ReadingKind.Temperature, 35),
                new HardwareReading("ASUS PRIME Z370-A", ReadingGroup.Motherboard, "CPU Fan", ReadingKind.Fan, 1200),
            ],
        };

        var rows = new MonitorTracker().Update(snapshot);

        Assert.DoesNotContain(rows, r => r.Name is "Fréquence moyenne" or "Zone thermique ACPI (approximative)");
        Assert.Contains(rows, r => r is { Component: "Intel Core i7-8700K", Name: "CPU Package", Kind: MonitorKind.Temperature, Value: 64 });
        Assert.Contains(rows, r => r is { Component: "Intel Core i7-8700K", Name: "Core #1", Kind: MonitorKind.Clock });
        Assert.Single(rows, r => r.Component == "Intel Core i7-8700K" && r.Kind == MonitorKind.Load);
        Assert.DoesNotContain(rows, r => r.Name is "Vcore" or "CPU Fan");
        Assert.Contains(rows, r => r is { Component: "ASUS PRIME Z370-A", Kind: MonitorKind.Temperature, Value: 35 });
    }

    [Fact]
    public void Error_watch_counts_whea_pcie_and_windows_errors_since_opening()
    {
        var since = new DateTime(2026, 9, 26, 14, 0, 0);
        var logs = new FakeEventLogs()
            .Add("System", "Microsoft-Windows-WHEA-Logger", 17, since.AddMinutes(1), level: 3,
                message: "A corrected hardware error has occurred. Component: PCI Express Root Port")
            .Add("System", "Microsoft-Windows-WHEA-Logger", 19, since.AddMinutes(2), level: 3, message: "Component: Processor Core")
            .Add("System", "Microsoft-Windows-WHEA-Logger", 17, since.AddMinutes(-5), level: 3, message: "PCI Express, before opening")
            .Add("System", "Microsoft-Windows-WHEA-Logger", 18, since.AddMinutes(3), level: 2, message: "Fatal: Processor Core")
            .Add("System", "Service Control Manager", 7031, since.AddMinutes(4), level: 2)
            .Add("Application", "Application Error", 1000, since.AddMinutes(5), level: 2)
            .Add("Application", "Some App", 1, since.AddMinutes(6), level: 3);

        var result = ErrorWatch.Read(logs, since);

        Assert.Equal(3, result.Hardware);
        Assert.Equal(1, result.PciExpress);
        Assert.Equal(2, result.Windows);
        Assert.False(result.Incomplete);
        Assert.Equal("Application Error", result.Events[0].Provider);
        Assert.Single(result.Events, e => e.Id == 18);
    }

    [Fact]
    public void Tail_reads_only_what_is_new_and_adds_up_the_counters()
    {
        var since = new DateTime(2026, 9, 26, 14, 0, 0);
        var logs = new FakeEventLogs()
            .Add("System", "Microsoft-Windows-WHEA-Logger", 17, since.AddMinutes(1), level: 3, message: "Component: PCI Express Root Port")
            .Add("System", "Microsoft-Windows-WHEA-Logger", 17, since.AddMinutes(-5), level: 3, message: "before opening")
            .Add("Application", "Some App", 1, since.AddMinutes(2), level: 3)
            .Add("Application", "Application Error", 1000, since.AddMinutes(3), level: 2);
        var tail = new ErrorWatchTail(logs, since);

        var first = tail.Poll();

        Assert.Equal((1, 1, 1), (first.Hardware, first.PciExpress, first.Windows));
        Assert.Equal(["Application Error", "Microsoft-Windows-WHEA-Logger"], first.Events.Select(e => e.Provider));

        var nothing = tail.Poll();

        Assert.Equal((1, 1, 1), (nothing.Hardware, nothing.PciExpress, nothing.Windows));
        Assert.Empty(nothing.Events);

        logs.Add("System", "Service Control Manager", 7031, since.AddMinutes(4), level: 2)
            .Add("System", "Microsoft-Windows-WHEA-Logger", 18, since.AddMinutes(5), level: 2, message: "Fatal: Processor Core");

        var next = tail.Poll();

        Assert.Equal((2, 1, 2), (next.Hardware, next.PciExpress, next.Windows));
        Assert.Equal([18, 7031], next.Events.Select(e => e.Id));
    }

    [Fact]
    public void Tail_counters_are_not_capped_by_the_displayed_list()
    {
        var since = new DateTime(2026, 9, 26, 14, 0, 0);
        var logs = new FakeEventLogs();
        for (var i = 0; i < ErrorWatch.MaxEvents + 50; i++)
        {
            logs.Add("Application", "Application Error", 1000, since.AddSeconds(i), level: 2);
        }

        Assert.Equal(ErrorWatch.MaxEvents + 50, new ErrorWatchTail(logs, since).Poll().Windows);
    }

    [Fact]
    public void Tail_says_when_a_log_cannot_be_read()
    {
        var result = new ErrorWatchTail(new FakeEventLogs().Deny("Application"), DateTime.Now.AddMinutes(-1)).Poll();

        Assert.True(result.Incomplete);
    }

    [Fact]
    public void Unreadable_log_is_reported_as_incomplete()
    {
        var result = ErrorWatch.Read(new FakeEventLogs().Deny("Application"), DateTime.Now.AddMinutes(-1));

        Assert.True(result.Incomplete);
        Assert.Equal(0, result.Windows);
    }

    [Fact]
    public void Fatal_and_corrected_whea_errors_are_both_read_despite_the_query_limit()
    {
        var since = new DateTime(2026, 5, 1, 12, 0, 0);
        var logs = new FakeEventLogs()
            .Add("System", "Microsoft-Windows-WHEA-Logger", 2, since.AddMinutes(1), level: 3, message: "corrigée")
            .Add("System", "Microsoft-Windows-WHEA-Logger", 1, since.AddMinutes(2), message: "irrécupérable");

        var result = ErrorWatch.Read(logs, since);

        Assert.False(result.Incomplete);
        Assert.Equal(2, result.Hardware);
        Assert.Equal(2, WheaEvents.CountSince(logs, since));
    }
}
