using Maus.Core.Workshop;

namespace Maus.Core.Tests.Workshop;

/// <summary>
/// Chez le porteur, une fois PawnIO installé, les mesures en direct restaient vides : une lecture du pilote qui traîne
/// ne doit plus jamais retenir les mesures sans pilote, ni laisser une valeur figée passer pour actuelle.
/// </summary>
public class CombinedSensorSourceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 9, 0, 0, TimeSpan.FromHours(2));

    private static HardwareReading CpuTemperature(double value) => new("Intel Core i7-8700K", ReadingGroup.Cpu, "CPU Package", ReadingKind.Temperature, value);

    [Fact]
    public void Driver_opening_in_the_background_does_not_delay_the_basic_readings()
    {
        using var release = new ManualResetEventSlim();
        var basic = new ClockBasic();
        using var source = new CombinedSensorSource(basic, () =>
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return new Advanced(() => [CpuTemperature(55)]);
        }, TimeSpan.FromMilliseconds(500));

        var first = source.Sample();
        Assert.True(source.IsOpening);
        Assert.Equal(12, first.CpuPercent);
        Assert.Empty(first.Readings);

        release.Set();
        SpinWait.SpinUntil(() => !source.IsOpening, TimeSpan.FromSeconds(5));
        basic.At = Start.AddSeconds(1);
        Assert.Equal(55, source.Sample().CpuTemperatureC);
    }

    [Fact]
    public void Slow_driver_read_lets_basic_readings_through_and_old_values_expire()
    {
        using var hang = new ManualResetEventSlim(true);
        var basic = new ClockBasic();
        using var source = new CombinedSensorSource(basic, new Advanced(() =>
        {
            hang.Wait(TimeSpan.FromSeconds(10));
            return [CpuTemperature(60)];
        }), TimeSpan.FromMilliseconds(200));

        Assert.Equal(60, source.Sample().CpuTemperatureC);

        // Le pilote ne répond plus : la mesure suivante sort quand même, avec la dernière valeur tant qu'elle est récente.
        hang.Reset();
        basic.At = Start.AddSeconds(1);
        var slow = source.Sample();
        Assert.Equal(12, slow.CpuPercent);
        Assert.Equal(60, slow.CpuTemperatureC);
        Assert.False(source.IsStalled);

        // Trop ancienne : plus affichée, et le blocage est signalé.
        basic.At = Start.AddSeconds(1) + CombinedSensorSource.MaxAge + TimeSpan.FromSeconds(1);
        var stale = source.Sample();
        Assert.Null(stale.CpuTemperatureC);
        Assert.Empty(stale.Readings);
        Assert.True(source.IsStalled);

        hang.Set();
    }

    [Fact]
    public void Driver_that_cannot_open_is_reported_and_basic_readings_continue()
    {
        using var source = new CombinedSensorSource(new ClockBasic(), () => throw new InvalidOperationException("module refusé"));

        SpinWait.SpinUntil(() => !source.IsOpening, TimeSpan.FromSeconds(5));
        Assert.Equal(12, source.Sample().CpuPercent);
        Assert.Equal("module refusé", source.Failure);
    }

    [Fact]
    public void Memory_voltage_is_only_taken_from_a_named_motherboard_sensor()
    {
        IReadOnlyList<HardwareReading> named =
        [
            new("Nuvoton NCT6798D", ReadingGroup.Motherboard, "Vcore", ReadingKind.Voltage, 1.3),
            new("Nuvoton NCT6798D", ReadingGroup.Motherboard, "DIMM", ReadingKind.Voltage, 1.452),
        ];
        Assert.Equal(1.452, AdvancedReadings.DramVoltage(named)?.Value);

        // Carte non décrite : entrées anonymes, rien n'est deviné.
        IReadOnlyList<HardwareReading> anonymous =
        [
            new("Nuvoton NCT6797D", ReadingGroup.Motherboard, "Voltage #5", ReadingKind.Voltage, 1.45),
            new("Intel Core i7-8700K", ReadingGroup.Cpu, "DRAM", ReadingKind.Voltage, 1.45),
        ];
        Assert.Null(AdvancedReadings.DramVoltage(anonymous));
    }

    private sealed class ClockBasic : ISensorSource
    {
        public DateTimeOffset At { get; set; } = Start;

        public SensorSnapshot Sample() => new() { At = At, CpuPercent = 12 };

        public void Dispose()
        {
        }
    }

    private sealed class Advanced(Func<IReadOnlyList<HardwareReading>> read) : IAdvancedSensors
    {
        public IReadOnlyList<HardwareReading> Read() => read();

        public void Dispose()
        {
        }
    }
}
