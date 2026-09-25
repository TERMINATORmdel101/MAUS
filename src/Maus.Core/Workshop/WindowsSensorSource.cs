using System.Runtime.InteropServices;

namespace Maus.Core.Workshop;

/// <summary>
/// Mesures en direct sans pilote : compteurs PDH (processeur, disques, réseau, zones thermiques), mémoire
/// (<c>GlobalMemoryStatusEx</c>), données de performance des cartes graphiques (<c>D3DKMT_ADAPTER_PERFDATA</c>,
/// comme le Gestionnaire des tâches) et NVML pour les cartes NVIDIA.
/// </summary>
public sealed class WindowsSensorSource : ISensorSource
{
    private readonly PdhQuery _pdh = new();
    private readonly nint _cpu;
    private readonly nint _cores;
    private readonly nint _performance;
    private readonly nint _frequency;
    private readonly nint _diskIdle;
    private readonly nint _diskRead;
    private readonly nint _diskWrite;
    private readonly nint _network;
    private readonly nint _thermal;
    private readonly nint _gpuEngine;
    private readonly nint _gpuMemory;
    private readonly INvmlSource _nvml;

    public WindowsSensorSource(INvmlSource? nvml = null)
    {
        _nvml = nvml ?? new WindowsNvmlSource();
        _cpu = _pdh.Add(@"\Processor Information(_Total)\% Processor Utility");
        _cores = _pdh.Add(@"\Processor Information(*)\% Processor Utility");
        _performance = _pdh.Add(@"\Processor Information(_Total)\% Processor Performance");
        _frequency = _pdh.Add(@"\Processor Information(_Total)\Processor Frequency");
        _diskIdle = _pdh.Add(@"\PhysicalDisk(_Total)\% Idle Time");
        _diskRead = _pdh.Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        _diskWrite = _pdh.Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        _network = _pdh.Add(@"\Network Interface(*)\Bytes Total/sec");
        _thermal = _pdh.Add(@"\Thermal Zone Information(*)\Temperature");
        _gpuEngine = _pdh.Add(@"\GPU Engine(*)\Utilization Percentage");
        _gpuMemory = _pdh.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        _pdh.Collect();
    }

    public SensorSnapshot Sample()
    {
        _pdh.Collect();
        var memory = MemoryStatus.Read();
        var cores = PdhQuery.Array(_cores)
            .Where(item => !item.Name.Contains("_Total", StringComparison.Ordinal))
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => item.Value)
            .ToList();
        var engines = PdhQuery.Array(_gpuEngine);
        var gpuPercent = engines.Where(e => e.Name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)).Sum(e => e.Value);
        var thermal = PdhQuery.Array(_thermal).Select(t => t.Value - 273.15).Where(c => c is > 0 and < 150).DefaultIfEmpty(double.NaN).Max();
        var nvidia = SafeNvml();
        var perf = GpuPerformance.Read();

        var gpus = new List<GpuSensor>();
        foreach (var adapter in perf)
        {
            var match = nvidia.FirstOrDefault(n => adapter.Name.Contains(n.Name.Replace("NVIDIA ", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase));
            gpus.Add(new GpuSensor(
                adapter.Name,
                (double?)match?.UtilizationPercent ?? (perf.Count == 1 ? Math.Min(100, gpuPercent) : null),
                match?.TemperatureC ?? adapter.TemperatureC,
                adapter.FanRpm,
                match?.FanPercent,
                adapter.PowerPercent,
                match?.PowerWatts,
                match?.GraphicsClockMhz,
                match?.MemoryUsedBytes,
                match?.SlowdownTemperatureC));
        }

        var performance = PdhQuery.Value(_performance);
        var nominal = PdhQuery.Value(_frequency);
        var idle = PdhQuery.Value(_diskIdle);
        return new SensorSnapshot
        {
            At = DateTimeOffset.Now,
            CpuPercent = Clamp(PdhQuery.Value(_cpu)),
            CorePercents = cores.Select(c => Clamp(c) ?? 0).ToList(),
            CpuMhz = performance is { } p && nominal is { } n ? n * p / 100 : null,
            MemoryUsedBytes = memory?.Used,
            MemoryTotalBytes = memory?.Total,
            DiskActivePercent = idle is { } i ? Clamp(100 - i) : null,
            DiskReadBytesPerSecond = PdhQuery.Value(_diskRead),
            DiskWriteBytesPerSecond = PdhQuery.Value(_diskWrite),
            NetworkBytesPerSecond = PdhQuery.Array(_network).Sum(n => n.Value),
            ThermalZoneC = double.IsNaN(thermal) ? null : thermal,
            Gpus = gpus,
        };
    }

    public void Dispose() => _pdh.Dispose();

    private IReadOnlyList<NvidiaGpuState> SafeNvml()
    {
        try
        {
            return _nvml.Read() ?? [];
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
    }

    private static double? Clamp(double? value) => value is { } v ? Math.Clamp(v, 0, 100) : null;

    private static class MemoryStatus
    {
        public static (long Used, long Total)? Read()
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status)
                ? ((long)(status.TotalPhys - status.AvailPhys), (long)status.TotalPhys)
                : null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
    }
}
