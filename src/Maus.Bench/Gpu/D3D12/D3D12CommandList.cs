using System.Runtime.CompilerServices;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Mathematics;

namespace Maus.Bench.Gpu.D3D12;

/// <summary>
/// Commandes Direct3D 12 avec le même comportement que le contexte immédiat de Direct3D 11 : les barrières d'état
/// (rendu, lecture, écriture, copie) sont posées automatiquement avant chaque dessin, calcul ou copie, et les tables
/// de descripteurs (16 textures, 8 sorties) sont recopiées dans le tas visible des shaders quand elles changent.
/// Signature racine commune (voir <see cref="D3D12RootSignature"/>) : b0, b1, table t0-t15, table u0-u7, s0-s5.
/// </summary>
internal sealed unsafe class D3D12CommandList : ICommandList
{
    private const int MaxTextures = 16;
    private const int MaxStorage = 8;
    private const int MaxVertexBuffers = 4;
    private const ResourceStates ShaderRead = ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource;

    private readonly D3D12GpuDevice _owner;
    private readonly List<ResourceBarrier> _barriers = [];
    private readonly D3D12Texture?[] _colors = new D3D12Texture?[8];
    private readonly Binding[] _srv = new Binding[MaxTextures];
    private readonly Binding[] _uav = new Binding[MaxStorage];
    private readonly ulong[] _constants = new ulong[2];
    private readonly VertexBufferView[] _vertexViews = new VertexBufferView[MaxVertexBuffers];
    private readonly D3D12Buffer?[] _vertexBuffers = new D3D12Buffer?[MaxVertexBuffers];
    private readonly CpuDescriptorHandle[] _tableScratch = new CpuDescriptorHandle[MaxTextures];
    private readonly HashSet<ID3D12Resource> _written = [];
    private int _colorCount;
    private int _targetMip;
    private int _targetSlice;
    private D3D12Texture? _depth;
    private D3D12Pipeline? _pipeline;
    private D3D12ComputePipeline? _compute;
    private D3D12Buffer? _indexBuffer;
    private Viewport _viewport;
    private RawRect _scissor;
    private bool _outputDirty = true;
    private bool _srvDirty = true;
    private bool _uavDirty = true;
    private GpuDescriptorHandle _srvTable;
    private GpuDescriptorHandle _uavTable;
    private int _version;
    private int _graphicsVersion = -1;
    private int _computeVersion = -1;

    public D3D12CommandList(D3D12GpuDevice owner)
    {
        _owner = owner;
    }

    public ID3D12GraphicsCommandList List { get; set; } = null!;

    /// <summary>Après Reset de la liste : tout l'état doit être réappliqué (tas, signature, cibles, pipeline, découpe).</summary>
    public void Restart(bool newFrame)
    {
        List.SetDescriptorHeaps(_owner.Ring.Heap);
        List.SetGraphicsRootSignature(_owner.RootSignature);
        List.SetComputeRootSignature(_owner.RootSignature);
        List.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _outputDirty = true;
        _srvDirty = true;
        _uavDirty = true;
        _graphicsVersion = -1;
        _computeVersion = -1;
        _written.Clear();
        if (newFrame)
        {
            Array.Clear(_srv);
            Array.Clear(_uav);
            Array.Clear(_vertexBuffers);
            Array.Clear(_vertexViews);
            _indexBuffer = null;
            _pipeline = null;
            _compute = null;
            _colorCount = 0;
            _depth = null;
            var zeros = _owner.Upload.Allocate(256, 256);
            new Span<byte>((void*)zeros.Cpu, 256).Clear();
            _constants[0] = zeros.Gpu;
            _constants[1] = zeros.Gpu;
            _version++;
            return;
        }

        if (_pipeline is not null)
        {
            List.SetPipelineState(_pipeline.State);
        }
        else if (_compute is not null)
        {
            List.SetPipelineState(_compute.State);
        }

        List.RSSetViewport(_viewport);
        List.RSSetScissorRect(_scissor);
        for (var i = 0; i < MaxVertexBuffers; i++)
        {
            if (_vertexViews[i].BufferLocation != 0)
            {
                List.IASetVertexBuffers((uint)i, _vertexViews[i]);
            }
        }

        if (_indexBuffer is not null)
        {
            List.IASetIndexBuffer(new IndexBufferView(_indexBuffer.Resource.GPUVirtualAddress, (uint)_indexBuffer.Desc.SizeInBytes, true));
        }
    }

