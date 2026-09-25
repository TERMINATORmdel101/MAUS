using Maus.Core.Hardware;
using Maus.Core.Modules.M16Workshop;
using Maus.Core.Platform;
using Maus.Core.Tests.Workshop;
using Maus.Core.Workshop;

namespace Maus.Core.Tests.Modules.M16Workshop;

public class WorkshopModuleTests
{
    private static Dictionary<string, object?> Dimm(uint millivolts, uint type = 34) => new()
    {
        ["DeviceLocator"] = "DIMM_A2", ["Capacity"] = 17179869184L, ["SMBIOSMemoryType"] = type, ["ConfiguredVoltage"] = millivolts,
    };

    private static Task<IReadOnlyList<Finding>> Run(FakeCim cim, HardwareProfile? hardware = null, params NvidiaGpuState[] nvidia) =>
        new WorkshopModule(new FakeCpuId(), new FakeNvml(nvidia)).DetectAsync(TestContext.Create(cim: cim, hardware: hardware), CancellationToken.None);

    [Theory]
    [InlineData(1350u, FindingStatus.Ok)]
    [InlineData(1450u, FindingStatus.Warning)]
    [InlineData(1550u, FindingStatus.Problem)]
    public async Task Ddr5_voltage_is_classified_against_the_safety_limits(uint millivolts, FindingStatus expected)
    {
        var findings = await Run(new FakeCim().Answer(HardwareInventoryReader.MemoryQuery, Dimm(millivolts)));

        var voltage = findings.Single(f => f.Id == "M16.memory-voltage");
        Assert.Equal(expected, voltage.Status);
        Assert.Contains("DDR5", voltage.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ddr4_at_xmp_voltage_is_normal()
    {
        var findings = await Run(new FakeCim().Answer(HardwareInventoryReader.MemoryQuery, Dimm(1350, type: 26)));

        Assert.Equal(FindingStatus.Ok, findings.Single(f => f.Id == "M16.memory-voltage").Status);
    }

    [Fact]
    public async Task Hot_idle_nvidia_card_and_raised_power_limit_are_reported()
    {
        var hardware = new HardwareProfile
        {
            FormFactor = FormFactor.Desktop,
            Gpus = [new GpuInfo("NVIDIA GeForce RTX 4070", HardwareVendor.Nvidia, null, null, @"PCI\VEN_10DE&DEV_2786", false)],
        };
        var state = new NvidiaGpuState
        {
            Name = "NVIDIA GeForce RTX 4070",
            TemperatureC = 82,
            SlowdownTemperatureC = 90,
            ShutdownTemperatureC = 95,
            UtilizationPercent = 3,
            PowerLimitWatts = 240,
            DefaultPowerLimitWatts = 200,
            MaxPowerLimitWatts = 240,
        };

        var findings = await Run(new FakeCim(), hardware, state);

        var temperature = findings.Single(f => f.Id == "M16.gpu-temperature.0");
        Assert.Equal(FindingStatus.Warning, temperature.Status);
        Assert.Contains("90", temperature.Expected, StringComparison.Ordinal);
        var power = findings.Single(f => f.Id == "M16.gpu-power.0");
        Assert.Contains("20", power.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Worn_battery_and_disabled_virtualization_are_reported()
    {
        var cim = new FakeCim()
            .Answer(HardwareInventoryReader.BatteryStaticQuery, CimScopes.Wmi, new Dictionary<string, object?> { ["InstanceName"] = "B0", ["DesignedCapacity"] = 50000u })
            .Answer(HardwareInventoryReader.BatteryFullQuery, CimScopes.Wmi, new Dictionary<string, object?> { ["InstanceName"] = "B0", ["FullChargedCapacity"] = 35000u })
            .Answer(WorkshopModule.VirtualizationQuery, new Dictionary<string, object?> { ["VirtualizationFirmwareEnabled"] = false })
            .Answer(WorkshopModule.HypervisorQuery, new Dictionary<string, object?> { ["HypervisorPresent"] = false });

        var findings = await Run(cim);

        Assert.Equal(FindingStatus.Warning, findings.Single(f => f.Id == "M16.battery.0").Status);
        Assert.Equal("désactivée dans le BIOS", findings.Single(f => f.Id == "M16.virtualization").Current);
        Assert.Equal("13th Gen Intel(R) Core(TM) i7-13700K", findings.Single(f => f.Id == "M16.cpu").Title);
        Assert.Contains("100 °C", findings.Single(f => f.Id == "M16.cpu").Expected, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hypervisor_means_virtualization_is_on()
    {
        var cim = new FakeCim()
            .Answer(WorkshopModule.VirtualizationQuery, new Dictionary<string, object?> { ["VirtualizationFirmwareEnabled"] = false })
            .Answer(WorkshopModule.HypervisorQuery, new Dictionary<string, object?> { ["HypervisorPresent"] = true });

        var findings = await Run(cim);

        Assert.Equal(FindingStatus.Ok, findings.Single(f => f.Id == "M16.virtualization").Status);
    }
}
