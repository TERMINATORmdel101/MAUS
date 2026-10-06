using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Maus.Bench.Gpu.D3D12;

/// <summary>
/// Lancer de rayons matériel de Direct3D 12 (DirectX Raytracing 1.1) : structures d'accélération des maillages (BLAS) et
/// de la scène (TLAS), construites par la carte pendant le chargement, puis interrogées par les shaders (requêtes de
/// rayons, modèle 6.5). Pas de tables de shaders ni d'objets d'état : les rayons partent d'un shader de pixels ordinaire.
/// </summary>
internal sealed unsafe class D3D12RayTracing : IRayTracing, IDisposable
{
    private const ResourceStates ShaderRead = ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource;
    private const int InstanceSize = 64;
    private readonly D3D12GpuDevice _owner;
    private readonly ID3D12Device5 _device;

    private D3D12RayTracing(D3D12GpuDevice owner, ID3D12Device5 device)
    {
        _owner = owner;
        _device = device;
    }

    /// <summary>
    /// Lancer de rayons disponible si la carte et son pilote annoncent le niveau 1.1 de DXR (requêtes de rayons dans les
    /// shaders) et le modèle de shader 6.5 ; sinon <c>null</c>.
    /// </summary>
    public static D3D12RayTracing? TryCreate(D3D12GpuDevice owner)
    {
        try
        {
            if (owner.Device.Options5.RaytracingTier < RaytracingTier.Tier1_1
                || owner.Device.CheckHighestShaderModel(ShaderModel.Model6_5) < ShaderModel.Model6_5)
            {
                return null;
            }
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            // Pilote ou Windows trop anciens pour répondre à la question : pas de lancer de rayons.
            return null;
        }

        var device = owner.Device.QueryInterfaceOrNull<ID3D12Device5>();
        return device is null ? null : new D3D12RayTracing(owner, device);
    }

    public IAccelerationStructure BuildMesh(IBuffer vertices, int stride, IBuffer indices, ReadOnlySpan<MeshRange> parts, string name)
    {
        var vb = (D3D12Buffer)vertices;
        var ib = (D3D12Buffer)indices;
        var commands = _owner.CommandList;
        commands.RequireBuffer(vb, ShaderRead);
        commands.RequireBuffer(ib, ShaderRead);
        commands.FlushBarriers();
        var geometries = new RaytracingGeometryDescription[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var triangles = new RaytracingGeometryTrianglesDescription
            {
                VertexBuffer = new GpuVirtualAddressAndStride(vb.Resource.GPUVirtualAddress + ((ulong)part.FirstVertex * (ulong)stride), (ulong)stride),
                VertexFormat = Format.R32G32B32_Float,
                VertexCount = (uint)part.VertexCount,
                IndexBuffer = ib.Resource.GPUVirtualAddress + ((ulong)part.FirstIndex * 4),
                IndexFormat = Format.R32_UInt,
                IndexCount = (uint)part.IndexCount,
            };
            geometries[i] = new RaytracingGeometryDescription(triangles, RaytracingGeometryFlags.Opaque);
        }

        var inputs = new BuildRaytracingAccelerationStructureInputs
        {
            Type = RaytracingAccelerationStructureType.BottomLevel,
            Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace,
            Layout = ElementsLayout.Array,
            DescriptorsCount = (uint)geometries.Length,
            GeometryDescriptions = geometries,
        };
        return Build(inputs, name, withView: false);
    }

    public IAccelerationStructure BuildScene(ReadOnlySpan<RayInstance> instances, string name)
    {
        // Description des instances (D3D12_RAYTRACING_INSTANCE_DESC, 64 octets) : matrice 3 × 4 en lignes appliquée à un
        // vecteur colonne (la transposée de nos matrices), numéro sur 24 bits et masque sur 8 bits, adresse du maillage.
        var upload = _owner.Device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer((ulong)(Math.Max(instances.Length, 1) * InstanceSize)), ResourceStates.GenericRead);
        upload.Name = name + " : instances";
        void* mapped;
        upload.Map(0, null, &mapped).CheckError();
        for (var i = 0; i < instances.Length; i++)
        {
            var instance = instances[i];
            var m = instance.Transform;
            var p = (float*)((byte*)mapped + (i * InstanceSize));
            p[0] = m.M11;
            p[1] = m.M21;
            p[2] = m.M31;
            p[3] = m.M41;
            p[4] = m.M12;
            p[5] = m.M22;
            p[6] = m.M32;
            p[7] = m.M42;
            p[8] = m.M13;
            p[9] = m.M23;
            p[10] = m.M33;
            p[11] = m.M43;
            var words = (uint*)(p + 12);
            words[0] = ((uint)instance.Id & 0xFFFFFFu) | ((uint)instance.Mask << 24);
            words[1] = 0;
            *(ulong*)(words + 2) = ((D3D12AccelerationStructure)instance.Mesh).Resource.GPUVirtualAddress;
        }

