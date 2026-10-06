using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Maus.Bench.Gpu.D3D11;

/// <summary>
/// Carte graphique par Direct3D 11 : carte la plus performante (DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, la carte dédiée
/// d'un portable), chaîne d'échange en mode « flip » sans synchronisation verticale (déchirement autorisé si l'écran
/// le permet) pour mesurer la vitesse réelle, horodatages de la carte pour la durée de chaque image.
/// </summary>
internal sealed class D3D11GpuDevice : IGpuDevice
{
    private const int TimerRing = 4;
    private readonly IDXGIFactory2 _factory;
    private readonly IDXGIAdapter1 _adapter;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGISwapChain1? _swapChain;
    private readonly bool _tearing;
    private readonly D3D11Texture _backBuffer;
    private readonly D3D11CommandList _commands;
    private readonly (ID3D11Query Disjoint, ID3D11Query Begin, ID3D11Query End, bool Issued)[] _timers = new (ID3D11Query, ID3D11Query, ID3D11Query, bool)[TimerRing];
    private long _frame;

    /// <param name="window">Fenêtre à remplir, ou zéro pour un rendu hors écran (captures de contrôle).</param>
    public D3D11GpuDevice(nint window, int width, int height)
    {
        _factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        _adapter = GpuAdapters.Preferred(_factory);
        var description = _adapter.Description1;
        AdapterName = description.Description.Trim();
        DedicatedVideoMemory = (long)(ulong)description.DedicatedVideoMemory;

        Vortice.Direct3D11.D3D11.D3D11CreateDevice(
            _adapter,
            DriverType.Unknown,
            DeviceCreationFlags.None,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out ID3D11Device? device,
            out ID3D11DeviceContext? context).CheckError();
        _device = device!;
        _context = context!;
        OutputWidth = width;
        OutputHeight = height;

        if (window != 0)
        {
            using (var factory5 = _factory.QueryInterfaceOrNull<IDXGIFactory5>())
            {
                _tearing = factory5?.PresentAllowTearing ?? false;
            }

            var swapDescription = new SwapChainDescription1((uint)width, (uint)height, Format.R8G8B8A8_UNorm, bufferCount: 3, swapEffect: SwapEffect.FlipDiscard)
            {
                Flags = _tearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None,
            };
            _swapChain = _factory.CreateSwapChainForHwnd(_device, window, swapDescription);
            _factory.MakeWindowAssociation(window, WindowAssociationFlags.IgnoreAltEnter);
            var buffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _backBuffer = new D3D11Texture(_device, TextureDesc.Target(width, height, PixelFormat.Rgba8Unorm, "Image affichée"), buffer);
        }
        else
        {
            _backBuffer = new D3D11Texture(_device, TextureDesc.Target(width, height, PixelFormat.Rgba8Unorm, "Image hors écran"));
        }

        _commands = new D3D11CommandList(_device, _context);
        for (var i = 0; i < TimerRing; i++)
        {
            _timers[i] = (_device.CreateQuery(QueryType.TimestampDisjoint), _device.CreateQuery(QueryType.Timestamp), _device.CreateQuery(QueryType.Timestamp), false);
        }
    }

    public GpuApi Api => GpuApi.Direct3D11;

    public string AdapterName { get; }

    public long DedicatedVideoMemory { get; }

    public int OutputWidth { get; }

    public int OutputHeight { get; }

    public ITexture BackBuffer => _backBuffer;

    public ICommandList Commands => _commands;

    public double LastGpuFrameMilliseconds { get; private set; }

    public ITexture CreateTexture(TextureDesc desc) => new D3D11Texture(_device, desc);

    public unsafe void UploadTexture(ITexture texture, int mip, int slice, ReadOnlySpan<byte> data, int rowPitch)
    {
        var t = (D3D11Texture)texture;
        var subresource = (uint)(mip + (slice * t.Desc.MipLevels));
        var slicePitch = (uint)(rowPitch * t.Desc.MipHeight(mip));
        fixed (byte* pointer = data)
        {
            _context.UpdateSubresource(new MappedSubresource((nint)pointer, (uint)rowPitch, slicePitch), t.Resource, subresource);
        }
    }

