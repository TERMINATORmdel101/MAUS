using System.Numerics;
using System.Runtime.InteropServices;
using Maus.Bench.Gpu;
using Maus.Bench.Render;
using static Maus.Core.Localization.Texts;

namespace Maus.Bench.Scenes;

/// <summary>
/// Test « Galerie des glaces » (lancer de rayons matériel, DirectX Raytracing 1.1) : une galerie baroque de 40 m, dix
/// fenêtres face à dix arcades de miroirs, deux grands miroirs aux extrémités qui se renvoient la salle à l'infini, voûte
/// peinte, statues en or, en chrome, en verre et en marbre (modèles scannés de Poly Haven), six lustres de cristal. La
/// géométrie est rangée dans des structures d'accélération construites par la carte ; chaque pixel y lance ses rayons
/// (vue, reflets en cascade, réfraction, ombres douces, rebond de la lumière, rayons de soleil dans la poussière). Il
/// faut Direct3D 12 et une carte qui gère DXR 1.1 ; son score est compté à part.
/// </summary>
internal sealed class MirrorHallScene : BenchScene
{
    private const float HalfLength = 20f;
    private const float HalfWidth = 5f;
    private const float WallDepth = 0.6f;
    private const float VaultBase = 7.35f;
    private const float VaultRadius = 5f;
    private const float ArchRadius = 1.3f;
    private const float Sill = 0.6f;
    private const float Spring = 5.0f;
    private const float MirrorRecess = 0.25f;
    private const float PedestalTop = 1.05f;
    private const int ArchSegments = 24;
    private const byte MaskSolid = 1;
    private const byte MaskGlass = 2;
    private const byte MaskFlame = 4;

    private static readonly float[] Bays = [-18f, -14f, -10f, -6f, -2f, 2f, 6f, 10f, 14f, 18f];

    private static readonly float[] ChandelierPositions = [-12.5f, -7.5f, -2.5f, 2.5f, 7.5f, 12.5f];

    // Statues sur leurs socles, au milieu de la galerie : modèle, position (x), matière, hauteur (m), orientation (degrés).
    private static readonly Statue[] Statues =
    [
        new("horse_statue_01", -15f, Material.Gold, 1.35f, 90f),
        new("carved_wooden_elephant", -10f, Material.Glass, 0.95f, 70f),
        new("marble_bust_01", -5f, Material.Chrome, 0.95f, 180f),
        new("antique_ceramic_vase_01", 0f, Material.BlueGlass, 1.25f, 0f),
        new("marble_bust_01", 5f, Material.StatueMarble, 0.95f, 200f),
        new("brass_vase_03", 10f, Material.Gold, 1.15f, 0f),
        new("horse_statue_01", 15f, Material.Chrome, 1.35f, -90f),
    ];

    private static readonly CameraPath Path = new(
        (0, new CameraPose(new Vector3(-18.2f, 1.75f, 3.1f), new Vector3(0f, 3.0f, -1.2f), 64f)),
        (8, new CameraPose(new Vector3(-14.2f, 1.45f, 2.2f), new Vector3(-10.2f, 1.35f, -0.2f), 56f)),
        (16, new CameraPose(new Vector3(-9.0f, 1.45f, 1.7f), new Vector3(-10.0f, 1.3f, -0.3f), 52f)),
        (24, new CameraPose(new Vector3(-6.2f, 1.85f, -2.7f), new Vector3(-5.0f, 1.65f, 0.4f), 54f)),
        (32, new CameraPose(new Vector3(-2.2f, 1.1f, -3.0f), new Vector3(1.2f, 2.8f, -5.0f), 60f)),
        (40, new CameraPose(new Vector3(1.4f, 3.3f, 2.4f), new Vector3(2.5f, 5.1f, 0f), 58f)),
        (48, new CameraPose(new Vector3(5.8f, 1.5f, 2.8f), new Vector3(10f, 1.55f, 0f), 54f)),
        (56, new CameraPose(new Vector3(12.2f, 1.75f, -1.9f), new Vector3(20f, 2.9f, 0.2f), 60f)),
        (64, new CameraPose(new Vector3(16.8f, 1.8f, -1.5f), new Vector3(20f, 2.8f, 0.3f), 62f)));

    private static readonly Vector3 SunDirection = Vector3.Normalize(new Vector3(0.42f, 0.46f, 0.78f));

    private readonly List<IAccelerationStructure> _meshes = [];
    private IRayTracing? _rayTracing;
    private IAccelerationStructure? _scene;
    private IBuffer? _vertices;
    private IBuffer? _indices;
    private IBuffer? _instances;
    private IPipeline? _pipeline;
    private bool _light;

    /// <summary>Matières, dans l'ordre des constantes Mat* du shader raytracing.hlsl.</summary>
    internal enum Material
    {
        Floor,
        Stucco,
        Pilaster,
        Gilt,
        Mirror,
        Vault,
        Frame,
        Glass,
        BlueGlass,
        Chrome,
        StatueMarble,
        Crystal,
        Flame,
        Wax,
        Pedestal,
        Gold,
    }

