using Maus.Bench.Gpu.D3D11;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Maus.Bench.Gpu.D3D12;

/// <summary>
/// Texture Direct3D 12 : ressource, état de chaque sous-ressource (niveau × face) pour les barrières, vues créées à la
/// demande dans les tas invisibles des shaders.
/// </summary>
internal sealed class D3D12Texture : ITexture
{
    private readonly D3D12GpuDevice _owner;
    private readonly bool _ownsResource;
    private readonly Dictionary<int, CpuDescriptorHandle> _shaderViews = [];
    private readonly Dictionary<(int Mip, int Slice), CpuDescriptorHandle> _targets = [];
    private readonly Dictionary<int, CpuDescriptorHandle> _storage = [];
    private CpuDescriptorHandle? _depth;

    public D3D12Texture(D3D12GpuDevice owner, TextureDesc desc, ID3D12Resource? existing = null, ResourceStates initialState = ResourceStates.Common)
    {
        _owner = owner;
        Desc = desc;
        _ownsResource = existing is null;
        Resource = existing ?? Create(owner.Device, desc);
        Slices = desc.Kind == TextureKind.TextureCube ? 6 : 1;
        States = new ResourceStates[desc.MipLevels * Slices];
        Array.Fill(States, initialState);
    }

    public TextureDesc Desc { get; }

    public ID3D12Resource Resource { get; }

    public int Slices { get; }

    /// <summary>État actuel de chaque sous-ressource (niveau + face × niveaux), tenu par la liste de commandes.</summary>
    public ResourceStates[] States { get; }

    public int Subresource(int mip, int slice) => mip + (slice * Desc.MipLevels);

    public CpuDescriptorHandle ShaderView(int mip)
    {
        if (_shaderViews.TryGetValue(mip, out var handle))
        {
            return handle;
        }

        var format = DxgiFormats.View(Desc.Format);
        var first = mip < 0 ? 0u : (uint)mip;
        var count = mip < 0 ? uint.MaxValue : 1u;
        var description = new ShaderResourceViewDescription
        {
            Format = format,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
        };
        switch (Desc.Kind)
        {
            case TextureKind.Texture3D:
                description.ViewDimension = ShaderResourceViewDimension.Texture3D;
                description.Texture3D = new Texture3DShaderResourceView { MostDetailedMip = first, MipLevels = count };
                break;
            case TextureKind.TextureCube:
                description.ViewDimension = ShaderResourceViewDimension.TextureCube;
                description.TextureCube = new TextureCubeShaderResourceView { MostDetailedMip = first, MipLevels = count };
                break;
            default:
                description.ViewDimension = ShaderResourceViewDimension.Texture2D;
                description.Texture2D = new Texture2DShaderResourceView { MostDetailedMip = first, MipLevels = count };
                break;
        }

        handle = _owner.ShaderHeap.Allocate();
        _owner.Device.CreateShaderResourceView(Resource, description, handle);
        _shaderViews[mip] = handle;
        return handle;
    }

    public CpuDescriptorHandle TargetView(int mip, int slice)
    {
        if (_targets.TryGetValue((mip, slice), out var handle))
        {
            return handle;
        }

        var description = new RenderTargetViewDescription { Format = DxgiFormats.View(Desc.Format) };
        if (Desc.Kind == TextureKind.TextureCube)
        {
            description.ViewDimension = RenderTargetViewDimension.Texture2DArray;
            description.Texture2DArray = new Texture2DArrayRenderTargetView { MipSlice = (uint)mip, FirstArraySlice = (uint)slice, ArraySize = 1 };
        }
        else
        {
            description.ViewDimension = RenderTargetViewDimension.Texture2D;
            description.Texture2D = new Texture2DRenderTargetView { MipSlice = (uint)mip };
        }

        handle = _owner.TargetHeap.Allocate();
        _owner.Device.CreateRenderTargetView(Resource, description, handle);
        _targets[(mip, slice)] = handle;
        return handle;
    }

