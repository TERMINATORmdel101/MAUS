using System.Runtime.InteropServices;

namespace Maus.Core.Modules.M09Gpu;

/// <summary>État de la planification GPU accélérée par le matériel (HAGS) pour un adaptateur.</summary>
internal sealed record GpuSchedulingInfo(int VendorId, int DeviceId, bool Supported, bool Enabled);

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
internal sealed class D3dkmtGpuSchedulingReader : IGpuSchedulingReader
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
        var result = new List<GpuSchedulingInfo>();
        var found = Platform.D3dkmtAdapters.ForEach(handle =>
        {
            if (!Platform.D3dkmtAdapters.Query(handle, QueryPhysicalAdapterDeviceIds, out QueryDeviceIds ids))
            {
                return;
            }

            // Un pilote antérieur à WDDM 2.7 refuse la requête : HAGS n'y est pas pris en charge.
            var hasCaps = Platform.D3dkmtAdapters.Query(handle, QueryWddm27Caps, out uint caps);
            result.Add(new GpuSchedulingInfo(
                (int)ids.VendorId,
                (int)ids.DeviceId,
                Supported: hasCaps && (caps & 1) != 0,
                Enabled: hasCaps && (caps & 2) != 0));
        });
        return found ? result : null;
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
}
