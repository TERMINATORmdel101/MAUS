using System.Runtime.InteropServices;

namespace Maus.Core.Modules.M09Gpu;

/// <summary>État de la planification GPU accélérée par le matériel (HAGS) pour un adaptateur.</summary>
internal sealed record GpuSchedulingInfo(int VendorId, int DeviceId, bool Supported, bool Enabled, bool EnabledByDefault);

/// <summary>Lecture de l'état réel de HAGS, tel que le noyau graphique l'applique.</summary>
internal interface IGpuSchedulingReader
{
    /// <summary>Un élément par adaptateur graphique, ou <c>null</c> si l'API est indisponible.</summary>
    IReadOnlyList<GpuSchedulingInfo>? Read();
}

/// <summary>
/// Implémentation par <c>D3DKMTEnumAdapters2</c> et <c>D3DKMTQueryAdapterInfo</c> (gdi32.dll), en lecture seule :
/// identifiants PCI de l'adaptateur (<c>KMTQAITYPE_PHYSICALADAPTERDEVICEIDS</c>) et bits <c>D3DKMT_WDDM_2_7_CAPS</c>.
/// </summary>
internal sealed unsafe partial class D3dkmtGpuSchedulingReader : IGpuSchedulingReader
{
    // Valeurs de KMTQUERYADAPTERINFOTYPE (d3dkmthk.h), confirmées sur Windows 11 25H2 : 30 renvoie le nombre d'adaptateurs physiques.
    private const uint QueryPhysicalAdapterDeviceIds = 31;
    private const uint QueryWddm27Caps = 70;

    public IReadOnlyList<GpuSchedulingInfo>? Read()
    {
        try
        {
            return ReadCore();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static List<GpuSchedulingInfo>? ReadCore()
    {
        var enumArgs = default(EnumAdapters2);
        if (D3DKMTEnumAdapters2(&enumArgs) != 0 || enumArgs.NumAdapters == 0)
        {
            return null;
        }

        var adapters = new AdapterInfo[enumArgs.NumAdapters];
        fixed (AdapterInfo* buffer = adapters)
        {
            enumArgs.Adapters = buffer;
            if (D3DKMTEnumAdapters2(&enumArgs) != 0)
            {
                return null;
            }
        }

        var result = new List<GpuSchedulingInfo>();
        for (var i = 0; i < enumArgs.NumAdapters && i < adapters.Length; i++)
        {
            var handle = adapters[i].Handle;
            try
            {
                var ids = default(QueryDeviceIds);
                if (!Query(handle, QueryPhysicalAdapterDeviceIds, &ids, (uint)sizeof(QueryDeviceIds)))
                {
                    continue;
                }

                // Un pilote antérieur à WDDM 2.7 refuse la requête : HAGS n'y est pas pris en charge.
                uint caps = 0;
                var hasCaps = Query(handle, QueryWddm27Caps, &caps, sizeof(uint));
                result.Add(new GpuSchedulingInfo(
                    (int)ids.VendorId,
                    (int)ids.DeviceId,
                    Supported: hasCaps && (caps & 1) != 0,
                    Enabled: hasCaps && (caps & 2) != 0,
                    EnabledByDefault: hasCaps && (caps & 4) != 0));
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

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryDeviceIds
    {
        public uint PhysicalAdapterIndex;
        public uint VendorId;
        public uint DeviceId;
        public uint SubVendorId;
        public uint SubSystemId;
        public uint RevisionId;
        public uint BusType;
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
