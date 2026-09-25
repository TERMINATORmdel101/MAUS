using Maus.Core.Hardware;
using Maus.Core.Modules.M10Memory;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Workshop;

public sealed record CpuIdentity(
    string Name,
    string Vendor,
    string? Signature,
    int Cores,
    int Threads,
    int BaseClockMhz,
    int? L2CacheKb,
    int? L3CacheKb,
    string? Socket,
    IReadOnlyList<string> InstructionSets,
    bool VirtualizationCapable,
    bool Hybrid,
    bool RunningInVirtualMachine);

public sealed record BoardIdentity(string? Manufacturer, string? Product, string? BiosVendor, string? BiosVersion, DateTime? BiosDate);

/// <summary>Une barrette de RAM, telle que la décrit le SMBIOS (tensions en millivolts).</summary>
public sealed record MemoryModuleInfo(
    string Slot,
    long CapacityBytes,
    int? RatedSpeedMts,
    int? ConfiguredSpeedMts,
    string? Manufacturer,
    string? PartNumber,
    int? Generation,
    int? ConfiguredMillivolts,
    int? MinMillivolts,
    int? MaxMillivolts);

public sealed record GpuIdentity(GpuInfo Info, long? MemoryBytes, NvidiaGpuState? Nvidia);

public sealed record DiskIdentity(string Model, string? Bus, string? Media, long? SizeBytes, string? Firmware, int? TemperatureC, int? WearPercent, long? PowerOnHours);

public sealed record BatteryIdentity(string? Name, string? Manufacturer, int? DesignCapacityMwh, int? FullChargeCapacityMwh, int? VoltageMv, int? ChargeRateMw, int? DischargeRateMw)
{
    /// <summary>Capacité restante par rapport à la capacité d'origine (usure).</summary>
    public int? HealthPercent => DesignCapacityMwh is > 0 && FullChargeCapacityMwh is { } full
        ? (int)Math.Round(100.0 * full / DesignCapacityMwh.Value)
        : null;
}

/// <summary>Fiche d'identité du PC, lue sans pilote et sans rien modifier.</summary>
public sealed record HardwareInventory(
    CpuIdentity Cpu,
    BoardIdentity Board,
    IReadOnlyList<MemoryModuleInfo> Memory,
    IReadOnlyList<GpuIdentity> Gpus,
    IReadOnlyList<DiskIdentity> Disks,
    IReadOnlyList<BatteryIdentity> Batteries)
{
    public long TotalMemoryBytes => Memory.Sum(m => m.CapacityBytes);
}