    public void SetRenderTargets(ReadOnlySpan<ITexture> colors, ITexture? depth, int mip = 0, int slice = 0)
    {
        Array.Clear(_colors);
        _colorCount = colors.Length;
        for (var i = 0; i < colors.Length; i++)
        {
            _colors[i] = (D3D12Texture)colors[i];
        }

        _depth = depth as D3D12Texture;
        _targetMip = mip;
        _targetSlice = slice;
        _outputDirty = true;
        var reference = colors.Length > 0 ? colors[0] : depth;
        if (reference is not null)
        {
            var width = reference.Desc.MipWidth(mip);
            var height = reference.Desc.MipHeight(mip);
            SetViewport(0, 0, width, height);
            SetScissor(0, 0, width, height);
        }
    }

    public void SetRenderTarget(ITexture? color, ITexture? depth = null, int mip = 0, int slice = 0)
    {
        if (color is null)
        {
            SetRenderTargets([], depth, mip, slice);
        }
        else
        {
            SetRenderTargets([color], depth, mip, slice);
        }
    }

    public void SetViewport(float x, float y, float width, float height)
    {
        _viewport = new Viewport(x, y, width, height, 0, 1);
        List.RSSetViewport(_viewport);
    }

    public void SetScissor(int left, int top, int right, int bottom)
    {
        _scissor = new RawRect(left, top, right, bottom);
        List.RSSetScissorRect(_scissor);
    }

    public void Clear(ITexture target, ColorF color, int mip = 0, int slice = 0)
    {
        var texture = (D3D12Texture)target;
        RequireTexture(texture, mip, slice, ResourceStates.RenderTarget);
        FlushBarriers();
        List.ClearRenderTargetView(texture.TargetView(mip, slice), new Color4(color.R, color.G, color.B, color.A));
    }

    public void ClearDepth(ITexture depth, float value)
    {
        var texture = (D3D12Texture)depth;
        RequireTexture(texture, 0, 0, ResourceStates.DepthWrite);
        FlushBarriers();
        List.ClearDepthStencilView(texture.DepthView(), ClearFlags.Depth, value, 0);
    }

    public void SetPipeline(IPipeline pipeline)
    {
        var p = (D3D12Pipeline)pipeline;
        if ((_pipeline?.Desc.DepthFormat ?? PixelFormat.Unknown) != p.Desc.DepthFormat || _pipeline?.Desc.Depth != p.Desc.Depth)
        {
            _outputDirty = true;
        }

        _pipeline = p;
        _compute = null;
        List.SetPipelineState(p.State);
    }

    public void SetComputePipeline(IComputePipeline pipeline)
    {
        _compute = (D3D12ComputePipeline)pipeline;
        _pipeline = null;
        _outputDirty = true;
        List.SetPipelineState(_compute.State);
    }

    public void SetConstants<T>(int slot, in T data)
        where T : unmanaged
    {
        var size = (sizeof(T) + 255) & ~255;
        var block = _owner.Upload.Allocate(size, 256);
        Unsafe.Write((void*)block.Cpu, data);
        _constants[slot] = block.Gpu;
        _version++;
    }

    public void SetTexture(int slot, ITexture? texture, int mip = -1)
    {
        _srv[slot] = new Binding(texture as D3D12Texture, mip, null);
        _srvDirty = true;
    }

    public void SetBuffer(int slot, IBuffer? buffer)
    {
        _srv[slot] = new Binding(null, -1, buffer as D3D12Buffer);
        _srvDirty = true;
    }

    public void SetStorageTexture(int slot, ITexture? texture, int mip = 0)
    {
        _uav[slot] = new Binding(texture as D3D12Texture, mip, null);
        _uavDirty = true;
    }

    public void SetStorageBuffer(int slot, IBuffer? buffer)
    {
        _uav[slot] = new Binding(null, 0, buffer as D3D12Buffer);
        _uavDirty = true;
    }

    public void SetVertexBuffer(int slot, IBuffer? buffer, int stride)
    {
        var b = buffer as D3D12Buffer;
        _vertexBuffers[slot] = b;
        _vertexViews[slot] = b is null ? default : new VertexBufferView(b.Resource.GPUVirtualAddress, (uint)b.Desc.SizeInBytes, (uint)stride);
        List.IASetVertexBuffers((uint)slot, _vertexViews[slot]);
    }

    public void SetTransientVertices<T>(int slot, ReadOnlySpan<T> vertices)
        where T : unmanaged
    {
        var bytes = vertices.Length * sizeof(T);
        var block = _owner.Upload.Allocate(Math.Max(bytes, 16), 16);
        fixed (T* source = vertices)
        {
            Buffer.MemoryCopy(source, (void*)block.Cpu, bytes, bytes);
        }

        _vertexBuffers[slot] = null;
        _vertexViews[slot] = new VertexBufferView(block.Gpu, (uint)bytes, (uint)sizeof(T));
        List.IASetVertexBuffers((uint)slot, _vertexViews[slot]);
    }

    public void SetIndexBuffer(IBuffer? buffer)
    {
        _indexBuffer = buffer as D3D12Buffer;
        if (_indexBuffer is not null)
        {
            List.IASetIndexBuffer(new IndexBufferView(_indexBuffer.Resource.GPUVirtualAddress, (uint)_indexBuffer.Desc.SizeInBytes, true));
        }
    }

