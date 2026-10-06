using System.Numerics;
using System.Runtime.InteropServices;
using Maus.Bench.Gpu;
using Maus.Bench.Render;
using static Maus.Core.Localization.Texts;

namespace Maus.Bench.Scenes;

/// <summary>
/// Test « Bande passante » : collision de deux galaxies (6 millions d'étoiles, 2 millions de nuages de gaz). À chaque
/// image, la carte relit et réécrit 256 Mo de particules, puis superpose des milliards de pixels par addition dans
/// une image HDR : la mémoire vidéo et les unités de mélange (ROP) sont les premières à saturer.
/// </summary>
internal sealed class GalaxyScene : BenchScene
{
    private const int StandardStars = 4 * 1024 * 1024;
    private const int StandardGas = 3 * 512 * 1024;
    private const float Step = 0.02f;
    private const float SimulationSpeed = 0.7f;
    private const float Softening = 0.04f;

    private static readonly CameraPath Path = new(
        (0, new CameraPose(new Vector3(-2f, 14f, -24f), new Vector3(0f, 0f, 0f), 50f)),
        (20, new CameraPose(new Vector3(6f, 8f, -16f), new Vector3(0.5f, 0.3f, 0.5f), 48f, 4f)),
        (40, new CameraPose(new Vector3(10f, 3f, -8f), new Vector3(0.5f, 0.2f, 0.5f), 52f, 8f)),
        (60, new CameraPose(new Vector3(5f, 2.5f, 7f), new Vector3(0f, 0f, 0f), 55f, 2f)),
        (75, new CameraPose(new Vector3(-6f, 4f, 9f), new Vector3(0f, 0.2f, 0f), 50f, -4f)),
        (92, new CameraPose(new Vector3(-12f, 7f, -6f), new Vector3(0f, 0f, 0f), 46f, 0f)));

    private IBuffer? _particles;
    private IComputePipeline? _init;
    private IComputePipeline? _update;
    private IPipeline? _background;
    private IPipeline? _stars;
    private IPipeline? _gas;
    private Core _a;
    private Core _b;
    private float _simulated;
    private float _lastTarget;

    // Mode léger : quatre fois moins d'étoiles et trois fois moins de gaz, chacun plus lumineux (même éclat d'ensemble).
    private int _starCount = StandardStars;
    private int _gasCount = StandardGas;

    private int Total => _starCount + _gasCount;

    public override string Id => "galaxy";

    public override string Title => T("Collision galactique");

    public override string Subtitle => T("Bande passante : des millions de particules simulées et superposées à chaque image");

    public override GpuCapability Capability => GpuCapability.Bandwidth;

    public override void Load(SceneContext context)
    {
        var device = context.Device;
        var shaders = context.Shaders;
        if (context.Light)
        {
            (_starCount, _gasCount) = (1024 * 1024, 512 * 1024);
        }

        _particles = device.CreateBuffer(new BufferDesc((long)Total * 32, BufferUsage.Structured | BufferUsage.Storage, 32, "Particules"));
        _init = device.CreateComputePipeline(shaders.Get("galaxy.hlsl", "InitCS", "cs_5_0"));
        _update = device.CreateComputePipeline(shaders.Get("galaxy.hlsl", "UpdateCS", "cs_5_0"));
        var fullscreen = shaders.Get("common.hlsli", "FullscreenVS", "vs_5_0");
        _background = device.CreatePipeline(new GraphicsPipelineDesc(
            "Galaxie : fond", fullscreen, shaders.Get("galaxy.hlsl", "BackgroundPS", "ps_5_0"), [], BlendMode.Opaque, DepthMode.None, CullMode.None,
            [PixelFormat.Rgba16Float, PixelFormat.Rg16Float]));
        _stars = device.CreatePipeline(new GraphicsPipelineDesc(
            "Galaxie : étoiles", shaders.Get("galaxy.hlsl", "StarVS", "vs_5_0"), shaders.Get("galaxy.hlsl", "StarPS", "ps_5_0"), [], BlendMode.Additive, DepthMode.None, CullMode.None,
            [PixelFormat.Rgba16Float]));
        _gas = device.CreatePipeline(new GraphicsPipelineDesc(
            "Galaxie : gaz", shaders.Get("galaxy.hlsl", "GasVS", "vs_5_0"), shaders.Get("galaxy.hlsl", "GasPS", "ps_5_0"), [], BlendMode.Additive, DepthMode.None, CullMode.None,
            [PixelFormat.Rgba16Float]));

        ResetSimulation();
        var cmd = context.Commands;
        cmd.SetComputePipeline(_init);
        cmd.SetStorageBuffer(0, _particles);
        cmd.SetConstants(1, Constants());
        cmd.Dispatch((Total + 255) / 256, 1, 1);
        cmd.SetStorageBuffer(0, null);
    }

