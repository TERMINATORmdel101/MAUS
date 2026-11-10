using Maus.Core.Workshop;

namespace Maus.Core.Tests.Workshop;

public class SensorTests
{
    private static SensorSnapshot Snapshot(double cpu, double? gpuTemperature = null, int? slowdown = null, double? thermal = null, long used = 8, long total = 16) => new()
    {
        At = DateTimeOffset.Now,
        CpuPercent = cpu,
        MemoryUsedBytes = used,
        MemoryTotalBytes = total,
        ThermalZoneC = thermal,
        Gpus = gpuTemperature is null ? [] : [new GpuSensor("RTX 4070", 50, gpuTemperature, 1500, 40, 60, 120, 2500, 1L << 30, slowdown)],
    };

    [Fact]
    public void History_keeps_only_the_most_recent_samples()
    {
        var history = new SensorHistory(capacity: 3);
        foreach (var cpu in new double[] { 10, 20, 30, 40 })
        {
            history.Add(Snapshot(cpu));
        }

        Assert.Equal([20, 30, 40], history.Series(s => s.CpuPercent));
        Assert.Equal(40, history.Latest!.CpuPercent);
    }

    [Fact]
    public void Gpu_alarms_follow_the_card_thresholds()
    {
        Assert.Empty(SensorAlarms.Check(Snapshot(10, gpuTemperature: 70, slowdown: 90)));
        Assert.Equal(AlarmLevel.Attention, SensorAlarms.Check(Snapshot(10, gpuTemperature: 86, slowdown: 90)).Single().Level);
        Assert.Equal(AlarmLevel.Danger, SensorAlarms.Check(Snapshot(10, gpuTemperature: 91, slowdown: 90)).Single().Level);

        // Carte qui ne donne pas son seuil : aucun seuil commun publié, donc aucune alarme inventée (principe 7).
        Assert.Empty(SensorAlarms.Check(Snapshot(10, gpuTemperature: 92)));
    }

    [Fact]
    public void Full_memory_raises_an_alarm_but_the_acpi_thermal_zone_does_not()
    {
        var alarms = SensorAlarms.Check(Snapshot(10, thermal: 97, used: 99, total: 100));

        // Zone thermique ACPI : aucun seuil publié commun à toutes les cartes mères, donc pas d'alarme (principe 7).
        Assert.DoesNotContain(alarms, a => a.Id == "thermal-zone");
        Assert.Contains(alarms, a => a.Id == "memory-full");
    }

    [Fact]
    public void Graphics_adapters_are_read_without_error()
    {
        // Lecture seule du matériel réel : les deux lecteurs passent par la même énumération, et rien n'y lève d'erreur.
        Assert.All(GpuPerformance.Read(), gpu => Assert.False(string.IsNullOrEmpty(gpu.Name)));
        Assert.All(new Maus.Core.Modules.M09Gpu.D3dkmtGpuSchedulingReader().Read() ?? [], gpu => Assert.True(gpu.VendorId >= 0));
    }

    private static GpuSensor Card(double? load) => new("Carte", load, null, null, null, null, null, null, null, null);

    [Fact]
    public void Engine_counter_only_serves_a_single_card_without_load_from_nvml()
    {
        Assert.True(WindowsSensorSource.NeedsGpuEngineCounter([Card(null)]));
        Assert.False(WindowsSensorSource.NeedsGpuEngineCounter([Card(40)]));
        Assert.False(WindowsSensorSource.NeedsGpuEngineCounter([Card(null), Card(null)]));
        Assert.False(WindowsSensorSource.NeedsGpuEngineCounter([]));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(9, true)]
    [InlineData(15, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    public void Only_a_lost_opening_makes_nvml_reopen(int status, bool lost) => Assert.Equal(lost, LiveNvmlSource.LosesOpening(status));

    [Fact]
    public void Live_nvml_reading_keeps_the_values_of_the_full_sheet()
    {
        // Lecture seule ; sur un PC sans carte NVIDIA, les deux rendent la même absence.
        using var live = new LiveNvmlSource();
        var full = new WindowsNvmlSource().Read();
        var first = live.Read();
        var second = live.Read();

        Assert.Equal(full?.Select(g => g.Name), first?.Select(g => g.Name));
        Assert.Equal(full?.Select(g => g.SlowdownTemperatureC), second?.Select(g => g.SlowdownTemperatureC));
        Assert.Equal(full?.Select(g => g.TemperatureC is null), second?.Select(g => g.TemperatureC is null));
        Assert.Equal(full?.Select(g => g.MemoryUsedBytes is null), second?.Select(g => g.MemoryUsedBytes is null));

        live.Dispose();
        Assert.Null(live.Read());
    }
}