    public void Draw(int vertexCount, int instanceCount = 1, int firstVertex = 0, int firstInstance = 0)
    {
        PrepareDraw();
        List.DrawInstanced((uint)vertexCount, (uint)instanceCount, (uint)firstVertex, (uint)firstInstance);
    }

    public void DrawIndexed(int indexCount, int instanceCount = 1, int firstIndex = 0, int baseVertex = 0, int firstInstance = 0)
    {
        if (_indexBuffer is not null)
        {
            RequireBuffer(_indexBuffer, ResourceStates.IndexBuffer);
        }

        PrepareDraw();
        List.DrawIndexedInstanced((uint)indexCount, (uint)instanceCount, (uint)firstIndex, baseVertex, (uint)firstInstance);
    }

    public void DrawIndexedIndirect(IBuffer arguments, int offsetBytes)
    {
        var args = (D3D12Buffer)arguments;
        RequireBuffer(args, ResourceStates.IndirectArgument);
        if (_indexBuffer is not null)
        {
            RequireBuffer(_indexBuffer, ResourceStates.IndexBuffer);
        }

        PrepareDraw();
        List.ExecuteIndirect(_owner.DrawIndexedSignature, 1, args.Resource, (ulong)offsetBytes, null, 0);
    }

    public void Dispatch(int groupsX, int groupsY, int groupsZ)
    {
        // Lecture-écriture d'une même sortie par deux calculs successifs : barrière d'accès non ordonné.
        foreach (var binding in _uav)
        {
            var resource = binding.Texture?.Resource ?? binding.Buffer?.Resource;
            if (resource is not null && _written.Contains(resource))
            {
                _barriers.Add(ResourceBarrier.BarrierUnorderedAccessView(resource));
                _written.Remove(resource);
            }
        }

        for (var i = 0; i < MaxStorage; i++)
        {
            RequireBinding(_uav[i], ResourceStates.UnorderedAccess);
        }

        for (var i = 0; i < MaxTextures; i++)
        {
            RequireBinding(_srv[i], ShaderRead);
        }

        FlushBarriers();
        UpdateTables();
        if (_computeVersion != _version)
        {
            List.SetComputeRootConstantBufferView(0, _constants[0]);
            List.SetComputeRootConstantBufferView(1, _constants[1]);
            List.SetComputeRootDescriptorTable(2, _srvTable);
            List.SetComputeRootDescriptorTable(3, _uavTable);
            _computeVersion = _version;
        }

        List.Dispatch((uint)groupsX, (uint)groupsY, (uint)groupsZ);
        foreach (var binding in _uav)
        {
            if ((binding.Texture?.Resource ?? binding.Buffer?.Resource) is { } resource)
            {
                _written.Add(resource);
            }
        }
    }

    public void UpdateBuffer<T>(IBuffer buffer, ReadOnlySpan<T> data, int offsetBytes = 0)
        where T : unmanaged
    {
        var target = (D3D12Buffer)buffer;
        var bytes = data.Length * sizeof(T);
        var block = _owner.Upload.Allocate(bytes, 16);
        fixed (T* source = data)
        {
            Buffer.MemoryCopy(source, (void*)block.Cpu, bytes, bytes);
        }

        RequireBuffer(target, ResourceStates.CopyDest);
        FlushBarriers();
        List.CopyBufferRegion(target.Resource, (ulong)offsetBytes, block.Resource, block.Offset, (ulong)bytes);
    }

    public void CopyTexture(ITexture destination, ITexture source)
    {
        var dst = (D3D12Texture)destination;
        var src = (D3D12Texture)source;
        RequireTexture(dst, -1, -1, ResourceStates.CopyDest);
        RequireTexture(src, -1, -1, ResourceStates.CopySource);
        FlushBarriers();
        List.CopyResource(dst.Resource, src.Resource);
    }

    public void Flush() => _owner.SubmitAndReopen();

    /// <summary>Passe une texture (ou une partie) dans l'état demandé ; les barrières partent au prochain FlushBarriers.</summary>
    public void RequireTexture(D3D12Texture texture, int mip, int slice, ResourceStates state)
    {
        var mips = texture.Desc.MipLevels;
        var slices = texture.Slices;
        if (mip < 0 && slice < 0)
        {
            var first = texture.States[0];
            var uniform = Array.TrueForAll(texture.States, s => s == first);
            if (uniform)
            {
                if (first != state)
                {
                    _barriers.Add(ResourceBarrier.BarrierTransition(texture.Resource, first, state));
                    Array.Fill(texture.States, state);
                }

                return;
            }
        }

        for (var s = 0; s < slices; s++)
        {
            if (slice >= 0 && s != slice)
            {
                continue;
            }

            for (var m = 0; m < mips; m++)
            {
                if (mip >= 0 && m != mip)
                {
                    continue;
                }

                var index = texture.Subresource(m, s);
                if (texture.States[index] != state)
                {
                    _barriers.Add(ResourceBarrier.BarrierTransition(texture.Resource, texture.States[index], state, (uint)index));
                    texture.States[index] = state;
                }
            }
        }
    }

