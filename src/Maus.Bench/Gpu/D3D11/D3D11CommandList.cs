using System.Runtime.CompilerServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Maus.Bench.Gpu.D3D11;

/// <summary>
/// Commandes Direct3D 11 sur le contexte immédiat. Les textures et tampons lus par les shaders sont appliqués au moment
/// du dessin, après les cibles : Direct3D 11 refuse (en les remplaçant par rien) les lectures d'une ressource encore
/// attachée en écriture, et cet ordre évite le piège quel que soit l'ordre des appels dans les scènes.
/// </summary>
internal sealed unsafe class D3D11CommandList : ICommandList, IDisposable
{
    public const int MaxTextures = 16;
    public const int MaxStorage = 8;
    public const int MaxConstantBytes = 4096;
    private const int TransientBytes = 32 * 1024 * 1024;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Buffer[] _constants = new ID3D11Buffer[2];
    private readonly ID3D11ShaderResourceView[] _srvs = new ID3D11ShaderResourceView[MaxTextures];
    private readonly ID3D11UnorderedAccessView[] _uavs = new ID3D11UnorderedAccessView[MaxStorage];
    private readonly ID3D11UnorderedAccessView[] _noUavs = new ID3D11UnorderedAccessView[MaxStorage];
    private readonly ID3D11RenderTargetView[] _rtvs = new ID3D11RenderTargetView[8];
    private readonly ID3D11Buffer _transient;
    private int _rtvCount;
    private ID3D11DepthStencilView? _dsv;
    private bool _outputDirty = true;
    private int _transientOffset;
    private ID3D11Buffer? _indirectBound;