    public override string Id => "raytracing";

    public override string Title => T("Galerie des glaces");

    public override string Subtitle => T("Lancer de rayons matériel (DXR) : reflets à l'infini, verre et cristal, ombres douces et lumière calculées rayon par rayon");

    public override GpuCapability Capability => GpuCapability.RayTracing;

    /// <summary>Un peu plus d'une minute : le test vient en plus des cinq scènes et a son propre score.</summary>
    public override double Duration => 64;

    /// <summary>Pourquoi le test ne peut pas tourner ici (<c>null</c> s'il peut tourner).</summary>
    public static string? Unavailable(IGpuDevice device) =>
        device.Api != GpuApi.Direct3D12 ? T("DirectX 12 seulement")
        : ShaderLibrary.Precompiled("raytracing.TracePS") is null ? T("absent de cette version de MAUS")
        : device.RayTracing is null ? T("carte ou pilote sans DXR 1.1")
        : null;

    public override void Load(SceneContext context)
    {
        var device = context.Device;
        _light = context.Light;
        _rayTracing = device.RayTracing ?? throw new InvalidOperationException("Lancer de rayons matériel indisponible.");
        var vertexShader = ShaderLibrary.Precompiled("raytracing.FullscreenVS") ?? throw new InvalidOperationException("Shader de lancer de rayons absent.");
        var pixelShader = ShaderLibrary.Precompiled(_light ? "raytracing.TracePS.light" : "raytracing.TracePS") ?? throw new InvalidOperationException("Shader de lancer de rayons absent.");
        _pipeline = device.CreatePipeline(new GraphicsPipelineDesc("Galerie des glaces : rayons", vertexShader, pixelShader, [], BlendMode.Opaque, DepthMode.None, CullMode.None, [PixelFormat.Rgba16Float, PixelFormat.Rg16Float]));

        var hall = new HallBuilder();
        BuildArchitecture(hall);
        BuildChandeliers(hall);
        BuildStatues(hall);

        // Toutes les parties dans deux tampons communs (sommets de 24 octets, indices 32 bits), relus par le shader.
        // Une structure par maillage : les murs, dorures, miroirs et la voûte de la galerie n'en font qu'une, sinon chaque
        // rayon devrait parcourir huit structures qui couvrent toute la salle.
        var vertexData = new List<float>();
        var indexData = new List<uint>();
        var ranges = new List<MeshRange[]>();
        foreach (var parts in hall.Meshes)
        {
            var list = new MeshRange[parts.Count];
            for (var p = 0; p < parts.Count; p++)
            {
                var mesh = parts[p].Mesh;
                list[p] = new MeshRange(vertexData.Count / 6, mesh.VertexCount, indexData.Count, mesh.Indices.Count);
                vertexData.AddRange(mesh.Vertices);
                indexData.AddRange(mesh.Indices);
            }

            ranges.Add(list);
        }

        _vertices = device.CreateBuffer(new BufferDesc(vertexData.Count * 4L, BufferUsage.Structured, 24, "Galerie : sommets"), MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(vertexData)));
        _indices = device.CreateBuffer(new BufferDesc(indexData.Count * 4L, BufferUsage.Structured, 4, "Galerie : indices"), MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(indexData)));
        for (var i = 0; i < ranges.Count; i++)
        {
            _meshes.Add(_rayTracing.BuildMesh(_vertices, 24, _indices, ranges[i], hall.Names[i]));
        }

        // Tableau des matières : pour chaque maillage placé (et sa matière imposée, pour les statues), une ligne par partie ;
        // le shader lit la ligne « numéro de l'instance + numéro de la partie touchée ».
        var rows = new List<uint>();
        var rowOf = new Dictionary<(int Mesh, Material? Material), int>();
        var instances = new RayInstance[hall.Placements.Count];
        for (var i = 0; i < hall.Placements.Count; i++)
        {
            var (mesh, material, transform, mask) = hall.Placements[i];
            if (!rowOf.TryGetValue((mesh, material), out var row))
            {
                row = rowOf[(mesh, material)] = rows.Count / 4;
                for (var p = 0; p < ranges[mesh].Length; p++)
                {
                    rows.AddRange([(uint)ranges[mesh][p].FirstVertex, (uint)ranges[mesh][p].FirstIndex, (uint)(material ?? hall.Meshes[mesh][p].Material), 0u]);
                }
            }

            instances[i] = new RayInstance(_meshes[mesh], transform, row, mask);
        }

        _instances = device.CreateBuffer(new BufferDesc(rows.Count * 4L, BufferUsage.Structured, 16, "Galerie : matières"), MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(rows)));
        _scene = _rayTracing.BuildScene(instances, "Galerie des glaces");
    }

    public override SceneState Evaluate(double time)
    {
        var camera = Path.Evaluate(PathTime(time, Path));
        return new SceneState(camera, new ColorGrade
        {
            Exposure = 0.85f,
            BloomIntensity = 0.32f,
            BloomThreshold = 2.2f,
            Vignette = 0.42f,
            Grain = 0.008f,
            Aberration = 0.002f,
            Saturation = 1.04f,
            Gain = new Vector3(1.02f, 1f, 0.96f),
            Contrast = 1.06f,
            Sharpen = 0.15f,
        })
        {
            SunDirection = SunDirection,
            SunColor = new Vector3(7.4f, 6.2f, 4.7f),
            NearPlane = 0.05f,
            FarPlane = 400f,
            // x : rayon apparent du soleil (radians, ombres douces) ; y : densité de la poussière (rayons de soleil).
            Params0 = new Vector4(0.012f, 0.018f, 0f, 0f),
        };
    }

    public override void Render(SceneContext context)
    {
        var cmd = context.Commands;
        var post = context.Post;
        cmd.SetRenderTargets([post.HdrColor, post.Velocity], null);
        cmd.SetPipeline(_pipeline!);
        _rayTracing!.Bind(0, _scene);
        cmd.SetBuffer(1, _vertices);
        cmd.SetBuffer(2, _indices);
        cmd.SetBuffer(3, _instances);

        // Image découpée en bandes envoyées une à une : sur une carte lente, aucune commande ne dépasse le délai de Windows.
        context.DrawFullscreenInBands(_light ? 6 : 8);
        _rayTracing.Bind(0, null);
        cmd.SetBuffer(1, null);
        cmd.SetBuffer(2, null);
        cmd.SetBuffer(3, null);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _scene?.Dispose();
            foreach (var mesh in _meshes)
            {
                mesh.Dispose();
            }

            _meshes.Clear();
            _vertices?.Dispose();
            _indices?.Dispose();
            _instances?.Dispose();
            _pipeline?.Dispose();
        }
    }

    /// <summary>Sol, murs percés d'arcades (fenêtres d'un côté, miroirs de l'autre), pilastres, corniches, voûte, miroirs des extrémités.</summary>
    private static void BuildArchitecture(HallBuilder hall)
    {
        var floor = new HallMesh();
        floor.Quad(new Vector3(-HalfLength, 0f, -HalfWidth), new Vector3(HalfLength, 0f, -HalfWidth), new Vector3(HalfLength, 0f, HalfWidth), new Vector3(-HalfLength, 0f, HalfWidth), Vector3.UnitY);

        var stucco = new HallMesh();
        var marble = new HallMesh();
        var gilt = new HallMesh();
        var mirror = new HallMesh();
        var frames = new HallMesh();
        foreach (var side in new[] { 1f, -1f })
        {
            // Côté fenêtres (z > 0) : mur de 60 cm percé de part en part. Côté miroirs (z < 0) : arcades creusées de 25 cm.
            var windows = side > 0f;
            var inner = side * HalfWidth;
            var outer = side * (HalfWidth + WallDepth);
            var back = windows ? outer : side * (HalfWidth + MirrorRecess);
            var piers = new List<(float From, float To)> { (-HalfLength, Bays[0] - ArchRadius) };
            for (var i = 0; i < Bays.Length; i++)
            {
                var x = Bays[i];
                piers.Add((x + ArchRadius, i + 1 < Bays.Length ? Bays[i + 1] - ArchRadius : HalfLength));
                stucco.Box(Corner(x - ArchRadius, 0f, inner, outer), Corner(x + ArchRadius, Sill, inner, outer, upper: true));
                stucco.Box(Corner(x - ArchRadius, Spring + ArchRadius, inner, outer), Corner(x + ArchRadius, VaultBase, inner, outer, upper: true));
                Spandrels(stucco, x, inner, back, side);
                if (windows)
                {
                    PaneBars(frames, x, side * (HalfWidth + (WallDepth / 2f)), 0.06f);
                }
                else
                {
                    var surface = side * (HalfWidth + MirrorRecess);
                    mirror.Quad(new Vector3(x - ArchRadius, Sill, surface), new Vector3(x + ArchRadius, Sill, surface), new Vector3(x + ArchRadius, Spring, surface), new Vector3(x - ArchRadius, Spring, surface), Vector3.UnitZ);
                    mirror.HalfDisk(new Vector3(x, Spring, surface), ArchRadius, Vector3.UnitZ, ArchSegments);
                    PaneBars(gilt, x, surface + 0.03f, 0.035f);
                }
            }

            foreach (var (from, to) in piers)
            {
                stucco.Box(Corner(from, 0f, inner, outer), Corner(to, VaultBase, inner, outer, upper: true));
                if (to - from > 1f)
                {
                    // Pilastre de marbre rouge, base et chapiteau dorés.
                    var middle = (from + to) / 2f;
                    marble.Box(Corner(middle - 0.4f, Sill, inner, inner - (side * 0.15f)), Corner(middle + 0.4f, 6.35f, inner, inner - (side * 0.15f), upper: true));
                    marble.Box(Corner(middle - 0.5f, 0f, inner, inner - (side * 0.2f)), Corner(middle + 0.5f, Sill, inner, inner - (side * 0.2f), upper: true));
                    gilt.Box(Corner(middle - 0.5f, 6.35f, inner, inner - (side * 0.24f)), Corner(middle + 0.5f, 6.75f, inner, inner - (side * 0.24f), upper: true));
                }
            }

            // Corniche dorée au pied de la voûte.
            gilt.Box(Corner(-HalfLength, 7.0f, inner, inner - (side * 0.32f)), Corner(HalfLength, VaultBase, inner, inner - (side * 0.32f), upper: true));
        }

        // Extrémités : mur, lunette sous la voûte, grand miroir encadré (les deux miroirs se font face).
        foreach (var end in new[] { 1f, -1f })
        {
            var x = end * HalfLength;
            var inward = new Vector3(-end, 0f, 0f);
            stucco.Quad(new Vector3(x, 0f, -HalfWidth), new Vector3(x, 0f, HalfWidth), new Vector3(x, VaultBase, HalfWidth), new Vector3(x, VaultBase, -HalfWidth), inward);
            stucco.HalfDiskX(new Vector3(x, VaultBase, 0f), VaultRadius, inward, 48);
            gilt.Box(new Vector3(MathF.Min(x, x - (end * 0.32f)), 7.0f, -HalfWidth), new Vector3(MathF.Max(x, x - (end * 0.32f)), VaultBase, HalfWidth));
            var glass = x - (end * 0.03f);
            mirror.Quad(new Vector3(glass, Sill, -3f), new Vector3(glass, Sill, 3f), new Vector3(glass, 6.3f, 3f), new Vector3(glass, 6.3f, -3f), inward);
            var bars = x - (end * 0.07f);
            for (var z = -3f; z <= 3.01f; z += 0.75f)
            {
                gilt.Bar(new Vector3(bars, Sill, z), new Vector3(bars, 6.3f, z), z is < -2.9f or > 2.9f ? 0.09f : 0.035f);
            }

            for (var y = Sill; y <= 6.31f; y += 0.95f)
            {
                gilt.Bar(new Vector3(bars, y, -3f), new Vector3(bars, y, 3f), y is < 0.7f or > 6.2f ? 0.09f : 0.035f);
            }
        }

        // Voûte en berceau, travées soulignées d'arcs dorés tous les 5 m.
        var vault = new HallMesh();
        const int around = 48;
        for (var i = 0; i < around; i++)
        {
            var a0 = i * MathF.PI / around;
            var a1 = (i + 1) * MathF.PI / around;
            var n0 = -new Vector3(0f, MathF.Sin(a0), MathF.Cos(a0));
            var n1 = -new Vector3(0f, MathF.Sin(a1), MathF.Cos(a1));
            for (var x = -HalfLength; x < HalfLength - 0.01f; x += 5f)
            {
                var p00 = new Vector3(x, VaultBase, 0f) - (n0 * VaultRadius);
                var p01 = new Vector3(x, VaultBase, 0f) - (n1 * VaultRadius);
                var p10 = p00 + new Vector3(5f, 0f, 0f);
                var p11 = p01 + new Vector3(5f, 0f, 0f);
                vault.Triangle(p00, p10, p11, n0, n0, n1);
                vault.Triangle(p00, p11, p01, n0, n1, n1);
            }
        }

        for (var x = -HalfLength + 5f; x < HalfLength - 0.01f; x += 5f)
        {
            for (var i = 0; i < ArchSegments; i++)
            {
                var a0 = i * MathF.PI / ArchSegments;
                var a1 = (i + 1) * MathF.PI / ArchSegments;
                var r = VaultRadius - 0.06f;
                gilt.Bar(new Vector3(x, VaultBase + (r * MathF.Sin(a0)), r * MathF.Cos(a0)), new Vector3(x, VaultBase + (r * MathF.Sin(a1)), r * MathF.Cos(a1)), 0.12f);
            }
        }

        // Socles des statues (marbre vert).
        var pedestals = new HallMesh();
        foreach (var statue in Statues)
        {
            pedestals.Box(new Vector3(statue.X - 0.45f, 0f, -0.45f), new Vector3(statue.X + 0.45f, 0.15f, 0.45f));
            pedestals.Box(new Vector3(statue.X - 0.35f, 0.15f, -0.35f), new Vector3(statue.X + 0.35f, 0.95f, 0.35f));
            pedestals.Box(new Vector3(statue.X - 0.42f, 0.95f, -0.42f), new Vector3(statue.X + 0.42f, PedestalTop, 0.42f));
        }

        hall.Place(hall.Add(
            "Galerie",
            (floor, Material.Floor),
            (stucco, Material.Stucco),
            (marble, Material.Pilaster),
            (gilt, Material.Gilt),
            (mirror, Material.Mirror),
            (frames, Material.Frame),
            (vault, Material.Vault),
            (pedestals, Material.Pedestal)));
    }

    /// <summary>Coin d'une boîte de mur : x, y, et l'épaisseur entre les plans <paramref name="a"/> et <paramref name="b"/>.</summary>
    private static Vector3 Corner(float x, float y, float a, float b, bool upper = false) =>
        new(x, y, upper ? MathF.Max(a, b) : MathF.Min(a, b));

    /// <summary>
    /// Écoinçons au-dessus d'un arc en plein cintre : face avant (dans la galerie), face arrière (fenêtres seulement) et
    /// intrados de l'arc, entre la naissance de l'arc et le linteau.
    /// </summary>
    private static void Spandrels(HallMesh mesh, float x, float inner, float back, float side)
    {
        var toward = new Vector3(0f, 0f, -side);
        var top = Spring + ArchRadius;
        var right = new Vector3(x + ArchRadius, top, 0f);
        var left = new Vector3(x - ArchRadius, top, 0f);
        for (var i = 0; i < ArchSegments; i++)
        {
            var a0 = i * MathF.PI / ArchSegments;
            var a1 = (i + 1) * MathF.PI / ArchSegments;
            var p0 = new Vector3(x + (ArchRadius * MathF.Cos(a0)), Spring + (ArchRadius * MathF.Sin(a0)), 0f);
            var p1 = new Vector3(x + (ArchRadius * MathF.Cos(a1)), Spring + (ArchRadius * MathF.Sin(a1)), 0f);
            var corner = (a0 + a1) / 2f < MathF.PI / 2f ? right : left;
            mesh.Triangle(At(corner, inner), At(p0, inner), At(p1, inner), toward);
            if (MathF.Abs(back - inner) > 0.3f)
            {
                mesh.Triangle(At(corner, back), At(p0, back), At(p1, back), -toward);
            }

            // Intrados : la surface courbe sous l'arc, tournée vers le centre de l'arc.
            var c = new Vector3(x, Spring, 0f);
            var n0 = Vector3.Normalize(c - p0);
            var n1 = Vector3.Normalize(c - p1);
            mesh.Triangle(At(p0, inner), At(p1, inner), At(p1, back), n0, n1, n1);
            mesh.Triangle(At(p0, inner), At(p1, back), At(p0, back), n0, n1, n0);
        }

        static Vector3 At(Vector3 p, float z) => new(p.X, p.Y, z);
    }

    /// <summary>Croisillons d'une arcade (fenêtre ou miroir) : montants, traverses et rayons de l'arc, dans le plan z.</summary>
    private static void PaneBars(HallMesh mesh, float x, float z, float width)
    {
        var third = ArchRadius / 3f;
        foreach (var dx in new[] { -third, third })
        {
            var top = Spring + MathF.Sqrt((ArchRadius * ArchRadius) - (dx * dx));
            mesh.Bar(new Vector3(x + dx, Sill, z), new Vector3(x + dx, top, z), width);
        }

        for (var y = Sill + 0.733f; y < Spring + 0.01f; y += 0.733f)
        {
            mesh.Bar(new Vector3(x - ArchRadius, y, z), new Vector3(x + ArchRadius, y, z), width);
        }

        for (var k = 1; k < 6; k++)
        {
            var a = k * MathF.PI / 6f;
            mesh.Bar(new Vector3(x, Spring, z), new Vector3(x + (ArchRadius * MathF.Cos(a)), Spring + (ArchRadius * MathF.Sin(a)), z), width);
        }

        // Cadre de l'arcade : montants, appui et arc.
        mesh.Bar(new Vector3(x - ArchRadius, Sill, z), new Vector3(x - ArchRadius, Spring, z), width * 1.6f);
        mesh.Bar(new Vector3(x + ArchRadius, Sill, z), new Vector3(x + ArchRadius, Spring, z), width * 1.6f);
        mesh.Bar(new Vector3(x - ArchRadius, Sill, z), new Vector3(x + ArchRadius, Sill, z), width * 1.6f);
        for (var i = 0; i < ArchSegments; i++)
        {
            var a0 = i * MathF.PI / ArchSegments;
            var a1 = (i + 1) * MathF.PI / ArchSegments;
            var r = ArchRadius - 0.02f;
            mesh.Bar(new Vector3(x + (r * MathF.Cos(a0)), Spring + (r * MathF.Sin(a0)), z), new Vector3(x + (r * MathF.Cos(a1)), Spring + (r * MathF.Sin(a1)), z), width * 1.6f);
        }
    }

    /// <summary>Six lustres de cristal (bronze doré, bougies, pendeloques), suspendus à la voûte.</summary>
    private static void BuildChandeliers(HallBuilder hall)
    {
        var metal = new HallMesh();
        var wax = new HallMesh();
        var flames = new HallMesh();
        metal.Cylinder(new Vector3(0f, 4.5f, 0f), 0.05f, 1.8f, 16);
        metal.Sphere(new Vector3(0f, 4.45f, 0f), 0.09f, 16, 8);
        metal.Cylinder(new Vector3(0f, 6.3f, 0f), 0.016f, VaultBase + VaultRadius - 6.3f, 8);
        var rings = new (float Height, float Radius, int Candles)[] { (4.95f, 0.8f, 12), (5.45f, 0.56f, 6), (5.9f, 0.33f, 0) };
        foreach (var (height, radius, candles) in rings)
        {
            metal.Torus(new Vector3(0f, height, 0f), radius, 0.022f, 48, 8);
            for (var i = 0; i < candles; i++)
            {
                var a = i * MathF.Tau / candles;
                var at = new Vector3(radius * MathF.Cos(a), height, radius * MathF.Sin(a));
                metal.Bar(new Vector3(0f, height + 0.3f, 0f), at, 0.018f);
                metal.Cylinder(at + new Vector3(0f, 0.01f, 0f), 0.032f, 0.025f, 12);
                wax.Cylinder(at + new Vector3(0f, 0.035f, 0f), 0.016f, 0.14f, 12);
                flames.Drop(at + new Vector3(0f, 0.215f, 0f), 0.012f, 0.035f, 0.035f, 6);
            }
        }

        // Pendeloques de cristal sous chaque anneau, et une boule de cristal sous le lustre.
        var crystals = new HallMesh();
        foreach (var (height, radius, count) in new (float Height, float Radius, int Count)[] { (4.86f, 0.83f, 24), (5.37f, 0.58f, 18), (5.83f, 0.35f, 12) })
        {
            for (var i = 0; i < count; i++)
            {
                var a = (i + 0.5f) * MathF.Tau / count;
                crystals.Drop(new Vector3(radius * MathF.Cos(a), height, radius * MathF.Sin(a)), 0.028f, 0.035f, 0.075f, 6, a);
            }
        }

        crystals.Sphere(new Vector3(0f, 4.22f, 0f), 0.12f, 24, 12);
        var solid = hall.Add("Lustre", (metal, Material.Gilt), (wax, Material.Wax));
        var light = hall.Add("Lustre : flammes", (flames, Material.Flame));
        var glass = hall.Add("Lustre : cristaux", (crystals, Material.Crystal));
        foreach (var x in ChandelierPositions)
        {
            var place = Matrix4x4.CreateTranslation(x, 0f, 0f);
            hall.Place(solid, place);
            hall.Place(light, place, MaskFlame);
            hall.Place(glass, place, MaskGlass);
        }
    }

    /// <summary>Statues scannées de Poly Haven (CC0), réduites à leur forme, en or, chrome, verre ou marbre.</summary>
    private static void BuildStatues(HallBuilder hall)
    {
        var meshes = new Dictionary<string, (int Mesh, Vector3 Min, Vector3 Max)>(StringComparer.Ordinal);
        foreach (var statue in Statues)
        {
            if (!meshes.TryGetValue(statue.Model, out var entry))
            {
                var folder = System.IO.Path.Combine(MaterialsScene.AssetsFolder, statue.Model);
                var model = GltfModel.Load(Directory.EnumerateFiles(folder, "*.gltf").First());
                var mesh = new HallMesh();
                mesh.Append(model);
                meshes[statue.Model] = entry = (hall.Add("Statue : " + statue.Model, (mesh, Material.StatueMarble)), model.Min, model.Max);
            }

            var scale = statue.Height / MathF.Max(entry.Max.Y - entry.Min.Y, 0.01f);
            var local = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY(statue.Yaw * MathF.PI / 180f);
            var (low, center) = Footprint(entry.Min, entry.Max, local);
            var glass = statue.Material is Material.Glass or Material.BlueGlass;
            hall.Place(entry.Mesh, local * Matrix4x4.CreateTranslation(statue.X - center.X, PedestalTop - low, -center.Y), glass ? MaskGlass : MaskSolid, statue.Material);
        }
    }

    /// <summary>Bas de l'objet tourné (y minimal) et centre de son emprise au sol, d'après les coins de sa boîte.</summary>
    private static (float Low, Vector2 Center) Footprint(Vector3 boxMin, Vector3 boxMax, Matrix4x4 local)
    {
        var low = float.MaxValue;
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        for (var k = 0; k < 8; k++)
        {
            var corner = new Vector3((k & 1) == 0 ? boxMin.X : boxMax.X, (k & 2) == 0 ? boxMin.Y : boxMax.Y, (k & 4) == 0 ? boxMin.Z : boxMax.Z);
            var p = Vector3.Transform(corner, local);
            low = MathF.Min(low, p.Y);
            min = Vector2.Min(min, new Vector2(p.X, p.Z));
            max = Vector2.Max(max, new Vector2(p.X, p.Z));
        }

        return (low, (min + max) * 0.5f);
    }

    private sealed record Statue(string Model, float X, Material Material, float Height, float Yaw);

    /// <summary>Maillages de la scène (chacun en parties, une par matière) et leurs placements (instances de la scène).</summary>
    private sealed class HallBuilder
    {
        public List<List<(HallMesh Mesh, Material Material)>> Meshes { get; } = [];

        public List<string> Names { get; } = [];

        public List<(int Mesh, Material? Material, Matrix4x4 Transform, byte Mask)> Placements { get; } = [];

        public int Add(string name, params (HallMesh Mesh, Material Material)[] parts)
        {
            Meshes.Add([.. parts.Where(p => p.Mesh.Indices.Count > 0)]);
            Names.Add(name);
            return Meshes.Count - 1;
        }

        /// <summary>Place un maillage ; <paramref name="material"/> remplace la matière de toutes ses parties (statues).</summary>
        public void Place(int mesh, Matrix4x4? transform = null, byte mask = MaskSolid, Material? material = null) =>
            Placements.Add((mesh, material, transform ?? Matrix4x4.Identity, mask));
    }
}

