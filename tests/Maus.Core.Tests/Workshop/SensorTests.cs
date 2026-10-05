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
        Assert.Equal(40, history.Max(s => s.CpuPercent));
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
}
