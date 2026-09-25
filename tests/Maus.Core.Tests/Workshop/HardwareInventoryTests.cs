using System.Text;
using Maus.Core.Hardware;
using Maus.Core.Platform;
using Maus.Core.Workshop;
using Microsoft.Win32;

namespace Maus.Core.Tests.Workshop;

/// <summary>CPUID simulé : un Core i7 de 13e génération (hybride, AVX2, SHA, VT-x).</summary>
internal sealed class FakeCpuId : ICpuIdSource
{
    private readonly Dictionary<(uint, uint), CpuIdRegisters> _leaves = [];

    public FakeCpuId()
    {
        _leaves[(0, 0)] = new CpuIdRegisters(0x20, Word("Genu"), Word("ntel"), Word("ineI"));
        _leaves[(1, 0)] = new CpuIdRegisters(0x000B0671, 0, (1u << 0) | (1u << 5) | (1u << 9) | (1u << 12) | (1u << 19) | (1u << 20) | (1u << 25) | (1u << 28), 1u << 26);
        _leaves[(7, 0)] = new CpuIdRegisters(0, (1u << 5) | (1u << 8) | (1u << 29), 0, 1u << 15);
        _leaves[(0x8000_0000, 0)] = new CpuIdRegisters(0x8000_0008, 0, 0, 0);
        var brand = Encoding.ASCII.GetBytes("13th Gen Intel(R) Core(TM) i7-13700K".PadRight(48, '\0'));
        for (uint i = 0; i < 3; i++)
        {
            var offset = (int)i * 16;
            _leaves[(0x8000_0002 + i, 0)] = new CpuIdRegisters(
                BitConverter.ToUInt32(brand, offset), BitConverter.ToUInt32(brand, offset + 4),
                BitConverter.ToUInt32(brand, offset + 8), BitConverter.ToUInt32(brand, offset + 12));
        }
    }

    public bool IsSupported { get; init; } = true;

    public CpuIdRegisters Query(uint leaf, uint subleaf = 0) => _leaves.GetValueOrDefault((leaf, subleaf));

    private static uint Word(string text) => BitConverter.ToUInt32(Encoding.ASCII.GetBytes(text), 0);
}

internal sealed class FakeNvml(params NvidiaGpuState[] gpus) : INvmlSource
{
    public IReadOnlyList<NvidiaGpuState>? Read() => gpus.Length == 0 ? null : gpus;
}

public class HardwareInventoryTests
{
    [Fact]
    public void Cpuid_is_decoded_into_vendor_brand_signature_and_instruction_sets()
    {
        var info = CpuIdParser.Read(new FakeCpuId())!;

        Assert.True(info.IsIntel);
        Assert.Equal("13th Gen Intel(R) Core(TM) i7-13700K", info.Brand);
        Assert.Equal(6, info.Family);
        Assert.Equal(0xB7, info.Model);
        Assert.Equal("6-B7-1", info.Signature);
        Assert.Contains("AVX2", info.InstructionSets);
        Assert.Contains("SHA", info.InstructionSets);
        Assert.DoesNotContain("AVX-512", info.InstructionSets);
        Assert.True(info.Hybrid);
        Assert.True(info.VirtualizationCapable);
        Assert.False(info.RunningInVirtualMachine);
        Assert.Null(CpuIdParser.Read(new FakeCpuId { IsSupported = false }));
    }

    [Fact]
    public void Memory_modules_give_voltages_and_vendor_names()
    {
        var cim = new FakeCim().Answer(HardwareInventoryReader.MemoryQuery,
            new Dictionary<string, object?>
            {
                ["DeviceLocator"] = "DIMM_A2", ["Capacity"] = 17179869184L, ["Speed"] = 6000u, ["ConfiguredClockSpeed"] = 6000u,
                ["Manufacturer"] = "04CD", ["PartNumber"] = "F5-6000J3038F16G   ", ["SMBIOSMemoryType"] = 34u,
                ["ConfiguredVoltage"] = 1350u, ["MinVoltage"] = 1100u, ["MaxVoltage"] = 1350u,
            },
            new Dictionary<string, object?> { ["DeviceLocator"] = "DIMM_B2", ["Capacity"] = 8589934592L, ["Manufacturer"] = "Unknown", ["SMBIOSMemoryType"] = 26u, ["ConfiguredVoltage"] = 0u });

        var memory = HardwareInventoryReader.ReadMemory(cim);

        Assert.Equal("G.Skill", memory[0].Manufacturer);
        Assert.Equal("F5-6000J3038F16G", memory[0].PartNumber);
        Assert.Equal(5, memory[0].Generation);
        Assert.Equal(1350, memory[0].ConfiguredMillivolts);
        Assert.Equal(4, memory[1].Generation);
        Assert.Null(memory[1].ConfiguredMillivolts);
        Assert.Null(memory[1].Manufacturer);
    }

