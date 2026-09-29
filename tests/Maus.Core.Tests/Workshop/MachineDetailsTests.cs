using Maus.Core.Hardware;
using Maus.Core.Modules.M14Display;
using Maus.Core.Platform;
using Maus.Core.Workshop;
using Microsoft.Win32;

namespace Maus.Core.Tests.Workshop;

public class MachineDetailsTests
{
    private sealed class FakeFirmware(int? type) : IFirmwareTypeSource
    {
        public int? Read() => type;
    }

    private sealed class FakeDisplays(IReadOnlyList<DisplayPath>? paths) : IDisplayConfigReader
    {
        public IReadOnlyList<DisplayPath>? ReadActivePaths() => paths;
    }

    private static FakeCim Machine() => new FakeCim()
        .Answer(MachineDetailsReader.OperatingSystemQuery, new Dictionary<string, object?>
        {
            ["OSArchitecture"] = "64 bits",
            ["InstallDate"] = new DateTime(2026, 8, 1, 21, 55, 26),
            ["LastBootUpTime"] = new DateTime(2026, 9, 29, 7, 45, 25),
        })
        .Answer(MachineDetailsReader.MemoryArrayQuery,
            new Dictionary<string, object?> { ["MemoryDevices"] = 4L, ["MaxCapacity"] = 67108864L, ["MaxCapacityEx"] = 67108864L, ["Use"] = 3L },
            new Dictionary<string, object?> { ["MemoryDevices"] = 1L, ["MaxCapacity"] = 1024L, ["Use"] = 5L })
        .Answer(MachineDetailsReader.TpmQuery, CimScopes.Tpm, new Dictionary<string, object?>
        {
            ["SpecVersion"] = "2.0, 0, 1.59",
            ["ManufacturerIdTxt"] = "INTC",
            ["IsEnabled_InitialValue"] = true,
            ["IsActivated_InitialValue"] = true,
        })
        .Answer(MachineDetailsReader.NetworkQuery, CimScopes.StandardCimv2,
            new Dictionary<string, object?> { ["Name"] = "Wi-Fi", ["InterfaceDescription"] = "Intel Wi-Fi 6 AX200", ["MediaConnectState"] = 2L, ["NdisPhysicalMedium"] = 9L, ["HardwareInterface"] = true },
            new Dictionary<string, object?> { ["Name"] = "Ethernet", ["InterfaceDescription"] = "Intel I219-V", ["MediaConnectState"] = 1L, ["ReceiveLinkSpeed"] = 1_000_000_000L, ["NdisPhysicalMedium"] = 14L, ["HardwareInterface"] = true },
            new Dictionary<string, object?> { ["Name"] = "vEthernet", ["InterfaceDescription"] = "Hyper-V Virtual Ethernet Adapter", ["MediaConnectState"] = 1L, ["NdisPhysicalMedium"] = 14L, ["HardwareInterface"] = false })
        .Answer(MachineDetailsReader.SoundQuery,
            new Dictionary<string, object?> { ["Name"] = "Realtek High Definition Audio", ["Manufacturer"] = "Realtek" },
            new Dictionary<string, object?> { ["Name"] = "NVIDIA High Definition Audio", ["Manufacturer"] = "NVIDIA" },
            new Dictionary<string, object?> { ["Name"] = "Realtek High Definition Audio", ["Manufacturer"] = "Realtek" });

    private static AuditContext Context(FakeCim cim, FakeRegistry? registry = null) => TestContext.Create(
        cim: cim,
        registry: registry ?? new FakeRegistry().Set(RegistryHive.LocalMachine, MachineDetailsReader.SecureBootStateKey, "UEFISecureBootEnabled", 1),
        hardware: new HardwareProfile { FormFactor = FormFactor.Desktop, Manufacturer = "Micro-Star International Co., Ltd.", Model = "MS-7B17" });

