using System.Runtime.InteropServices;

namespace Maus.Core.Platform;

/// <summary>
/// Adaptateurs graphiques vus par le noyau : <c>D3DKMTEnumAdapters2</c>, <c>D3DKMTQueryAdapterInfo</c> et
/// <c>D3DKMTCloseAdapter</c> (gdi32.dll), en lecture seule. Chaque lecteur garde ses types de requête et ses structures.
/// </summary>
internal static unsafe partial class D3dkmtAdapters
{
    /// <summary>Donne le numéro de chaque adaptateur à <paramref name="visit"/>, et le referme après.</summary>
    /// <returns>Faux si Windows ne donne aucun adaptateur.</returns>
    public static bool ForEach(Action<uint> visit)
    {
        var enumArgs = default(EnumAdapters2);
        if (D3DKMTEnumAdapters2(&enumArgs) != 0 || enumArgs.NumAdapters == 0)
        {
            return false;
        }

        var adapters = new AdapterInfo[enumArgs.NumAdapters];
        fixed (AdapterInfo* buffer = adapters)
        {
            enumArgs.Adapters = buffer;
            if (D3DKMTEnumAdapters2(&enumArgs) != 0)
            {
                return false;
            }
        }

        for (var i = 0; i < enumArgs.NumAdapters && i < adapters.Length; i++)
        {
            var handle = adapters[i].Handle;
            try
            {
                visit(handle);
            }
            finally
            {
                var close = new CloseAdapter { Handle = handle };
                _ = D3DKMTCloseAdapter(&close);
            }
        }

        return true;
    }

    /// <summary>Une requête sur un adaptateur ; <paramref name="data"/> reste vide si Windows ou le pilote la refuse.</summary>
    public static bool Query<T>(uint handle, uint type, out T data)
        where T : unmanaged
    {
        data = default;
        fixed (T* pointer = &data)
        {
            var query = new QueryAdapterInfo { Handle = handle, Type = type, PrivateDriverData = pointer, PrivateDriverDataSize = (uint)sizeof(T) };
            return D3DKMTQueryAdapterInfo(&query) == 0;
        }
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
