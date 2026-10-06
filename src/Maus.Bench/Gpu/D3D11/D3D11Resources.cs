using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Maus.Bench.Gpu.D3D11;

/// <summary>Correspondance entre nos formats et ceux de DXGI (communs à Direct3D 11 et 12).</summary>
internal static class DxgiFormats
{
    public static Format ToDxgi(PixelFormat format) => format switch
    {
        PixelFormat.Rgba8Unorm => Format.R8G8B8A8_UNorm,
        PixelFormat.Rgba8UnormSrgb => Format.R8G8B8A8_UNorm_SRgb,
        PixelFormat.Bgra8Unorm => Format.B8G8R8A8_UNorm,
        PixelFormat.Rgba16Float => Format.R16G16B16A16_Float,
        PixelFormat.Rgba32Float => Format.R32G32B32A32_Float,
        PixelFormat.Rg16Float => Format.R16G16_Float,
        PixelFormat.R16Float => Format.R16_Float,
        PixelFormat.R32Float => Format.R32_Float,
        PixelFormat.R11G11B10Float => Format.R11G11B10_Float,
        PixelFormat.R32Uint => Format.R32_UInt,
        PixelFormat.R8Unorm => Format.R8_UNorm,
        PixelFormat.D32Float => Format.D32_Float,
        _ => Format.Unknown,
    };

    /// <summary>Format de stockage : la profondeur est créée « sans type » pour être aussi lue par les shaders.</summary>
    public static Format Storage(PixelFormat format) => format == PixelFormat.D32Float ? Format.R32_Typeless : ToDxgi(format);

    /// <summary>Format de lecture par les shaders (profondeur lue comme un flottant 32 bits).</summary>
    public static Format View(PixelFormat format) => format == PixelFormat.D32Float ? Format.R32_Float : ToDxgi(format);

    public static int BytesPerPixel(PixelFormat format) => format switch
    {
        PixelFormat.Rgba16Float => 8,
        PixelFormat.Rgba32Float => 16,
        PixelFormat.R16Float => 2,
        PixelFormat.R8Unorm => 1,
        _ => 4,
    };

    public static Format ToDxgi(VertexFormat format) => format switch
    {
        VertexFormat.Float1 => Format.R32_Float,
        VertexFormat.Float2 => Format.R32G32_Float,
        VertexFormat.Float3 => Format.R32G32B32_Float,
        VertexFormat.Float4 => Format.R32G32B32A32_Float,
        VertexFormat.UInt1 => Format.R32_UInt,
        VertexFormat.Unorm4 => Format.R8G8B8A8_UNorm,
        _ => Format.R32G32B32A32_Float,
    };
}

/// <summary>Texture Direct3D 11 et ses vues, créées à la demande pour chaque niveau, face ou tranche.</summary>
internal sealed class D3D11Texture : ITexture
{
    private readonly ID3D11Device _device;
    private readonly Dictionary<int, ID3D11ShaderResourceView> _mipViews = [];
    private readonly Dictionary<(int Mip, int Slice), ID3D11RenderTargetView> _targets = [];
    private readonly Dictionary<int, ID3D11UnorderedAccessView> _storage = [];
    private readonly bool _ownsResource;
    private ID3D11ShaderResourceView? _allMips;
    private ID3D11DepthStencilView? _depth;

    public D3D11Texture(ID3D11Device device, TextureDesc desc, ID3D11Resource? existing = null)
    {
        _device = device;
        Desc = desc;
        _ownsResource = existing is null;
        Resource = existing ?? Create(device, desc);
    }

    public TextureDesc Desc { get; }

    public ID3D11Resource Resource { get; }

    public ID3D11ShaderResourceView ShaderView(int mip)
    {
        if (mip < 0)
        {
            return _allMips ??= _device.CreateShaderResourceView(Resource, ViewDesc(-1));
        }

        if (!_mipViews.TryGetValue(mip, out var view))
        {
            _mipViews[mip] = view = _device.CreateShaderResourceView(Resource, ViewDesc(mip));
        }

        return view;
    }

