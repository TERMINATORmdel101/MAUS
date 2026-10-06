using SharpGen.Runtime;
using Vortice.D3DCompiler;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Maus.Bench.Gpu.D3D12;

/// <summary>
/// Carte graphique par Direct3D 12 : deux images en vol (le processeur prépare l'image suivante pendant que la carte
/// dessine), chaîne d'échange « flip » à trois tampons sans synchronisation verticale, horodatages de la carte,
/// mémoire « upload » tournante, libération différée des ressources tant que la carte peut encore les lire.
/// </summary>
internal sealed unsafe class D3D12GpuDevice : IGpuDevice
{
    public const int FramesInFlight = 2;
    private const int BackBufferCount = 3;
    private const long UploadFlushBytes = 256L * 1024 * 1024;

    private readonly IDXGIFactory4 _factory;
    private readonly IDXGIAdapter1 _adapter;
    private readonly ID3D12CommandQueue _queue;
    private readonly IDXGISwapChain3? _swapChain;
    private readonly bool _tearing;
    private readonly D3D12Texture[] _backBuffers;
    private readonly ID3D12Fence _fence;
    private readonly AutoResetEvent _fenceEvent = new(false);
    private readonly Frame[] _frames = new Frame[FramesInFlight];
    private readonly D3D12CommandList _commands;
    private readonly ID3D12QueryHeap _timestamps;
    private readonly ID3D12Resource _timestampReadback;
    private readonly double _timestampPeriodMs;
    private readonly ID3D12CommandAllocator _uploadAllocator;
    private readonly ID3D12GraphicsCommandList _uploadList;
    private readonly List<ID3D12Resource> _uploadStaging = [];
    private readonly List<(ulong Fence, ID3D12Resource? Resource)> _retired = [];
    private readonly List<ID3D12Resource> _retirePending = [];
    private ulong _fenceValue;
    private int _slot;
    private bool _uploadPending;
    private long _uploadBytes;
    private bool _recording;

    /// <param name="window">Fenêtre à remplir, ou zéro pour un rendu hors écran (captures de contrôle).</param>
    public D3D12GpuDevice(nint window, int width, int height)
    {
        _factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(false);
        _adapter = GpuAdapters.Preferred(_factory);
        var description = _adapter.Description1;
        AdapterName = description.Description.Trim();
        DedicatedVideoMemory = (long)(ulong)description.DedicatedVideoMemory;
        Vortice.Direct3D12.D3D12.D3D12CreateDevice(_adapter, FeatureLevel.Level_11_0, out ID3D12Device? device).CheckError();
        Device = device!;
        OutputWidth = width;
        OutputHeight = height;

        _queue = Device.CreateCommandQueue(CommandListType.Direct);
        _queue.Name = "MAUS benchmark";
        _fence = Device.CreateFence(0);
        RootSignature = D3D12RootSignature.Create(Device);
        DrawIndexedSignature = Device.CreateCommandSignature<ID3D12CommandSignature>(
            new CommandSignatureDescription(20, [new IndirectArgumentDescription { Type = IndirectArgumentType.DrawIndexed }]),
            null!);

        ShaderHeap = new CpuDescriptorHeap(Device, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 65536);
        TargetHeap = new CpuDescriptorHeap(Device, DescriptorHeapType.RenderTargetView, 4096);
        DepthHeap = new CpuDescriptorHeap(Device, DescriptorHeapType.DepthStencilView, 512);
        Ring = new GpuDescriptorRing(Device, FramesInFlight, 196608);

        NullTexture = ShaderHeap.Allocate();
        Device.CreateShaderResourceView(null, new ShaderResourceViewDescription
        {
            Format = Format.R8G8B8A8_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
        }, NullTexture);
        NullStorage = ShaderHeap.Allocate();
        Device.CreateUnorderedAccessView(null, null, new UnorderedAccessViewDescription
        {
            Format = Format.R32_UInt,
            ViewDimension = UnorderedAccessViewDimension.Texture2D,
        }, NullStorage);

        for (var i = 0; i < FramesInFlight; i++)
        {
            _frames[i] = new Frame(Device.CreateCommandAllocator(CommandListType.Direct), new UploadArena(Device));
        }

        _uploadAllocator = Device.CreateCommandAllocator(CommandListType.Direct);
        _uploadList = Device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, _uploadAllocator);
        _uploadList.Close();

