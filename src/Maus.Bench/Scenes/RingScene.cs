using System.Numerics;
using System.Runtime.InteropServices;
using Maus.Bench.Gpu;
using Maus.Bench.Render;
using static Maus.Core.Localization.Texts;

namespace Maus.Bench.Scenes;

/// <summary>
/// Test « Géométrie » : vol dans l'anneau d'une planète géante. 250 000 rochers détaillés (320 triangles) le long du
/// trajet de la caméra et 500 000 rochers simples (80 triangles) sur le reste de l'anneau, dessinés en une passe pour
/// la caméra et une passe pour les ombres du soleil : environ 200 millions de triangles par image.
/// </summary>
internal sealed class RingScene : BenchScene
{
    private const int Detailed = 250_000;
    private const int Coarse = 500_000;
    private const float PlanetRadius = 40f;
    private const int ShadowSize = 4096;

    private static readonly Vector3 Sun = Vector3.Normalize(new Vector3(0.55f, 0.22f, -0.8f));

    private static readonly CameraPath Path = new(
        (0, new CameraPose(OnRing(-0.25f, 100f, 22f), OnRing(0.1f, 80f, 0f), 55f)),
        (20, new CameraPose(OnRing(0.02f, 90f, 10f), OnRing(0.22f, 82f, 2f), 58f, -6f)),
        (40, new CameraPose(OnRing(0.32f, 85f, 6.5f), OnRing(0.48f, 78f, 1f), 62f, 4f)),
        (60, new CameraPose(OnRing(0.6f, 82f, -8f), new Vector3(0f, 4f, 0f), 60f, -3f)),
        (75, new CameraPose(OnRing(0.85f, 88f, 7f), OnRing(1.0f, 80f, 0f), 58f, 5f)),
        (92, new CameraPose(OnRing(1.05f, 114f, 24f), OnRing(0.6f, 70f, 0f), 52f)));

    private IBuffer? _rocks;
    private IBuffer? _detailedVertices;
    private IBuffer? _detailedIndices;
    private IBuffer? _coarseVertices;
    private IBuffer? _coarseIndices;
    private int _detailedIndexCount;
    private int _coarseIndexCount;
    private ITexture? _shadowMap;
    private IPipeline? _background;
    private IPipeline? _rocksPipeline;
    private IPipeline? _shadowPipeline;

    public override string Id => "ring";

    public override string Title => T("Anneau de la géante");

    public override string Subtitle => T("Géométrie : 750 000 rochers, environ 200 millions de triangles par image avec les ombres");

    public override GpuCapability Capability => GpuCapability.Geometry;

    public override void Load(SceneContext context)
    {
        var device = context.Device;
        var shaders = context.Shaders;
        var (dv, di) = RockMesh.Create(subdivisions: 2, seed: 7);
        var (cv, ci) = RockMesh.Create(subdivisions: 1, seed: 7);
        _detailedVertices = device.CreateBuffer(new BufferDesc(dv.Length * 4, BufferUsage.Vertex, 24, "Rocher détaillé"), MemoryMarshal.AsBytes(dv.AsSpan()));
        _detailedIndices = device.CreateBuffer(new BufferDesc(di.Length * 4, BufferUsage.Index, 4, "Rocher détaillé : indices"), MemoryMarshal.AsBytes(di.AsSpan()));
        _coarseVertices = device.CreateBuffer(new BufferDesc(cv.Length * 4, BufferUsage.Vertex, 24, "Rocher simple"), MemoryMarshal.AsBytes(cv.AsSpan()));
        _coarseIndices = device.CreateBuffer(new BufferDesc(ci.Length * 4, BufferUsage.Index, 4, "Rocher simple : indices"), MemoryMarshal.AsBytes(ci.AsSpan()));
        _detailedIndexCount = di.Length;
        _coarseIndexCount = ci.Length;

        var rocks = CreateRocks();
        _rocks = device.CreateBuffer(new BufferDesc(rocks.Length * 48L, BufferUsage.Structured, 48, "Rochers"), MemoryMarshal.AsBytes(rocks.AsSpan()));
        _shadowMap = device.CreateTexture(TextureDesc.DepthTarget(ShadowSize, ShadowSize, "Ombres du soleil"));

        VertexElement[] layout = [new("POSITION", 0, VertexFormat.Float3, 0), new("NORMAL", 0, VertexFormat.Float3, 12)];
        _background = device.CreatePipeline(new GraphicsPipelineDesc(
            "Anneau : fond", shaders.Get("common.hlsli", "FullscreenVS", "vs_5_0"), shaders.Get("ring.hlsl", "BackgroundPS", "ps_5_0"), [],
            BlendMode.Opaque, DepthMode.TestWrite, CullMode.None, [PixelFormat.Rgba16Float, PixelFormat.Rg16Float], PixelFormat.D32Float));
        _rocksPipeline = device.CreatePipeline(new GraphicsPipelineDesc(
            "Anneau : rochers", shaders.Get("ring.hlsl", "RockVS", "vs_5_0"), shaders.Get("ring.hlsl", "RockPS", "ps_5_0"), layout,
            BlendMode.Opaque, DepthMode.TestWrite, CullMode.Back, [PixelFormat.Rgba16Float, PixelFormat.Rg16Float], PixelFormat.D32Float));
        _shadowPipeline = device.CreatePipeline(new GraphicsPipelineDesc(
            "Anneau : ombres", shaders.Get("ring.hlsl", "ShadowVS", "vs_5_0"), null, layout,
            BlendMode.Opaque, DepthMode.TestWrite, CullMode.Back, [], PixelFormat.D32Float, DepthBias: 2000, SlopeScaledDepthBias: 2.5f));
    }