    [Fact]
    public void Windows_firmware_secure_boot_and_tpm_are_read_without_writing()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, MachineDetailsReader.SecureBootStateKey, "UEFISecureBootEnabled", 1)
            .Set(RegistryHive.LocalMachine, Maus.Core.Modules.M08Bios.BiosModule.CpuKey, "Update Revision", new byte[] { 0xF0, 0, 0, 0 });
        var details = MachineDetailsReader.Read(Context(Machine(), registry), new FakeDisplays(null), new FakeFirmware(2));

        var system = details.System;
        Assert.Equal("MS-7B17", system.Model);
        Assert.Equal(FormFactor.Desktop, system.FormFactor);
        Assert.Equal("64 bits", system.Architecture);
        Assert.Equal(new DateTime(2026, 9, 29, 7, 45, 25), system.BootedAt);
        Assert.Equal(FirmwareKind.Uefi, system.Firmware);
        Assert.True(system.SecureBoot);
        Assert.Equal(new TpmIdentity("2.0", "INTC", true, true), system.Tpm);
        Assert.Equal("0xF0", details.Microcode);
        Assert.False(system.TpmNeedsAdministrator);
        Assert.Empty(registry.Writes);
    }

    [Fact]
    public void Unreadable_values_stay_unknown_instead_of_being_guessed()
    {
        var cim = new FakeCim().Throw(MachineDetailsReader.TpmQuery, new MausAccessDeniedException("refusé"), CimScopes.Tpm);
        var details = MachineDetailsReader.Read(Context(cim, new FakeRegistry()), new FakeDisplays(null), new FakeFirmware(null));

        Assert.Equal(FirmwareKind.Unknown, details.System.Firmware);
        Assert.Null(details.System.SecureBoot);
        Assert.Null(details.System.Tpm);
        Assert.True(details.System.TpmNeedsAdministrator);
        Assert.Null(details.System.BootedAt);
        Assert.Null(details.Slots);
        Assert.Empty(details.Displays);
        Assert.Empty(details.Network);
        Assert.Empty(details.Audio);
        Assert.Equal(FirmwareKind.LegacyBios, MachineDetailsReader.Read(Context(cim), new FakeDisplays(null), new FakeFirmware(1)).System.Firmware);
    }

    [Fact]
    public void Memory_slots_count_only_system_memory_arrays()
    {
        var slots = MachineDetailsReader.ReadSlots(Machine())!;

        Assert.Equal(4, slots.Total);
        Assert.Equal(64L * 1024 * 1024 * 1024, slots.MaxCapacityBytes);
    }

    [Fact]
    public void Only_physical_network_adapters_are_listed_connected_first_without_addresses()
    {
        var network = MachineDetailsReader.ReadNetwork(Machine());

        Assert.Equal(["Ethernet", "Wi-Fi"], network.Select(a => a.Name));
        Assert.Equal(new NetworkAdapterIdentity("Ethernet", "Intel I219-V", NetworkKind.Ethernet, true, 1_000_000_000L), network[0]);
        Assert.Equal(NetworkKind.WiFi, network[1].Kind);
        Assert.False(network[1].Connected);
    }

    [Fact]
    public void Audio_devices_are_listed_once()
    {
        var audio = MachineDetailsReader.ReadAudio(Machine());

        Assert.Equal(["Realtek High Definition Audio", "NVIDIA High Definition Audio"], audio.Select(d => d.Name));
    }

    [Fact]
    public void Displays_come_from_the_display_configuration()
    {
        var displays = MachineDetailsReader.ReadDisplays(new FakeDisplays(
        [
            new DisplayPath
            {
                MonitorName = "AW3423DWF",
                AdapterName = "NVIDIA GeForce RTX 2080 Ti",
                Output = OutputTechnology.DisplayPortExternal,
                Width = 3440,
                Height = 1440,
                RefreshHz = 164.9,
                NativeWidth = 3440,
                NativeHeight = 1440,
                Color = new AdvancedColorInfo(true, false, ColorEncoding.Rgb, 10),
            },
            new DisplayPath { Output = OutputTechnology.DisplayPortEmbedded, Width = 1920, Height = 1080 },
        ]));

        Assert.Equal(new DisplayIdentity("AW3423DWF", "NVIDIA GeForce RTX 2080 Ti", "DisplayPort", 3440, 1440, 164.9, 3440, 1440, true, false, 10), displays[0]);
        Assert.Equal("Écran intégré", displays[1].Name);
        Assert.Null(displays[1].HdrSupported);
    }
}
