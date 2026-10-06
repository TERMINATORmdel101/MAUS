using System.Numerics;
using System.Runtime.InteropServices;
using Maus.Bench.Gpu;
using Maus.Bench.Render;
using static Maus.Core.Localization.Texts;

namespace Maus.Bench.Scenes;

/// <summary>
/// Test « Effets » (idée du porteur : l'explosion d'un char) : champ de bataille vallonné au coucher du soleil, semé de
/// dizaines de milliers de pierres. 24 chars avancent en suivant le relief et tirent, puis sont touchés l'un après
/// l'autre : l'obus perce un trou dans le blindage, les munitions explosent, la tourelle est arrachée et retombe sur le
/// sol, des milliers d'éclats rebondissent sur les pentes, des centaines de milliers de particules de feu, de fumée et
/// d'étincelles sont recalculées à chaque image : transparences superposées, éclairage par les explosions, ombres du
/// soleil et des collines.
/// </summary>
internal sealed class BattleScene : BenchScene
{
    private const int Tanks = 24;
    private const int MuzzlePerTank = 48;
    private const int ShellParticles = 10;

    private static readonly Vector3 Sun = Vector3.Normalize(new Vector3(-0.75f, 0.2f, 0.45f));

    // Tirs ennemis : de loin devant les chars (la plupart touchés de face), et sur le flanc pour un char sur trois.
    private static readonly Vector3 FrontEnemy = new(230f, 8f, -95f);
    private static readonly Vector3 FlankEnemy = new(30f, 6f, 200f);

    private static readonly CameraPath Path = new(
        (0, new CameraPose(new Vector3(-75f, 13f, -62f), new Vector3(-5f, 2f, 0f), 48f)),
        (15, new CameraPose(new Vector3(-50f, 6f, -40f), new Vector3(-24f, 3f, -10f), 44f, -2f)),
        (35, new CameraPose(new Vector3(-24f, 4.2f, -32f), new Vector3(-8f, 4f, -2f), 50f, 3f)),
        (55, new CameraPose(new Vector3(8f, 10f, -42f), new Vector3(4f, 5f, 0f), 55f)),
        (75, new CameraPose(new Vector3(42f, 5f, -26f), new Vector3(16f, 4f, 4f), 50f, -3f)),
        (95, new CameraPose(new Vector3(58f, 20f, 12f), new Vector3(2f, 6f, 0f), 50f)),
        (120, new CameraPose(new Vector3(18f, 48f, 72f), new Vector3(0f, 2f, 0f), 55f)));

    private readonly TankData[] _tanks = CreateTanks();
    private readonly TankPoseData[] _poses = new TankPoseData[Tanks];
    private readonly Vector4[] _lightPosition = new Vector4[8];
    private readonly Vector4[] _lightColor = new Vector4[8];
    private IBuffer? _tankBuffer;
    private IBuffer? _poseBuffer;
    private IBuffer? _stoneBuffer;
    private IBuffer? _motionBuffer;
    private IComputePipeline? _motion;
    private Mesh? _hull;
    private Mesh? _turret;
    private Mesh? _debris;
    private RockBuffers? _pebble;
    private RockBuffers? _boulder;
    private ITexture? _shadowMap;
    private IPipeline? _ground;
    private IPipeline? _solid;
    private IPipeline? _shadow;
    private IPipeline? _stones;
    private IPipeline? _stoneShadow;
    private IPipeline? _smoke;
    private IPipeline? _fire;

    // Par char : éclats, flammèches, bouffées de fumée, étincelles (environ trois fois moins en mode léger).
    private int _debrisPerTank = 320;
    private int _firePerTank = 6000;
    private int _smokePerTank = 1900;
    private int _sparksPerTank = 5000;
    private int _shadowSize = 4096;
    private int _boulders = 1_200;
    private int _pebbles = 70_000;

    public override string Id => "battle";

    public override string Title => T("Champ de bataille");

    public override string Subtitle => T("Effets : 24 chars explosent, des centaines de milliers de particules de feu, de fumée et d'étincelles");

    public override GpuCapability Capability => GpuCapability.Effects;

    public override bool HasOverlay => true;

