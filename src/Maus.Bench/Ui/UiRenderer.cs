using System.Numerics;
using System.Runtime.InteropServices;
using Maus.Bench.Gpu;
using Maus.Bench.Render;

namespace Maus.Bench.Ui;

/// <summary>
/// Interface par-dessus l'image : rectangles arrondis, textes et logo, regroupés en un seul dessin par image (sommets
/// temporaires). Coordonnées en pixels de l'image affichée, couleurs sRVB avec opacité.
/// </summary>
internal sealed class UiRenderer : IDisposable
{
    private readonly IGpuDevice _device;
    private readonly IPipeline _pipeline;
    private readonly List<UiVertex> _vertices = new(16384);
    private ITexture? _picture;

    public UiRenderer(IGpuDevice device, ShaderLibrary shaders, FontAtlas font)
    {
        _device = device;
        Font = font;
        _pipeline = device.CreatePipeline(new GraphicsPipelineDesc(
            "Interface",
            shaders.Get("ui.hlsl", "UiVS", "vs_5_0"),
            shaders.Get("ui.hlsl", "UiPS", "ps_5_0"),
            [
                new VertexElement("POSITION", 0, VertexFormat.Float2, 0),
                new VertexElement("TEXCOORD", 0, VertexFormat.Float2, 8),
                new VertexElement("COLOR", 0, VertexFormat.Float4, 16),
                new VertexElement("TEXCOORD", 1, VertexFormat.Float4, 32),
            ],
            BlendMode.Premultiplied,
            DepthMode.None,
            CullMode.None,
            [PixelFormat.Rgba8Unorm]));
    }

    public FontAtlas Font { get; }

    public float Width => _device.OutputWidth;

    public float Height => _device.OutputHeight;

    /// <summary>Échelle de l'interface : 1 pour un écran de 1080 lignes.</summary>
    public float Scale => Height / 1080f;

    public void Rect(float x, float y, float width, float height, Vector4 color, float radius = 0f)
    {
        if (width <= 0 || height <= 0 || color.W <= 0)
        {
            return;
        }

        var half = new Vector2(width * 0.5f, height * 0.5f);
        var shape = new Vector4(0, MathF.Min(radius, MathF.Min(half.X, half.Y)), half.X, half.Y);
        // Une marge d'un pixel autour du rectangle laisse au bord lissé la place de s'estomper.
        Quad(x - 1, y - 1, width + 2, height + 2, new Vector2(-half.X - 1, -half.Y - 1), new Vector2(half.X + 1, half.Y + 1), color, shape);
    }

    public void Image(ITexture picture, float x, float y, float width, float height, float opacity = 1f)
    {
        if (!ReferenceEquals(_picture, picture) && _picture is not null)
        {
            throw new InvalidOperationException("Une seule image par dessin d'interface.");
        }

        _picture = picture;
        Quad(x, y, width, height, Vector2.Zero, Vector2.One, new Vector4(1, 1, 1, opacity), new Vector4(2, 0, 0, 0));
    }

    /// <summary>Largeur d'un texte une fois affiché, en pixels.</summary>
    public float Measure(string text, float size, bool bold = false)
    {
        var width = 0f;
        foreach (var c in text)
        {
            if (Font.TryGet(c, bold, out var glyph))
            {
                width += glyph.Advance * size;
            }
        }

        return width;
    }

    /// <summary>Écrit un texte ; <paramref name="y"/> est le haut de la ligne. Rend la largeur écrite.</summary>
    public float Text(string text, float x, float y, float size, Vector4 color, bool bold = false, TextAlign align = TextAlign.Left, float glow = 0f)
    {
        var width = Measure(text, size, bold);
        x = align switch
        {
            TextAlign.Center => x - (width * 0.5f),
            TextAlign.Right => x - width,
            _ => x,
        };
        var baseline = y + (Font.Ascent * size);
        var scale = size / FontAtlas.EmPixels;

        // Le halo passe en premier, sous les lettres.
        if (glow > 0f)
        {
            Glyphs(text, x, baseline, scale, size, bold, new Vector4(color.X, color.Y, color.Z, color.W * glow), new Vector4(3, 0.5f, 0, 0));
        }

        // Seuil un peu plus bas pour les petits textes : traits plus pleins, meilleure lisibilité.
        var threshold = size < 20f ? 0.47f : 0.5f;
        Glyphs(text, x, baseline, scale, size, bold, color, new Vector4(1, threshold, 0, 0));
        return width;
    }

