using System.Numerics;
using Maus.Bench.Gpu;
using Maus.Bench.Render;
using static Maus.Core.Localization.Texts;

namespace Maus.Bench.Scenes;

/// <summary>
/// Test « Calcul » : vol dans une Mandelbox éclairée par un orbe de plasma. Tout est calculé par pixel (aucune
/// géométrie, aucune texture) : le score dépend de la puissance de calcul en virgule flottante de la carte.
/// </summary>
internal sealed class FractalScene : BenchScene
{
    // Trajet calculé hors ligne pour rester à distance de la surface (distance à la fractale toujours supérieure à 0,2).
    private static readonly CameraPath Path = new(
        (0, new CameraPose(new Vector3(3.600f, 3.200f, -3.900f), new Vector3(0.4f, 0.3f, -0.3f), 48f)),
        (12, new CameraPose(new Vector3(3.078f, 2.786f, -3.054f), new Vector3(0.9f, 0.8f, -0.8f), 50f, 2f)),
        (24, new CameraPose(new Vector3(2.640f, 2.389f, -2.415f), new Vector3(1.3f, 1.2f, -1.15f), 56f, 4f)),
        (36, new CameraPose(new Vector3(2.351f, 2.005f, -2.089f), new Vector3(1.45f, 1.35f, -1.35f), 64f, 3f)),
        (48, new CameraPose(new Vector3(2.222f, 1.591f, -2.049f), new Vector3(1.5f, 1.0f, -1.45f), 70f, -2f)),
        (60, new CameraPose(new Vector3(2.258f, 1.092f, -2.201f), new Vector3(1.4f, 0.5f, -1.5f), 66f, -4f)),
        (72, new CameraPose(new Vector3(2.512f, 0.497f, -2.499f), new Vector3(1.1f, 0.1f, -1.2f), 58f, -2f)),
        (92, new CameraPose(new Vector3(3.400f, -0.600f, -3.300f), new Vector3(0.5f, 0.2f, -0.4f), 50f)));

    private IPipeline? _pipeline;

    public override string Id => "fractal";

    public override string Title => T("Forge fractale");

    public override string Subtitle => T("Calcul pur : la fractale est recalculée des centaines de fois par pixel");

    public override GpuCapability Capability => GpuCapability.Compute;

    public override void Load(SceneContext context)
    {
        _pipeline = context.Device.CreatePipeline(new GraphicsPipelineDesc(
            "Fractale",
            context.Shaders.Get("common.hlsli", "FullscreenVS", "vs_5_0"),
            context.Shaders.Get("fractal.hlsl", "FractalPS", "ps_5_0"),
            [],
            BlendMode.Opaque,
            DepthMode.None,
            CullMode.None,
            [PixelFormat.Rgba16Float, PixelFormat.Rg16Float]));
    }

    public override SceneState Evaluate(double time)
    {
        var t = (float)time;
        // L'orbe dérive lentement devant la caméra, à travers la structure.
        var camera = Path.Evaluate(PathTime(time, Path));
        // L'orbe flotte entre la caméra et ce qu'elle regarde : sa lumière balaie la surface pendant le vol.
        var toTarget = camera.Target - camera.Position;
        var orb = camera.Position + (toTarget * 0.42f) + (new Vector3(MathF.Sin(t * 0.37f) * 0.25f, MathF.Sin(t * 0.23f) * 0.18f, MathF.Cos(t * 0.31f) * 0.2f) * MathF.Min(1f, toTarget.Length() * 0.5f));
        return new SceneState(camera, new ColorGrade
        {
            Exposure = 1.25f,
            BloomIntensity = 0.9f,
            BloomThreshold = 1.0f,
            Vignette = 0.45f,
            Grain = 0.015f,
            Aberration = 0.008f,
            Saturation = 1.1f,
            Gain = new Vector3(1.04f, 1f, 0.96f),
            Contrast = 1.08f,
        })
        {
            // Soleil derrière la fractale : contre-jour, rayons de lumière vers la caméra à travers les ouvertures.
            SunDirection = Vector3.Normalize(Vector3.Transform(new Vector3(-0.55f, 0.3f, 0.62f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.35f * MathF.Sin(t * 0.045f)))),
            SunColor = new Vector3(1f, 0.78f, 0.55f) * 1.4f,
            Params0 = new Vector4(-1.77f, 0.25f, 1f, 14f),
            Params1 = new Vector4(orb, 1.6f),
            Params2 = new Vector4(0.06f, 0.35f, 260f, 56f),
            Params3 = new Vector4(t * 0.01f, 2.4f, 1f, 0.06f),
        };
    }

    public override void Render(SceneContext context)
    {
        var cmd = context.Commands;
        cmd.SetRenderTargets([context.Post.HdrColor, context.Post.Velocity], null);
        cmd.SetPipeline(_pipeline!);
        context.DrawFullscreenInBands(8);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pipeline?.Dispose();
        }

        base.Dispose(disposing);
    }
}
