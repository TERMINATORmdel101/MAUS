using System.Numerics;
using System.Runtime.InteropServices;
using Maus.Bench.Gpu;
using Maus.Bench.Render;
using static Maus.Core.Localization.Texts;

namespace Maus.Bench.Scenes;

/// <summary>
/// Test « Effets » (idée du porteur : l'explosion d'un char) : champ de bataille au coucher du soleil, 24 chars qui
/// explosent l'un après l'autre. Boules de feu, tourelles projetées, 7 680 éclats qui rebondissent, 430 000 particules
/// de feu, de fumée et d'étincelles dont les trajectoires sont recalculées à chaque image, colonnes de fumée qui
/// s'accumulent jusqu'à la fin : transparences superposées, éclairage par les explosions, ombres du soleil.
/// </summary>
internal sealed class BattleScene : BenchScene
{
    private const int Tanks = 24;
    private const int DebrisPerTank = 320;
    private const int FirePerTank = 6000;
    private const int SmokePerTank = 2200;
    private const int SparksPerTank = 5000;
    private const int ShadowSize = 4096;

    private static readonly Vector3 Sun = Vector3.Normalize(new Vector3(-0.75f, 0.2f, 0.45f));

    private static readonly CameraPath Path = new(
        (0, new CameraPose(new Vector3(-75f, 13f, -62f), new Vector3(-5f, 2f, 0f), 48f)),
        (15, new CameraPose(new Vector3(-50f, 5f, -40f), new Vector3(-24f, 3f, -10f), 44f, -2f)),
        (35, new CameraPose(new Vector3(-24f, 3.2f, -32f), new Vector3(-8f, 4f, -2f), 50f, 3f)),
        (55, new CameraPose(new Vector3(8f, 10f, -42f), new Vector3(4f, 5f, 0f), 55f)),
        (75, new CameraPose(new Vector3(42f, 4f, -26f), new Vector3(16f, 4f, 4f), 50f, -3f)),
        (95, new CameraPose(new Vector3(58f, 20f, 12f), new Vector3(2f, 6f, 0f), 50f)),
        (120, new CameraPose(new Vector3(18f, 48f, 72f), new Vector3(0f, 2f, 0f), 55f)));

    private readonly TankData[] _tanks = CreateTanks();
    private readonly Vector4[] _lightPosition = new Vector4[8];
    private readonly Vector4[] _lightColor = new Vector4[8];
    private IBuffer? _tankBuffer;
    private Mesh? _hull;
    private Mesh? _turret;
    private Mesh? _debris;
    private ITexture? _shadowMap;
    private IPipeline? _ground;
    private IPipeline? _solid;
    private IPipeline? _shadow;
    private IPipeline? _smoke;
    private IPipeline? _fire;

    public override string Id => "battle";

    public override string Title => T("Champ de bataille");

    public override string Subtitle => T("Effets : 24 chars explosent, 320 000 particules de feu, de fumée et d'étincelles");

    public override GpuCapability Capability => GpuCapability.Effects;

    public override bool HasOverlay => true;

