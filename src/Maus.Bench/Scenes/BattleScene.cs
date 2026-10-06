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
    private const int MuzzlePerTank = 48;
    private const float TankSpeed = 1.6f;
    private const float TankTravel = 20f;
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

    public override string Subtitle => T("Effets : 24 chars explosent, des centaines de milliers de particules de feu, de fumée et d'étincelles");

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

        VertexElement[] layout = [new("POSITION", 0, VertexFormat.Float3, 0), new("NORMAL", 0, VertexFormat.Float3, 12), new("TEXCOORD", 0, VertexFormat.Float1, 24)];
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
        cmd.SetConstants(1, Constants(time, 0, 3));
        cmd.Draw(6, Tanks * MuzzlePerTank);
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

    /// <summary>
    /// Les huit sources les plus lumineuses éclairent la scène : explosions (éclair bref, puis lueur du feu qui décroît)
    /// et tirs (flamme de bouche). Mêmes formules que battle.hlsl (position du char qui avance, rotation de la tourelle).
    /// </summary>
    private void UpdateLights(float time)
    {
        var sources = new List<(Vector3 Position, float Intensity)>();
        for (var i = 0; i < Tanks; i++)
        {
            var tank = _tanks[i];
            var since = time - tank.Explosion.X;
            if (since >= 0)
            {
                var at = TankPosition(tank, tank.Explosion.X);
                sources.Add((at + new Vector3(0, 3f, 0), (60f * MathF.Exp(-since * 5f)) + (22f * MathF.Exp(-since / 7f)) + (5f * MathF.Exp(-since / 40f))));
                continue;
            }

            var period = 5.5f + (3f * Rand((uint)i, 31));
            var phase = 1f + (4f * Rand((uint)i, 32));
            var sinceFirst = time - phase;
            if (sinceFirst < 0)
            {
                continue;
            }

            var shot = sinceFirst - (MathF.Floor(sinceFirst / period) * period);
            if (shot < 0.12f)
            {
                var start = time - shot;
                var yaw = TurretYaw(tank, start);
                var tip = RotateY(RotateY(new Vector3(5.9f, 2.06f, 0f) - new Vector3(-0.25f, 0, 0), yaw) + new Vector3(-0.25f, 0, 0), tank.PositionHeading.W);
                sources.Add((tip + TankPosition(tank, start), 35f * (1f - (shot / 0.12f))));
            }
        }

        var lights = sources.OrderByDescending(x => x.Intensity).Take(8).ToArray();
        for (var i = 0; i < 8; i++)
        {
            if (i < lights.Length)
            {
                _lightPosition[i] = new Vector4(lights[i].Position, lights[i].Intensity);
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

    private static Vector3 TankPosition(TankData tank, float time)
    {
        var heading = tank.PositionHeading.W;
        var forward = new Vector3(MathF.Cos(heading), 0f, -MathF.Sin(heading));
        return new Vector3(tank.PositionHeading.X, tank.PositionHeading.Y, tank.PositionHeading.Z) + (forward * MathF.Min(TankSpeed * Math.Clamp(time, 0f, tank.Explosion.X), TankTravel));
    }

    private static float TurretYaw(TankData tank, float time)
    {
        var t = MathF.Min(time, tank.Explosion.X);
        return (0.45f * MathF.Sin((0.23f * t) + (tank.Explosion.Z * 1.7f))) + (0.15f * MathF.Sin((0.61f * t) + tank.Explosion.Z));
    }

    private static Vector3 RotateY(Vector3 v, float angle)
    {
        var c = MathF.Cos(angle);
        var s = MathF.Sin(angle);
        return new Vector3((v.X * c) + (v.Z * s), v.Y, (-v.X * s) + (v.Z * c));
    }

    /// <summary>Même hasard que Rand() de battle.hlsl (mélange de type PCG), pour retrouver les instants de tir.</summary>
    private static float Rand(uint seed, uint salt) => Hash((seed * 0x9E3779B9u) ^ Hash(salt + 0x632BE5ABu)) * (1f / 4294967296f);

    private static uint Hash(uint x)
    {
        x = (x * 747796405u) + 2891336453u;
        var w = ((x >> (int)((x >> 28) + 4u)) ^ x) * 277803737u;
        return (w >> 22) ^ w;
    }

    private static TankData[] CreateTanks()
    {
        var random = new Random(1944);
        var tanks = new TankData[Tanks];
        for (var i = 0; i < Tanks; i++)
        {
            var row = i / 6;
            var column = i % 6;
            var heading = 0.35f + ((random.NextSingle() - 0.5f) * 0.9f);
            var arrival = new Vector3(-42f + (column * 16.5f) + (random.NextSingle() * 5f), 0f, -27f + (row * 17f) + (random.NextSingle() * 6f));
            var position = arrival - (new Vector3(MathF.Cos(heading), 0f, -MathF.Sin(heading)) * TankTravel);
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