    public ID3D11RenderTargetView TargetView(int mip, int slice)
    {
        if (!_targets.TryGetValue((mip, slice), out var view))
        {
            var format = DxgiFormats.View(Desc.Format);
            var description = Desc.Kind == TextureKind.TextureCube
                ? new RenderTargetViewDescription(RenderTargetViewDimension.Texture2DArray, format, (uint)mip, (uint)slice, 1)
                : new RenderTargetViewDescription(RenderTargetViewDimension.Texture2D, format, (uint)mip);
            _targets[(mip, slice)] = view = _device.CreateRenderTargetView(Resource, description);
        }

        return view;
    }

    public ID3D11DepthStencilView DepthView() =>
        _depth ??= _device.CreateDepthStencilView(Resource, new DepthStencilViewDescription(DepthStencilViewDimension.Texture2D, Format.D32_Float));

    public ID3D11UnorderedAccessView StorageView(int mip)
    {
        if (!_storage.TryGetValue(mip, out var view))
        {
            var format = DxgiFormats.View(Desc.Format);
            var description = Desc.Kind == TextureKind.Texture3D
                ? new UnorderedAccessViewDescription(UnorderedAccessViewDimension.Texture3D, format, (uint)mip, 0, uint.MaxValue)
                : new UnorderedAccessViewDescription(UnorderedAccessViewDimension.Texture2D, format, (uint)mip);
            _storage[mip] = view = _device.CreateUnorderedAccessView(Resource, description);
        }

        return view;
    }

    public void Dispose()
    {
        _allMips?.Dispose();
        _depth?.Dispose();
        foreach (var view in _mipViews.Values)
        {
            view.Dispose();
        }

        foreach (var view in _targets.Values)
        {
            view.Dispose();
        }

        foreach (var view in _storage.Values)
        {
            view.Dispose();
        }

        if (_ownsResource)
        {
            Resource.Dispose();
        }
    }

    private ShaderResourceViewDescription ViewDesc(int mip)
    {
        var format = DxgiFormats.View(Desc.Format);
        var first = mip < 0 ? 0u : (uint)mip;
        var count = mip < 0 ? uint.MaxValue : 1u;
        var dimension = Desc.Kind switch
        {
            TextureKind.Texture3D => Vortice.Direct3D.ShaderResourceViewDimension.Texture3D,
            TextureKind.TextureCube => Vortice.Direct3D.ShaderResourceViewDimension.TextureCube,
            _ => Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,
        };
        return new ShaderResourceViewDescription(dimension, format, first, count);
    }

    private static ID3D11Resource Create(ID3D11Device device, TextureDesc desc)
    {
        var bind = BindFlags.None;
        if (desc.Usage.HasFlag(TextureUsage.Sampled))
        {
            bind |= BindFlags.ShaderResource;
        }

        if (desc.Usage.HasFlag(TextureUsage.RenderTarget))
        {
            bind |= BindFlags.RenderTarget;
        }

        if (desc.Usage.HasFlag(TextureUsage.DepthStencil))
        {
            bind |= BindFlags.DepthStencil;
        }

        if (desc.Usage.HasFlag(TextureUsage.Storage))
        {
            bind |= BindFlags.UnorderedAccess;
        }

        if (desc.Kind == TextureKind.Texture3D)
        {
            var volume = device.CreateTexture3D(new Texture3DDescription(DxgiFormats.Storage(desc.Format), (uint)desc.Width, (uint)desc.Height, (uint)desc.Depth, (uint)desc.MipLevels, bind));
            volume.DebugName = desc.Name;
            return volume;
        }

        var texture = device.CreateTexture2D(new Texture2DDescription(
            DxgiFormats.Storage(desc.Format),
            (uint)desc.Width,
            (uint)desc.Height,
            desc.Kind == TextureKind.TextureCube ? 6u : 1u,
            (uint)desc.MipLevels,
            bind,
            miscFlags: desc.Kind == TextureKind.TextureCube ? ResourceOptionFlags.TextureCube : ResourceOptionFlags.None));
        texture.DebugName = desc.Name;
        return texture;
    }
}

