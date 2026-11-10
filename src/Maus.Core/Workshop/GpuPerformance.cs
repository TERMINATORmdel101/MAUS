using System.Runtime.InteropServices;

namespace Maus.Core.Workshop;

/// <summary>Données de performance d'un adaptateur graphique, lues comme le Gestionnaire des tâches.</summary>
internal sealed record GpuPerformanceData(string Name, double? TemperatureC, int? FanRpm, double? PowerPercent);

/// <summary>
/// <c>D3DKMTQueryAdapterInfo</c> avec <c>KMTQAITYPE_ADAPTERPERFDATA</c> (62 dans d3dkmthk.h ; les champs remplis dépendent du pilote de chaque marque) et
/// <c>KMTQAITYPE_ADAPTERREGISTRYINFO</c> (8) pour le nom. Structures « réservées au système » : toute erreur donne une liste vide.
/// </summary>
internal static unsafe class GpuPerformance
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
        var result = new List<GpuPerformanceData>();
        Platform.D3dkmtAdapters.ForEach(handle =>
        {
            var name = Platform.D3dkmtAdapters.Query(handle, QueryRegistryInfo, out RegistryInfo registry)
                ? new string(registry.AdapterString).TrimEnd('\0')
                : string.Empty;
            if (name.Length == 0 || name.Contains("Microsoft Basic Render", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var hasPerf = Platform.D3dkmtAdapters.Query(handle, QueryPerfData, out PerfData perf);
            result.Add(new GpuPerformanceData(
                name,
                hasPerf && perf.Temperature is > 0 and < 2000 ? perf.Temperature / 10.0 : null,
                hasPerf && perf.FanRpm is > 0 and < 20000 ? (int)perf.FanRpm : null,
                hasPerf && perf.Power <= 1000 ? perf.Power / 10.0 : null));
        });
        return result;
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
}