    public IBuffer CreateBuffer(BufferDesc desc, ReadOnlySpan<byte> initialData = default) => new D3D11Buffer(_device, desc, initialData);

    public IPipeline CreatePipeline(GraphicsPipelineDesc desc) => new D3D11Pipeline(_device, desc);

    public IComputePipeline CreateComputePipeline(ShaderCode computeShader) => new D3D11ComputePipeline(_device, computeShader);

    public void BeginFrame()
    {
        _commands.BeginFrame();
        var slot = (int)(_frame % TimerRing);
        var timer = _timers[slot];
        _context.Begin(timer.Disjoint);
        _context.End(timer.Begin);
    }

    public void Present()
    {
        var slot = (int)(_frame % TimerRing);
        var timer = _timers[slot];
        _context.End(timer.End);
        _context.End(timer.Disjoint);
        _timers[slot].Issued = true;

        if (_swapChain is not null)
        {
            _swapChain.Present(0, _tearing ? PresentFlags.AllowTearing : PresentFlags.None).CheckError();
        }
        else
        {
            _context.Flush();
        }

        _frame++;
        ReadOldestTimer();
        DeviceLost.ThrowIfRemoved(_device.DeviceRemovedReason);
    }

    public void WaitIdle()
    {
        using var query = _device.CreateQuery(QueryType.Event);
        _context.End(query);
        _context.Flush();
        while (!_context.IsDataAvailable(query, AsyncGetDataFlags.None))
        {
            Thread.Yield();
            DeviceLost.ThrowIfRemoved(_device.DeviceRemovedReason);
        }
    }

    public byte[] CaptureBackBuffer(out int width, out int height)
    {
        width = OutputWidth;
        height = OutputHeight;
        return ReadTexture(_backBuffer);
    }

    public unsafe byte[] ReadTexture(ITexture texture)
    {
        var source = (D3D11Texture)texture;
        var width = texture.Desc.Width;
        var height = texture.Desc.Height;
        using var staging = _device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        _context.CopySubresourceRegion(staging, 0, 0, 0, 0, source.Resource, 0);
        var mapped = _context.Map(staging, 0, MapMode.Read);
        var pixels = new byte[width * height * 4];
        try
        {
            for (var y = 0; y < height; y++)
            {
                new ReadOnlySpan<byte>((byte*)mapped.DataPointer + ((long)y * mapped.RowPitch), width * 4).CopyTo(pixels.AsSpan(y * width * 4));
            }
        }
        finally
        {
            _context.Unmap(staging, 0);
        }

        return pixels;
    }

    public void Dispose()
    {
        try
        {
            _context.ClearState();
            _context.Flush();
        }
        catch (SharpGenException)
        {
            // Carte déjà perdue : on libère quand même ce qui peut l'être.
        }

        foreach (var (disjoint, begin, end, _) in _timers)
        {
            disjoint?.Dispose();
            begin?.Dispose();
            end?.Dispose();
        }

        _commands.Dispose();
        _backBuffer.Dispose();
        _swapChain?.Dispose();
        _context.Dispose();
        _device.Dispose();
        _adapter.Dispose();
        _factory.Dispose();
    }

    private void ReadOldestTimer()
    {
        // L'emplacement suivant est le plus ancien (trois images de retard) : ses résultats sont en général prêts.
        var slot = (int)(_frame % TimerRing);
        var timer = _timers[slot];
        if (!timer.Issued)
        {
            return;
        }

        if (_context.GetData<QueryDataTimestampDisjoint>(timer.Disjoint, AsyncGetDataFlags.DoNotFlush, out var disjoint)
            && !disjoint.Disjoint
            && _context.GetData<ulong>(timer.Begin, AsyncGetDataFlags.DoNotFlush, out var begin)
            && _context.GetData<ulong>(timer.End, AsyncGetDataFlags.DoNotFlush, out var end)
            && end > begin
            && disjoint.Frequency > 0)
        {
            LastGpuFrameMilliseconds = (end - begin) * 1000.0 / disjoint.Frequency;
        }

        _timers[slot].Issued = false;
    }
}