        _commands = new D3D12CommandList(this)
        {
            List = Device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, _frames[0].Allocator),
        };
        _commands.List.Close();

        _timestamps = Device.CreateQueryHeap<ID3D12QueryHeap>(new QueryHeapDescription(QueryHeapType.Timestamp, FramesInFlight * 2));
        _timestampReadback = Device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(FramesInFlight * 16), ResourceStates.CopyDest);
        _queue.GetTimestampFrequency(out var frequency).CheckError();
        _timestampPeriodMs = frequency > 0 ? 1000.0 / frequency : 0;

        if (window != 0)
        {
            using (var factory5 = _factory.QueryInterfaceOrNull<IDXGIFactory5>())
            {
                _tearing = factory5?.PresentAllowTearing ?? false;
            }

            var swapDescription = new SwapChainDescription1((uint)width, (uint)height, Format.R8G8B8A8_UNorm, bufferCount: BackBufferCount, swapEffect: SwapEffect.FlipDiscard)
            {
                Flags = _tearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None,
            };
            using var swapChain1 = _factory.CreateSwapChainForHwnd(_queue, window, swapDescription);
            _swapChain = swapChain1.QueryInterface<IDXGISwapChain3>();
            _factory.MakeWindowAssociation(window, WindowAssociationFlags.IgnoreAltEnter);
            _backBuffers = new D3D12Texture[BackBufferCount];
            for (var i = 0; i < BackBufferCount; i++)
            {
                var buffer = _swapChain.GetBuffer<ID3D12Resource>((uint)i);
                _backBuffers[i] = new D3D12Texture(this, TextureDesc.Target(width, height, PixelFormat.Rgba8Unorm, "Image affichée " + i), buffer, ResourceStates.Present);
            }
        }
        else
        {
            _backBuffers = [new D3D12Texture(this, TextureDesc.Target(width, height, PixelFormat.Rgba8Unorm, "Image hors écran"))];
        }
    }

    public GpuApi Api => GpuApi.Direct3D12;

    public string AdapterName { get; }

    public long DedicatedVideoMemory { get; }

    public int OutputWidth { get; }

    public int OutputHeight { get; }

    public ITexture BackBuffer => _backBuffers[_swapChain is null ? 0 : (int)_swapChain.CurrentBackBufferIndex];

    public ICommandList Commands => _commands;

    public double LastGpuFrameMilliseconds { get; private set; }

    public ID3D12Device Device { get; }

    public ID3D12RootSignature RootSignature { get; }

    public ID3D12CommandSignature DrawIndexedSignature { get; }

    public CpuDescriptorHeap ShaderHeap { get; }

    public CpuDescriptorHeap TargetHeap { get; }

    public CpuDescriptorHeap DepthHeap { get; }

    public GpuDescriptorRing Ring { get; }

    public CpuDescriptorHandle NullTexture { get; }

    public CpuDescriptorHandle NullStorage { get; }

    /// <summary>Mémoire « upload » de l'image en cours.</summary>
    public UploadArena Upload => _frames[_slot].Upload;

    public ITexture CreateTexture(TextureDesc desc) => new D3D12Texture(this, desc);

    public void UploadTexture(ITexture texture, int mip, int slice, ReadOnlySpan<byte> data, int rowPitch)
    {
        var t = (D3D12Texture)texture;
        var subresource = (uint)t.Subresource(mip, t.Slices > 1 ? slice : 0);
        Span<PlacedSubresourceFootPrint> layouts = stackalloc PlacedSubresourceFootPrint[1];
        Span<uint> rows = stackalloc uint[1];
        Span<ulong> rowSizes = stackalloc ulong[1];
        Device.GetCopyableFootprints(t.Resource.Description, subresource, 1, 0, layouts, rows, rowSizes, out var total);
        var staging = Device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer(total), ResourceStates.GenericRead);
        void* mapped;
        staging.Map(0, null, &mapped).CheckError();
        var footprint = layouts[0].Footprint;
        var rowBytes = (int)rowSizes[0];
        var depth = (int)footprint.Depth;
        fixed (byte* source = data)
        {
            for (var z = 0; z < depth; z++)
            {
                for (var y = 0; y < rows[0]; y++)
                {
                    var from = source + ((long)z * rowPitch * rows[0]) + ((long)y * rowPitch);
                    var to = (byte*)mapped + layouts[0].Offset + ((ulong)z * footprint.RowPitch * rows[0]) + ((ulong)y * footprint.RowPitch);
                    Buffer.MemoryCopy(from, to, rowBytes, Math.Min(rowBytes, rowPitch));
                }
            }
        }

        staging.Unmap(0);
        BeginUpload();
        var stateBefore = t.States[subresource];
        if (stateBefore != ResourceStates.CopyDest)
        {
            _uploadList.ResourceBarrierTransition(t.Resource, stateBefore, ResourceStates.CopyDest, subresource);
            t.States[subresource] = ResourceStates.CopyDest;
        }

        _uploadList.CopyTextureRegion(new TextureCopyLocation(t.Resource, subresource), 0, 0, 0, new TextureCopyLocation(staging, layouts[0]));
        EndUpload(staging, (long)total);
    }

    public IBuffer CreateBuffer(BufferDesc desc, ReadOnlySpan<byte> initialData = default)
    {
        var buffer = new D3D12Buffer(this, desc);
        if (!initialData.IsEmpty)
        {
            var staging = Device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer((ulong)initialData.Length), ResourceStates.GenericRead);
            void* mapped;
            staging.Map(0, null, &mapped).CheckError();
            initialData.CopyTo(new Span<byte>(mapped, initialData.Length));
            staging.Unmap(0);
            BeginUpload();
            _uploadList.ResourceBarrierTransition(buffer.Resource, buffer.State, ResourceStates.CopyDest);
            buffer.State = ResourceStates.CopyDest;
            _uploadList.CopyBufferRegion(buffer.Resource, 0, staging, 0, (ulong)initialData.Length);
            EndUpload(staging, initialData.Length);
        }

        return buffer;
    }

    public IPipeline CreatePipeline(GraphicsPipelineDesc desc) => new D3D12Pipeline(Device, RootSignature, desc);

    public IComputePipeline CreateComputePipeline(ShaderCode computeShader) => new D3D12ComputePipeline(Device, RootSignature, computeShader);

    public void BeginFrame()
    {
        var frame = _frames[_slot];
        WaitFor(frame.FenceValue);
        ReadTimestamps(frame);
        ReleaseRetired();
        frame.Allocator.Reset();
        frame.Upload.Reset();
        Ring.BeginFrame(_slot);
        _commands.List.Reset(frame.Allocator, null);
        _recording = true;
        _commands.Restart(newFrame: true);
        _commands.List.EndQuery(_timestamps, QueryType.Timestamp, (uint)(_slot * 2));
    }

    public void Present()
    {
        var back = (D3D12Texture)BackBuffer;
        if (_swapChain is not null)
        {
            _commands.RequireTexture(back, -1, -1, ResourceStates.Present);
            _commands.FlushBarriers();
        }

        var list = _commands.List;
        list.EndQuery(_timestamps, QueryType.Timestamp, (uint)((_slot * 2) + 1));
        list.ResolveQueryData(_timestamps, QueryType.Timestamp, (uint)(_slot * 2), 2, _timestampReadback, (ulong)(_slot * 16));
        list.Close();
        _recording = false;
        FlushUploads();
        _queue.ExecuteCommandList(list);
        if (_swapChain is not null)
        {
            var result = _swapChain.Present(0, _tearing ? PresentFlags.AllowTearing : PresentFlags.None);
            if (result.Failure)
            {
                DeviceLost.ThrowIfRemoved(Device.DeviceRemovedReason);
                result.CheckError();
            }
        }

        _frames[_slot].FenceValue = Signal();
        _frames[_slot].TimestampsPending = true;
        AssignPendingRetirements(_frames[_slot].FenceValue);
        _slot = (_slot + 1) % FramesInFlight;
    }

    /// <summary>Envoie le travail enregistré au milieu d'une image (découpage des scènes lourdes) puis reprend l'enregistrement.</summary>
    public void SubmitAndReopen()
    {
        var list = _commands.List;
        list.Close();
        FlushUploads();
        _queue.ExecuteCommandList(list);
        list.Reset(_frames[_slot].Allocator, null);
        _commands.Restart(newFrame: false);
    }

    public void WaitIdle()
    {
        FlushUploads();
        var value = Signal();
        AssignPendingRetirements(value);
        WaitFor(value);
        ReleaseRetired();
    }

    public byte[] CaptureBackBuffer(out int width, out int height)
    {
        width = OutputWidth;
        height = OutputHeight;
        return ReadTexture(BackBuffer);
    }

    public byte[] ReadTexture(ITexture texture)
    {
        var back = (D3D12Texture)texture;
        var width = texture.Desc.Width;
        var height = texture.Desc.Height;
        Span<PlacedSubresourceFootPrint> layouts = stackalloc PlacedSubresourceFootPrint[1];
        Span<uint> rows = stackalloc uint[1];
        Span<ulong> rowSizes = stackalloc ulong[1];
        Device.GetCopyableFootprints(back.Resource.Description, 0, 1, 0, layouts, rows, rowSizes, out var total);
        using var readback = Device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(total), ResourceStates.CopyDest);
        _commands.RequireTexture(back, 0, 0, ResourceStates.CopySource);
        _commands.FlushBarriers();
        _commands.List.CopyTextureRegion(new TextureCopyLocation(readback, layouts[0]), 0, 0, 0, new TextureCopyLocation(back.Resource, 0));
        SubmitAndReopen();
        WaitFor(Signal());

        var pixels = new byte[width * height * 4];
        void* mapped;
        readback.Map(0, null, &mapped).CheckError();
        try
        {
            for (var y = 0; y < height; y++)
            {
                new ReadOnlySpan<byte>((byte*)mapped + layouts[0].Offset + ((ulong)y * layouts[0].Footprint.RowPitch), width * 4).CopyTo(pixels.AsSpan(y * width * 4));
            }
        }
        finally
        {
            readback.Unmap(0);
        }

        return pixels;
    }

    /// <summary>
    /// Libère une ressource quand la carte ne peut plus l'utiliser : elle peut encore servir à l'image en cours
    /// d'enregistrement, donc on attend le signal de fin de cette image (pas celui d'une copie intermédiaire).
    /// </summary>
    public void Retire(object owner, ID3D12Resource? resource)
    {
        _ = owner;
        if (resource is not null)
        {
            _retirePending.Add(resource);
        }
    }

    public void Dispose()
    {
        try
        {
            if (_recording)
            {
                _commands.List.Close();
                _recording = false;
            }

            WaitFor(Signal());
        }
        catch (Exception ex) when (ex is SharpGenException or DeviceLostException)
        {
            // Carte perdue : on libère quand même ce qui peut l'être.
        }

        foreach (var buffer in _backBuffers)
        {
            buffer.Dispose();
        }

        foreach (var (_, resource) in _retired)
        {
            resource?.Dispose();
        }

        foreach (var resource in _retirePending)
        {
            resource.Dispose();
        }

        _retired.Clear();
        _retirePending.Clear();
        foreach (var staging in _uploadStaging)
        {
            staging.Dispose();
        }

        foreach (var frame in _frames)
        {
            frame.Allocator.Dispose();
            frame.Upload.Dispose();
        }

        _commands.List.Dispose();
        _uploadList.Dispose();
        _uploadAllocator.Dispose();
        _timestamps.Dispose();
        _timestampReadback.Dispose();
        Ring.Dispose();
        ShaderHeap.Dispose();
        TargetHeap.Dispose();
        DepthHeap.Dispose();
        DrawIndexedSignature.Dispose();
        RootSignature.Dispose();
        _swapChain?.Dispose();
        _fence.Dispose();
        _fenceEvent.Dispose();
        _queue.Dispose();
        Device.Dispose();
        _adapter.Dispose();
        _factory.Dispose();
    }

    private void BeginUpload()
    {
        if (!_uploadPending)
        {
            _uploadAllocator.Reset();
            _uploadList.Reset(_uploadAllocator, null);
            _uploadPending = true;
        }
    }

    private void EndUpload(ID3D12Resource staging, long bytes)
    {
        _uploadStaging.Add(staging);
        _uploadBytes += bytes;
        if (_uploadBytes > UploadFlushBytes)
        {
            FlushUploads();
        }
    }

    /// <summary>
    /// Exécute les copies de chargement avant le travail de l'image (même file : l'ordre est garanti), puis attend leur
    /// fin pour rendre la mémoire intermédiaire et pouvoir réutiliser l'allocateur des copies (chargement seulement).
    /// </summary>
    private void FlushUploads()
    {
        if (!_uploadPending)
        {
            return;
        }

        _uploadList.Close();
        _queue.ExecuteCommandList(_uploadList);
        _uploadPending = false;
        WaitFor(Signal());
        foreach (var staging in _uploadStaging)
        {
            staging.Dispose();
        }

        _uploadStaging.Clear();
        _uploadBytes = 0;
    }

    private void AssignPendingRetirements(ulong fence)
    {
        foreach (var resource in _retirePending)
        {
            _retired.Add((fence, resource));
        }

        _retirePending.Clear();
    }

    private ulong Signal()
    {
        _fenceValue++;
        _queue.Signal(_fence, _fenceValue).CheckError();
        return _fenceValue;
    }

    private void WaitFor(ulong value)
    {
        if (value == 0 || _fence.CompletedValue >= value)
        {
            return;
        }

        _fence.SetEventOnCompletion(value, _fenceEvent).CheckError();
        while (!_fenceEvent.WaitOne(500))
        {
            DeviceLost.ThrowIfRemoved(Device.DeviceRemovedReason);
            if (_fence.CompletedValue >= value)
            {
                break;
            }
        }
    }

    private void ReleaseRetired()
    {
        var completed = _fence.CompletedValue;
        for (var i = _retired.Count - 1; i >= 0; i--)
        {
            if (_retired[i].Fence <= completed)
            {
                _retired[i].Resource?.Dispose();
                _retired.RemoveAt(i);
            }
        }
    }

    private void ReadTimestamps(Frame frame)
    {
        if (!frame.TimestampsPending || _timestampPeriodMs <= 0)
        {
            return;
        }

        frame.TimestampsPending = false;
        void* mapped;
        var range = new Vortice.Direct3D12.Range((nuint)(_slot * 16), (nuint)((_slot * 16) + 16));
        if (_timestampReadback.Map(0, range, &mapped).Success)
        {
            var values = (ulong*)((byte*)mapped + (_slot * 16));
            if (values[1] > values[0])
            {
                LastGpuFrameMilliseconds = (values[1] - values[0]) * _timestampPeriodMs;
            }

            _timestampReadback.Unmap(0, new Vortice.Direct3D12.Range(0, 0));
        }
    }

    private sealed class Frame(ID3D12CommandAllocator allocator, UploadArena upload)
    {
        public ID3D12CommandAllocator Allocator { get; } = allocator;

        public UploadArena Upload { get; } = upload;

        public ulong FenceValue { get; set; }

        public bool TimestampsPending { get; set; }
    }
}

