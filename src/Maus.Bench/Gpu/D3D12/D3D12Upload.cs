using Vortice.Direct3D12;

namespace Maus.Bench.Gpu.D3D12;

/// <summary>
/// Mémoire « upload » (lisible par la carte, écrite par le processeur) d'une image en vol : constantes, sommets
/// temporaires, mises à jour de tampons. Pages de 16 Mo (ou plus pour une grosse demande), rendues au début de la
/// même image deux présentations plus tard, quand la carte a fini de les lire.
/// </summary>
internal sealed unsafe class UploadArena : IDisposable
{
    private const long PageBytes = 16L * 1024 * 1024;
    private readonly ID3D12Device _device;
    private readonly List<Page> _pages = [];
    private int _current;

    public UploadArena(ID3D12Device device)
    {
        _device = device;
        _pages.Add(new Page(device, PageBytes));
    }

    /// <summary>Réserve <paramref name="bytes"/> octets alignés ; rend l'adresse pour le processeur et pour la carte.</summary>
    public (IntPtr Cpu, ulong Gpu, ID3D12Resource Resource, ulong Offset) Allocate(long bytes, int alignment)
    {
        var page = _pages[_current];
        var offset = Align(page.Used, alignment);
        if (offset + bytes > page.Size)
        {
            _current++;
            if (_current == _pages.Count || _pages[_current].Size < bytes)
            {
                _pages.Insert(_current, new Page(_device, Math.Max(PageBytes, Align(bytes, 65536))));
            }

            page = _pages[_current];
            page.Used = 0;
            offset = 0;
        }

        page.Used = offset + bytes;
        return ((IntPtr)(page.Mapped + offset), page.Resource.GPUVirtualAddress + (ulong)offset, page.Resource, (ulong)offset);
    }

    /// <summary>Début d'image : toutes les pages redeviennent libres (la carte a terminé l'image précédente sur cette place).</summary>
    public void Reset()
    {
        foreach (var page in _pages)
        {
            page.Used = 0;
        }

        _current = 0;
    }

    public void Dispose()
    {
        foreach (var page in _pages)
        {
            page.Dispose();
        }
    }

    private static long Align(long value, int alignment) => (value + alignment - 1) & ~((long)alignment - 1);

    private sealed class Page : IDisposable
    {
        public Page(ID3D12Device device, long size)
        {
            Size = size;
            Resource = device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer((ulong)size), ResourceStates.GenericRead);
            void* pointer;
            Resource.Map(0, null, &pointer).CheckError();
            Mapped = (byte*)pointer;
        }

        public long Size { get; }

        public long Used { get; set; }

        public ID3D12Resource Resource { get; }

        public byte* Mapped { get; }

        public void Dispose()
        {
            Resource.Unmap(0);
            Resource.Dispose();
        }
    }
}