internal sealed class D3D11Buffer : IBuffer
{
    public D3D11Buffer(ID3D11Device device, BufferDesc desc, ReadOnlySpan<byte> initial)
    {
        Desc = desc;
        var raw = desc.Usage.HasFlag(BufferUsage.Raw) || desc.Usage.HasFlag(BufferUsage.IndirectArgs);
        var bind = BindFlags.None;
        var misc = ResourceOptionFlags.None;
        if (desc.Usage.HasFlag(BufferUsage.Vertex))
        {
            bind |= BindFlags.VertexBuffer;
        }

        if (desc.Usage.HasFlag(BufferUsage.Index))
        {
            bind |= BindFlags.IndexBuffer;
        }

        if (desc.Usage.HasFlag(BufferUsage.Structured))
        {
            bind |= BindFlags.ShaderResource;
        }

        if (desc.Usage.HasFlag(BufferUsage.Storage))
        {
            bind |= BindFlags.UnorderedAccess;
        }

        if (desc.Usage.HasFlag(BufferUsage.IndirectArgs))
        {
            misc |= ResourceOptionFlags.DrawIndirectArguments;
        }

        if ((bind & (BindFlags.ShaderResource | BindFlags.UnorderedAccess)) != 0)
        {
            misc |= raw ? ResourceOptionFlags.BufferAllowRawViews : ResourceOptionFlags.BufferStructured;
        }

        var description = new BufferDescription(
            (uint)desc.SizeInBytes,
            bind,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            misc,
            misc.HasFlag(ResourceOptionFlags.BufferStructured) ? (uint)desc.Stride : 0);
        unsafe
        {
            if (initial.IsEmpty)
            {
                Buffer = device.CreateBuffer(description);
            }
            else
            {
                fixed (byte* data = initial)
                {
                    Buffer = device.CreateBuffer(description, new SubresourceData(data));
                }
            }
        }

        Buffer.DebugName = desc.Name;
        var elements = raw ? (uint)(desc.SizeInBytes / 4) : (uint)(desc.SizeInBytes / Math.Max(1, desc.Stride));
        if (desc.Usage.HasFlag(BufferUsage.Structured))
        {
            var view = new ShaderResourceViewDescription
            {
                Format = raw ? Format.R32_Typeless : Format.Unknown,
                ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.BufferExtended,
                BufferEx = new BufferExtendedShaderResourceView
                {
                    FirstElement = 0,
                    NumElements = elements,
                    Flags = raw ? BufferExtendedShaderResourceViewFlags.Raw : BufferExtendedShaderResourceViewFlags.None,
                },
            };
            ShaderView = device.CreateShaderResourceView(Buffer, view);
        }

        if (desc.Usage.HasFlag(BufferUsage.Storage))
        {
            var view = new UnorderedAccessViewDescription
            {
                Format = raw ? Format.R32_Typeless : Format.Unknown,
                ViewDimension = UnorderedAccessViewDimension.Buffer,
                Buffer = new BufferUnorderedAccessView
                {
                    FirstElement = 0,
                    NumElements = elements,
                    Flags = raw ? BufferUnorderedAccessViewFlags.Raw : BufferUnorderedAccessViewFlags.None,
                },
            };
            StorageView = device.CreateUnorderedAccessView(Buffer, view);
        }
    }

    public BufferDesc Desc { get; }

    public ID3D11Buffer Buffer { get; }

    public ID3D11ShaderResourceView? ShaderView { get; }

    public ID3D11UnorderedAccessView? StorageView { get; }

    public void Dispose()
    {
        ShaderView?.Dispose();
        StorageView?.Dispose();
        Buffer.Dispose();
    }
}