/// <summary>
/// Maillage de la galerie : position et normale (6 flottants par sommet). Chaque triangle est tourné vers sa normale
/// (le lancer de rayons distingue l'entrée et la sortie du verre d'après ce sens).
/// </summary>
internal sealed class HallMesh
{
    public List<float> Vertices { get; } = [];

    public List<uint> Indices { get; } = [];

    public int VertexCount => Vertices.Count / 6;

    public void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 na, Vector3 nb, Vector3 nc)
    {
        if (Vector3.Dot(Vector3.Cross(b - a, c - a), na + nb + nc) < 0f)
        {
            (b, c) = (c, b);
            (nb, nc) = (nc, nb);
        }

        var start = (uint)VertexCount;
        Add(a, na);
        Add(b, nb);
        Add(c, nc);
        Indices.AddRange([start, start + 1, start + 2]);
    }

    public void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 n) => Triangle(a, b, c, n, n, n);

    public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
    {
        Triangle(a, b, c, n);
        Triangle(a, c, d, n);
    }

    public void Box(Vector3 min, Vector3 max)
    {
        Quad(new(min.X, min.Y, min.Z), new(min.X, min.Y, max.Z), new(min.X, max.Y, max.Z), new(min.X, max.Y, min.Z), -Vector3.UnitX);
        Quad(new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z), new(max.X, max.Y, max.Z), new(max.X, min.Y, max.Z), Vector3.UnitX);
        Quad(new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, min.Y, max.Z), new(min.X, min.Y, max.Z), -Vector3.UnitY);
        Quad(new(min.X, max.Y, min.Z), new(min.X, max.Y, max.Z), new(max.X, max.Y, max.Z), new(max.X, max.Y, min.Z), Vector3.UnitY);
        Quad(new(min.X, min.Y, min.Z), new(min.X, max.Y, min.Z), new(max.X, max.Y, min.Z), new(max.X, min.Y, min.Z), -Vector3.UnitZ);
        Quad(new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z), Vector3.UnitZ);
    }

    /// <summary>Barre de section carrée entre deux points (croisillons, bras, arcs dorés).</summary>
    public void Bar(Vector3 from, Vector3 to, float width)
    {
        var axis = to - from;
        if (axis.LengthSquared() < 1e-8f)
        {
            return;
        }

        var w = Vector3.Normalize(axis);
        var u = Vector3.Normalize(Vector3.Cross(w, MathF.Abs(w.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX)) * (width / 2f);
        var v = Vector3.Normalize(Vector3.Cross(w, u)) * (width / 2f);
        Vector3 P(Vector3 at, float su, float sv) => at + (u * su) + (v * sv);
        var nu = Vector3.Normalize(u);
        var nv = Vector3.Normalize(v);
        Quad(P(from, 1, -1), P(to, 1, -1), P(to, 1, 1), P(from, 1, 1), nu);
        Quad(P(from, -1, -1), P(from, -1, 1), P(to, -1, 1), P(to, -1, -1), -nu);
        Quad(P(from, -1, 1), P(from, 1, 1), P(to, 1, 1), P(to, -1, 1), nv);
        Quad(P(from, -1, -1), P(to, -1, -1), P(to, 1, -1), P(from, 1, -1), -nv);
        Quad(P(from, -1, -1), P(from, 1, -1), P(from, 1, 1), P(from, -1, 1), -w);
        Quad(P(to, -1, -1), P(to, -1, 1), P(to, 1, 1), P(to, 1, -1), w);
    }

    /// <summary>Demi-disque vertical (au-dessus de son centre) dans un plan z constant.</summary>
    public void HalfDisk(Vector3 center, float radius, Vector3 normal, int segments)
    {
        for (var i = 0; i < segments; i++)
        {
            var a0 = i * MathF.PI / segments;
            var a1 = (i + 1) * MathF.PI / segments;
            Triangle(center, center + new Vector3(radius * MathF.Cos(a0), radius * MathF.Sin(a0), 0f), center + new Vector3(radius * MathF.Cos(a1), radius * MathF.Sin(a1), 0f), normal);
        }
    }

    /// <summary>Demi-disque vertical dans un plan x constant (lunettes des extrémités).</summary>
    public void HalfDiskX(Vector3 center, float radius, Vector3 normal, int segments)
    {
        for (var i = 0; i < segments; i++)
        {
            var a0 = i * MathF.PI / segments;
            var a1 = (i + 1) * MathF.PI / segments;
            Triangle(center, center + new Vector3(0f, radius * MathF.Sin(a0), radius * MathF.Cos(a0)), center + new Vector3(0f, radius * MathF.Sin(a1), radius * MathF.Cos(a1)), normal);
        }
    }

    /// <summary>Cylindre vertical posé sur <paramref name="bottom"/>, flanc lissé, deux couvercles.</summary>
    public void Cylinder(Vector3 bottom, float radius, float height, int segments)
    {
        var top = bottom + new Vector3(0f, height, 0f);
        for (var i = 0; i < segments; i++)
        {
            var a0 = i * MathF.Tau / segments;
            var a1 = (i + 1) * MathF.Tau / segments;
            var n0 = new Vector3(MathF.Cos(a0), 0f, MathF.Sin(a0));
            var n1 = new Vector3(MathF.Cos(a1), 0f, MathF.Sin(a1));
            Triangle(bottom + (n0 * radius), bottom + (n1 * radius), top + (n1 * radius), n0, n1, n1);
            Triangle(bottom + (n0 * radius), top + (n1 * radius), top + (n0 * radius), n0, n1, n0);
            Triangle(top, top + (n0 * radius), top + (n1 * radius), Vector3.UnitY);
            Triangle(bottom, bottom + (n1 * radius), bottom + (n0 * radius), -Vector3.UnitY);
        }
    }

    /// <summary>Sphère lissée (méridiens et parallèles).</summary>
    public void Sphere(Vector3 center, float radius, int slices, int stacks)
    {
        Vector3 N(int i, int j)
        {
            var theta = j * MathF.PI / stacks;
            var phi = i * MathF.Tau / slices;
            return new Vector3(MathF.Sin(theta) * MathF.Cos(phi), MathF.Cos(theta), MathF.Sin(theta) * MathF.Sin(phi));
        }

        for (var j = 0; j < stacks; j++)
        {
            for (var i = 0; i < slices; i++)
            {
                var n00 = N(i, j);
                var n10 = N(i + 1, j);
                var n01 = N(i, j + 1);
                var n11 = N(i + 1, j + 1);
                if (j > 0)
                {
                    Triangle(center + (n00 * radius), center + (n10 * radius), center + (n11 * radius), n00, n10, n11);
                }

                if (j < stacks - 1)
                {
                    Triangle(center + (n00 * radius), center + (n11 * radius), center + (n01 * radius), n00, n11, n01);
                }
            }
        }
    }

    /// <summary>Tore horizontal (anneaux des lustres).</summary>
    public void Torus(Vector3 center, float major, float minor, int segments, int sides)
    {
        Vector3 Point(int i, int j, out Vector3 normal)
        {
            var a = i * MathF.Tau / segments;
            var b = j * MathF.Tau / sides;
            var radial = new Vector3(MathF.Cos(a), 0f, MathF.Sin(a));
            normal = (radial * MathF.Cos(b)) + (Vector3.UnitY * MathF.Sin(b));
            return center + (radial * major) + (normal * minor);
        }

        for (var i = 0; i < segments; i++)
        {
            for (var j = 0; j < sides; j++)
            {
                var p00 = Point(i, j, out var n00);
                var p10 = Point(i + 1, j, out var n10);
                var p01 = Point(i, j + 1, out var n01);
                var p11 = Point(i + 1, j + 1, out var n11);
                Triangle(p00, p10, p11, n00, n10, n11);
                Triangle(p00, p11, p01, n00, n11, n01);
            }
        }
    }

    /// <summary>Pendeloque taillée : pointe en haut et en bas, facettes planes (verre qui scintille).</summary>
    public void Drop(Vector3 center, float radius, float up, float down, int sides, float turn = 0f)
    {
        var top = center + new Vector3(0f, up, 0f);
        var bottom = center - new Vector3(0f, down, 0f);
        for (var i = 0; i < sides; i++)
        {
            var a0 = turn + (i * MathF.Tau / sides);
            var a1 = turn + ((i + 1) * MathF.Tau / sides);
            var p0 = center + new Vector3(radius * MathF.Cos(a0), 0f, radius * MathF.Sin(a0));
            var p1 = center + new Vector3(radius * MathF.Cos(a1), 0f, radius * MathF.Sin(a1));
            Facet(top, p0, p1, center);
            Facet(bottom, p1, p0, center);
        }
    }

    /// <summary>Ajoute les parties d'un modèle glTF (positions et normales, sans les textures).</summary>
    public void Append(GltfModel model)
    {
        foreach (var part in model.Primitives)
        {
            var start = (uint)VertexCount;
            for (var i = 0; i < part.Vertices.Length; i += GltfModel.FloatsPerVertex)
            {
                for (var k = 0; k < 6; k++)
                {
                    Vertices.Add(part.Vertices[i + k]);
                }
            }

            foreach (var index in part.Indices)
            {
                Indices.Add(start + index);
            }
        }
    }

    private void Facet(Vector3 a, Vector3 b, Vector3 c, Vector3 inside)
    {
        var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        if (Vector3.Dot(n, ((a + b + c) / 3f) - inside) < 0f)
        {
            n = -n;
        }

        Triangle(a, b, c, n);
    }

    private void Add(Vector3 p, Vector3 n)
    {
        n = n.LengthSquared() > 0f ? Vector3.Normalize(n) : Vector3.UnitY;
        Vertices.AddRange([p.X, p.Y, p.Z, n.X, n.Y, n.Z]);
    }
}
