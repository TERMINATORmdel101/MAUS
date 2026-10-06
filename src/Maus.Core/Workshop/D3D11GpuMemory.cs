using System.Runtime.InteropServices;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>
/// Accès à la mémoire vidéo par DXGI et Direct3D 11, présents dans Windows : aucun pilote ni logiciel à installer.
/// Les appels COM passent par les tables de méthodes (index fixés par les en-têtes dxgi.h et d3d11.h).
/// </summary>
public sealed unsafe partial class D3D11GpuMemoryProvider : IGpuMemoryProvider
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint SoftwareAdapterFlag = 2;
    private const uint SdkVersion = 7;

    private static readonly Guid FactoryInterface = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid Adapter3Interface = new("645967a4-1392-4310-a798-8053ce3e93fd");

    /// <summary>Cartes matérielles (le rendu logiciel de Microsoft est écarté).</summary>
    public IReadOnlyList<GpuAdapterInfo> Adapters()
    {
        var list = new List<GpuAdapterInfo>();
        var factory = CreateFactory();
        try
        {
            for (uint index = 0; ; index++)
            {
                nint adapter = 0;
                var hr = Com.Call(factory, 12, index, &adapter);
                if (hr < 0)
                {
                    break;
                }

                try
                {
                    AdapterDescription desc;
                    if (Com.Call(adapter, 10, &desc) >= 0 && (desc.Flags & SoftwareAdapterFlag) == 0)
                    {
                        list.Add(new GpuAdapterInfo((int)index, new string(desc.Description).TrimEnd('\0').Trim(), (long)desc.DedicatedVideoMemory));
                    }
                }
                finally
                {
                    Com.Release(adapter);
                }
            }
        }
        finally
        {
            Com.Release(factory);
        }

        return list;
    }

    public IGpuMemory Open(GpuAdapterInfo adapter)
    {
        var factory = CreateFactory();
        nint dxgiAdapter = 0;
        try
        {
            Com.Check(Com.Call(factory, 12, (uint)adapter.Index, &dxgiAdapter));
            Com.Check(D3D11CreateDevice(dxgiAdapter, 0, 0, 0, 0, 0, SdkVersion, out var device, out _, out var context));
            return new D3D11GpuMemory(device, context);
        }
        finally
        {
            Com.Release(dxgiAdapter);
            Com.Release(factory);
        }
    }

    /// <summary>
    /// Budget de mémoire vidéo locale que Windows accorde à MAUS sur cette carte, moins ce que MAUS y utilise déjà
    /// (IDXGIAdapter3::QueryVideoMemoryInfo, index 14 de dxgi1_4.h) ; il tient compte de ce qu'occupent les autres
    /// programmes. <c>null</c> si Windows ne répond pas.
    /// </summary>
    public long? AvailableBytes(GpuAdapterInfo adapter)
    {
        var factory = CreateFactory();
        nint dxgiAdapter = 0;
        nint adapter3 = 0;
        try
        {
            if (Com.Call(factory, 12, (uint)adapter.Index, &dxgiAdapter) < 0)
            {
                return null;
            }

            var iid = Adapter3Interface;
            if (((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Com.Slot(dxgiAdapter, 0))(dxgiAdapter, &iid, &adapter3) < 0)
            {
                return null;
            }

            VideoMemoryInfo info;
            if (((delegate* unmanaged[Stdcall]<nint, uint, int, VideoMemoryInfo*, int>)Com.Slot(adapter3, 14))(adapter3, 0, 0, &info) < 0)
            {
                return null;
            }

            return info.Budget > info.CurrentUsage ? (long)Math.Min(info.Budget - info.CurrentUsage, (ulong)long.MaxValue) : 0;
        }
        finally
        {
            Com.Release(adapter3);
            Com.Release(dxgiAdapter);
            Com.Release(factory);
        }
    }

    private static nint CreateFactory()
    {
        Com.Check(CreateDXGIFactory1(in FactoryInterface, out var factory));
        return factory;
    }

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(in Guid riid, out nint factory);

    [LibraryImport("d3d11.dll")]
    private static partial int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, out int featureLevel, out nint context);

    /// <summary>DXGI_QUERY_VIDEO_MEMORY_INFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct VideoMemoryInfo
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
    }

    /// <summary>DXGI_ADAPTER_DESC1.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDescription
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }
}

/// <summary>Périphérique Direct3D 11 d'une carte : blocs en mémoire dédiée, tampons de transfert lisibles par le processeur.</summary>
internal sealed unsafe class D3D11GpuMemory(nint device, nint context) : IGpuMemory
{
    private const uint UsageDefault = 0;
    private const uint UsageStaging = 3;
    private const uint BindShaderResource = 0x8;
    private const uint CpuAccessWrite = 0x10000;
    private const uint CpuAccessRead = 0x20000;
    private const int OutOfMemory = unchecked((int)0x8007000E);