    public override void Load(SceneContext context)
    {
        var device = context.Device;
        var shaders = context.Shaders;
        if (context.Light)
        {
            (_debrisPerTank, _firePerTank, _smokePerTank, _sparksPerTank, _shadowSize) = (120, 1500, 700, 1200, 2048);
            (_boulders, _pebbles) = (450, 22_000);
        }

        _tankBuffer = device.CreateBuffer(new BufferDesc(Tanks * TankData.Size, BufferUsage.Structured, TankData.Size, "Chars"), MemoryMarshal.AsBytes(_tanks.AsSpan()));
        _poseBuffer = device.CreateBuffer(new BufferDesc(Tanks * TankPoseData.Size, BufferUsage.Structured, TankPoseData.Size, "Chars : position"), MemoryMarshal.AsBytes(_poses.AsSpan()));
        var stones = CreateStones(_boulders, _pebbles, _tanks);
        _stoneBuffer = device.CreateBuffer(new BufferDesc(stones.Length * 48L, BufferUsage.Structured, 48, "Pierres"), MemoryMarshal.AsBytes(stones.AsSpan()));
        _motionBuffer = device.CreateBuffer(new BufferDesc(((Tanks * 4) + (Tanks * _debrisPerTank * 2)) * 16L, BufferUsage.Structured | BufferUsage.Storage, 16, "Tourelles et éclats : mouvement"));
        _motion = device.CreateComputePipeline(shaders.Get("battle.hlsl", "MotionCS", "cs_5_0"));
        _hull = Mesh.Create(device, "Char : caisse", TankMeshes.Hull());
        _turret = Mesh.Create(device, "Char : tourelle", TankMeshes.Turret());
        _debris = Mesh.Create(device, "Éclat", TankMeshes.Shard());
        _pebble = RockBuffers.Create(device, "Caillou", context.Light ? 0 : 1, 41);
        _boulder = RockBuffers.Create(device, "Rocher", context.Light ? 1 : 2, 93);
        _shadowMap = device.CreateTexture(TextureDesc.DepthTarget(_shadowSize, _shadowSize, "Ombres du soleil couchant"));

        VertexElement[] layout = [new("POSITION", 0, VertexFormat.Float3, 0), new("NORMAL", 0, VertexFormat.Float3, 12), new("TEXCOORD", 0, VertexFormat.Float1, 24)];
        VertexElement[] stoneLayout = [new("POSITION", 0, VertexFormat.Float3, 0), new("NORMAL", 0, VertexFormat.Float3, 12)];
        PixelFormat[] targets = [PixelFormat.Rgba16Float, PixelFormat.Rg16Float];
        _ground = device.CreatePipeline(new GraphicsPipelineDesc(
            "Bataille : sol", shaders.Get("common.hlsli", "FullscreenVS", "vs_5_0"), shaders.Get("battle.hlsl", "GroundPS", "ps_5_0", context.Defines), [],
            BlendMode.Opaque, DepthMode.TestWrite, CullMode.None, targets, PixelFormat.D32Float));
        _solid = device.CreatePipeline(new GraphicsPipelineDesc(
            "Bataille : chars et éclats", shaders.Get("battle.hlsl", "SolidVS", "vs_5_0"), shaders.Get("battle.hlsl", "SolidPS", "ps_5_0", context.Defines), layout,
            BlendMode.Opaque, DepthMode.TestWrite, CullMode.None, targets, PixelFormat.D32Float));
        _shadow = device.CreatePipeline(new GraphicsPipelineDesc(
            "Bataille : ombres", shaders.Get("battle.hlsl", "SolidShadowVS", "vs_5_0"), null, layout,
            BlendMode.Opaque, DepthMode.TestWrite, CullMode.None, [], PixelFormat.D32Float, DepthBias: 1500, SlopeScaledDepthBias: 2f));
        _stones = device.CreatePipeline(new GraphicsPipelineDesc(
            "Bataille : pierres", shaders.Get("battle.hlsl", "StoneVS", "vs_5_0"), shaders.Get("battle.hlsl", "StonePS", "ps_5_0", context.Defines), stoneLayout,
            BlendMode.Opaque, DepthMode.TestWrite, CullMode.Back, targets, PixelFormat.D32Float));
        _stoneShadow = device.CreatePipeline(new GraphicsPipelineDesc(
            "Bataille : ombres des pierres", shaders.Get("battle.hlsl", "StoneShadowVS", "vs_5_0"), null, stoneLayout,
            BlendMode.Opaque, DepthMode.TestWrite, CullMode.Back, [], PixelFormat.D32Float, DepthBias: 1500, SlopeScaledDepthBias: 2f));
        var particles = shaders.Get("battle.hlsl", "ParticleVS", "vs_5_0");
        _smoke = device.CreatePipeline(new GraphicsPipelineDesc(
            "Bataille : fumée", particles, shaders.Get("battle.hlsl", "SmokePS", "ps_5_0", context.Defines), [],
            BlendMode.Premultiplied, DepthMode.TestOnly, CullMode.None, [PixelFormat.Rgba16Float], PixelFormat.D32Float));
        _fire = device.CreatePipeline(new GraphicsPipelineDesc(
            "Bataille : feu", particles, shaders.Get("battle.hlsl", "FirePS", "ps_5_0"), [],
            BlendMode.Additive, DepthMode.TestOnly, CullMode.None, [PixelFormat.Rgba16Float], PixelFormat.D32Float));
    }

