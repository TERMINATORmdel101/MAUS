using Maus.Bench.Gpu;
using MeasuringMode = Vortice.DCommon.MeasuringMode;
using Vortice.DirectWrite;

namespace Maus.Bench.Ui;

/// <summary>
/// Police de l'interface en « champ de distance » : chaque caractère est dessiné en grand par DirectWrite (police
/// Segoe UI de Windows, jamais redistribuée), puis converti en distances au bord (transformée exacte de Felzenszwalb
/// et Huttenlocher, « Distance Transforms of Sampled Functions », 2012). Le texte reste net à toutes les tailles et
/// peut recevoir un halo, sur Direct3D 11 comme 12 sans passer par Direct2D.
/// </summary>
internal sealed class FontAtlas : IDisposable
{
    /// <summary>Taille de l'em dans l'atlas (pixels).</summary>
    public const float EmPixels = 64f;

    /// <summary>Portée du champ de distance de part et d'autre du bord (pixels de l'atlas).</summary>
    public const float Spread = 8f;

    private const int Supersample = 2;
    private const int AtlasWidth = 2048;
    private readonly Dictionary<(char Character, bool Bold), Glyph> _glyphs = [];

    private FontAtlas(ITexture texture, float ascent, float descent)
    {
        Texture = texture;
        Ascent = ascent;
        Descent = descent;
    }

    public ITexture Texture { get; }

    /// <summary>Hauteur au-dessus de la ligne de base, en fraction de l'em.</summary>
    public float Ascent { get; }

    public float Descent { get; }

    public static FontAtlas Create(IGpuDevice device)
    {
        var characters = CharacterSet();
        using var factory = DWrite.DWriteCreateFactory<IDWriteFactory>();
        using var collection = factory.GetSystemFontCollection(false);
        var family = FindFamily(collection, "Segoe UI Variable Display") ?? FindFamily(collection, "Segoe UI") ?? collection.GetFontFamily(0);
        using (family)
        {
            using var regular = family.GetFirstMatchingFont(FontWeight.Normal, FontStretch.Normal, FontStyle.Normal);
            using var bold = family.GetFirstMatchingFont(FontWeight.SemiBold, FontStretch.Normal, FontStyle.Normal);
            using var regularFace = regular.CreateFontFace();
            using var boldFace = bold.CreateFontFace();
            var metrics = regularFace.Metrics;
            var unitsPerEm = (float)metrics.DesignUnitsPerEm;

            var rendered = new List<(char Character, bool Bold, Bitmap Sdf, float Left, float Top, float Advance)>();
            foreach (var (face, isBold) in new[] { (regularFace, false), (boldFace, true) })
            {
                var codePoints = characters.Select(c => (uint)c).ToArray();
                var indices = face.GetGlyphIndices(codePoints);
                var glyphMetrics = face.GetDesignGlyphMetrics(indices, false);
                for (var i = 0; i < characters.Length; i++)
                {
                    if (indices[i] == 0 && characters[i] != ' ')
                    {
                        continue;
                    }

                    var advance = glyphMetrics[i].AdvanceWidth / unitsPerEm;
                    var (sdf, left, top) = RenderGlyph(factory, face, indices[i]);
                    rendered.Add((characters[i], isBold, sdf, left, top, advance));
                }
            }

            // Rangement en lignes dans un atlas de 2048 pixels de large.
            var x = 1;
            var y = 1;
            var rowHeight = 0;
            var placements = new List<(int X, int Y)>();
            foreach (var glyph in rendered)
            {
                if (x + glyph.Sdf.Width + 1 > AtlasWidth)
                {
                    x = 1;
                    y += rowHeight + 1;
                    rowHeight = 0;
                }

                placements.Add((x, y));
                x += glyph.Sdf.Width + 1;
                rowHeight = Math.Max(rowHeight, glyph.Sdf.Height);
            }

            var atlasHeight = 1;
            while (atlasHeight < y + rowHeight + 1)
            {
                atlasHeight *= 2;
            }

            var pixels = new byte[AtlasWidth * atlasHeight];
            for (var i = 0; i < rendered.Count; i++)
            {
                var (px, py) = placements[i];
                var sdf = rendered[i].Sdf;
                for (var row = 0; row < sdf.Height; row++)
                {
                    sdf.Pixels.AsSpan(row * sdf.Width, sdf.Width).CopyTo(pixels.AsSpan(((py + row) * AtlasWidth) + px));
                }
            }

            var texture = device.CreateTexture(TextureDesc.Image(AtlasWidth, atlasHeight, PixelFormat.R8Unorm, "Police de l'interface"));
            device.UploadTexture(texture, 0, 0, pixels, AtlasWidth);
            var atlas = new FontAtlas(texture, metrics.Ascent / unitsPerEm, metrics.Descent / unitsPerEm);
            for (var i = 0; i < rendered.Count; i++)
            {
                var (px, py) = placements[i];
                var r = rendered[i];
                atlas._glyphs[(r.Character, r.Bold)] = new Glyph(
                    r.Advance,
                    r.Left,
                    r.Top,
                    r.Sdf.Width,
                    r.Sdf.Height,
                    px / (float)AtlasWidth,
                    py / (float)atlasHeight,
                    (px + r.Sdf.Width) / (float)AtlasWidth,
                    (py + r.Sdf.Height) / (float)atlasHeight);
            }

            return atlas;
        }
    }

    public bool TryGet(char character, bool bold, out Glyph glyph) =>
        _glyphs.TryGetValue((character, bold), out glyph) || _glyphs.TryGetValue(('?', bold), out glyph);

    public void Dispose() => Texture.Dispose();

