using SharpGen.Runtime;
using Vortice.DXGI;

namespace Maus.Bench.Gpu;

/// <summary>Choix de la carte graphique et détection de sa perte (pilote réinitialisé par Windows).</summary>
internal static class GpuAdapters
{
    /// <summary>
    /// La carte la plus performante (IDXGIFactory6.EnumAdapterByGpuPreference, préférence « haute performance » : la carte
    /// dédiée d'un portable), en écartant le rendu logiciel de Microsoft (WARP).
    /// </summary>
    public static IDXGIAdapter1 Preferred(IDXGIFactory2 factory)
    {
        using (var factory6 = factory.QueryInterfaceOrNull<IDXGIFactory6>())
        {
            if (factory6 is not null)
            {
                for (uint i = 0; factory6.EnumAdapterByGpuPreference(i, GpuPreference.HighPerformance, out IDXGIAdapter1? adapter).Success; i++)
                {
                    if (adapter is not null && !IsSoftware(adapter))
                    {
                        return adapter;
                    }

                    adapter?.Dispose();
                }
            }
        }

        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            if (!IsSoftware(adapter))
            {
                return adapter;
            }

            adapter.Dispose();
        }

        throw new InvalidOperationException("Aucune carte graphique compatible avec Direct3D n'a été trouvée.");
    }

    private static bool IsSoftware(IDXGIAdapter1 adapter) => (adapter.Description1.Flags & AdapterFlags.Software) != 0;
}

/// <summary>La carte graphique a été perdue (plantage du pilote, réinitialisation par Windows, carte retirée).</summary>
public sealed class DeviceLostException(string message, Exception? inner = null) : Exception(message, inner)
{
}

internal static class DeviceLost
{
    public static void ThrowIfRemoved(Result reason)
    {
        if (reason.Failure)
        {
            throw new DeviceLostException($"La carte graphique a cessé de répondre (code 0x{reason.Code:X8}).");
        }
    }
}
