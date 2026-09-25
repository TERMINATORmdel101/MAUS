using System.Runtime.InteropServices;

namespace Maus.Core.Workshop;

/// <summary>Données de performance d'un adaptateur graphique, lues comme le Gestionnaire des tâches.</summary>
internal sealed record GpuPerformanceData(string Name, double? TemperatureC, int? FanRpm, double? PowerPercent);

/// <summary>
/// <c>D3DKMTQueryAdapterInfo</c> avec <c>KMTQAITYPE_ADAPTERPERFDATA</c> (62, à vérifier sur chaque marque) et
/// <c>KMTQAITYPE_ADAPTERREGISTRYINFO</c> (8) pour le nom. Structures « réservées au système » : toute erreur donne une liste vide.
/// </summary>
internal static unsafe partial class GpuPerformance
{
    private const uint QueryRegistryInfo = 8;
    private const uint QueryPerfData = 62;

    public static IReadOnlyList<GpuPerformanceData> Read()
    {
        try
        {
            return ReadCore();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
    }

    private static List<GpuPerformanceData> ReadCore()
    {
        var enumArgs = default(EnumAdapters2);
        if (D3DKMTEnumAdapters2(&enumArgs) != 0 || enumArgs.NumAdapters == 0)
        {
            return [];
        }

        var adapters = new AdapterInfo[enumArgs.NumAdapters];
        fixed (AdapterInfo* buffer = adapters)
        {
            enumArgs.Adapters = buffer;
            if (D3DKMTEnumAdapters2(&enumArgs) != 0)
            {
                return [];
            }
        }

        var result = new List<GpuPerformanceData>();
        for (var i = 0; i < enumArgs.NumAdapters && i < adapters.Length; i++)
        {
            var handle = adapters[i].Handle;
            try
            {
                var registry = default(RegistryInfo);
                var name = Query(handle, QueryRegistryInfo, &registry, (uint)sizeof(RegistryInfo))
                    ? new string(registry.AdapterString).TrimEnd('\0')
                    : string.Empty;
                if (name.Length == 0 || name.Contains("Microsoft Basic Render", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var perf = default(PerfData);
                var hasPerf = Query(handle, QueryPerfData, &perf, (uint)sizeof(PerfData));
                result.Add(new GpuPerformanceData(
                    name,
                    hasPerf && perf.Temperature is > 0 and < 2000 ? perf.Temperature / 10.0 : null,
                    hasPerf && perf.FanRpm is > 0 and < 20000 ? (int)perf.FanRpm : null,
                    hasPerf && perf.Power <= 1000 ? perf.Power / 10.0 : null));
            }
            finally
            {
                var close = new CloseAdapter { Handle = handle };
                _ = D3DKMTCloseAdapter(&close);
            }
        }

        return result;
    }

    private static bool Query(uint handle, uint type, void* data, uint size)
    {
        var query = new QueryAdapterInfo { Handle = handle, Type = type, PrivateDriverData = data, PrivateDriverDataSize = size };
        return D3DKMTQueryAdapterInfo(&query) == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumAdapters2
    {
        public uint NumAdapters;
        public AdapterInfo* Adapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterInfo
    {
        public uint Handle;
        public uint LuidLow;
        public int LuidHigh;
        public uint NumOfSources;
        public int PrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryAdapterInfo
    {
        public uint Handle;
        public uint Type;
        public void* PrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RegistryInfo
    {
        public fixed char AdapterString[260];
        public fixed char BiosString[260];
        public fixed char DacType[260];
        public fixed char ChipType[260];
    }

    /// <summary><c>D3DKMT_ADAPTER_PERFDATA</c> : fréquences en Hz, puissance en dixièmes de %, température en dixièmes de degré.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PerfData
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth;
        public ulong PcieBandwidth;
        public uint FanRpm;
        public uint Power;
        public uint Temperature;
        public byte PowerStateOverride;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CloseAdapter
    {
        public uint Handle;
    }

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTEnumAdapters2(EnumAdapters2* args);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTQueryAdapterInfo(QueryAdapterInfo* args);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTCloseAdapter(CloseAdapter* args);
}