    private static IDWriteFontFamily? FindFamily(IDWriteFontCollection collection, string name) =>
        collection.FindFamilyName(name, out var index) ? collection.GetFontFamily(index) : null;

    private static char[] CharacterSet()
    {
        var set = new List<char>();
        for (var c = (char)32; c < 127; c++)
        {
            set.Add(c);
        }

        for (var c = (char)160; c <= 255; c++)
        {
            set.Add(c);
        }

        set.AddRange("ŒœŸ‘’“”«»…–—•·€™×°±µ²³½¼¾←→↑↓▲▼●○✓".Distinct());
        return [.. set.Distinct()];
    }

    /// <summary>Dessine un caractère en grand (2 × 64 pixels par em), calcule les distances, réduit de moitié.</summary>
    private static (Bitmap Sdf, float Left, float Top) RenderGlyph(IDWriteFactory factory, IDWriteFontFace face, ushort index)
    {
        const int pad = (int)(Spread * Supersample) + 2;
        using var run = new GlyphRun
        {
            FontFace = face,
            FontEmSize = EmPixels * Supersample,
            Indices = [index],
            Advances = [0f],
            Offsets = [default],
        };
        using var analysis = factory.CreateGlyphRunAnalysis(run, 1f, null, RenderingMode.Aliased, MeasuringMode.Natural, 0f, 0f);
        var bounds = analysis.GetAlphaTextureBounds(TextureType.Aliased1x1);
        var w = Math.Max(0, bounds.Right - bounds.Left);
        var h = Math.Max(0, bounds.Bottom - bounds.Top);
        var coverage = new byte[Math.Max(1, w * h)];
        if (w > 0 && h > 0)
        {
            analysis.CreateAlphaTexture(TextureType.Aliased1x1, bounds, coverage, (uint)coverage.Length);
        }

        // Image élargie de la portée du champ, alignée sur 2 pour la réduction.
        var bigW = (w + (2 * pad) + 1) & ~1;
        var bigH = (h + (2 * pad) + 1) & ~1;
        var inside = new bool[bigW * bigH];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                inside[((y + pad) * bigW) + x + pad] = coverage[(y * w) + x] > 127;
            }
        }

        var outsideDistance = DistanceTransform.Squared(inside, bigW, bigH, featureIsInside: true);
        var insideDistance = DistanceTransform.Squared(inside, bigW, bigH, featureIsInside: false);
        var smallW = bigW / Supersample;
        var smallH = bigH / Supersample;
        var pixels = new byte[smallW * smallH];
        for (var y = 0; y < smallH; y++)
        {
            for (var x = 0; x < smallW; x++)
            {
                var sum = 0f;
                for (var sy = 0; sy < Supersample; sy++)
                {
                    for (var sx = 0; sx < Supersample; sx++)
                    {
                        var i = (((y * Supersample) + sy) * bigW) + (x * Supersample) + sx;
                        var signed = inside[i] ? -(MathF.Sqrt(insideDistance[i]) - 0.5f) : MathF.Sqrt(outsideDistance[i]) - 0.5f;
                        sum += signed;
                    }
                }

                var distance = sum / (Supersample * Supersample) / Supersample;
                var value = 0.5f - (distance / (2f * Spread));
                pixels[(y * smallW) + x] = (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
            }
        }

        var left = (bounds.Left - pad) / (float)Supersample;
        var top = (bounds.Top - pad) / (float)Supersample;
        return (new Bitmap(smallW, smallH, pixels), left, top);
    }

    /// <summary>Un caractère de l'atlas, mesures en pixels de l'atlas (em de 64 pixels) sauf l'avance (fraction d'em).</summary>
    internal readonly record struct Glyph(float Advance, float Left, float Top, int Width, int Height, float U0, float V0, float U1, float V1);

    private sealed record Bitmap(int Width, int Height, byte[] Pixels);
}

/// <summary>Transformée en distance euclidienne exacte, au carré (Felzenszwalb et Huttenlocher), en deux passes 1D.</summary>
internal static class DistanceTransform
{
    private const float Infinity = 1e20f;

    public static float[] Squared(bool[] inside, int width, int height, bool featureIsInside)
    {
        var grid = new float[width * height];
        for (var i = 0; i < grid.Length; i++)
        {
            grid[i] = inside[i] == featureIsInside ? 0f : Infinity;
        }

        var n = Math.Max(width, height);
        var f = new float[n];
        var d = new float[n];
        var v = new int[n];
        var z = new float[n + 1];
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                f[y] = grid[(y * width) + x];
            }

            OneDimension(f, height, d, v, z);
            for (var y = 0; y < height; y++)
            {
                grid[(y * width) + x] = d[y];
            }
        }

        for (var y = 0; y < height; y++)
        {
            Array.Copy(grid, y * width, f, 0, width);
            OneDimension(f, width, d, v, z);
            Array.Copy(d, 0, grid, y * width, width);
        }

        return grid;
    }

    private static void OneDimension(float[] f, int n, float[] d, int[] v, float[] z)
    {
        var k = 0;
        v[0] = 0;
        z[0] = -Infinity;
        z[1] = Infinity;
        for (var q = 1; q < n; q++)
        {
            var s = Intersection(f, v[k], q);
            while (s <= z[k])
            {
                k--;
                s = Intersection(f, v[k], q);
            }

            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = Infinity;
        }

        k = 0;
        for (var q = 0; q < n; q++)
        {
            while (z[k + 1] < q)
            {
                k++;
            }

            var delta = q - v[k];
            d[q] = (delta * delta) + f[v[k]];
        }
    }

    private static float Intersection(float[] f, int p, int q) => ((f[q] + (q * q)) - (f[p] + (p * p))) / ((2f * q) - (2f * p));
}
