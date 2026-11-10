using Maus.Core.Engine.Broker;
using Maus.Core.Preferences;
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
        Assert.Contains("non identifié", new DriverLatencyAggregator([(0UL, "a.sys")]).DriverOf(0x1000), StringComparison.Ordinal);
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

    private sealed class WaitingTracer : IDriverLatencyTracer
    {
        public bool Closed { get; private set; }

        public async Task<DriverLatencyResult> MeasureAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(duration, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Comme la vraie mesure : arrêtée, elle rend ce qu'elle a vu, après avoir refermé sa session.
            }

            await Task.Delay(100, CancellationToken.None);
            Closed = true;
            return new DriverLatencyAggregator([]).Result(TimeSpan.Zero, lostEvents: 0);
        }
    }

    [Fact]
    public void Closing_the_engine_stops_the_running_measure_and_waits_for_it()
    {
        var tracer = new WaitingTracer();
        var engine = new LocalMausEngine(preferences: new MemoryPreferencesStore(), journal: new InMemoryJournalStore()) { LatencyTracer = () => tracer };

        var run = engine.MeasureLatencyAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
        engine.Dispose();

        Assert.True(tracer.Closed);
        Assert.True(run.IsCompletedSuccessfully);
    }

    private static readonly DateTime Boot = new(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_session_is_ours_to_close_only_when_its_engine_is_gone_since_this_boot()
    {
        var marker = new LatencySessionMarker(1234, Boot.AddHours(1), Boot);

        Assert.True(marker.IsLeftover(Boot.AddSeconds(30), ownerRunning: false));
        Assert.False(marker.IsLeftover(Boot, ownerRunning: true));
        Assert.False(marker.IsLeftover(Boot.AddHours(3), ownerRunning: false));
        Assert.True(marker.IsFromEarlierBoot(Boot.AddHours(3)));
        Assert.False(marker.IsFromEarlierBoot(Boot.AddSeconds(-30)));
    }

    [Fact]
    public void A_session_with_other_settings_belongs_to_another_tool()
    {
        const uint Flags = 0x60;
        const uint RealTime = 0x100;

        Assert.True(LatencySessionMarker.LooksLikeOurSession(Flags, RealTime, Flags, RealTime));
        Assert.False(LatencySessionMarker.LooksLikeOurSession(Flags | 0x1, RealTime, Flags, RealTime));
        Assert.False(LatencySessionMarker.LooksLikeOurSession(Flags, 0, Flags, RealTime));
    }

    [Fact]
    public void Marker_comes_back_from_disk_and_a_damaged_one_is_not_believed()
    {
        var folder = Directory.CreateTempSubdirectory("maus-latence-").FullName;
        try
        {
            Assert.Null(LatencySessionMarker.Read(folder));

            var marker = new LatencySessionMarker(1234, Boot.AddHours(1), Boot);
            marker.Write(folder);
            Assert.Equal(marker, LatencySessionMarker.Read(folder));

            LatencySessionMarker.Delete(folder);
            Assert.Null(LatencySessionMarker.Read(folder));
            Assert.Null(LatencySessionMarker.Parse("1234\nvingt\n3"));
            Assert.Null(LatencySessionMarker.Parse("1234"));
            Assert.Null(LatencySessionMarker.Parse("1\n" + long.MaxValue + "\n3"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_vanished_process_is_not_running_and_this_one_is()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();

        Assert.True(LatencySessionMarker.IsRunning(self.Id, self.StartTime.ToUniversalTime()));
        Assert.False(LatencySessionMarker.IsRunning(self.Id, self.StartTime.ToUniversalTime().AddHours(-1)));
        Assert.False(LatencySessionMarker.IsRunning(int.MaxValue, Boot));
    }
}