    [Fact]
    public void Video_memory_is_found_through_the_matching_display_driver_key()
    {
        const string pnp = @"PCI\VEN_1002&DEV_73BF&SUBSYS_00000000&REV_C1\6&1&0&00000019";
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, HardwareInventoryReader.DisplayClassKey + @"\0000", "MatchingDeviceId", @"pci\ven_10de&dev_2786")
            .Set(RegistryHive.LocalMachine, HardwareInventoryReader.DisplayClassKey + @"\0001", "MatchingDeviceId", @"pci\ven_1002&dev_73bf")
            .Set(RegistryHive.LocalMachine, HardwareInventoryReader.DisplayClassKey + @"\0001", "HardwareInformation.qwMemorySize", 17163091968L)
            .Set(RegistryHive.LocalMachine, HardwareInventoryReader.DisplayClassKey + @"\Properties", "x", 1);

        Assert.Equal(17163091968L, HardwareInventoryReader.ReadVideoMemory(registry, pnp));
        Assert.Null(HardwareInventoryReader.ReadVideoMemory(registry, @"PCI\VEN_8086&DEV_3E92"));
    }

    [Fact]
    public void Nvidia_details_are_attached_to_the_matching_card()
    {
        var hardware = new HardwareProfile
        {
            FormFactor = FormFactor.Desktop,
            Gpus = [new GpuInfo("NVIDIA GeForce RTX 4070", HardwareVendor.Nvidia, "32.0.16.1714", null, @"PCI\VEN_10DE&DEV_2786", false)],
        };
        var context = TestContext.Create(hardware: hardware);
        var state = new NvidiaGpuState { Name = "NVIDIA GeForce RTX 4070", SlowdownTemperatureC = 90, MemoryTotalBytes = 12L << 30 };

        var gpus = HardwareInventoryReader.ReadGpus(context, new FakeNvml(state));

        Assert.Same(state, gpus.Single().Nvidia);
        Assert.Equal(12L << 30, gpus.Single().MemoryBytes);
    }

    [Fact]
    public void Battery_health_compares_full_charge_to_design_capacity()
    {
        var cim = new FakeCim()
            .Answer(HardwareInventoryReader.BatteryStaticQuery, CimScopes.Wmi, new Dictionary<string, object?> { ["InstanceName"] = "B0", ["DeviceName"] = "5B10W13930", ["DesignedCapacity"] = 57000u })
            .Answer(HardwareInventoryReader.BatteryFullQuery, CimScopes.Wmi, new Dictionary<string, object?> { ["InstanceName"] = "B0", ["FullChargedCapacity"] = 45600u })
            .Answer(HardwareInventoryReader.BatteryStatusQuery, CimScopes.Wmi, new Dictionary<string, object?> { ["InstanceName"] = "B0", ["Voltage"] = 12400u });

        var battery = HardwareInventoryReader.ReadBatteries(cim).Single();

        Assert.Equal(80, battery.HealthPercent);
        Assert.Equal(12400, battery.VoltageMv);
    }

    [Fact]
    public void Missing_sources_give_an_empty_but_valid_inventory()
    {
        var inventory = HardwareInventoryReader.Read(TestContext.Create(), new FakeCpuId { IsSupported = false }, new FakeNvml());

        Assert.Equal("Processeur inconnu", inventory.Cpu.Name);
        Assert.Empty(inventory.Memory);
        Assert.Empty(inventory.Batteries);
    }
}