    public override SceneState Evaluate(double time)
    {
        // La caméra reste toujours au-dessus du relief, même au creux d'une colline.
        var camera = Path.Evaluate(PathTime(time, Path));
        var floor = BattleTerrain.Height(camera.Position.X, camera.Position.Z) + 2.2f;
        if (camera.Position.Y < floor)
        {
            camera = camera with { Position = camera.Position with { Y = floor } };
        }

        return new SceneState(camera, new ColorGrade
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
            FarPlane = 3000f,
        };
    }

    public override void Render(SceneContext context)
    {
        var cmd = context.Commands;
        var time = (float)PathTime(context.Frame.Time, Path);
        UpdatePoses(time);
        UpdateLights(time);
        cmd.UpdateBuffer<TankPoseData>(_poseBuffer!, _poses);
        var constants = Constants(time, 0, 0);

        // Tourelles arrachées et éclats : une trajectoire par objet, calculée une fois pour les deux passes.
        cmd.SetComputePipeline(_motion!);
        BindBuffers(cmd);
        cmd.SetStorageBuffer(0, _motionBuffer);
        cmd.SetConstants(1, constants);
        cmd.Dispatch(((Tanks * (1 + _debrisPerTank)) + 63) / 64, 1, 1);
        cmd.SetStorageBuffer(0, null);

        // Ombres du soleil couchant sur tout le champ de bataille : chars, éclats et pierres.
        cmd.SetRenderTarget(null, _shadowMap);
        cmd.ClearDepth(_shadowMap!, 1f);
        BindSolidBuffers(cmd);
        cmd.SetPipeline(_shadow!);
        DrawSolids(cmd, constants);
        cmd.SetPipeline(_stoneShadow!);
        DrawStones(cmd, constants);
        cmd.Flush();

        var post = context.Post;
        cmd.SetRenderTargets([post.HdrColor, post.Velocity], post.Depth);
        cmd.ClearDepth(post.Depth, 1f);
        cmd.SetPipeline(_ground!);
        BindBuffers(cmd);
        cmd.SetTexture(1, _shadowMap);
        cmd.SetConstants(1, constants);
        context.DrawFullscreenInBands(4);

        cmd.SetPipeline(_solid!);
        BindSolidBuffers(cmd);
        cmd.SetTexture(1, _shadowMap);
        DrawSolids(cmd, constants);
        cmd.SetPipeline(_stones!);
        DrawStones(cmd, constants);
        cmd.SetTexture(1, null);
        cmd.SetBuffer(4, null);
    }