    // Index des méthodes : ID3D11Device::CreateBuffer = 3 ; ID3D11DeviceContext::Map = 14, Unmap = 15, CopyResource = 47.
    private const int CreateBufferSlot = 3;
    private const int MapSlot = 14;
    private const int UnmapSlot = 15;
    private const int CopyResourceSlot = 47;

    private readonly Dictionary<int, nint> _staging = [];

    public IGpuBlock? TryAllocate(int bytes)
    {
        var buffer = CreateBuffer(bytes, UsageDefault, BindShaderResource, 0, out var hr);
        return hr == OutOfMemory ? null : new Block(this, Com.Check(hr) == 0 ? buffer : 0, bytes);
    }

    public void Dispose()
    {
        foreach (var staging in _staging.Values)
        {
            Com.Release(staging);
        }

        _staging.Clear();
        Com.Release(context);
        Com.Release(device);
    }

    private nint CreateBuffer(int bytes, uint usage, uint bind, uint cpuAccess, out int hr)
    {
        var desc = stackalloc uint[6] { (uint)bytes, usage, bind, cpuAccess, 0, 0 };
        nint buffer = 0;
        hr = ((delegate* unmanaged[Stdcall]<nint, uint*, nint, nint*, int>)Com.Slot(device, CreateBufferSlot))(device, desc, 0, &buffer);
        return buffer;
    }

    private nint Staging(int bytes)
    {
        if (!_staging.TryGetValue(bytes, out var staging))
        {
            staging = CreateBuffer(bytes, UsageStaging, 0, CpuAccessRead | CpuAccessWrite, out var hr);
            Com.Check(hr);
            _staging[bytes] = staging;
        }

        return staging;
    }

    private void Copy(nint target, nint source) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Com.Slot(context, CopyResourceSlot))(context, target, source);

    private void* Map(nint resource, uint mapType)
    {
        MappedSubresource mapped;
        Com.Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, MappedSubresource*, int>)Com.Slot(context, MapSlot))(context, resource, 0, mapType, 0, &mapped));
        return mapped.Data;
    }

    private void Unmap(nint resource) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Com.Slot(context, UnmapSlot))(context, resource, 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource
    {
        public void* Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    private sealed class Block(D3D11GpuMemory owner, nint buffer, int bytes) : IGpuBlock
    {
        private nint _buffer = buffer;

        public int Bytes { get; } = bytes;

        public void Upload(ReadOnlySpan<uint> data)
        {
            var staging = owner.Staging(Bytes);
            var target = owner.Map(staging, 2);
            try
            {
                data.CopyTo(new Span<uint>(target, Bytes / 4));
            }
            finally
            {
                owner.Unmap(staging);
            }

            owner.Copy(_buffer, staging);
        }

        public void Download(Span<uint> data)
        {
            var staging = owner.Staging(Bytes);
            owner.Copy(staging, _buffer);
            var source = owner.Map(staging, 1);
            try
            {
                new ReadOnlySpan<uint>(source, Bytes / 4).CopyTo(data);
            }
            finally
            {
                owner.Unmap(staging);
            }
        }

        public void Dispose()
        {
            Com.Release(_buffer);
            _buffer = 0;
        }
    }
}

/// <summary>Appels COM par la table des méthodes.</summary>
internal static unsafe class Com
{
    private const int DeviceRemoved = unchecked((int)0x887A0005);
    private const int DeviceReset = unchecked((int)0x887A0007);
    private const int DeviceHung = unchecked((int)0x887A0006);

    public static nint Slot(nint instance, int index) => (*(nint**)instance)[index];

    public static int Call(nint instance, int index, uint value, nint* result) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(instance, index))(instance, value, result);

    public static int Call<TStruct>(nint instance, int index, TStruct* result)
        where TStruct : unmanaged =>
        ((delegate* unmanaged[Stdcall]<nint, TStruct*, int>)Slot(instance, index))(instance, result);

    public static void Release(nint instance)
    {
        if (instance != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance);
        }
    }

    /// <summary>Renvoie 0 si l'appel a réussi, sinon lève <see cref="GpuMemoryException"/> avec une explication.</summary>
    public static int Check(int hr) => hr >= 0
        ? 0
        : throw new GpuMemoryException(hr is DeviceRemoved or DeviceReset or DeviceHung
            ? T("La carte graphique a cessé de répondre pendant le test (pilote réinitialisé) : c'est un signe d'instabilité (surcadençage, température ou alimentation).")
            : T("La carte graphique a refusé l'opération (code 0x{0:X8}).", hr));
}