    public void RequireBuffer(D3D12Buffer buffer, ResourceStates state)
    {
        if (buffer.State != state)
        {
            _barriers.Add(ResourceBarrier.BarrierTransition(buffer.Resource, buffer.State, state));
            buffer.State = state;
        }
    }

    public void FlushBarriers()
    {
        if (_barriers.Count > 0)
        {
            List.ResourceBarrier(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_barriers));
            _barriers.Clear();
        }
    }

    private void PrepareDraw()
    {
        var pipeline = _pipeline ?? throw new InvalidOperationException("Aucun pipeline de dessin choisi.");
        var depthBound = _depth is not null && pipeline.Desc.DepthFormat != PixelFormat.Unknown;
        var depthReadOnly = depthBound && pipeline.Desc.Depth != DepthMode.TestWrite;

        for (var i = 0; i < _colorCount; i++)
        {
            RequireTexture(_colors[i]!, _targetMip, _colors[i]!.Slices > 1 ? _targetSlice : 0, ResourceStates.RenderTarget);
        }

        // La profondeur peut être à la fois testée (lecture seule) et lue par les shaders : états combinés.
        var depthAlsoSampled = depthReadOnly && Array.Exists(_srv, b => ReferenceEquals(b.Texture, _depth));
        if (depthBound)
        {
            var state = depthReadOnly ? ResourceStates.DepthRead | (depthAlsoSampled ? ShaderRead : 0) : ResourceStates.DepthWrite;
            RequireTexture(_depth!, 0, 0, state);
        }

        for (var i = 0; i < MaxTextures; i++)
        {
            if (depthAlsoSampled && ReferenceEquals(_srv[i].Texture, _depth))
            {
                continue;
            }

            RequireBinding(_srv[i], ShaderRead);
        }

        for (var i = 0; i < MaxVertexBuffers; i++)
        {
            if (_vertexBuffers[i] is { } vb)
            {
                RequireBuffer(vb, ResourceStates.VertexAndConstantBuffer);
            }
        }

        FlushBarriers();
        if (_outputDirty)
        {
            Span<CpuDescriptorHandle> targets = stackalloc CpuDescriptorHandle[8];
            for (var i = 0; i < _colorCount; i++)
            {
                targets[i] = _colors[i]!.TargetView(_targetMip, _colors[i]!.Slices > 1 ? _targetSlice : 0);
            }

            CpuDescriptorHandle? depthView = depthBound ? _depth!.DepthView() : null;
            List.OMSetRenderTargets(targets[.._colorCount], depthView);
            _outputDirty = false;
        }

        UpdateTables();
        if (_graphicsVersion != _version)
        {
            List.SetGraphicsRootConstantBufferView(0, _constants[0]);
            List.SetGraphicsRootConstantBufferView(1, _constants[1]);
            List.SetGraphicsRootDescriptorTable(2, _srvTable);
            List.SetGraphicsRootDescriptorTable(3, _uavTable);
            _graphicsVersion = _version;
        }
    }

    private void RequireBinding(Binding binding, ResourceStates state)
    {
        if (binding.Texture is { } texture)
        {
            RequireTexture(texture, binding.Mip, -1, state);
        }
        else if (binding.Buffer is { } buffer)
        {
            RequireBuffer(buffer, state);
        }
    }

    private void UpdateTables()
    {
        if (_srvDirty)
        {
            for (var i = 0; i < MaxTextures; i++)
            {
                var b = _srv[i];
                _tableScratch[i] = b.Texture is not null ? b.Texture.ShaderView(b.Mip) : b.Buffer is not null ? b.Buffer.ShaderView : _owner.NullTexture;
            }

            _srvTable = _owner.Ring.Upload(_tableScratch);
            _srvDirty = false;
            _version++;
        }

        if (_uavDirty)
        {
            for (var i = 0; i < MaxStorage; i++)
            {
                var b = _uav[i];
                _tableScratch[i] = b.Texture is not null ? b.Texture.StorageView(b.Mip) : b.Buffer is not null ? b.Buffer.StorageView : _owner.NullStorage;
            }

            _uavTable = _owner.Ring.Upload(_tableScratch.AsSpan(0, MaxStorage));
            _uavDirty = false;
            _version++;
        }
    }

    private readonly record struct Binding(D3D12Texture? Texture, int Mip, D3D12Buffer? Buffer);
}