    /// <summary>Feu, fumée, étincelles, tirs et obus, dessinés après l'anticrénelage (nets, sans traînées).</summary>
    public override void RenderOverlay(SceneContext context, ITexture target)
    {
        var cmd = context.Commands;
        var time = (float)PathTime(context.Frame.Time, Path);
        cmd.SetRenderTarget(target, context.Post.Depth);
        BindBuffers(cmd);
        cmd.SetPipeline(_smoke!);
        cmd.SetConstants(1, Constants(time, 0, 0));
        cmd.Draw(6, Tanks * _smokePerTank);
        cmd.Flush();
        cmd.SetPipeline(_fire!);
        cmd.SetConstants(1, Constants(time, 0, 1));
        cmd.Draw(6, Tanks * _firePerTank);
        cmd.SetConstants(1, Constants(time, 0, 2));
        cmd.Draw(6, Tanks * _sparksPerTank);
        cmd.SetConstants(1, Constants(time, 0, 3));
        cmd.Draw(6, Tanks * MuzzlePerTank);
        cmd.SetConstants(1, Constants(time, 0, 4));
        cmd.Draw(6, Tanks * ShellParticles);
        cmd.SetBuffer(0, null);
        cmd.SetBuffer(2, null);
        cmd.SetBuffer(3, null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tankBuffer?.Dispose();
            _poseBuffer?.Dispose();
            _stoneBuffer?.Dispose();
            _motionBuffer?.Dispose();
            _motion?.Dispose();
            _hull?.Dispose();
            _turret?.Dispose();
            _debris?.Dispose();
            _pebble?.Dispose();
            _boulder?.Dispose();
            _shadowMap?.Dispose();
            _ground?.Dispose();
            _solid?.Dispose();
            _shadow?.Dispose();
            _stones?.Dispose();
            _stoneShadow?.Dispose();
            _smoke?.Dispose();
            _fire?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void BindBuffers(ICommandList cmd)
    {
        cmd.SetBuffer(0, _tankBuffer);
        cmd.SetBuffer(2, _stoneBuffer);
        cmd.SetBuffer(3, _poseBuffer);
    }

    /// <summary>Tampons des objets solides, dont le mouvement des tourelles et des éclats calculé pour cette image.</summary>
    private void BindSolidBuffers(ICommandList cmd)
    {
        BindBuffers(cmd);
        cmd.SetBuffer(4, _motionBuffer);
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
        cmd.DrawIndexed(_debris.IndexCount, Tanks * _debrisPerTank);
    }

    /// <summary>Les rochers (maillage plus fin) puis les cailloux, rangés dans cet ordre dans le tampon des pierres.</summary>
    private void DrawStones(ICommandList cmd, BattleConstants constants)
    {
        cmd.SetConstants(1, constants with { Battle = constants.Battle with { Y = 0 } });
        _boulder!.Bind(cmd);
        cmd.DrawIndexed(_boulder.IndexCount, _boulders);
        cmd.SetConstants(1, constants with { Battle = constants.Battle with { Y = _boulders } });
        _pebble!.Bind(cmd);
        cmd.DrawIndexed(_pebble.IndexCount, _pebbles);
    }

    /// <summary>Position et orientation de chaque char sur le relief à cet instant, et angle de sa tourelle.</summary>
    private void UpdatePoses(float time)
    {
        for (var i = 0; i < Tanks; i++)
        {
            var pose = BattleTerrain.Frame(_tanks[i], time);
            _poses[i] = new TankPoseData(new Vector4(pose.Position, BattleTerrain.TurretYaw(_tanks[i], time)), new Vector4(pose.Forward, 0), new Vector4(pose.Up, 0), new Vector4(pose.Side, 0));
        }
    }

    /// <summary>
    /// Les huit sources les plus lumineuses éclairent la scène : impacts d'obus (éclair très bref), explosions (éclair,
    /// puis lueur du feu qui décroît) et tirs (flamme de bouche). Mêmes formules que battle.hlsl.
    /// </summary>
    private void UpdateLights(float time)
    {
        var sources = new List<(Vector3 Position, float Intensity)>();
        for (var i = 0; i < Tanks; i++)
        {
            var tank = _tanks[i];
            var sinceBlast = time - tank.Explosion.X;
            var sinceHit = time - tank.Explosion.W;
            if (sinceBlast >= 0)
            {
                sources.Add((new Vector3(tank.Ring.X, tank.Ring.Y + 1.5f, tank.Ring.Z), (60f * MathF.Exp(-sinceBlast * 5f)) + (22f * MathF.Exp(-sinceBlast / 7f)) + (5f * MathF.Exp(-sinceBlast / 40f))));
                continue;
            }

            if (sinceHit >= 0)
            {
                sources.Add((new Vector3(tank.Impact.X, tank.Impact.Y, tank.Impact.Z), (55f * MathF.Exp(-sinceHit * 18f)) + 6f));
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
                var pose = BattleTerrain.Frame(tank, start);
                var yaw = BattleTerrain.TurretYaw(tank, start);
                var pivot = new Vector3(TankMeshes.TurretPivotX, 0f, 0f);
                var tip = BattleTerrain.RotateY(new Vector3(5.9f, 2.06f, 0f) - pivot, yaw) + pivot;
                sources.Add((pose.ToWorld(tip), 35f * (1f - (shot / 0.12f))));
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
            Battle = new Vector4(time, offset, kind, 1f / _shadowSize),
            Counts = new Vector4(_debrisPerTank, _firePerTank, _smokePerTank, _sparksPerTank),

            // Moins de flammèches : chacune porte plus de lumière, la boule de feu garde son éclat ; la fumée s'épaissit un peu.
            Gains = new Vector4(6000f / _firePerTank, MathF.Sqrt(2200f / _smokePerTank), 1f, 0f),
        };
        _lightPosition.CopyTo(constants.LightPositions);
        _lightColor.CopyTo(constants.LightColors);
        return constants;
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
            var position = arrival - (new Vector3(MathF.Cos(heading), 0f, -MathF.Sin(heading)) * BattleTerrain.TankTravel);
            // Explosions en cascade : d'abord la colonne de gauche, toutes les 4,5 secondes environ ; l'obus frappe un peu avant.
            var order = (column * 4) + row;
            var blast = 5f + (order * 4.5f) + (random.NextSingle() * 1.5f);
            var hit = blast - (0.3f + (0.25f * random.NextSingle()));
            var tank = new TankData
            {
                PositionHeading = new Vector4(position, heading),
                Explosion = new Vector4(blast, random.NextSingle(), i, hit),
            };

            // Point d'impact : sur le glacis pour un tir de face, sur la jupe du côté du tireur pour un tir de flanc.
            var pose = BattleTerrain.Frame(tank, hit);
            var enemy = i % 3 == 2 ? FlankEnemy : FrontEnemy;
            var incoming = Vector3.Normalize(pose.ToWorld(new Vector3(0f, 1.3f, 0f)) - enemy);
            var local = new Vector3(Vector3.Dot(incoming, pose.Forward), Vector3.Dot(incoming, pose.Up), Vector3.Dot(incoming, pose.Side));
            Vector3 point;
            if (MathF.Abs(local.Z) > MathF.Abs(local.X))
            {
                var side = local.Z > 0 ? -1f : 1f;
                point = new Vector3(-1.4f + (2.6f * random.NextSingle()), 0.9f + (0.3f * random.NextSingle()), side * 2.266f);
            }
            else
            {
                var along = 0.25f + (0.4f * random.NextSingle());
                point = new Vector3(3.02f - (1.22f * along), 1.24f + (0.38f * along), -0.9f + (1.8f * random.NextSingle()));
            }

            tanks[i] = tank with
            {
                Hit = new Vector4(point, 0.17f + (0.07f * random.NextSingle())),
                Shell = new Vector4(Vector3.Normalize(local), 101 + (row * 10) + column + ((i % 2) * 100)),
                Ring = new Vector4(BattleTerrain.Frame(tank, blast).ToWorld(new Vector3(TankMeshes.TurretPivotX, TankMeshes.DeckY, 0f)), 0f),
                Impact = new Vector4(pose.ToWorld(point), 0f),
                ShellWorld = new Vector4(incoming, 0f),
            };
        }

        return tanks;
    }

    /// <summary>
    /// Pierres posées sur le relief : rochers épars, cailloux partout, en tas par endroits. Aucune sur le chemin des chars
    /// (ils roulent sur leurs traces) ni, pour les rochers, sur celui de la caméra. Tirage reproductible.
    /// </summary>
    private static StoneInstance[] CreateStones(int boulders, int pebbles, TankData[] tanks)
    {
        var random = new Random(20261007);
        var corridor = Enumerable.Range(0, 240).Select(k => Path.Evaluate(k * Path.Duration / 239).Position).ToArray();
        var clusters = Enumerable.Range(0, 60).Select(_ => new Vector2((random.NextSingle() - 0.5f) * 230f, (random.NextSingle() - 0.5f) * 190f)).ToArray();
        var stones = new StoneInstance[boulders + pebbles];
        for (var i = 0; i < stones.Length; i++)
        {
            var boulder = i < boulders;
            Vector2 at;
            if (random.NextSingle() < 0.35f)
            {
                var center = clusters[random.Next(clusters.Length)];
                var radius = 5f * MathF.Sqrt(-2f * MathF.Log(MathF.Max(random.NextSingle(), 1e-6f)));
                var angle = random.NextSingle() * MathF.Tau;
                at = center + (new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius);
            }
            else
            {
                // Plus dense là où se déroule la bataille, plus clairsemé vers l'horizon.
                var spread = random.NextSingle() < 0.7f ? 1f : 1.8f;
                at = new Vector2((random.NextSingle() - 0.5f) * 190f * spread, (random.NextSingle() - 0.5f) * 160f * spread);
            }

            var size = boulder ? 0.5f + (1.3f * MathF.Pow(random.NextSingle(), 2f)) : 0.06f + (0.3f * MathF.Pow(random.NextSingle(), 2f));
            // Les cailloux restent sous les chenilles ; seules les pierres plus grosses quittent le chemin des chars.
            if ((size > 0.12f && NearTankPath(at, tanks, 2.6f + size)) || (boulder && Array.Exists(corridor, c => Vector2.DistanceSquared(new Vector2(c.X, c.Z), at) < 36f)))
            {
                i--;
                continue;
            }

            var flatten = boulder ? 0.6f + (0.3f * random.NextSingle()) : 0.45f + (0.4f * random.NextSingle());
            var y = BattleTerrain.Height(at.X, at.Y) - (size * flatten * 0.35f);
            var axis = Vector3.Normalize(new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f) + new Vector3(1e-3f));
            var tone = 0.16f + (0.2f * random.NextSingle());
            var color = random.NextSingle() switch
            {
                < 0.4f => new Vector3(tone * 1.15f, tone, tone * 0.85f),
                < 0.75f => new Vector3(tone, tone * 0.97f, tone * 0.95f),
                _ => new Vector3(tone * 1.25f, tone * 0.95f, tone * 0.75f),
            };
            stones[i] = new StoneInstance(new Vector4(at.X, y, at.Y, size), new Vector4(axis, random.NextSingle() * MathF.Tau), new Vector4(color, flatten));
        }

        return stones;
    }

    /// <summary>Distance au chemin d'un char : depuis 30 m en arrière jusqu'à quelques mètres devant son arrêt.</summary>
    private static bool NearTankPath(Vector2 point, TankData[] tanks, float distance)
    {
        foreach (var tank in tanks)
        {
            var heading = tank.PositionHeading.W;
            var forward = new Vector2(MathF.Cos(heading), -MathF.Sin(heading));
            var start = new Vector2(tank.PositionHeading.X, tank.PositionHeading.Z);
            var along = Math.Clamp(Vector2.Dot(point - start, forward), -30f, BattleTerrain.TankTravel + 6f);
            if (Vector2.DistanceSquared(point, start + (forward * along)) < distance * distance)
            {
                return true;
            }
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct StoneInstance(Vector4 PositionScale, Vector4 AxisAngle, Vector4 Color);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct TankPoseData(Vector4 PositionYaw, Vector4 Forward, Vector4 Up, Vector4 Side)
    {
        public const int Size = 64;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BattleConstants
    {
        public Matrix4x4 ShadowViewProj;
        public Vector4 Battle;
        public Vector4 Counts;
        public Vector4 Gains;
        public LightArray LightPositions;
        public LightArray LightColors;
    }

    [System.Runtime.CompilerServices.InlineArray(8)]
    private struct LightArray
    {
        private Vector4 _element;
    }

    /// <summary>Maillage d'une pierre (sommets de 24 octets : position, normale).</summary>
    private sealed class RockBuffers(IBuffer vertices, IBuffer indices, int indexCount) : IDisposable
    {
        public int IndexCount { get; } = indexCount;

        public static RockBuffers Create(IGpuDevice device, string name, int subdivisions, int seed)
        {
            var (vertices, indices) = RockMesh.Create(subdivisions, seed);
            return new RockBuffers(
                device.CreateBuffer(new BufferDesc(vertices.Length * 4, BufferUsage.Vertex, 24, name), MemoryMarshal.AsBytes(vertices.AsSpan())),
                device.CreateBuffer(new BufferDesc(indices.Length * 4, BufferUsage.Index, 4, name + " : indices"), MemoryMarshal.AsBytes(indices.AsSpan())),
                indices.Length);
        }

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
}

/// <summary>Données d'un char, dans l'ordre de TankData de battle.hlsl (112 octets).</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct TankData
{
    public const int Size = 112;

    /// <summary>Départ (xz), cap (w, radians).</summary>
    public Vector4 PositionHeading { get; init; }

    /// <summary>Explosion des munitions (x), teinte du camouflage (y), graine (z), impact de l'obus (w).</summary>
    public Vector4 Explosion { get; init; }

    /// <summary>Point d'impact dans le repère du char (xyz), rayon du trou (w).</summary>
    public Vector4 Hit { get; init; }

    /// <summary>Direction de l'obus dans le repère du char (xyz), numéro peint sur les jupes (w).</summary>
    public Vector4 Shell { get; init; }

    /// <summary>Couronne de la tourelle dans le monde, char arrêté.</summary>
    public Vector4 Ring { get; init; }

    public Vector4 Impact { get; init; }

    public Vector4 ShellWorld { get; init; }
}

/// <summary>Position d'un char sur le relief : centre au sol et axes (avant, haut, côté).</summary>
internal readonly record struct TankFrame(Vector3 Position, Vector3 Forward, Vector3 Up, Vector3 Side)
{
    public Vector3 ToWorld(Vector3 local) => Position + (Forward * local.X) + (Up * local.Y) + (Side * local.Z);
}

/// <summary>
/// Relief du champ de bataille et pose des chars, mêmes formules que battle.hlsl (TerrainHeight, TankFrame, TurretYaw) :
/// le processeur place les chars, les pierres, les lumières et la caméra exactement là où la carte dessine le sol.
/// </summary>
internal static class BattleTerrain
{
    public const float TankSpeed = 1.6f;
    public const float TankTravel = 20f;

    public static float Height(float x, float z)
    {
        var h = (1.6f * MathF.Sin((x * 0.047f) + 0.7f) * MathF.Cos((z * 0.041f) - 0.4f))
            + (0.9f * MathF.Sin((x * 0.083f) - (z * 0.061f) + 2.1f))
            + (0.45f * MathF.Cos((x * 0.163f) + (z * 0.129f) + 0.3f))
            + (0.18f * MathF.Sin((x * 0.37f) - 1.2f) * MathF.Sin((z * 0.29f) + 0.8f));
        var far = SmoothStep(70f, 190f, MathF.Sqrt((x * x) + (z * z)));
        return h + (far * (7f + (7f * MathF.Sin((x * 0.019f) + 1.3f) * MathF.Cos((z * 0.016f) + 0.2f)) + (3.5f * MathF.Sin((x * 0.051f) + (z * 0.043f)))));
    }

    public static TankFrame Frame(TankData tank, float time)
    {
        var heading = tank.PositionHeading.W;
        var f = new Vector3(MathF.Cos(heading), 0f, -MathF.Sin(heading));
        var s = new Vector3(-f.Z, 0f, f.X);
        var travel = MathF.Min(TankSpeed * Math.Clamp(time, 0f, tank.Explosion.X), TankTravel);
        var cx = tank.PositionHeading.X + (f.X * travel);
        var cz = tank.PositionHeading.Z + (f.Z * travel);
        var hf = Height(cx + (f.X * 2.4f), cz + (f.Z * 2.4f));
        var hb = Height(cx - (f.X * 2.4f), cz - (f.Z * 2.4f));
        var hl = Height(cx + (s.X * 1.9f), cz + (s.Z * 1.9f));
        var hr = Height(cx - (s.X * 1.9f), cz - (s.Z * 1.9f));
        var forward = Vector3.Normalize((f * 4.8f) + new Vector3(0f, hf - hb, 0f));
        var side = Vector3.Normalize((s * 3.8f) + new Vector3(0f, hl - hr, 0f));
        var up = Vector3.Normalize(Vector3.Cross(side, forward));
        return new TankFrame(new Vector3(cx, (hf + hb + hl + hr) * 0.25f, cz), forward, up, Vector3.Cross(forward, up));
    }

    public static float TurretYaw(TankData tank, float time)
    {
        var t = MathF.Min(time, tank.Explosion.X);
        return (0.45f * MathF.Sin((0.23f * t) + (tank.Explosion.Z * 1.7f))) + (0.15f * MathF.Sin((0.61f * t) + tank.Explosion.Z));
    }

    public static Vector3 RotateY(Vector3 v, float angle)
    {
        var c = MathF.Cos(angle);
        var s = MathF.Sin(angle);
        return new Vector3((v.X * c) + (v.Z * s), v.Y, (-v.X * s) + (v.Z * c));
    }

    private static float SmoothStep(float from, float to, float x)
    {
        var t = Math.Clamp((x - from) / (to - from), 0f, 1f);
        return t * t * (3f - (2f * t));
    }
}
