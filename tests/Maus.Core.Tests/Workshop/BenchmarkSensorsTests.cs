using Maus.Core.Workshop.Benchmark;

namespace Maus.Core.Tests.Workshop;

public sealed class BenchmarkSensorsTests
{
    [Fact]
    public void Summary_of_a_graphics_test_keeps_the_peak_the_averages_and_the_share_of_each_slowdown()
    {
        BenchmarkSensorSample[] samples =
        [
            new() { GpuTemperatureC = 70, GpuClockMhz = 1900, GpuPowerWatts = 250, GpuClockReasons = NvidiaClockReasons.SwPowerCap, GpuSlowdownC = 89 },
            new() { GpuTemperatureC = 76, GpuClockMhz = 1800, GpuPowerWatts = 240, GpuClockReasons = NvidiaClockReasons.SwPowerCap | NvidiaClockReasons.SwThermalSlowdown, GpuSlowdownC = 89 },
            new() { GpuTemperatureC = 74, GpuClockMhz = 1850, GpuPowerWatts = 245, GpuClockReasons = 0, GpuSlowdownC = 89 },
            new() { GpuTemperatureC = 73, GpuClockMhz = 1850, GpuPowerWatts = 245, GpuClockReasons = NvidiaClockReasons.SwPowerCap, GpuSlowdownC = 89 },
        ];

        var summary = BenchmarkSensorSummary.Summarize(samples, "gpu")!;

        Assert.Equal(4, summary.Samples);
        Assert.Equal(76, summary.MaxTemperatureC);
        Assert.Equal(1850, summary.AverageClockMhz);
        Assert.Equal(245, summary.AveragePowerWatts);
        Assert.Equal(89, summary.LimitTemperatureC);
        Assert.Equal(25, summary.ThermalSlowdownPercent);
        Assert.Equal(75, summary.PowerLimitPercent);
        Assert.Equal(0, summary.HardwareBrakePercent);
    }

    [Fact]
    public void Nothing_readable_gives_no_summary_and_a_card_without_nvml_gives_no_slowdown_share()
    {
        Assert.Null(BenchmarkSensorSummary.Summarize([new BenchmarkSensorSample(), new BenchmarkSensorSample()], "gpu"));
        Assert.Null(BenchmarkSensorSummary.Summarize([], "cpu"));

        var other = BenchmarkSensorSummary.Summarize([new() { GpuTemperatureC = 65 }], "gpu")!;
        Assert.Equal(65, other.MaxTemperatureC);
        Assert.Null(other.ThermalSlowdownPercent);
        Assert.Null(other.AverageClockMhz);
    }

    [Fact]
    public void Components_merge_their_tests_weighted_by_the_number_of_samples()
    {
        var first = new BenchmarkSensorSummary { Samples = 30, MaxTemperatureC = 70, AverageClockMhz = 1900, ThermalSlowdownPercent = 0 };
        var second = new BenchmarkSensorSummary { Samples = 90, MaxTemperatureC = 80, AverageClockMhz = 1700, ThermalSlowdownPercent = 20 };

        var merged = BenchmarkSensorSummary.Merge([first, null, second])!;

        Assert.Equal(120, merged.Samples);
        Assert.Equal(80, merged.MaxTemperatureC);
        Assert.Equal(1750, merged.AverageClockMhz);
        Assert.Equal(15, merged.ThermalSlowdownPercent);
        Assert.Null(BenchmarkSensorSummary.Merge([null, null]));
    }

    [Fact]
    public void Warnings_only_come_from_what_the_chip_reports()
    {
        // Limite de puissance seule : normal à pleine charge, aucun avertissement.
        Assert.Null(BenchmarkSensorText.Warning(new BenchmarkSensorSummary { Samples = 10, MaxTemperatureC = 83, PowerLimitPercent = 100, ThermalSlowdownPercent = 0 }, "gpu"));
        Assert.Contains("chaleur", BenchmarkSensorText.Warning(new BenchmarkSensorSummary { Samples = 10, ThermalSlowdownPercent = 12.5 }, "gpu"), StringComparison.Ordinal);

        // Processeur : seulement si la limite lue dans la puce est atteinte ; sans limite connue, rien n'est supposé.
        Assert.Null(BenchmarkSensorText.Warning(new BenchmarkSensorSummary { Samples = 10, MaxTemperatureC = 99, LimitTemperatureC = 100 }, "cpu"));
        Assert.Null(BenchmarkSensorText.Warning(new BenchmarkSensorSummary { Samples = 10, MaxTemperatureC = 105 }, "cpu"));
        Assert.Contains("100 °C", BenchmarkSensorText.Warning(new BenchmarkSensorSummary { Samples = 10, MaxTemperatureC = 100, LimitTemperatureC = 100 }, "cpu"), StringComparison.Ordinal);
    }