    public override SceneState Evaluate(double time) => new(Path.Evaluate(PathTime(time, Path)), new ColorGrade
    {
        Exposure = 1.15f,
        BloomIntensity = 0.55f,
        BloomThreshold = 1.4f,
        Vignette = 0.45f,
        Grain = 0.012f,
        Aberration = 0.005f,
        Saturation = 1.08f,
        Gain = new Vector3(1.03f, 1f, 0.97f),
        Contrast = 1.1f,
    })
    {
        SunDirection = Sun,
        SunColor = new Vector3(1f, 0.93f, 0.82f),
        NearPlane = 0.1f,
        FarPlane = 3000f,
    };

    public override void Render(SceneContext context)
    {
        var cmd = context.Commands;
        var frame = context.Frame;

        // Ombres : vue orthographique du soleil centrée sur ce que regarde la caméra.
        var forward = Vector3.Normalize(new Vector3(frame.InvView.M31, frame.InvView.M32, frame.InvView.M33) * -1f);
        var center = frame.CameraPos + (forward * 35f);
        var lightView = Matrix4x4.CreateLookAt(center + (Sun * 250f), center, Vector3.UnitY);
        var lightProj = Matrix4x4.CreateOrthographic(110f, 110f, 1f, 500f);
        var constants = new RingConstants
        {
            ShadowViewProj = lightView * lightProj,
            RingParams = new Vector4(0, frame.Time, PlanetRadius, 1f / ShadowSize),
            PlanetCenter = new Vector4(0, 0, 0, 0),
        };

        cmd.SetRenderTarget(null, _shadowMap);
        cmd.ClearDepth(_shadowMap!, 1f);
        cmd.SetPipeline(_shadowPipeline!);
        cmd.SetBuffer(0, _rocks);
        cmd.SetConstants(1, constants);
        cmd.SetVertexBuffer(0, _detailedVertices, 24);
        cmd.SetIndexBuffer(_detailedIndices);
        cmd.DrawIndexed(_detailedIndexCount, Detailed);
        cmd.Flush();

        var post = context.Post;
        cmd.SetRenderTargets([post.HdrColor, post.Velocity], post.Depth);
        cmd.ClearDepth(post.Depth, 1f);
        cmd.SetPipeline(_background!);
        cmd.SetConstants(1, constants);
        cmd.Draw(3);

        cmd.SetPipeline(_rocksPipeline!);
        cmd.SetBuffer(0, _rocks);
        cmd.SetTexture(1, _shadowMap);
        cmd.SetVertexBuffer(0, _detailedVertices, 24);
        cmd.SetIndexBuffer(_detailedIndices);
        cmd.DrawIndexed(_detailedIndexCount, Detailed);
        cmd.Flush();
        cmd.SetConstants(1, constants with { RingParams = constants.RingParams with { X = Detailed } });
        cmd.SetVertexBuffer(0, _coarseVertices, 24);
        cmd.SetIndexBuffer(_coarseIndices);
        cmd.DrawIndexed(_coarseIndexCount, Coarse);
        cmd.SetTexture(1, null);
        cmd.SetBuffer(0, null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _rocks?.Dispose();
            _detailedVertices?.Dispose();
            _detailedIndices?.Dispose();
            _coarseVertices?.Dispose();
            _coarseIndices?.Dispose();
            _shadowMap?.Dispose();
            _background?.Dispose();
            _rocksPipeline?.Dispose();
            _shadowPipeline?.Dispose();
        }

        base.Dispose(disposing);
    }

    private static Vector3 OnRing(float angle, float radius, float height) =>
        new(radius * MathF.Cos(angle), height, radius * MathF.Sin(angle));