        upload.Unmap(0);
        var inputs = new BuildRaytracingAccelerationStructureInputs
        {
            Type = RaytracingAccelerationStructureType.TopLevel,
            Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace,
            Layout = ElementsLayout.Array,
            DescriptorsCount = (uint)instances.Length,
            InstanceDescriptions = upload.GPUVirtualAddress,
        };
        var scene = Build(inputs, name, withView: true);
        _owner.Retire(this, upload);
        return scene;
    }

    public void Bind(int slot, IAccelerationStructure? scene) =>
        _owner.CommandList.SetRawView(slot, (scene as D3D12AccelerationStructure)?.View);

    public void Dispose() => _device.Dispose();

    /// <summary>Construction par la carte, dans l'image en cours ; la mémoire de travail est rendue quand l'image est finie.</summary>
    private D3D12AccelerationStructure Build(BuildRaytracingAccelerationStructureInputs inputs, string name, bool withView)
    {
        var sizes = _device.GetRaytracingAccelerationStructurePrebuildInfo(inputs);
        var result = _owner.Device.CreateCommittedResource(HeapType.Default, ResourceDescription.Buffer(Math.Max(sizes.ResultDataMaxSizeInBytes, 256), ResourceFlags.AllowUnorderedAccess), ResourceStates.RaytracingAccelerationStructure);
        result.Name = name;
        var scratch = _owner.Device.CreateCommittedResource(HeapType.Default, ResourceDescription.Buffer(Math.Max(sizes.ScratchDataSizeInBytes, 256), ResourceFlags.AllowUnorderedAccess), ResourceStates.UnorderedAccess);
        scratch.Name = name + " : mémoire de travail";
        var commands = _owner.CommandList;
        using (var list = commands.List.QueryInterface<ID3D12GraphicsCommandList4>())
        {
            list.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription
            {
                DestinationAccelerationStructureData = result.GPUVirtualAddress,
                Inputs = inputs,
                ScratchAccelerationStructureData = scratch.GPUVirtualAddress,
            });
        }

        // La structure suivante (la scène) lit celle-ci : barrière d'écriture avant toute lecture.
        commands.List.ResourceBarrier(ResourceBarrier.BarrierUnorderedAccessView(result));
        _owner.Retire(this, scratch);

        CpuDescriptorHandle? view = null;
        if (withView)
        {
            var handle = _owner.ShaderHeap.Allocate();
            _owner.Device.CreateShaderResourceView(null, new ShaderResourceViewDescription
            {
                Format = Format.Unknown,
                ViewDimension = ShaderResourceViewDimension.RaytracingAccelerationStructure,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                RaytracingAccelerationStructure = new RaytracingAccelerationStructureShaderResourceView { Location = result.GPUVirtualAddress },
            }, handle);
            view = handle;
        }

        return new D3D12AccelerationStructure(_owner, result, name, view);
    }
}

/// <summary>Structure d'accélération : toujours dans l'état « structure de lancer de rayons », libérée après la carte.</summary>
internal sealed class D3D12AccelerationStructure(D3D12GpuDevice owner, ID3D12Resource resource, string name, CpuDescriptorHandle? view) : IAccelerationStructure
{
    public string Name { get; } = name;

    public ID3D12Resource Resource { get; } = resource;

    /// <summary>Vue des shaders (scène seulement).</summary>
    public CpuDescriptorHandle? View { get; } = view;

    public void Dispose()
    {
        owner.Retire(this, Resource);
        if (View is { } handle)
        {
            owner.ShaderHeap.Free(handle);
        }
    }
}