internal sealed class D3D11Pipeline : IPipeline
{
    public D3D11Pipeline(ID3D11Device device, GraphicsPipelineDesc desc)
    {
        Desc = desc;
        VertexShader = device.CreateVertexShader(desc.VertexShader.Bytecode.Span);
        PixelShader = desc.PixelShader is { } ps ? device.CreatePixelShader(ps.Bytecode.Span) : null;
        if (desc.Layout.Count > 0)
        {
            var elements = desc.Layout.Select(e => new InputElementDescription(
                e.Semantic,
                (uint)e.SemanticIndex,
                DxgiFormats.ToDxgi(e.Format),
                (uint)e.Offset,
                (uint)e.Slot,
                e.PerInstance ? InputClassification.PerInstanceData : InputClassification.PerVertexData,
                e.PerInstance ? 1u : 0u)).ToArray();
            InputLayout = device.CreateInputLayout(elements, desc.VertexShader.Bytecode.Span);
        }

        Blend = device.CreateBlendState(BlendStates.Describe(desc.Blend));
        DepthStencil = device.CreateDepthStencilState(new DepthStencilDescription(
            desc.Depth != DepthMode.None,
            desc.Depth == DepthMode.TestWrite ? DepthWriteMask.All : DepthWriteMask.Zero,
            desc.DepthGreater ? ComparisonFunction.GreaterEqual : ComparisonFunction.LessEqual));
        Rasterizer = device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = desc.Cull switch
            {
                CullMode.Back => Vortice.Direct3D11.CullMode.Back,
                CullMode.Front => Vortice.Direct3D11.CullMode.Front,
                _ => Vortice.Direct3D11.CullMode.None,
            },
            FrontCounterClockwise = false,
            DepthBias = desc.DepthBias,
            SlopeScaledDepthBias = desc.SlopeScaledDepthBias,
            DepthClipEnable = !desc.DepthClamp,
            ScissorEnable = true,
        });
    }

    public GraphicsPipelineDesc Desc { get; }

    public ID3D11VertexShader VertexShader { get; }

    public ID3D11PixelShader? PixelShader { get; }

    public ID3D11InputLayout? InputLayout { get; }

    public ID3D11BlendState Blend { get; }

    public ID3D11DepthStencilState DepthStencil { get; }

    public ID3D11RasterizerState Rasterizer { get; }

    public void Dispose()
    {
        VertexShader.Dispose();
        PixelShader?.Dispose();
        InputLayout?.Dispose();
        Blend.Dispose();
        DepthStencil.Dispose();
        Rasterizer.Dispose();
    }
}

internal static class BlendStates
{
    public static BlendDescription Describe(BlendMode mode)
    {
        var description = new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
        var target = mode switch
        {
            BlendMode.Additive => Enabled(Blend.One, Blend.One, Blend.One, Blend.One),
            BlendMode.AlphaBlend => Enabled(Blend.SourceAlpha, Blend.InverseSourceAlpha, Blend.One, Blend.InverseSourceAlpha),
            BlendMode.Premultiplied => Enabled(Blend.One, Blend.InverseSourceAlpha, Blend.One, Blend.InverseSourceAlpha),
            BlendMode.Multiply => Enabled(Blend.DestinationColor, Blend.Zero, Blend.DestinationAlpha, Blend.Zero),
            _ => new RenderTargetBlendDescription
            {
                BlendEnable = false,
                SourceBlend = Blend.One,
                DestinationBlend = Blend.Zero,
                BlendOperation = BlendOperation.Add,
                SourceBlendAlpha = Blend.One,
                DestinationBlendAlpha = Blend.Zero,
                BlendOperationAlpha = BlendOperation.Add,
                RenderTargetWriteMask = ColorWriteEnable.All,
            },
        };
        var span = description.RenderTarget.AsSpan();
        for (var i = 0; i < span.Length; i++)
        {
            span[i] = target;
        }

        return description;
    }

    private static RenderTargetBlendDescription Enabled(Blend source, Blend destination, Blend sourceAlpha, Blend destinationAlpha) => new()
    {
        BlendEnable = true,
        SourceBlend = source,
        DestinationBlend = destination,
        BlendOperation = BlendOperation.Add,
        SourceBlendAlpha = sourceAlpha,
        DestinationBlendAlpha = destinationAlpha,
        BlendOperationAlpha = BlendOperation.Add,
        RenderTargetWriteMask = ColorWriteEnable.All,
    };
}

internal sealed class D3D11ComputePipeline(ID3D11Device device, ShaderCode code) : IComputePipeline
{
    public string Name => code.Name;

    public ID3D11ComputeShader Shader { get; } = device.CreateComputeShader(code.Bytecode.Span);

    public void Dispose() => Shader.Dispose();
}