    public CpuDescriptorHandle DepthView()
    {
        if (_depth is { } existing)
        {
            return existing;
        }

        var handle = _owner.DepthHeap.Allocate();
        _owner.Device.CreateDepthStencilView(Resource, new DepthStencilViewDescription
        {
            Format = Format.D32_Float,
            ViewDimension = DepthStencilViewDimension.Texture2D,
            Texture2D = new Texture2DDepthStencilView { MipSlice = 0 },
        }, handle);
        _depth = handle;
        return handle;
    }

    public CpuDescriptorHandle StorageView(int mip)
    {
        if (_storage.TryGetValue(mip, out var handle))
        {
            return handle;
        }

        var description = new UnorderedAccessViewDescription { Format = DxgiFormats.View(Desc.Format) };
        if (Desc.Kind == TextureKind.Texture3D)
        {
            description.ViewDimension = UnorderedAccessViewDimension.Texture3D;
            description.Texture3D = new Texture3DUnorderedAccessView { MipSlice = (uint)mip, FirstWSlice = 0, WSize = uint.MaxValue };
        }
        else
        {
            description.ViewDimension = UnorderedAccessViewDimension.Texture2D;
            description.Texture2D = new Texture2DUnorderedAccessView { MipSlice = (uint)mip };
        }

        handle = _owner.ShaderHeap.Allocate();
        _owner.Device.CreateUnorderedAccessView(Resource, null, description, handle);
        _storage[mip] = handle;
        return handle;
    }

    public void Dispose()
    {
        _owner.Retire(this, _ownsResource ? Resource : null);
        foreach (var handle in _shaderViews.Values)
        {
            _owner.ShaderHeap.Free(handle);
        }

        foreach (var handle in _storage.Values)
        {
            _owner.ShaderHeap.Free(handle);
        }

        foreach (var handle in _targets.Values)
        {
            _owner.TargetHeap.Free(handle);
        }

        if (_depth is { } depth)
        {
            _owner.DepthHeap.Free(depth);
        }
    }

    private static ID3D12Resource Create(ID3D12Device device, TextureDesc desc)
    {
        var flags = ResourceFlags.None;
        if (desc.Usage.HasFlag(TextureUsage.RenderTarget))
        {
            flags |= ResourceFlags.AllowRenderTarget;
        }

        if (desc.Usage.HasFlag(TextureUsage.DepthStencil))
        {
            flags |= ResourceFlags.AllowDepthStencil;
        }

        if (desc.Usage.HasFlag(TextureUsage.Storage))
        {
            flags |= ResourceFlags.AllowUnorderedAccess;
        }

        var format = DxgiFormats.Storage(desc.Format);
        var description = desc.Kind switch
        {
            TextureKind.Texture3D => ResourceDescription.Texture3D(format, (uint)desc.Width, (uint)desc.Height, (ushort)desc.Depth, (ushort)desc.MipLevels, flags),
            TextureKind.TextureCube => ResourceDescription.Texture2D(format, (uint)desc.Width, (uint)desc.Height, 6, (ushort)desc.MipLevels, flags: flags),
            _ => ResourceDescription.Texture2D(format, (uint)desc.Width, (uint)desc.Height, 1, (ushort)desc.MipLevels, flags: flags),
        };
        var resource = device.CreateCommittedResource(HeapType.Default, description, ResourceStates.Common);
        resource.Name = desc.Name;
        return resource;
    }
}

internal sealed class D3D12Buffer : IBuffer
{
    private readonly D3D12GpuDevice _owner;
    private readonly CpuDescriptorHandle? _shaderView;
    private readonly CpuDescriptorHandle? _storageView;