    [Fact]
    public void Descriptions_show_only_what_was_read()
    {
        Assert.Equal("4,62 GHz en moyenne", BenchmarkSensorText.Describe(new BenchmarkSensorSummary { Samples = 5, AverageClockMhz = 4620 }, "cpu"));
        Assert.Equal("76 °C au plus · 1880 MHz en moyenne · 245 W en moyenne",
            BenchmarkSensorText.Describe(new BenchmarkSensorSummary { Samples = 5, MaxTemperatureC = 76, AverageClockMhz = 1880, AveragePowerWatts = 245 }, "gpu"));
        Assert.Null(BenchmarkSensorText.Describe(null, "gpu"));
        Assert.Equal("72 °C  ·  1905 MHz  ·  243 W", BenchmarkSensorText.Live(new BenchmarkSensorSample { GpuTemperatureC = 72, GpuClockMhz = 1905, GpuPowerWatts = 243 }, "gpu"));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 2080 Ti", "NVIDIA GeForce RTX 2080 Ti", true)]
    [InlineData("NVIDIA GeForce RTX 2080 Ti", "GeForce RTX 2080 Ti", true)]
    [InlineData("AMD Radeon RX 7800 XT", "NVIDIA GeForce RTX 2080 Ti", false)]
    [InlineData("", "NVIDIA GeForce RTX 2080 Ti", false)]
    public void The_benchmark_card_is_recognised_by_name(string a, string b, bool same) =>
        Assert.Equal(same, WindowsBenchmarkSensors.SameGpu(a, b));

    [Fact]
    public void Sensor_summaries_survive_the_history_file()
    {
        var report = new BenchmarkReport
        {
            Tests = [new BenchmarkTestResult("ring", "gpu", "geometry", 30, "images par seconde", 9000) { Sensors = new BenchmarkSensorSummary { Samples = 3, MaxTemperatureC = 71 } }],
        };

        var copy = BenchmarkHistoryStore.Deserialize(BenchmarkHistoryStore.Serialize(report))!;

        Assert.Equal(71, copy.Tests[0].Sensors!.MaxTemperatureC);
        Assert.Null(BenchmarkHistoryStore.Deserialize("""{"tests":[{"id":"ring","device":"gpu","capability":"geometry","value":30,"unit":"x","score":9000}]}""")!.Tests[0].Sensors);
    }

    [Fact]
    public void The_sampler_records_only_during_a_segment()
    {
        var reader = new CountingReader();
        using (var sampler = new BenchmarkSensorSampler(() => reader, TimeSpan.FromMilliseconds(5)))
        {
            SpinWait.SpinUntil(() => sampler.Latest is not null, TimeSpan.FromSeconds(5));
            sampler.BeginSegment();
            SpinWait.SpinUntil(() => reader.Reads > 10, TimeSpan.FromSeconds(5));
            var summary = sampler.EndSegment("gpu");
            Assert.NotNull(summary);
            Assert.True(summary.Samples > 0);
            Assert.Equal(60, summary.MaxTemperatureC);
        }

        Assert.True(reader.Disposed);
    }

    private sealed class CountingReader : IBenchmarkSensorReader
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public bool Disposed { get; private set; }

        public BenchmarkSensorSample Read()
        {
            Interlocked.Increment(ref _reads);
            return new BenchmarkSensorSample { GpuTemperatureC = 60 };
        }

        public void Dispose() => Disposed = true;
    }
}

public sealed class BenchmarkImagesTests
{
    [Fact]
    public void Images_of_runs_that_left_the_history_are_deleted_and_nothing_else()
    {
        var folder = Path.Combine(Path.GetTempPath(), "maus-bench-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new BenchmarkHistoryStore(Path.Combine(folder, "benchmark-history.json"));
            Directory.CreateDirectory(store.ImagesFolder);
            var date = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
            var kept = store.ImagePathFor(date);
            var old = Path.Combine(store.ImagesFolder, "MAUS-benchmark-20260101-000000.png");
            var other = Path.Combine(store.ImagesFolder, "photo.png");
            foreach (var file in new[] { kept, old, other })
            {
                File.WriteAllText(file, "png");
            }

            store.Add(new BenchmarkReport { Date = date, Image = kept, Tests = [new BenchmarkTestResult("ring", "gpu", "geometry", 30, "x", 9000)] });
            store.PruneImages();

            Assert.StartsWith("MAUS-benchmark-", Path.GetFileName(kept), StringComparison.Ordinal);
            Assert.True(File.Exists(kept));
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(other));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
