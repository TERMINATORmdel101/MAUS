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
    private readonly INvmlSource _nvml;
    private readonly LiveNvmlSource? _ownNvml;

    /// <summary>Compteur par moteur graphique : lourd, ajouté seulement sur un PC dont l'unique carte ne donne pas sa charge par NVML.</summary>
    private nint _gpuEngine;

    public WindowsSensorSource(INvmlSource? nvml = null)
    {
        _ownNvml = nvml is null ? new LiveNvmlSource() : null;
        _nvml = nvml ?? _ownNvml!;
        _cpu = _pdh.Add(@"\Processor Information(_Total)\% Processor Utility");
        _cores = _pdh.Add(@"\Processor Information(*)\% Processor Utility");
        _performance = _pdh.Add(@"\Processor Information(_Total)\% Processor Performance");
        _frequency = _pdh.Add(@"\Processor Information(_Total)\Processor Frequency");
        _diskIdle = _pdh.Add(@"\PhysicalDisk(_Total)\% Idle Time");
        _diskRead = _pdh.Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        _diskWrite = _pdh.Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        _network = _pdh.Add(@"\Network Interface(*)\Bytes Total/sec");
        _thermal = _pdh.Add(@"\Thermal Zone Information(*)\Temperature");
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
        double? gpuPercent = _gpuEngine == 0 ? null : Math.Min(100, PdhQuery.Array(_gpuEngine).Where(e => e.Name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)).Sum(e => e.Value));
        var thermal = PdhQuery.Array(_thermal).Select(t => t.Value - 273.15).Where(c => c is > 0 and < 150).DefaultIfEmpty(double.NaN).Max();
        var nvidia = SafeNvml();
        var perf = GpuPerformance.Read();

        var gpus = new List<GpuSensor>();
        var cards = nvidia.ToList();
        foreach (var adapter in perf)
        {
            var match = TakeCard(cards, adapter.Name);
            gpus.Add(new GpuSensor(
                adapter.Name,
                (double?)match?.UtilizationPercent ?? (perf.Count == 1 ? gpuPercent : null),
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
        var snapshot = new SensorSnapshot
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

        // Après la lecture des valeurs : le nouveau relevé sert de point de départ à la mesure suivante.
        if (_gpuEngine == 0 && NeedsGpuEngineCounter(gpus))
        {
            _gpuEngine = _pdh.Add(@"\GPU Engine(*)\Utilization Percentage");
            _pdh.Collect();
        }

        return snapshot;
    }

    public void Dispose()
    {
        _pdh.Dispose();
        _ownNvml?.Dispose();
    }

    /// <summary>Le compteur des moteurs graphiques ne sert qu'à une carte seule dont NVML ne donne pas la charge.</summary>
    internal static bool NeedsGpuEngineCounter(IReadOnlyList<GpuSensor> gpus) => gpus.Count == 1 && gpus[0].UtilizationPercent is null;

    /// <summary>
    /// Carte NVML d'un adaptateur : nom exact d'abord, sinon morceau de nom, et chaque carte ne sert qu'une fois (une RTX 4070 et
    /// une RTX 4070 Ti dans le même PC gardent chacune leurs mesures).
    /// </summary>
    internal static NvidiaGpuState? TakeCard(List<NvidiaGpuState> cards, string adapter)
    {
        var match = cards.FirstOrDefault(n => string.Equals(WithoutBrand(n.Name), WithoutBrand(adapter), StringComparison.OrdinalIgnoreCase))
            ?? cards.FirstOrDefault(n => adapter.Contains(WithoutBrand(n.Name), StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            cards.Remove(match);
        }

        return match;
    }

    private static string WithoutBrand(string name) => name.Replace("NVIDIA ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();

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

    /// <summary>Mémoire que Windows peut encore engager (fichier d'échange compris), ou <c>null</c> si illisible.</summary>
    public static long? AvailableCommitBytes() => MemoryStatus.AvailableCommit();

    private static class MemoryStatus
    {
        public static long? AvailableCommit()
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status) ? (long)status.AvailPageFile : null;
        }

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