    public D3D12Buffer(D3D12GpuDevice owner, BufferDesc desc)
    {
        _owner = owner;
        Desc = desc;
        var flags = desc.Usage.HasFlag(BufferUsage.Storage) ? ResourceFlags.AllowUnorderedAccess : ResourceFlags.None;
        Resource = owner.Device.CreateCommittedResource(HeapType.Default, ResourceDescription.Buffer((ulong)desc.SizeInBytes, flags), ResourceStates.Common);
        Resource.Name = desc.Name;
        var raw = desc.Usage.HasFlag(BufferUsage.Raw) || desc.Usage.HasFlag(BufferUsage.IndirectArgs);
        var elements = raw ? (uint)(desc.SizeInBytes / 4) : (uint)(desc.SizeInBytes / Math.Max(1, desc.Stride));
        if (desc.Usage.HasFlag(BufferUsage.Structured))
        {
            var handle = owner.ShaderHeap.Allocate();
            owner.Device.CreateShaderResourceView(Resource, new ShaderResourceViewDescription
            {
                Format = raw ? Format.R32_Typeless : Format.Unknown,
                ViewDimension = ShaderResourceViewDimension.Buffer,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Buffer = new BufferShaderResourceView
                {
                    FirstElement = 0,
                    NumElements = elements,
                    StructureByteStride = raw ? 0u : (uint)desc.Stride,
                    Flags = raw ? BufferShaderResourceViewFlags.Raw : BufferShaderResourceViewFlags.None,
                },
            }, handle);
            _shaderView = handle;
        }

        if (desc.Usage.HasFlag(BufferUsage.Storage))
        {
            var handle = owner.ShaderHeap.Allocate();
            owner.Device.CreateUnorderedAccessView(Resource, null, new UnorderedAccessViewDescription
            {
                Format = raw ? Format.R32_Typeless : Format.Unknown,
                ViewDimension = UnorderedAccessViewDimension.Buffer,
                Buffer = new BufferUnorderedAccessView
                {
                    FirstElement = 0,
                    NumElements = elements,
                    StructureByteStride = raw ? 0u : (uint)desc.Stride,
                    Flags = raw ? BufferUnorderedAccessViewFlags.Raw : BufferUnorderedAccessViewFlags.None,
                },
            }, handle);
            _storageView = handle;
        }
    }

    public BufferDesc Desc { get; }

    public ID3D12Resource Resource { get; }

    /// <summary>État actuel, tenu par la liste de commandes.</summary>
    public ResourceStates State { get; set; } = ResourceStates.Common;

    public CpuDescriptorHandle ShaderView => _shaderView ?? throw new InvalidOperationException("Tampon non lisible : " + Desc.Name);

    public CpuDescriptorHandle StorageView => _storageView ?? throw new InvalidOperationException("Tampon non inscriptible : " + Desc.Name);

    public void Dispose()
    {
        _owner.Retire(this, Resource);
        if (_shaderView is { } srv)
        {
            _owner.ShaderHeap.Free(srv);
        }

        if (_storageView is { } uav)
        {
            _owner.ShaderHeap.Free(uav);
        }
    }
}