    /// <summary>
    /// Les rochers : détaillés sur l'arc parcouru par la caméra, simples ailleurs. Tirage reproductible ; un couloir
    /// est dégagé autour du trajet de la caméra pour qu'elle ne traverse jamais un rocher.
    /// </summary>
    private static RockInstance[] CreateRocks()
    {
        var random = new Random(20261006);
        var rocks = new RockInstance[Detailed + Coarse];
        var corridor = Enumerable.Range(0, 400).Select(k => Path.Evaluate(k * Path.Duration / 399).Position).ToArray();
        for (var i = 0; i < rocks.Length; i++)
        {
            var detailed = i < Detailed;
            var angle = detailed ? -0.45f + (1.8f * random.NextSingle()) : 1.35f + ((MathF.Tau - 1.8f) * random.NextSingle());
            // Densité plus forte dans des sillons de l'anneau (divisions comme celles des vraies planètes à anneaux).
            float radius;
            do
            {
                radius = 60f + (52f * random.NextSingle());
            }
            while (random.NextSingle() > 0.35f + (0.65f * MathF.Pow(MathF.Sin(radius * 0.21f) * 0.5f + 0.5f, 2f)));

            var height = ((random.NextSingle() + random.NextSingle() + random.NextSingle()) - 1.5f) * 5f;
            var size = 0.04f + (1.1f * MathF.Pow(random.NextSingle(), 11f));
            var position = new Vector3(radius * MathF.Cos(angle), height, radius * MathF.Sin(angle));
            if (detailed && Array.Exists(corridor, c => Vector3.DistanceSquared(c, position) < MathF.Pow(1.6f + (size * 2f), 2f)))
            {
                i--;
                continue;
            }

            var axis = Vector3.Normalize(new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f) + new Vector3(1e-3f));
            var ice = random.NextSingle() < 0.25f ? 1f : 0f;
            var tone = 0.08f + (0.32f * random.NextSingle());
            var color = random.NextSingle() < 0.3f ? new Vector3(tone * 1.25f, tone * 0.9f, tone * 0.7f) : new Vector3(tone * 1.05f, tone, tone * 0.95f);
            rocks[i] = new RockInstance(
                new Vector4(position, size),
                new Vector4(axis, (0.1f + (0.5f * random.NextSingle())) * (random.NextSingle() < 0.5f ? -1f : 1f)),
                new Vector4(color, ice));
        }

        return rocks;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RockInstance(Vector4 PositionScale, Vector4 AxisSpin, Vector4 Color);

    [StructLayout(LayoutKind.Sequential)]
    private record struct RingConstants(Matrix4x4 ShadowViewProj, Vector4 RingParams, Vector4 PlanetCenter);
}

/// <summary>Maillage de rocher : icosaèdre subdivisé, déformé par du bruit (bosses et facettes), normales lissées.</summary>
internal static class RockMesh
{
    public static (float[] Vertices, uint[] Indices) Create(int subdivisions, int seed)
    {
        var t = (1f + MathF.Sqrt(5f)) / 2f;
        var positions = new List<Vector3>
        {
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
            new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
            new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
        };
        for (var i = 0; i < positions.Count; i++)
        {
            positions[i] = Vector3.Normalize(positions[i]);
        }

        var faces = new List<(int A, int B, int C)>
        {
            (0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11), (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
            (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9), (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1),
        };

        for (var level = 0; level < subdivisions; level++)
        {
            var cache = new Dictionary<(int, int), int>();
            var next = new List<(int, int, int)>();
            int Middle(int a, int b)
            {
                var key = a < b ? (a, b) : (b, a);
                if (!cache.TryGetValue(key, out var index))
                {
                    positions.Add(Vector3.Normalize((positions[a] + positions[b]) * 0.5f));
                    index = positions.Count - 1;
                    cache[key] = index;
                }

                return index;
            }

            foreach (var (a, b, c) in faces)
            {
                var ab = Middle(a, b);
                var bc = Middle(b, c);
                var ca = Middle(c, a);
                next.Add((a, ab, ca));
                next.Add((b, bc, ab));
                next.Add((c, ca, bc));
                next.Add((ab, bc, ca));
            }

            faces = next;
        }

        // Déformation : quelques grandes bosses et des facettes, reproductibles d'un lancement à l'autre.
        var random = new Random(seed);
        var bumps = Enumerable.Range(0, 7).Select(_ => (Direction: Vector3.Normalize(new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f)), Strength: (random.NextSingle() - 0.4f) * 0.5f)).ToArray();
        for (var i = 0; i < positions.Count; i++)
        {
            var p = positions[i];
            var radius = 1f;
            foreach (var (direction, strength) in bumps)
            {
                radius += strength * MathF.Pow(MathF.Max(0f, Vector3.Dot(p, direction)), 3f);
            }

            radius += 0.06f * MathF.Sin(p.X * 9.1f) * MathF.Sin(p.Y * 7.3f + 1f) * MathF.Sin(p.Z * 8.7f + 2f);
            positions[i] = p * radius * new Vector3(1f, 0.78f, 0.9f);
        }

        var normals = new Vector3[positions.Count];
        foreach (var (a, b, c) in faces)
        {
            var n = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            normals[a] += n;
            normals[b] += n;
            normals[c] += n;
        }

        var vertices = new float[positions.Count * 6];
        for (var i = 0; i < positions.Count; i++)
        {
            var n = Vector3.Normalize(normals[i]);
            vertices[(i * 6) + 0] = positions[i].X;
            vertices[(i * 6) + 1] = positions[i].Y;
            vertices[(i * 6) + 2] = positions[i].Z;
            vertices[(i * 6) + 3] = n.X;
            vertices[(i * 6) + 4] = n.Y;
            vertices[(i * 6) + 5] = n.Z;
        }

        // Ordre des sommets inversé : Direct3D considère les faces dans le sens horaire comme visibles.
        var indices = faces.SelectMany(f => new[] { (uint)f.A, (uint)f.C, (uint)f.B }).ToArray();
        return (vertices, indices);
    }
}