public static class HardwareInventoryReader
{
    internal const string ProcessorQuery = "SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation FROM Win32_Processor";
    internal const string BoardQuery = "SELECT Manufacturer, Product FROM Win32_BaseBoard";
    internal const string BiosQuery = "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS";
    internal const string MemoryQuery =
        "SELECT BankLabel, DeviceLocator, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, SMBIOSMemoryType, ConfiguredVoltage, MinVoltage, MaxVoltage FROM Win32_PhysicalMemory";
    internal const string DiskQuery = "SELECT DeviceId, FriendlyName, MediaType, BusType, Size, FirmwareVersion FROM MSFT_PhysicalDisk";
    internal const string ReliabilityQuery = "SELECT DeviceId, Temperature, Wear, PowerOnHours FROM MSFT_StorageReliabilityCounter";
    internal const string BatteryStaticQuery = "SELECT InstanceName, DeviceName, ManufactureName, DesignedCapacity FROM BatteryStaticData";
    internal const string BatteryFullQuery = "SELECT InstanceName, FullChargedCapacity FROM BatteryFullChargedCapacity";
    internal const string BatteryStatusQuery = "SELECT InstanceName, Voltage, ChargeRate, DischargeRate FROM BatteryStatus";
    internal const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>Codes fabricant JEDEC (JEP106) que le SMBIOS donne parfois à la place du nom.</summary>
    private static readonly Dictionary<string, string> JedecVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["80CE"] = "Samsung",
        ["80AD"] = "SK hynix",
        ["802C"] = "Micron",
        ["0198"] = "Kingston",
        ["029E"] = "Corsair",
        ["04CD"] = "G.Skill",
        ["859B"] = "Crucial",
        ["04CB"] = "ADATA",
        ["04EF"] = "TeamGroup",
    };

    public static HardwareInventory Read(AuditContext context, ICpuIdSource cpuId, INvmlSource nvml) => new(
        ReadCpu(context.Cim, cpuId),
        ReadBoard(context.Cim),
        ReadMemory(context.Cim),
        ReadGpus(context, nvml),
        ReadDisks(context.Cim),
        ReadBatteries(context.Cim));

    internal static CpuIdentity ReadCpu(ICimReader cim, ICpuIdSource cpuId)
    {
        var row = First(Query(cim, ProcessorQuery));
        var id = Safe(() => CpuIdParser.Read(cpuId));
        var name = id?.Brand ?? row?.GetString("Name")?.Trim() ?? Localization.Texts.T("Processeur inconnu");
        return new CpuIdentity(
            name,
            id?.Vendor ?? row?.GetString("Manufacturer") ?? "?",
            id?.Signature,
            (int)(row?.GetInt64("NumberOfCores") ?? 0),
            (int)(row?.GetInt64("NumberOfLogicalProcessors") ?? 0),
            (int)(row?.GetInt64("MaxClockSpeed") ?? 0),
            (int?)row?.GetInt64("L2CacheSize"),
            (int?)row?.GetInt64("L3CacheSize"),
            row?.GetString("SocketDesignation"),
            id?.InstructionSets ?? [],
            id?.VirtualizationCapable ?? false,
            id?.Hybrid ?? false,
            id?.RunningInVirtualMachine ?? false);
    }

    internal static BoardIdentity ReadBoard(ICimReader cim)
    {
        var board = First(Query(cim, BoardQuery));
        var bios = First(Query(cim, BiosQuery));
        return new BoardIdentity(
            Clean(board?.GetString("Manufacturer")),
            Clean(board?.GetString("Product")),
            Clean(bios?.GetString("Manufacturer")),
            Clean(bios?.GetString("SMBIOSBIOSVersion")),
            bios?.GetDateTime("ReleaseDate"));
    }

    internal static IReadOnlyList<MemoryModuleInfo> ReadMemory(ICimReader cim) =>
        Query(cim, MemoryQuery).Select(row =>
        {
            var part = Clean(row.GetString("PartNumber"));
            var decoded = MemoryPartDecoder.Decode(part);
            var generation = row.GetInt64("SMBIOSMemoryType") switch
            {
                26 => 4,
                34 => 5,
                24 => 3,
                _ => decoded?.Generation,
            };
            var slot = Clean(row.GetString("DeviceLocator")) ?? Clean(row.GetString("BankLabel")) ?? "?";
            return new MemoryModuleInfo(
                slot,
                row.GetInt64("Capacity") ?? 0,
                (int?)row.GetInt64("Speed") ?? decoded?.RatedSpeed,
                (int?)row.GetInt64("ConfiguredClockSpeed"),
                VendorName(row.GetString("Manufacturer")) ?? decoded?.Brand,
                part,
                generation,
                Millivolts(row.GetInt64("ConfiguredVoltage")),
                Millivolts(row.GetInt64("MinVoltage")),
                Millivolts(row.GetInt64("MaxVoltage")));
        }).ToList();

    internal static IReadOnlyList<GpuIdentity> ReadGpus(AuditContext context, INvmlSource nvml)
    {
        var nvidia = Safe(nvml.Read) ?? [];
        var usedNvidia = new HashSet<NvidiaGpuState>();
        return context.Hardware.Gpus.Select(gpu =>
        {
            NvidiaGpuState? state = null;
            if (gpu.Vendor == HardwareVendor.Nvidia)
            {
                state = nvidia.FirstOrDefault(n => !usedNvidia.Contains(n) && NamesMatch(n.Name, gpu.Name)) ?? nvidia.FirstOrDefault(n => !usedNvidia.Contains(n));
                if (state is not null)
                {
                    usedNvidia.Add(state);
                }
            }

            return new GpuIdentity(gpu, state?.MemoryTotalBytes ?? ReadVideoMemory(context.Registry, gpu.PnpDeviceId), state);
        }).ToList();
    }

    /// <summary>Mémoire vidéo annoncée par le pilote : clé de la classe d'affichage dont <c>MatchingDeviceId</c> correspond à la carte.</summary>
    internal static long? ReadVideoMemory(IRegistryReader registry, string pnpDeviceId)
    {
        try
        {
            foreach (var sub in registry.GetSubKeyNames(RegistryHive.LocalMachine, DisplayClassKey).Where(k => k.Length == 4 && k.All(char.IsAsciiDigit)))
            {
                var path = $@"{DisplayClassKey}\{sub}";
                var matching = registry.GetString(RegistryHive.LocalMachine, path, "MatchingDeviceId");
                if (matching is null || !pnpDeviceId.StartsWith(matching, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return registry.GetValue(RegistryHive.LocalMachine, path, "HardwareInformation.qwMemorySize") switch
                {
                    long bytes when bytes > 0 => bytes,
                    int bytes when bytes > 0 => bytes,
                    _ => registry.GetValue(RegistryHive.LocalMachine, path, "HardwareInformation.MemorySize") switch
                    {
                        int bytes when bytes > 0 => (uint)bytes,
                        byte[] { Length: >= 4 } raw => BitConverter.ToUInt32(raw, 0),
                        _ => null,
                    },
                };
            }
        }
        catch (MausAccessDeniedException)
        {
            return null;
        }

        return null;
    }

    internal static IReadOnlyList<DiskIdentity> ReadDisks(ICimReader cim)
    {
        var reliability = Query(cim, ReliabilityQuery, CimScopes.Storage)
            .GroupBy(r => r.GetString("DeviceId") ?? string.Empty)
            .ToDictionary(g => g.Key, g => g.First());
        return Query(cim, DiskQuery, CimScopes.Storage).Select(row =>
        {
            reliability.TryGetValue(row.GetString("DeviceId") ?? string.Empty, out var health);
            return new DiskIdentity(
                Clean(row.GetString("FriendlyName")) ?? "?",
                row.GetInt64("BusType") switch
                {
                    17 => "NVMe",
                    11 => "SATA",
                    7 => "USB",
                    8 => "RAID",
                    _ => null,
                },
                row.GetInt64("MediaType") switch
                {
                    4 => "SSD",
                    3 => "HDD",
                    _ => null,
                },
                row.GetInt64("Size"),
                Clean(row.GetString("FirmwareVersion")),
                (int?)health?.GetInt64("Temperature") is > 0 and var t ? t : null,
                (int?)health?.GetInt64("Wear"),
                health?.GetInt64("PowerOnHours"));
        }).ToList();
    }

    internal static IReadOnlyList<BatteryIdentity> ReadBatteries(ICimReader cim)
    {
        var full = Query(cim, BatteryFullQuery, CimScopes.Wmi).ToDictionary(r => r.GetString("InstanceName") ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        var status = Query(cim, BatteryStatusQuery, CimScopes.Wmi).ToDictionary(r => r.GetString("InstanceName") ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        return Query(cim, BatteryStaticQuery, CimScopes.Wmi).Select(row =>
        {
            var name = row.GetString("InstanceName") ?? string.Empty;
            full.TryGetValue(name, out var capacity);
            status.TryGetValue(name, out var state);
            return new BatteryIdentity(
                Clean(row.GetString("DeviceName")),
                Clean(row.GetString("ManufactureName")),
                (int?)row.GetInt64("DesignedCapacity"),
                (int?)capacity?.GetInt64("FullChargedCapacity"),
                (int?)state?.GetInt64("Voltage"),
                (int?)state?.GetInt64("ChargeRate"),
                (int?)state?.GetInt64("DischargeRate"));
        }).ToList();
    }

    /// <summary>Nom du fabricant : tel quel, ou traduit depuis un code JEDEC hexadécimal (« 80CE… » = Samsung).</summary>
    internal static string? VendorName(string? manufacturer)
    {
        var value = Clean(manufacturer);
        if (value is null)
        {
            return null;
        }

        if (value.Length >= 4 && value.All(char.IsAsciiHexDigit))
        {
            return JedecVendors.TryGetValue(value[..4], out var known) ? known : null;
        }

        return value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? null : value;
    }

    private static int? Millivolts(long? value) => value is > 0 and < 5000 ? (int)value : null;

    private static bool NamesMatch(string nvml, string wmi) =>
        wmi.Contains(nvml.Replace("NVIDIA ", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Equals("To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("Default string", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }

    private static IReadOnlyList<CimRow> Query(ICimReader cim, string wql, string scope = CimScopes.Default)
    {
        try
        {
            return cim.Query(wql, scope);
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or DataSourceUnavailableException)
        {
            return [];
        }
    }

    private static T? Safe<T>(Func<T?> read)
        where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static CimRow? First(IReadOnlyList<CimRow> rows) => rows.Count > 0 ? rows[0] : null;
}
