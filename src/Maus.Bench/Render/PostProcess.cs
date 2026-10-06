using System.Numerics;
using System.Runtime.InteropServices;
using Maus.Bench.Gpu;

namespace Maus.Bench.Render;

/// <summary>Réglages d'image d'une scène (étalonnage « cinéma »).</summary>
internal sealed record ColorGrade
{
    public float Exposure { get; init; } = 1f;

    public float BloomIntensity { get; init; } = 0.6f;

    public float BloomThreshold { get; init; } = 1.2f;

    public float Vignette { get; init; } = 0.35f;

    public float Grain { get; init; } = 0.012f;

    public float Aberration { get; init; } = 0.006f;

    public Vector3 Lift { get; init; } = Vector3.Zero;

    public float Saturation { get; init; } = 1.05f;

    public Vector3 Gain { get; init; } = Vector3.One;

    public float Contrast { get; init; } = 1.05f;

    public float Sharpen { get; init; } = 0.25f;

    /// <summary>Fondu au noir (0 = image normale, 1 = noir).</summary>
    public float Fade { get; init; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct PostConstants
{
    public Vector4 SourceSize;
    public Vector4 OutputRect;
    public Vector4 Grade;
    public Vector4 Lift;
    public Vector4 Gain;
    public Vector4 Extra;
}

/// <summary>
/// Chaîne d'image commune à toutes les scènes : image HDR (16 bits flottants) et mouvement écrits par la scène,
/// anticrénelage temporel, halo sur six niveaux, puis image finale mise à l'échelle de l'écran (bandes noires si le
/// format diffère : la résolution de calcul est fixe pour que les scores restent comparables).
/// </summary>
internal sealed class PostProcess : IDisposable
{
    private const int BloomLevels = 6;
    private readonly IGpuDevice _device;
    private readonly ITexture[] _history = new ITexture[2];
    private readonly ITexture[] _bloom = new ITexture[BloomLevels];
    private readonly IPipeline _temporal;
    private readonly IPipeline _bloomFirst;
    private readonly IPipeline _bloomDown;
    private readonly IPipeline _bloomUp;
    private readonly IPipeline _final;
    private readonly ITexture _composite;
    private int _current;
    private bool _historyValid;

    public PostProcess(IGpuDevice device, ShaderLibrary shaders, RenderSize size)
    {
        _device = device;
        Size = size;
        HdrColor = device.CreateTexture(TextureDesc.Target(size.Width, size.Height, PixelFormat.Rgba16Float, "Image HDR"));
        Velocity = device.CreateTexture(TextureDesc.Target(size.Width, size.Height, PixelFormat.Rg16Float, "Mouvement"));
        Depth = device.CreateTexture(TextureDesc.DepthTarget(size.Width, size.Height, "Profondeur"));
        for (var i = 0; i < 2; i++)
        {
            _history[i] = device.CreateTexture(TextureDesc.Target(size.Width, size.Height, PixelFormat.Rgba16Float, "Historique " + i));
        }

        _composite = device.CreateTexture(TextureDesc.Target(size.Width, size.Height, PixelFormat.Rgba16Float, "Image et effets"));
        var width = size.Width;
        var height = size.Height;
        for (var i = 0; i < BloomLevels; i++)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
            _bloom[i] = device.CreateTexture(TextureDesc.Target(width, height, PixelFormat.Rgba16Float, "Halo " + i));
        }

        var vs = shaders.Get("common.hlsli", "FullscreenVS", "vs_5_0");
        _temporal = device.CreatePipeline(Fullscreen("Anticrénelage", vs, shaders.Get("post.hlsl", "TemporalPS", "ps_5_0"), PixelFormat.Rgba16Float, BlendMode.Opaque));
        _bloomFirst = device.CreatePipeline(Fullscreen("Halo : première réduction", vs, shaders.Get("post.hlsl", "BloomFirstDownPS", "ps_5_0"), PixelFormat.Rgba16Float, BlendMode.Opaque));
        _bloomDown = device.CreatePipeline(Fullscreen("Halo : réduction", vs, shaders.Get("post.hlsl", "BloomDownPS", "ps_5_0"), PixelFormat.Rgba16Float, BlendMode.Opaque));
        _bloomUp = device.CreatePipeline(Fullscreen("Halo : agrandissement", vs, shaders.Get("post.hlsl", "BloomUpPS", "ps_5_0"), PixelFormat.Rgba16Float, BlendMode.Additive));
        _final = device.CreatePipeline(Fullscreen("Image finale", vs, shaders.Get("post.hlsl", "FinalPS", "ps_5_0"), PixelFormat.Rgba8Unorm, BlendMode.Opaque));
    }

    public RenderSize Size { get; }

    /// <summary>Image de la scène en lumière réelle (avant la courbe filmique).</summary>
    public ITexture HdrColor { get; }

    /// <summary>Mouvement de chaque pixel depuis l'image précédente (coordonnées de texture).</summary>
    public ITexture Velocity { get; }

    /// <summary>Profondeur commune aux scènes rastérisées.</summary>
    public ITexture Depth { get; }

    /// <summary>Oublie l'image précédente (changement de plan, nouvelle scène).</summary>
    public void ResetHistory() => _historyValid = false;

    public static GraphicsPipelineDesc Fullscreen(string name, ShaderCode vs, ShaderCode ps, PixelFormat target, BlendMode blend) =>
        new(name, vs, ps, [], blend, DepthMode.None, CullMode.None, [target]);

    /// <summary>
    /// Anticrénelage, halo et image finale vers l'image affichée ; avec <paramref name="snapshot"/>, la même image finale
    /// est aussi dessinée en plus petit dans cette texture (vignette de la scène pour l'image du résultat).
    /// </summary>
    public void Run(ICommandList cmd, ColorGrade grade, bool temporal, Action<ITexture>? overlay = null, ITexture? snapshot = null)
    {
        var size = new Vector4(Size.Width, Size.Height, 1f / Size.Width, 1f / Size.Height);
        var previous = _history[_current];
        _current ^= 1;
        var resolved = _history[_current];

        cmd.SetRenderTarget(resolved);
        cmd.SetPipeline(_temporal);
        cmd.SetTexture(0, HdrColor);
        cmd.SetTexture(1, previous);
        cmd.SetTexture(2, Velocity);
        cmd.SetConstants(1, new PostConstants { SourceSize = size, Extra = new Vector4(_historyValid && temporal ? 0.1f : 1f, 0, 0, 0) });
        cmd.Draw(3);
        _historyValid = true;

        // Effets transparents (feu, fumée) : ajoutés à une copie, pour ne pas laisser de traînées dans l'historique.
        if (overlay is not null)
        {
            cmd.CopyTexture(_composite, resolved);
            overlay(_composite);
            resolved = _composite;
        }

        // Halo : réductions successives…
        var source = resolved;
        for (var i = 0; i < BloomLevels; i++)
        {
            var target = _bloom[i];
            cmd.SetRenderTarget(target);
            cmd.SetPipeline(i == 0 ? _bloomFirst : _bloomDown);
            cmd.SetTexture(0, source);
            cmd.SetTexture(1, null);
            cmd.SetTexture(2, null);
            cmd.SetConstants(1, new PostConstants
            {
                SourceSize = new Vector4(source.Desc.Width, source.Desc.Height, 1f / source.Desc.Width, 1f / source.Desc.Height),
                Extra = new Vector4(0, grade.BloomThreshold, 0, 0),
            });
            cmd.Draw(3);
            source = target;
        }

        // … puis agrandissements additionnés jusqu'au premier niveau.
        for (var i = BloomLevels - 1; i > 0; i--)
        {
            var from = _bloom[i];
            cmd.SetRenderTarget(_bloom[i - 1]);
            cmd.SetPipeline(_bloomUp);
            cmd.SetTexture(0, from);
            cmd.SetConstants(1, new PostConstants { SourceSize = new Vector4(from.Desc.Width, from.Desc.Height, 1f / from.Desc.Width, 1f / from.Desc.Height) });
            cmd.Draw(3);
        }

        // Image finale, centrée dans l'écran avec le format de l'image calculée.
        var back = _device.BackBuffer;
        var outW = (float)back.Desc.Width;
        var outH = (float)back.Desc.Height;
        var scale = MathF.Min(outW / Size.Width, outH / Size.Height);
        var rectW = Size.Width * scale;
        var rectH = Size.Height * scale;
        var rect = new Vector4((outW - rectW) * 0.5f, (outH - rectH) * 0.5f, rectW, rectH);

        var constants = new PostConstants
        {
            SourceSize = size,
            OutputRect = rect,
            Grade = new Vector4(grade.BloomIntensity, grade.Vignette, grade.Grain, grade.Aberration),
            Lift = new Vector4(grade.Lift, grade.Saturation),
            Gain = new Vector4(grade.Gain, grade.Contrast),
            Extra = new Vector4(0, grade.BloomThreshold, grade.Fade, grade.Sharpen),
        };
        cmd.SetRenderTarget(back);
        cmd.SetPipeline(_final);
        cmd.SetTexture(0, resolved);
        cmd.SetTexture(1, null);
        cmd.SetTexture(2, null);
        cmd.SetTexture(3, _bloom[0]);
        cmd.SetConstants(1, constants);
        cmd.Draw(3);
        if (snapshot is not null)
        {
            cmd.SetRenderTarget(snapshot);
            cmd.SetConstants(1, constants with
            {
                OutputRect = new Vector4(0, 0, snapshot.Desc.Width, snapshot.Desc.Height),
                Extra = constants.Extra with { Z = 0f },
            });
            cmd.Draw(3);
        }

        cmd.SetTexture(0, null);
        cmd.SetTexture(3, null);
    }

    public void Dispose()
    {
        HdrColor.Dispose();
        _composite.Dispose();
        Velocity.Dispose();
        Depth.Dispose();
        foreach (var texture in _history)
        {
            texture.Dispose();
        }

        foreach (var texture in _bloom)
        {
            texture.Dispose();
        }

        _temporal.Dispose();
        _bloomFirst.Dispose();
        _bloomDown.Dispose();
        _bloomUp.Dispose();
        _final.Dispose();
    }
}
