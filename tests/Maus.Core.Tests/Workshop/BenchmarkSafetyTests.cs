using Maus.Core.Workshop;
using Maus.Core.Workshop.Benchmark;

namespace Maus.Core.Tests.Workshop;

public sealed class BenchmarkSafetyTests
{
    private static BenchmarkSensorSample Gpu(double temperature) => new() { GpuTemperatureC = temperature, GpuSlowdownC = 90 };

    [Fact]
    public void Two_hot_readings_in_a_row_stop_the_benchmark()
    {
        var safety = new BenchmarkSafetyStop();

        Assert.Null(safety.Check(Gpu(91)));
        Assert.NotNull(safety.Check(Gpu(92)));
    }

    [Fact]
    public void A_single_spike_or_the_same_reading_read_twice_does_not_stop_it()
    {
        var safety = new BenchmarkSafetyStop();
        var hot = Gpu(95);

        Assert.Null(safety.Check(hot));
        Assert.Null(safety.Check(hot));
        Assert.Null(safety.Check(Gpu(70)));
        Assert.Null(safety.Check(Gpu(95)));
    }

    [Fact]
    public void The_processor_reading_stops_at_its_limit_and_never_stops_the_benchmark()
    {
        Assert.Null(BenchmarkSafetyStop.Danger(new BenchmarkSensorSample { CpuTemperatureC = 100, CpuTjMaxC = 100 }));
    }

    [Fact]
    public void A_processor_well_beyond_its_catalog_limit_stops_the_benchmark_with_the_same_margin_as_the_alarms()
    {
        Assert.Null(BenchmarkSafetyStop.Danger(new BenchmarkSensorSample { CpuTemperatureC = 99, CpuCatalogLimitC = 95 }));
        Assert.NotNull(BenchmarkSafetyStop.Danger(new BenchmarkSensorSample { CpuTemperatureC = 100, CpuCatalogLimitC = 95 }));

        var snapshot = new SensorSnapshot { At = DateTimeOffset.Now, CpuTemperatureC = 100 };
        Assert.Contains(SensorAlarms.Check(snapshot, 95), a => a.Level == AlarmLevel.Danger);
    }

    [Fact]
    public void The_limit_given_by_the_chip_wins_over_the_catalog()
    {
        Assert.Null(BenchmarkSafetyStop.Danger(new BenchmarkSensorSample { CpuTemperatureC = 100, CpuTjMaxC = 100, CpuCatalogLimitC = 90 }));
        Assert.Equal(100, ThermalRules.CpuLimit(100, 90));
        Assert.Equal(90, ThermalRules.CpuLimit(null, 90));
        Assert.Null(ThermalRules.CpuLimit(null, null));
    }

    [Fact]
    public void A_temperature_read_with_its_limit_then_lost_stops_the_benchmark_after_the_grace_delay()
    {
        var now = TimeSpan.FromHours(1);
        var safety = new BenchmarkSafetyStop(() => now);
        Assert.Null(safety.Check(Gpu(70)));

        // Mesures sans température, puis plus de mesure du tout : la surveillance est perdue dans les deux cas.
        now += ThermalWatchdog.Grace;
        Assert.Null(safety.Check(new BenchmarkSensorSample { GpuClockMhz = 1800 }));
        now += TimeSpan.FromSeconds(1);
        var reason = safety.Check(null);
        Assert.NotNull(reason);
        Assert.Contains("n'est plus lue", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stale_reading_read_again_does_not_count_as_a_new_one()
    {
        var now = TimeSpan.FromHours(1);
        var safety = new BenchmarkSafetyStop(() => now);
        var reading = Gpu(70);
        Assert.Null(safety.Check(reading));

        now += ThermalWatchdog.Grace + TimeSpan.FromSeconds(1);
        Assert.NotNull(safety.Check(reading));
    }

    [Fact]
    public void A_processor_temperature_lost_is_named_and_new_readings_keep_the_benchmark_running()
    {
        var now = TimeSpan.FromHours(1);
        var safety = new BenchmarkSafetyStop(() => now);
        Assert.Null(safety.Check(new BenchmarkSensorSample { CpuTemperatureC = 70, CpuTjMaxC = 100 }));

        now += ThermalWatchdog.Grace;
        Assert.Null(safety.Check(new BenchmarkSensorSample { CpuTemperatureC = 71, CpuCatalogLimitC = 95 }));
        now += ThermalWatchdog.Grace;
        Assert.Null(safety.Check(null));
        now += TimeSpan.FromSeconds(1);
        Assert.Contains("processeur", safety.Check(null), StringComparison.Ordinal);
    }

    [Fact]
    public void A_temperature_never_read_with_a_limit_stops_nothing()
    {
        var now = TimeSpan.FromHours(1);
        var safety = new BenchmarkSafetyStop(() => now);
        Assert.Null(safety.Check(new BenchmarkSensorSample { GpuTemperatureC = 70, CpuTemperatureC = 60, CpuMhz = 4000 }));

        now += TimeSpan.FromHours(1);
        Assert.Null(safety.Check(null));
    }

    [Fact]
    public void Without_a_limit_given_by_the_chip_nothing_is_invented()
    {
        Assert.Null(BenchmarkSafetyStop.Danger(new BenchmarkSensorSample { GpuTemperatureC = 110, CpuTemperatureC = 120 }));
    }
}
