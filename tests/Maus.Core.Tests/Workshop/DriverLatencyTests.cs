using Maus.Core.Workshop;

namespace Maus.Core.Tests.Workshop;

public class DriverLatencyTests
{
    private static DriverLatencyAggregator Aggregator() => new(
    [
        (0xFFFFF80000000000, "ntoskrnl.exe"),
        (0xFFFFF80010000000, "nvlddmkm.sys"),
        (0xFFFFF80020000000, "HDAudBus.sys"),
    ]);

    [Fact]
    public void Routine_belongs_to_the_driver_loaded_just_below_it()
    {
        var aggregator = Aggregator();

        Assert.Equal("nvlddmkm.sys", aggregator.DriverOf(0xFFFFF80010001234));
        Assert.Equal("HDAudBus.sys", aggregator.DriverOf(0xFFFFF80020000000));
        Assert.Equal("ntoskrnl.exe", aggregator.DriverOf(0xFFFFF8000FFFFFFF));
        Assert.Equal("adresse inconnue", aggregator.DriverOf(0x1000));
    }

    [Fact]
    public void Drivers_are_ranked_by_their_longest_run()
    {
        var aggregator = Aggregator();
        aggregator.Add(0xFFFFF80010000010, 20);
        aggregator.Add(0xFFFFF80010000010, 400);
        aggregator.Add(0xFFFFF80020000010, 90);
        aggregator.Add(0xFFFFF80020000010, -5);

        var result = aggregator.Result(TimeSpan.FromSeconds(30), lostEvents: 0);

        Assert.Equal(3, result.Events);
        Assert.Equal("nvlddmkm.sys", result.Drivers[0].Driver);
        Assert.Equal(400, result.Drivers[0].MaxMicroseconds);
        Assert.Equal(420, result.Drivers[0].TotalMicroseconds);
        Assert.Contains("nvlddmkm.sys : jusqu'à 400 µs d'affilée", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Lost_events_are_reported()
    {
        var aggregator = Aggregator();
        aggregator.Add(0xFFFFF80010000010, 20);

        Assert.Contains("événements perdus", aggregator.Result(TimeSpan.FromSeconds(10), lostEvents: 12).Describe(), StringComparison.Ordinal);
    }
}
