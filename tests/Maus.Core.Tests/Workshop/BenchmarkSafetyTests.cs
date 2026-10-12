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
    public void Processor_stops_only_beyond_its_own_limit_plus_five_degrees()
    {
        Assert.Null(BenchmarkSafetyStop.Danger(new BenchmarkSensorSample { CpuTemperatureC = 100, CpuTjMaxC = 100 }));
        Assert.NotNull(BenchmarkSafetyStop.Danger(new BenchmarkSensorSample { CpuTemperatureC = 105, CpuTjMaxC = 100 }));
    }

    [Fact]
    public void Without_a_limit_given_by_the_chip_nothing_is_invented()
    {
        Assert.Null(BenchmarkSafetyStop.Danger(new BenchmarkSensorSample { GpuTemperatureC = 110, CpuTemperatureC = 120 }));
    }
}
