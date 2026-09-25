using Maus.Core.Workshop;
using Microsoft.Win32;

namespace Maus.Core.Tests.Workshop;

public class AdvancedSensorsTests
{
    private static HardwareReading Cpu(string name, ReadingKind kind, double value) => new("AMD Ryzen 7 7800X3D", ReadingGroup.Cpu, name, kind, value);

    private sealed class FixedBasic : ISensorSource
    {
        public bool Disposed { get; private set; }

        public SensorSnapshot Sample() => new() { At = DateTimeOffset.Now, CpuPercent = 12 };

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeAdvanced(Func<IReadOnlyList<HardwareReading>> read) : IAdvancedSensors
    {
        public IReadOnlyList<HardwareReading> Read() => read();

        public void Dispose()
        {
        }
    }

    [Fact]
    public void Amd_names_give_temperature_power_and_voltage()
    {
        IReadOnlyList<HardwareReading> readings =
        [
            Cpu("Core #1", ReadingKind.Temperature, 60),
            Cpu("Core (Tctl/Tdie)", ReadingKind.Temperature, 71.5),
            Cpu("Package", ReadingKind.Power, 88),
            Cpu("Core #1 VID", ReadingKind.Voltage, 1.25),
            Cpu("Core (SVI3 TFN)", ReadingKind.Voltage, 1.18),
            new("Nuvoton NCT6799D", ReadingGroup.Motherboard, "CPU Core", ReadingKind.Voltage, 1.3),
        ];

        Assert.Equal(71.5, AdvancedReadings.CpuTemperature(readings));
        Assert.Equal(88, AdvancedReadings.CpuPower(readings));
        Assert.Equal(1.18, AdvancedReadings.CpuVoltage(readings));
    }

    [Fact]
    public void Intel_package_and_highest_vid_are_used_as_fallbacks()
    {
        IReadOnlyList<HardwareReading> readings =
        [
            Cpu("CPU Core #1", ReadingKind.Temperature, 55),
            Cpu("CPU Package", ReadingKind.Temperature, 64),
            Cpu("CPU Core #1 VID", ReadingKind.Voltage, 1.1),
            Cpu("CPU Core #2 VID", ReadingKind.Voltage, 1.2),
        ];

        Assert.Equal(64, AdvancedReadings.CpuTemperature(readings));
        Assert.Equal(1.2, AdvancedReadings.CpuVoltage(readings));
        Assert.Null(AdvancedReadings.CpuPower(readings));
        Assert.Null(AdvancedReadings.CpuTemperature([]));
    }

    [Fact]
    public void Combined_source_adds_driver_readings_and_survives_a_failing_driver()
    {
        using var ok = new CombinedSensorSource(new FixedBasic(), new FakeAdvanced(() => [Cpu("Core (Tctl/Tdie)", ReadingKind.Temperature, 70)]));
        var snapshot = ok.Sample();
        Assert.Equal(70, snapshot.CpuTemperatureC);
        Assert.Equal(12, snapshot.CpuPercent);
        Assert.Single(snapshot.Readings);

        using var broken = new CombinedSensorSource(new FixedBasic(), new FakeAdvanced(() => throw new InvalidOperationException("pilote retiré")));
        var fallback = broken.Sample();
        Assert.Null(fallback.CpuTemperatureC);
        Assert.Equal(12, fallback.CpuPercent);
    }

    [Theory]
    [InlineData(80, null)]
    [InlineData(93, AlarmLevel.Attention)]
    [InlineData(95, AlarmLevel.Attention)]
    [InlineData(101, AlarmLevel.Danger)]
    public void Cpu_at_its_limit_is_attention_beyond_it_danger(double temperature, AlarmLevel? expected)
    {
        var alarms = SensorAlarms.Check(new SensorSnapshot { At = DateTimeOffset.Now, CpuTemperatureC = temperature }, cpuMaxC: 95);

        Assert.Equal(expected, alarms.Select(a => (AlarmLevel?)a.Level).FirstOrDefault());
    }

    [Fact]
    public void PawnIo_state_is_read_from_installed_programs()
    {
        Assert.False(PawnIo.State(new FakeRegistry()).Installed);

        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, PawnIo.UninstallKey, "DisplayVersion", "2.2.0");
        Assert.Equal((true, "2.2.0"), PawnIo.State(registry));
    }

    [Fact]
    public void PawnIo_is_installed_and_removed_by_winget_in_a_visible_window()
    {
        const string winget = @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.24_x64__8wekyb3d8bbwe\winget.exe";

        Assert.Contains($"\"{winget}\" install --id namazso.PawnIO --exact --source winget", PawnIo.InstallConsoleArguments(winget), StringComparison.Ordinal);
        Assert.Contains($"\"{winget}\" uninstall --id namazso.PawnIO --exact --source winget", PawnIo.UninstallConsoleArguments(winget), StringComparison.Ordinal);
        Assert.StartsWith("/s /k \"", PawnIo.InstallConsoleArguments(winget), StringComparison.Ordinal);
    }
}