/// <summary>
/// Signature racine unique, écrite en HLSL et compilée par d3dcompiler_47 (cible rootsig_1_0) : deux tampons de
/// constantes en descripteurs racine, une table de 16 textures, une table de 8 sorties de calcul, six échantillonneurs
/// fixes identiques à ceux du moteur Direct3D 11.
/// </summary>
internal static class D3D12RootSignature
{
    private const string Source =
        "#define MAUS_RS \"RootFlags(ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT), CBV(b0), CBV(b1), " +
        "DescriptorTable(SRV(t0, numDescriptors = 16)), DescriptorTable(UAV(u0, numDescriptors = 8)), " +
        "StaticSampler(s0, filter = FILTER_MIN_MAG_MIP_LINEAR, addressU = TEXTURE_ADDRESS_WRAP, addressV = TEXTURE_ADDRESS_WRAP, addressW = TEXTURE_ADDRESS_WRAP), " +
        "StaticSampler(s1, filter = FILTER_MIN_MAG_MIP_LINEAR, addressU = TEXTURE_ADDRESS_CLAMP, addressV = TEXTURE_ADDRESS_CLAMP, addressW = TEXTURE_ADDRESS_CLAMP), " +
        "StaticSampler(s2, filter = FILTER_MIN_MAG_MIP_POINT, addressU = TEXTURE_ADDRESS_CLAMP, addressV = TEXTURE_ADDRESS_CLAMP, addressW = TEXTURE_ADDRESS_CLAMP), " +
        "StaticSampler(s3, filter = FILTER_ANISOTROPIC, maxAnisotropy = 16, addressU = TEXTURE_ADDRESS_WRAP, addressV = TEXTURE_ADDRESS_WRAP, addressW = TEXTURE_ADDRESS_WRAP), " +
        "StaticSampler(s4, filter = FILTER_COMPARISON_MIN_MAG_MIP_LINEAR, comparisonFunc = COMPARISON_LESS_EQUAL, addressU = TEXTURE_ADDRESS_CLAMP, addressV = TEXTURE_ADDRESS_CLAMP, addressW = TEXTURE_ADDRESS_CLAMP), " +
        "StaticSampler(s5, filter = FILTER_MIN_MAG_MIP_POINT, addressU = TEXTURE_ADDRESS_WRAP, addressV = TEXTURE_ADDRESS_WRAP, addressW = TEXTURE_ADDRESS_WRAP)\"\n";

    public static ID3D12RootSignature Create(ID3D12Device device)
    {
        var result = Compiler.Compile(Source, "MAUS_RS", "maus-root-signature", "rootsig_1_0", out var blob, out var errors);
        if (result.Failure || blob is null)
        {
            throw new InvalidOperationException("Signature racine invalide : " + errors?.AsString());
        }

        using (blob)
        {
            errors?.Dispose();
            return device.CreateRootSignature(0, blob);
        }
    }
}