    public override void Load(SceneContext context)
    {
        var device = context.Device;
        var shaders = context.Shaders;
        _tankBuffer = device.CreateBuffer(new BufferDesc(Tanks * 32, BufferUsage.Structured, 32, "Chars"), MemoryMarshal.AsBytes(_tanks.AsSpan()));
        _hull = Mesh.Create(device, "Char : caisse", TankMeshes.Hull());
        _turret = Mesh.Create(device, "Char : tourelle", TankMeshes.Turret());
        _debris = Mesh.Create(device, "Éclat", TankMeshes.Shard());
        _shadowMap = device.CreateTexture(TextureDesc.DepthTarget(ShadowSize, ShadowSize, "Ombres du soleil couchant"));

        VertexElement[] layout = [new("POSITION", 0, VertexFormat.Float3, 0), new("NORMAL", 0, VertexFormat.Float3, 12)];
        PixelFormat[] targets = [PixelFormat.Rgba16Float, PixelFormat.Rg16Float];
        _ground = device.CreatePipeline(new GraphicsPipelineDesc(
            "Bataille : sol", shaders.Get("common.hlsli", "FullscreenVS", "vs_5_0"), shaders.Get("battle.hlsl", "GroundPS", "ps_5_0"), [],
            BlendMode.Opaque, DepthMode.TestWrite, CullMode.None, targets, PixelFormat.D32Float));
        _solid = device.CreatePipeline(new GraphicsPipelineDesc(
            "Bataille : chars et éclats", shaders.Get("battle.hlsl", "SolidVS", "vs_5_0"), shaders.Get("battle.hlsl", "SolidPS", "ps_5_0"), layout,
            BlendMode.Opaque, DepthMode.TestWrite, CullMode.None, targets, PixelFormat.D32Float));
        _shadow = device.CreatePipeline(new GraphicsPipelineDesc(
            "Bataille : ombres", shaders.Get("battle.hlsl", "SolidShadowVS", "vs_5_0"), null, layout,
            BlendMode.Opaque, DepthMode.TestWrite, CullMode.None, [], PixelFormat.D32Float, DepthBias: 1500, SlopeScaledDepthBias: 2f));
        var particles = shaders.Get("battle.hlsl", "ParticleVS", "vs_5_0");
        _smoke = device.CreatePipeline(new GraphicsPipelineDesc(
            "Bataille : fumée", particles, shaders.Get("battle.hlsl", "SmokePS", "ps_5_0"), [],
            BlendMode.Premultiplied, DepthMode.TestOnly, CullMode.None, [PixelFormat.Rgba16Float], PixelFormat.D32Float));
        _fire = device.CreatePipeline(new GraphicsPipelineDesc(
            "Bataille : feu", particles, shaders.Get("battle.hlsl", "FirePS", "ps_5_0"), [],
            BlendMode.Additive, DepthMode.TestOnly, CullMode.None, [PixelFormat.Rgba16Float], PixelFormat.D32Float));
    }

    public override SceneState Evaluate(double time) => new(Path.Evaluate(PathTime(time, Path)), new ColorGrade
    {
        Exposure = 1.0f,
        BloomIntensity = 0.75f,
        BloomThreshold = 1.5f,
        Vignette = 0.5f,
        Grain = 0.016f,
        Aberration = 0.006f,
        Saturation = 1.1f,
        Gain = new Vector3(1.05f, 0.98f, 0.92f),
        Contrast = 1.12f,
    })
    {
        SunDirection = Sun,
        SunColor = new Vector3(1f, 0.62f, 0.36f) * 1.2f,
        NearPlane = 0.1f,
        FarPlane = 2500f,
    };

    public override void Render(SceneContext context)
    {
        var cmd = context.Commands;
        var time = (float)PathTime(context.Frame.Time, Path);
        UpdateLights(time);
        var constants = Constants(time, 0, 0);

        // Ombres du soleil couchant sur tout le champ de bataille.
        cmd.SetRenderTarget(null, _shadowMap);
        cmd.ClearDepth(_shadowMap!, 1f);
        cmd.SetPipeline(_shadow!);
        cmd.SetBuffer(0, _tankBuffer);
        DrawSolids(cmd, constants);
        cmd.Flush();

        var post = context.Post;
        cmd.SetRenderTargets([post.HdrColor, post.Velocity], post.Depth);
        cmd.ClearDepth(post.Depth, 1f);
        cmd.SetPipeline(_ground!);
        cmd.SetBuffer(0, _tankBuffer);
        cmd.SetTexture(1, _shadowMap);
        cmd.SetConstants(1, constants);
        context.DrawFullscreenInBands(4);

        cmd.SetPipeline(_solid!);
        cmd.SetBuffer(0, _tankBuffer);
        cmd.SetTexture(1, _shadowMap);
        DrawSolids(cmd, constants);
        cmd.SetTexture(1, null);
    }