    public D3D11CommandList(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;
        for (var i = 0; i < _constants.Length; i++)
        {
            _constants[i] = device.CreateBuffer(MaxConstantBytes, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
            _context.VSSetConstantBuffer((uint)i, _constants[i]);
            _context.PSSetConstantBuffer((uint)i, _constants[i]);
            _context.CSSetConstantBuffer((uint)i, _constants[i]);
        }

        _transient = device.CreateBuffer(TransientBytes, BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
        Samplers = CreateSamplers(device);
        BindSamplers();
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
    }

    public ID3D11SamplerState[] Samplers { get; }

    public void BindSamplers()
    {
        for (var i = 0; i < Samplers.Length; i++)
        {
            _context.VSSetSampler((uint)i, Samplers[i]);
            _context.PSSetSampler((uint)i, Samplers[i]);
            _context.CSSetSampler((uint)i, Samplers[i]);
        }
    }

    public void SetRenderTargets(ReadOnlySpan<ITexture> colors, ITexture? depth, int mip = 0, int slice = 0)
    {
        Array.Clear(_rtvs);
        _rtvCount = colors.Length;
        for (var i = 0; i < colors.Length; i++)
        {
            _rtvs[i] = ((D3D11Texture)colors[i]).TargetView(mip, slice);
        }

        _dsv = depth is null ? null : ((D3D11Texture)depth).DepthView();
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

    public void SetViewport(float x, float y, float width, float height) => _context.RSSetViewport(x, y, width, height);

    public void SetScissor(int left, int top, int right, int bottom) => _context.RSSetScissorRect(left, top, right - left, bottom - top);

    public void Clear(ITexture target, ColorF color, int mip = 0, int slice = 0) =>
        _context.ClearRenderTargetView(((D3D11Texture)target).TargetView(mip, slice), new Color4(color.R, color.G, color.B, color.A));

    public void ClearDepth(ITexture depth, float value) =>
        _context.ClearDepthStencilView(((D3D11Texture)depth).DepthView(), DepthStencilClearFlags.Depth, value, 0);

    public void SetPipeline(IPipeline pipeline)
    {
        var p = (D3D11Pipeline)pipeline;
        _context.VSSetShader(p.VertexShader);
        _context.PSSetShader(p.PixelShader);
        _context.IASetInputLayout(p.InputLayout);
        _context.OMSetBlendState(p.Blend);
        _context.OMSetDepthStencilState(p.DepthStencil);
        _context.RSSetState(p.Rasterizer);
    }

    public void SetComputePipeline(IComputePipeline pipeline) => _context.CSSetShader(((D3D11ComputePipeline)pipeline).Shader);

    public void SetConstants<T>(int slot, in T data)
        where T : unmanaged
    {
        if (sizeof(T) > MaxConstantBytes)
        {
            throw new ArgumentException("Constantes trop grandes : " + typeof(T).Name);
        }

        var mapped = _context.Map(_constants[slot], MapMode.WriteDiscard);
        Unsafe.Write((void*)mapped.DataPointer, data);
        _context.Unmap(_constants[slot]);
    }

    public void SetTexture(int slot, ITexture? texture, int mip = -1) =>
        _srvs[slot] = texture is null ? null! : ((D3D11Texture)texture).ShaderView(mip);

    public void SetBuffer(int slot, IBuffer? buffer) =>
        _srvs[slot] = buffer is null ? null! : ((D3D11Buffer)buffer).ShaderView ?? throw new InvalidOperationException("Tampon non lisible : " + buffer.Desc.Name);

    public void SetStorageTexture(int slot, ITexture? texture, int mip = 0) =>
        _uavs[slot] = texture is null ? null! : ((D3D11Texture)texture).StorageView(mip);

    public void SetStorageBuffer(int slot, IBuffer? buffer) =>
        _uavs[slot] = buffer is null ? null! : ((D3D11Buffer)buffer).StorageView ?? throw new InvalidOperationException("Tampon non inscriptible : " + buffer.Desc.Name);

    public void SetVertexBuffer(int slot, IBuffer? buffer, int stride) =>
        _context.IASetVertexBuffer((uint)slot, (buffer as D3D11Buffer)?.Buffer!, (uint)stride);

    public void SetTransientVertices<T>(int slot, ReadOnlySpan<T> vertices)
        where T : unmanaged
    {
        var bytes = vertices.Length * sizeof(T);
        if (bytes > TransientBytes)
        {
            throw new ArgumentException("Trop de sommets temporaires.");
        }

        var mode = MapMode.WriteNoOverwrite;
        var offset = (_transientOffset + 15) & ~15;
        if (offset + bytes > TransientBytes)
        {
            mode = MapMode.WriteDiscard;
            offset = 0;
        }

        var mapped = _context.Map(_transient, mode);
        fixed (T* source = vertices)
        {
            Buffer.MemoryCopy(source, (byte*)mapped.DataPointer + offset, TransientBytes - offset, bytes);
        }

        _context.Unmap(_transient);
        _context.IASetVertexBuffer((uint)slot, _transient, (uint)sizeof(T), (uint)offset);
        _transientOffset = offset + bytes;
    }

    public void SetIndexBuffer(IBuffer? buffer) => _context.IASetIndexBuffer((buffer as D3D11Buffer)?.Buffer, Format.R32_UInt, 0);

    public void Draw(int vertexCount, int instanceCount = 1, int firstVertex = 0, int firstInstance = 0)
    {
        PrepareDraw();
        _context.DrawInstanced((uint)vertexCount, (uint)instanceCount, (uint)firstVertex, (uint)firstInstance);
    }

    public void DrawIndexed(int indexCount, int instanceCount = 1, int firstIndex = 0, int baseVertex = 0, int firstInstance = 0)
    {
        PrepareDraw();
        _context.DrawIndexedInstanced((uint)indexCount, (uint)instanceCount, (uint)firstIndex, baseVertex, (uint)firstInstance);
    }

    public void DrawIndexedIndirect(IBuffer arguments, int offsetBytes)
    {
        PrepareDraw();
        var buffer = ((D3D11Buffer)arguments).Buffer;
        _indirectBound = buffer;
        _context.DrawIndexedInstancedIndirect(buffer, (uint)offsetBytes);
    }

    public void Dispatch(int groupsX, int groupsY, int groupsZ)
    {
        // Une cible encore attachée empêcherait d'écrire la même texture depuis le shader de calcul.
        if (_rtvCount > 0 || _dsv is not null)
        {
            _context.OMSetRenderTargets(0, Array.Empty<ID3D11RenderTargetView>(), null);
            _outputDirty = true;
        }

        _context.CSSetUnorderedAccessViews(0, MaxStorage, _uavs);
        _context.CSSetShaderResources(0, MaxTextures, _srvs);
        _context.Dispatch((uint)groupsX, (uint)groupsY, (uint)groupsZ);

        // Libère les écritures : la même ressource pourra être lue au passage suivant.
        _context.CSSetUnorderedAccessViews(0, MaxStorage, _noUavs);
        Array.Clear(_uavs);
    }

    public void UpdateBuffer<T>(IBuffer buffer, ReadOnlySpan<T> data, int offsetBytes = 0)
        where T : unmanaged
    {
        var bytes = data.Length * sizeof(T);
        var box = new Box(offsetBytes, 0, 0, offsetBytes + bytes, 1, 1);
        _context.UpdateSubresource(data, ((D3D11Buffer)buffer).Buffer, 0, 0, 0, box);
    }

    public void CopyTexture(ITexture destination, ITexture source) =>
        _context.CopyResource(((D3D11Texture)destination).Resource, ((D3D11Texture)source).Resource);

    public void Flush() => _context.Flush();

    /// <summary>Début d'image : le tampon tournant repart de zéro, l'état des sorties est réappliqué.</summary>
    public void BeginFrame()
    {
        _outputDirty = true;
        Array.Clear(_srvs);
        Array.Clear(_uavs);
    }

    public void Dispose()
    {
        foreach (var buffer in _constants)
        {
            buffer.Dispose();
        }

        foreach (var sampler in Samplers)
        {
            sampler.Dispose();
        }

        _transient.Dispose();
        GC.KeepAlive(_device);
        GC.KeepAlive(_indirectBound);
    }

    private void PrepareDraw()
    {
        if (_outputDirty)
        {
            _context.OMSetRenderTargets((uint)_rtvCount, _rtvs, _dsv);
            _outputDirty = false;
        }

        _context.VSSetShaderResources(0, MaxTextures, _srvs);
        _context.PSSetShaderResources(0, MaxTextures, _srvs);
    }

    /// <summary>Échantillonneurs s0 à s5, identiques aux échantillonneurs fixes du moteur Direct3D 12 (voir common.hlsli).</summary>
    private static ID3D11SamplerState[] CreateSamplers(ID3D11Device device) =>
    [
        device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Wrap)),
        device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp)),
        device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp)),
        device.CreateSamplerState(new SamplerDescription(Filter.Anisotropic, TextureAddressMode.Wrap, 0, 16)),
        device.CreateSamplerState(new SamplerDescription(Filter.ComparisonMinMagMipLinear, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.LessEqual)),
        device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Wrap)),
    ];
}