    /// <summary>Envoie tout ce qui a été préparé, par-dessus l'image affichée.</summary>
    public void Flush(ICommandList cmd)
    {
        if (_vertices.Count == 0)
        {
            return;
        }

        cmd.SetRenderTarget(_device.BackBuffer);
        cmd.SetPipeline(_pipeline);
        cmd.SetTexture(0, Font.Texture);
        cmd.SetTexture(1, _picture ?? Font.Texture);
        cmd.SetConstants(1, new Vector4(Width, Height, 1f / Width, 1f / Height));
        cmd.SetTransientVertices<UiVertex>(0, CollectionsMarshal.AsSpan(_vertices));
        cmd.Draw(_vertices.Count);
        cmd.SetTexture(0, null);
        cmd.SetTexture(1, null);
        _vertices.Clear();
        _picture = null;
    }

    public void Dispose() => _pipeline.Dispose();

    private void Glyphs(string text, float x, float baseline, float scale, float size, bool bold, Vector4 color, Vector4 shape)
    {
        foreach (var c in text)
        {
            if (!Font.TryGet(c, bold, out var glyph))
            {
                continue;
            }

            if (c != ' ')
            {
                Quad(
                    x + (glyph.Left * scale),
                    baseline + (glyph.Top * scale),
                    glyph.Width * scale,
                    glyph.Height * scale,
                    new Vector2(glyph.U0, glyph.V0),
                    new Vector2(glyph.U1, glyph.V1),
                    color,
                    shape);
            }

            x += glyph.Advance * size;
        }
    }

    private void Quad(float x, float y, float width, float height, Vector2 local0, Vector2 local1, Vector4 color, Vector4 shape)
    {
        var a = new UiVertex(new Vector2(x, y), local0, color, shape);
        var b = new UiVertex(new Vector2(x + width, y), new Vector2(local1.X, local0.Y), color, shape);
        var c = new UiVertex(new Vector2(x, y + height), new Vector2(local0.X, local1.Y), color, shape);
        var d = new UiVertex(new Vector2(x + width, y + height), local1, color, shape);
        _vertices.Add(a);
        _vertices.Add(b);
        _vertices.Add(c);
        _vertices.Add(c);
        _vertices.Add(b);
        _vertices.Add(d);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct UiVertex(Vector2 Position, Vector2 Local, Vector4 Color, Vector4 Shape);
}

internal enum TextAlign
{
    Left,
    Center,
    Right,
}

/// <summary>Couleurs de l'interface (sRVB), reprises de la palette de MAUS.</summary>
internal static class UiColors
{
    public static Vector4 White(float alpha = 1f) => new(1f, 1f, 1f, alpha);

    public static Vector4 Ink(float alpha = 1f) => new(0.035f, 0.04f, 0.06f, alpha);

    public static Vector4 Blue(float alpha = 1f) => new(0.47f, 0.69f, 1f, alpha);

    public static Vector4 Rose(float alpha = 1f) => new(1f, 0.55f, 0.66f, alpha);

    public static Vector4 Mint(float alpha = 1f) => new(0.46f, 0.92f, 0.74f, alpha);

    public static Vector4 Sand(float alpha = 1f) => new(1f, 0.82f, 0.5f, alpha);

    public static Vector4 Lilac(float alpha = 1f) => new(0.75f, 0.64f, 1f, alpha);

    public static Vector4 Grey(float alpha = 1f) => new(0.72f, 0.75f, 0.8f, alpha);
}