    /// <summary>Feu, fumée et étincelles, dessinés après l'anticrénelage (nets, sans traînées).</summary>
    public override void RenderOverlay(SceneContext context, ITexture target)
    {
        var cmd = context.Commands;
        var time = (float)PathTime(context.Frame.Time, Path);
        cmd.SetRenderTarget(target, context.Post.Depth);
        cmd.SetBuffer(0, _tankBuffer);
        cmd.SetPipeline(_smoke!);
        cmd.SetConstants(1, Constants(time, 0, 0));
        cmd.Draw(6, Tanks * SmokePerTank);
        cmd.Flush();
        cmd.SetPipeline(_fire!);
        cmd.SetConstants(1, Constants(time, 0, 1));
        cmd.Draw(6, Tanks * FirePerTank);
        cmd.SetConstants(1, Constants(time, 0, 2));
        cmd.Draw(6, Tanks * SparksPerTank);
        cmd.SetBuffer(0, null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tankBuffer?.Dispose();
            _hull?.Dispose();
            _turret?.Dispose();
            _debris?.Dispose();
            _shadowMap?.Dispose();
            _ground?.Dispose();
            _solid?.Dispose();
            _shadow?.Dispose();
            _smoke?.Dispose();
            _fire?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void DrawSolids(ICommandList cmd, BattleConstants constants)
    {
        _hull!.Bind(cmd);
        cmd.SetConstants(1, constants with { Battle = constants.Battle with { Z = 0 } });
        cmd.DrawIndexed(_hull.IndexCount, Tanks);
        _turret!.Bind(cmd);
        cmd.SetConstants(1, constants with { Battle = constants.Battle with { Z = 1 } });
        cmd.DrawIndexed(_turret.IndexCount, Tanks);
        _debris!.Bind(cmd);
        cmd.SetConstants(1, constants with { Battle = constants.Battle with { Z = 2 } });
        cmd.DrawIndexed(_debris.IndexCount, Tanks * DebrisPerTank);
    }

    /// <summary>Les huit explosions les plus lumineuses éclairent la scène : éclair bref, puis lueur du feu qui décroît.</summary>
    private void UpdateLights(float time)
    {
        var lights = _tanks
            .Select(tank => (Tank: tank, Since: time - tank.Explosion.X))
            .Where(x => x.Since >= 0)
            .Select(x => (x.Tank, Intensity: (60f * MathF.Exp(-x.Since * 5f)) + (22f * MathF.Exp(-x.Since / 7f)) + (5f * MathF.Exp(-x.Since / 40f))))
            .OrderByDescending(x => x.Intensity)
            .Take(8)
            .ToArray();
        for (var i = 0; i < 8; i++)
        {
            if (i < lights.Length)
            {
                var p = lights[i].Tank.PositionHeading;
                _lightPosition[i] = new Vector4(p.X, 3f, p.Z, lights[i].Intensity);
                _lightColor[i] = new Vector4(1f, 0.55f, 0.22f, 0f);
            }
            else
            {
                _lightPosition[i] = Vector4.Zero;
                _lightColor[i] = Vector4.Zero;
            }
        }
    }

    private BattleConstants Constants(float time, int offset, int kind)
    {
        var lightView = Matrix4x4.CreateLookAt(Sun * 300f, Vector3.Zero, Vector3.UnitY);
        var lightProj = Matrix4x4.CreateOrthographic(170f, 170f, 1f, 700f);
        var constants = new BattleConstants
        {
            ShadowViewProj = lightView * lightProj,
            Battle = new Vector4(time, offset, kind, 1f / ShadowSize),
        };
        _lightPosition.CopyTo(constants.LightPositions);
        _lightColor.CopyTo(constants.LightColors);
        return constants;
    }

    private static TankData[] CreateTanks()
    {
        var random = new Random(1944);
        var tanks = new TankData[Tanks];
        for (var i = 0; i < Tanks; i++)
        {
            var row = i / 6;
            var column = i % 6;
            var position = new Vector3(-42f + (column * 16.5f) + (random.NextSingle() * 5f), 0f, -27f + (row * 17f) + (random.NextSingle() * 6f));
            var heading = 0.35f + ((random.NextSingle() - 0.5f) * 0.9f);
            // Explosions en cascade : d'abord la colonne de gauche, toutes les 4,5 secondes environ.
            var order = (column * 4) + row;
            tanks[i] = new TankData(new Vector4(position, heading), new Vector4(5f + (order * 4.5f) + (random.NextSingle() * 1.5f), random.NextSingle(), i, 0));
        }

        return tanks;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct TankData(Vector4 PositionHeading, Vector4 Explosion);

    [StructLayout(LayoutKind.Sequential)]
    private struct BattleConstants
    {
        public Matrix4x4 ShadowViewProj;
        public Vector4 Battle;
        public LightArray LightPositions;
        public LightArray LightColors;
    }

    [System.Runtime.CompilerServices.InlineArray(8)]
    private struct LightArray
    {
        private Vector4 _element;
    }
}

/// <summary>Maillage simple (sommets : position, normale) prêt à dessiner.</summary>
internal sealed class Mesh(IBuffer vertices, IBuffer indices, int indexCount) : IDisposable
{
    public int IndexCount { get; } = indexCount;

    public static Mesh Create(IGpuDevice device, string name, (float[] Vertices, uint[] Indices) data) => new(
        device.CreateBuffer(new BufferDesc(data.Vertices.Length * 4, BufferUsage.Vertex, 24, name), MemoryMarshal.AsBytes(data.Vertices.AsSpan())),
        device.CreateBuffer(new BufferDesc(data.Indices.Length * 4, BufferUsage.Index, 4, name + " : indices"), MemoryMarshal.AsBytes(data.Indices.AsSpan())),
        data.Indices.Length);

    public void Bind(ICommandList cmd)
    {
        cmd.SetVertexBuffer(0, vertices, 24);
        cmd.SetIndexBuffer(indices);
    }

    public void Dispose()
    {
        vertices.Dispose();
        indices.Dispose();
    }
}

/// <summary>Char d'assaut construit avec des pavés et des cylindres (faces planes, comme un modèle simplifié).</summary>
internal static class TankMeshes
{
    public static (float[] Vertices, uint[] Indices) Hull()
    {
        var builder = new MeshBuilder();
        // Caisse avec glacis incliné à l'avant, chenilles, roues et garde-boue.
        builder.Box(new Vector3(0f, 1.15f, 0f), new Vector3(3.1f, 0.55f, 1.55f), frontSlope: 0.6f);
        builder.Box(new Vector3(0f, 0.55f, 1.55f), new Vector3(3.35f, 0.5f, 0.38f));
        builder.Box(new Vector3(0f, 0.55f, -1.55f), new Vector3(3.35f, 0.5f, 0.38f));
        builder.Box(new Vector3(0f, 1.1f, 1.62f), new Vector3(3.4f, 0.05f, 0.42f));
        builder.Box(new Vector3(0f, 1.1f, -1.62f), new Vector3(3.4f, 0.05f, 0.42f));
        for (var w = 0; w < 6; w++)
        {
            var x = -2.6f + (w * 1.04f);
            builder.Cylinder(new Vector3(x, 0.45f, 1.95f), 0.4f, 0.1f, Vector3.UnitZ, 14);
            builder.Cylinder(new Vector3(x, 0.45f, -1.95f), 0.4f, 0.1f, Vector3.UnitZ, 14);
        }

        builder.Box(new Vector3(-3.0f, 1.45f, 0f), new Vector3(0.12f, 0.25f, 1.2f));
        return builder.Build();
    }

    public static (float[] Vertices, uint[] Indices) Turret()
    {
        var builder = new MeshBuilder();
        builder.Box(new Vector3(-0.2f, 2.05f, 0f), new Vector3(1.55f, 0.38f, 1.25f), frontSlope: 0.5f);
        builder.Cylinder(new Vector3(-0.4f, 2.5f, 0.45f), 0.32f, 0.08f, Vector3.UnitY, 12);
        builder.Cylinder(new Vector3(3.15f, 2.12f, 0f), 0.13f, 1.9f, Vector3.UnitX, 12);
        builder.Cylinder(new Vector3(5.0f, 2.12f, 0f), 0.18f, 0.18f, Vector3.UnitX, 12);
        builder.Box(new Vector3(-1.85f, 2.0f, 0f), new Vector3(0.3f, 0.28f, 1.05f));
        return builder.Build();
    }

    public static (float[] Vertices, uint[] Indices) Shard()
    {
        var builder = new MeshBuilder();
        builder.Box(Vector3.Zero, new Vector3(0.5f, 0.35f, 0.45f), frontSlope: 0.35f);
        return builder.Build();
    }
}

/// <summary>Assemble des pavés et des cylindres en un seul maillage à normales par face.</summary>
internal sealed class MeshBuilder
{
    private readonly List<float> _vertices = [];
    private readonly List<uint> _indices = [];

    public void Box(Vector3 center, Vector3 half, float frontSlope = 0f)
    {
        var c = new Vector3[8];
        for (var i = 0; i < 8; i++)
        {
            var sx = (i & 1) == 0 ? -1f : 1f;
            var sy = (i & 2) == 0 ? -1f : 1f;
            var sz = (i & 4) == 0 ? -1f : 1f;
            var p = new Vector3(sx * half.X, sy * half.Y, sz * half.Z);
            if (sx > 0 && sy > 0)
            {
                // Arête avant-haute reculée : glacis incliné.
                p.X -= half.X * frontSlope * 0.5f;
            }

            c[i] = center + p;
        }

        Quad(c[0], c[2], c[3], c[1]);
        Quad(c[4], c[5], c[7], c[6]);
        Quad(c[0], c[4], c[6], c[2]);
        Quad(c[1], c[3], c[7], c[5]);
        Quad(c[0], c[1], c[5], c[4]);
        Quad(c[2], c[6], c[7], c[3]);
    }

    public void Cylinder(Vector3 center, float radius, float halfLength, Vector3 axis, int segments)
    {
        var a = Vector3.Normalize(axis);
        var u = Vector3.Normalize(Vector3.Cross(MathF.Abs(a.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY, a));
        var v = Vector3.Cross(a, u);
        for (var s = 0; s < segments; s++)
        {
            var a0 = s * MathF.Tau / segments;
            var a1 = (s + 1) * MathF.Tau / segments;
            var r0 = ((u * MathF.Cos(a0)) + (v * MathF.Sin(a0))) * radius;
            var r1 = ((u * MathF.Cos(a1)) + (v * MathF.Sin(a1))) * radius;
            var top = center + (a * halfLength);
            var bottom = center - (a * halfLength);
            Quad(bottom + r0, top + r0, top + r1, bottom + r1);
            Triangle(top, top + r1, top + r0);
            Triangle(bottom, bottom + r0, bottom + r1);
        }
    }

    public (float[] Vertices, uint[] Indices) Build() => ([.. _vertices], [.. _indices]);

    private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        Triangle(a, b, c);
        Triangle(a, c, d);
    }

    private void Triangle(Vector3 a, Vector3 b, Vector3 c)
    {
        var n = Vector3.Normalize(Vector3.Cross(b - a, c - a) + new Vector3(1e-9f));
        foreach (var p in new[] { a, b, c })
        {
            _indices.Add((uint)(_vertices.Count / 6));
            _vertices.AddRange([p.X, p.Y, p.Z, n.X, n.Y, n.Z]);
        }
    }
}