internal sealed class D3D12Pipeline : IPipeline
{
    public D3D12Pipeline(ID3D12Device device, ID3D12RootSignature rootSignature, GraphicsPipelineDesc desc)
    {
        Desc = desc;
        var description = new GraphicsPipelineStateDescription
        {
            RootSignature = rootSignature,
            VertexShader = desc.VertexShader.Bytecode,
            PixelShader = desc.PixelShader?.Bytecode ?? ReadOnlyMemory<byte>.Empty,
            BlendState = Blend(desc.Blend),
            SampleMask = uint.MaxValue,
            RasterizerState = new RasterizerDescription(
                desc.Cull switch { CullMode.Back => Vortice.Direct3D12.CullMode.Back, CullMode.Front => Vortice.Direct3D12.CullMode.Front, _ => Vortice.Direct3D12.CullMode.None },
                FillMode.Solid,
                frontCounterClockwise: false,
                depthBias: desc.DepthBias,
                slopeScaledDepthBias: desc.SlopeScaledDepthBias,
                depthClipEnable: !desc.DepthClamp),
            DepthStencilState = new DepthStencilDescription(
                desc.Depth != DepthMode.None,
                desc.Depth == DepthMode.TestWrite ? DepthWriteMask.All : DepthWriteMask.Zero,
                desc.DepthGreater ? ComparisonFunction.GreaterEqual : ComparisonFunction.LessEqual),
            InputLayout = new InputLayoutDescription(desc.Layout.Select(e => new InputElementDescription(
                e.Semantic,
                (uint)e.SemanticIndex,
                DxgiFormats.ToDxgi(e.Format),
                (uint)e.Offset,
                (uint)e.Slot,
                e.PerInstance ? InputClassification.PerInstanceData : InputClassification.PerVertexData,
                e.PerInstance ? 1u : 0u)).ToArray()),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = desc.RenderTargets.Select(DxgiFormats.ToDxgi).ToArray(),
            DepthStencilFormat = desc.DepthFormat == PixelFormat.Unknown ? Format.Unknown : Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0),
        };
        State = device.CreateGraphicsPipelineState(description);
        State.Name = desc.Name;
    }

    public GraphicsPipelineDesc Desc { get; }

    public ID3D12PipelineState State { get; }

    public void Dispose() => State.Dispose();

    private static BlendDescription Blend(BlendMode mode)
    {
        var description = new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
        var target = mode switch
        {
            BlendMode.Additive => Enabled(Vortice.Direct3D12.Blend.One, Vortice.Direct3D12.Blend.One, Vortice.Direct3D12.Blend.One, Vortice.Direct3D12.Blend.One),
            BlendMode.AlphaBlend => Enabled(Vortice.Direct3D12.Blend.SourceAlpha, Vortice.Direct3D12.Blend.InverseSourceAlpha, Vortice.Direct3D12.Blend.One, Vortice.Direct3D12.Blend.InverseSourceAlpha),
            BlendMode.Premultiplied => Enabled(Vortice.Direct3D12.Blend.One, Vortice.Direct3D12.Blend.InverseSourceAlpha, Vortice.Direct3D12.Blend.One, Vortice.Direct3D12.Blend.InverseSourceAlpha),
            BlendMode.Multiply => Enabled(Vortice.Direct3D12.Blend.DestinationColor, Vortice.Direct3D12.Blend.Zero, Vortice.Direct3D12.Blend.DestinationAlpha, Vortice.Direct3D12.Blend.Zero),
            _ => new RenderTargetBlendDescription(false, false, Vortice.Direct3D12.Blend.One, Vortice.Direct3D12.Blend.Zero, BlendOperation.Add, Vortice.Direct3D12.Blend.One, Vortice.Direct3D12.Blend.Zero, BlendOperation.Add),
        };
        var span = description.RenderTarget.AsSpan();
        for (var i = 0; i < span.Length; i++)
        {
            span[i] = target;
        }

        return description;
    }

    private static RenderTargetBlendDescription Enabled(Vortice.Direct3D12.Blend source, Vortice.Direct3D12.Blend destination, Vortice.Direct3D12.Blend sourceAlpha, Vortice.Direct3D12.Blend destinationAlpha) =>
        new(true, false, source, destination, BlendOperation.Add, sourceAlpha, destinationAlpha, BlendOperation.Add);
}

internal sealed class D3D12ComputePipeline : IComputePipeline
{
    public D3D12ComputePipeline(ID3D12Device device, ID3D12RootSignature rootSignature, ShaderCode code)
    {
        Name = code.Name;
        State = device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = rootSignature, ComputeShader = code.Bytecode });
        State.Name = code.Name;
    }

    public string Name { get; }

    public ID3D12PipelineState State { get; }

    public void Dispose() => State.Dispose();
}