    public override SceneState Evaluate(double time) => new(Path.Evaluate(PathTime(time, Path)), new ColorGrade
    {
        Exposure = 1.0f,
        BloomIntensity = 1.1f,
        BloomThreshold = 0.8f,
        Vignette = 0.5f,
        Grain = 0.01f,
        Aberration = 0.004f,
        Saturation = 1.15f,
        Contrast = 1.06f,
        Sharpen = 0f,
    })
    {
        // Des millions de points qui bougent : l'anticrénelage temporel laisserait des traînées, il est coupé ici.
        Temporal = false,
        FarPlane = 400f,
    };

    public override void Render(SceneContext context)
    {
        var cmd = context.Commands;
        Simulate(cmd, (float)PathTime(context.Frame.Time, Path) * SimulationSpeed);

        cmd.SetRenderTargets([context.Post.HdrColor, context.Post.Velocity], null);
        cmd.SetPipeline(_background!);
        cmd.Draw(3);

        cmd.SetRenderTarget(context.Post.HdrColor);
        cmd.SetBuffer(0, _particles);
        cmd.SetConstants(1, Constants());
        cmd.SetPipeline(_gas!);
        cmd.Draw(6, _gasCount);
        cmd.Flush();
        cmd.SetPipeline(_stars!);
        cmd.Draw(6, _starCount);
        cmd.SetBuffer(0, null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _particles?.Dispose();
            _init?.Dispose();
            _update?.Dispose();
            _background?.Dispose();
            _stars?.Dispose();
            _gas?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Avance la simulation par pas fixes jusqu'au temps demandé : le même trajet quelle que soit la vitesse de la carte
    /// (8 pas au plus par image ; un saut dans le temps, pour les captures, est rattrapé par paquets).
    /// </summary>
    private void Simulate(ICommandList cmd, float target)
    {
        var steps = 0;

        // Saut dans le temps (captures) : rattrapé d'un coup. Simple retard d'une carte lente : 8 pas au plus par image.
        var jump = target - _lastTarget > 2f;
        _lastTarget = target;
        cmd.SetComputePipeline(_update!);
        cmd.SetStorageBuffer(0, _particles);
        while (_simulated + Step <= target && (jump || steps < 8))
        {
            AdvanceCores();
            cmd.SetConstants(1, Constants());
            cmd.Dispatch((Total + 255) / 256, 1, 1);
            _simulated += Step;
            steps++;
            if (jump && steps % 64 == 0)
            {
                cmd.Flush();
            }
        }

        cmd.SetStorageBuffer(0, null);
    }

    private void ResetSimulation()
    {
        _a = new Core(new Vector3(-4.2f, 0f, -1.4f), new Vector3(0.17f, 0.01f, 0.1f), 1.0f, 0.35f);
        _b = new Core(new Vector3(4.2f, 0.7f, 2.4f), new Vector3(-0.21f, -0.012f, -0.04f), 0.8f, -0.9f);
        _simulated = 0;
        _lastTarget = 0;
    }

    /// <summary>Les deux noyaux s'attirent l'un l'autre (calculé ici, sur le processeur, au même pas que les étoiles).</summary>
    private void AdvanceCores()
    {
        var d = _b.Position - _a.Position;
        var r2 = d.LengthSquared() + Softening;
        var force = d / (r2 * MathF.Sqrt(r2));
        _a.Velocity += force * _b.Mass * Step;
        _b.Velocity -= force * _a.Mass * Step;
        _a.Position += _a.Velocity * Step;
        _b.Position += _b.Velocity * Step;
    }

    private GalaxyConstants Constants() => new()
    {
        CoreA = new Vector4(_a.Position, _a.Mass),
        CoreB = new Vector4(_b.Position, _b.Mass),
        Simulation = new Vector4(Step, Softening, _starCount, Total),
        Look = new Vector4(1.4f, 0.17f, 0.0019f * StandardGas / _gasCount, 0.045f * StandardStars / _starCount),
        InitA = new Vector4(_a.Velocity, _a.Tilt),
        InitB = new Vector4(_b.Velocity, _b.Tilt),
    };

    private record struct Core(Vector3 Position, Vector3 Velocity, float Mass, float Tilt);

    [StructLayout(LayoutKind.Sequential)]
    private struct GalaxyConstants
    {
        public Vector4 CoreA;
        public Vector4 CoreB;
        public Vector4 Simulation;
        public Vector4 Look;
        public Vector4 InitA;
        public Vector4 InitB;
    }
}
