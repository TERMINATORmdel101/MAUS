using Vortice.Direct3D12;

namespace Maus.Bench.Gpu.D3D12;

/// <summary>
/// Tas de descripteurs invisible des shaders (vues créées une fois par ressource), avec réemploi des places libérées.
/// </summary>
internal sealed class CpuDescriptorHeap : IDisposable
{
    private readonly ID3D12DescriptorHeap _heap;
    private readonly CpuDescriptorHandle _start;
    private readonly Stack<int> _free = new();
    private readonly int _capacity;
    private int _next;

    public CpuDescriptorHeap(ID3D12Device device, DescriptorHeapType type, int capacity)
    {
        _capacity = capacity;
        _heap = device.CreateDescriptorHeap(new DescriptorHeapDescription(type, (uint)capacity));
        _start = _heap.GetCPUDescriptorHandleForHeapStart();
        Increment = device.GetDescriptorHandleIncrementSize(type);
    }

    public uint Increment { get; }

    public CpuDescriptorHandle Allocate()
    {
        int index;
        if (_free.Count > 0)
        {
            index = _free.Pop();
        }
        else if (_next < _capacity)
        {
            index = _next++;
        }
        else
        {
            throw new InvalidOperationException("Plus de place pour les descripteurs Direct3D 12.");
        }

        return _start.Offset(index, Increment);
    }

    public void Free(CpuDescriptorHandle handle)
    {
        var index = (int)((handle.Ptr - _start.Ptr) / Increment);
        _free.Push(index);
    }

    public void Dispose() => _heap.Dispose();
}

/// <summary>
/// Tas de descripteurs visible des shaders, découpé en une portion par image en vol : chaque dessin y recopie ses
/// tables (16 textures, 8 sorties de calcul) ; la portion d'une image n'est réécrite qu'après la fin de cette image.
/// </summary>
internal sealed class GpuDescriptorRing : IDisposable
{
    private readonly ID3D12Device _device;
    private readonly CpuDescriptorHandle _cpuStart;
    private readonly GpuDescriptorHandle _gpuStart;
    private readonly int _perFrame;
    private int _frameBase;
    private int _used;

    public GpuDescriptorRing(ID3D12Device device, int framesInFlight, int perFrame)
    {
        _device = device;
        _perFrame = perFrame;
        Heap = device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, (uint)(framesInFlight * perFrame), DescriptorHeapFlags.ShaderVisible));
        _cpuStart = Heap.GetCPUDescriptorHandleForHeapStart();
        _gpuStart = Heap.GetGPUDescriptorHandleForHeapStart();
        Increment = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
    }

    public ID3D12DescriptorHeap Heap { get; }

    public uint Increment { get; }

    public void BeginFrame(int frameSlot)
    {
        _frameBase = frameSlot * _perFrame;
        _used = 0;
    }

    /// <summary>Recopie <paramref name="sources"/> à la suite dans la portion de l'image et rend l'adresse de la table.</summary>
    public GpuDescriptorHandle Upload(ReadOnlySpan<CpuDescriptorHandle> sources)
    {
        if (_used + sources.Length > _perFrame)
        {
            throw new InvalidOperationException("Trop de tables de descripteurs pour une seule image.");
        }

        var index = _frameBase + _used;
        for (var i = 0; i < sources.Length; i++)
        {
            _device.CopyDescriptorsSimple(1, _cpuStart.Offset(index + i, Increment), sources[i], DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        }

        _used += sources.Length;
        return _gpuStart.Offset(index, Increment);
    }

    public void Dispose() => Heap.Dispose();
}
