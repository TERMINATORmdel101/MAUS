using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Maus.Bench.Gpu;

namespace Maus.Bench.Render;

/// <summary>Ce que contient une image de matière : couleur (sRVB), normales (vecteurs), ou valeurs (rugosité, métal…).</summary>
internal enum ImageRole
{
    Color,
    Normal,
    Data,
}

/// <summary>
/// Images des scènes : JPEG et PNG lus par WIC, réduites en niveaux de détail sur le processeur (moyenne de 2 × 2 pixels,
/// en lumière linéaire pour les couleurs, normales renormalisées), puis envoyées à la carte. Ciel HDR au format Radiance
/// (RGBE, G. Ward) lu sans bibliothèque.
/// </summary>
internal static class ImageAssets
{
    private static readonly float[] SrgbToLinear = [.. Enumerable.Range(0, 256).Select(i => Linear(i / 255f))];

    /// <summary>Charge une image et tous ses niveaux de détail ; <paramref name="skipLevels"/> saute les plus fins (mode léger).</summary>
    public static ITexture LoadTexture(IGpuDevice device, string path, ImageRole role, int skipLevels = 0)
    {
        using var stream = File.OpenRead(path);
        var (pixels, width, height) = ImageFile.LoadRgba(stream);
        var levels = MipChain(pixels, width, height, role);
        skipLevels = Math.Clamp(skipLevels, 0, levels.Count - 1);
        var format = role == ImageRole.Color ? PixelFormat.Rgba8UnormSrgb : PixelFormat.Rgba8Unorm;
        var first = levels[skipLevels];
        var texture = device.CreateTexture(TextureDesc.Image(first.Width, first.Height, format, Path.GetFileName(path), levels.Count - skipLevels));
        for (var i = skipLevels; i < levels.Count; i++)
        {
            device.UploadTexture(texture, i - skipLevels, 0, levels[i].Pixels, levels[i].Width * 4);
        }

        return texture;
    }

    /// <summary>Niveaux de détail d'une image RVBA 8 bits, du plus fin au plus grossier (1 × 1).</summary>
    internal static List<(byte[] Pixels, int Width, int Height)> MipChain(byte[] pixels, int width, int height, ImageRole role)
    {
        var levels = new List<(byte[] Pixels, int Width, int Height)> { (pixels, width, height) };
        while (width > 1 || height > 1)
        {
            var w = Math.Max(1, width / 2);
            var h = Math.Max(1, height / 2);
            var source = pixels;
            var (sw, sh) = (width, height);
            var next = new byte[w * h * 4];
            Parallel.For(0, h, y =>
            {
                var y0 = Math.Min(y * 2, sh - 1);
                var y1 = Math.Min((y * 2) + 1, sh - 1);
                for (var x = 0; x < w; x++)
                {
                    var x0 = Math.Min(x * 2, sw - 1);
                    var x1 = Math.Min((x * 2) + 1, sw - 1);
                    var a = ((y0 * sw) + x0) * 4;
                    var b = ((y0 * sw) + x1) * 4;
                    var c = ((y1 * sw) + x0) * 4;
                    var d = ((y1 * sw) + x1) * 4;
                    var o = ((y * w) + x) * 4;
                    if (role == ImageRole.Color)
                    {
                        for (var k = 0; k < 3; k++)
                        {
                            var linear = (SrgbToLinear[source[a + k]] + SrgbToLinear[source[b + k]] + SrgbToLinear[source[c + k]] + SrgbToLinear[source[d + k]]) * 0.25f;
                            next[o + k] = (byte)Math.Clamp((int)MathF.Round(Srgb(linear) * 255f), 0, 255);
                        }

                        next[o + 3] = (byte)((source[a + 3] + source[b + 3] + source[c + 3] + source[d + 3] + 2) / 4);
                    }
                    else if (role == ImageRole.Normal)
                    {
                        var n = Decode(source, a) + Decode(source, b) + Decode(source, c) + Decode(source, d);
                        n = n.LengthSquared() > 1e-8f ? Vector3.Normalize(n) : Vector3.UnitZ;
                        next[o] = (byte)Math.Clamp((int)MathF.Round(((n.X * 0.5f) + 0.5f) * 255f), 0, 255);
                        next[o + 1] = (byte)Math.Clamp((int)MathF.Round(((n.Y * 0.5f) + 0.5f) * 255f), 0, 255);
                        next[o + 2] = (byte)Math.Clamp((int)MathF.Round(((n.Z * 0.5f) + 0.5f) * 255f), 0, 255);
                        next[o + 3] = 255;
                    }
                    else
                    {
                        for (var k = 0; k < 4; k++)
                        {
                            next[o + k] = (byte)((source[a + k] + source[b + k] + source[c + k] + source[d + k] + 2) / 4);
                        }
                    }
                }
            });
            levels.Add((next, w, h));
            (pixels, width, height) = (next, w, h);
        }

        return levels;
    }

    /// <summary>Lit un ciel HDR Radiance (.hdr, RGBE avec ou sans compression par plages) en lumière linéaire RVB.</summary>
    public static (Vector3[] Pixels, int Width, int Height) LoadHdr(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var position = 0;
        string Line()
        {
            var start = position;
            while (position < bytes.Length && bytes[position] != (byte)'\n')
            {
                position++;
            }

            var text = Encoding.ASCII.GetString(bytes, start, position - start);
            position++;
            return text.TrimEnd('\r');
        }

        if (!Line().StartsWith("#?", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Fichier HDR Radiance attendu.");
        }

        while (Line().Length > 0)
        {
            // En-tête : FORMAT=32-bit_rle_rgbe, EXPOSURE… jusqu'à la ligne vide.
        }

        var size = Line().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (size.Length != 4 || size[0] != "-Y" || size[2] != "+X")
        {
            throw new InvalidDataException("Orientation HDR non prise en charge : " + string.Join(' ', size));
        }

        var height = int.Parse(size[1], System.Globalization.CultureInfo.InvariantCulture);
        var width = int.Parse(size[3], System.Globalization.CultureInfo.InvariantCulture);
        var result = new Vector3[width * height];
        var line = new byte[width * 4];
        for (var y = 0; y < height; y++)
        {
            if (width is >= 8 and < 32768 && bytes[position] == 2 && bytes[position + 1] == 2 && (bytes[position + 2] & 0x80) == 0)
            {
                // Plages compressées par composante (R, G, B, E l'une après l'autre).
                position += 4;
                for (var c = 0; c < 4; c++)
                {
                    var x = 0;
                    while (x < width)
                    {
                        int count = bytes[position++];
                        if (count > 128)
                        {
                            count -= 128;
                            var value = bytes[position++];
                            for (var k = 0; k < count && x < width; k++)
                            {
                                line[((x++) * 4) + c] = value;
                            }
                        }
                        else
                        {
                            for (var k = 0; k < count && x < width; k++)
                            {
                                line[((x++) * 4) + c] = bytes[position++];
                            }
                        }
                    }
                }
            }
            else
            {
                Array.Copy(bytes, position, line, 0, width * 4);
                position += width * 4;
            }

            for (var x = 0; x < width; x++)
            {
                var e = line[(x * 4) + 3];
                var scale = e == 0 ? 0f : MathF.ScaleB(1f, e - 136);
                result[(y * width) + x] = new Vector3(line[x * 4], line[(x * 4) + 1], line[(x * 4) + 2]) * scale;
            }
        }

        return (result, width, height);
    }

    /// <summary>Ciel HDR en texture RVBA 16 bits flottants avec tous ses niveaux de détail (moyenne 2 × 2, bord horizontal bouclé).</summary>
    public static ITexture HdrTexture(IGpuDevice device, Vector3[] pixels, int width, int height, string name)
    {
        var levels = new List<(Vector3[] Pixels, int Width, int Height)> { (pixels, width, height) };
        while (width > 1 || height > 1)
        {
            var w = Math.Max(1, width / 2);
            var h = Math.Max(1, height / 2);
            var next = new Vector3[w * h];
            var (src, sw, sh) = (pixels, width, height);
            Parallel.For(0, h, y =>
            {
                for (var x = 0; x < w; x++)
                {
                    var y0 = Math.Min(y * 2, sh - 1);
                    var y1 = Math.Min((y * 2) + 1, sh - 1);
                    var x0 = Math.Min(x * 2, sw - 1);
                    var x1 = ((x * 2) + 1) % sw;
                    next[(y * w) + x] = (src[(y0 * sw) + x0] + src[(y0 * sw) + x1] + src[(y1 * sw) + x0] + src[(y1 * sw) + x1]) * 0.25f;
                }
            });
            levels.Add((next, w, h));
            (pixels, width, height) = (next, w, h);
        }

        var texture = device.CreateTexture(TextureDesc.Image(levels[0].Width, levels[0].Height, PixelFormat.Rgba16Float, name, levels.Count));
        for (var i = 0; i < levels.Count; i++)
        {
            var (p, w, h) = levels[i];
            var half = new Half[w * h * 4];
            for (var k = 0; k < p.Length; k++)
            {
                half[k * 4] = (Half)MathF.Min(p[k].X, 60000f);
                half[(k * 4) + 1] = (Half)MathF.Min(p[k].Y, 60000f);
                half[(k * 4) + 2] = (Half)MathF.Min(p[k].Z, 60000f);
                half[(k * 4) + 3] = (Half)1f;
            }

            device.UploadTexture(texture, i, 0, MemoryMarshal.AsBytes(half.AsSpan()), w * 8);
        }

        return texture;
    }

    /// <summary>
    /// Éclairage diffus du ciel en harmoniques sphériques (9 coefficients RVB, déjà multipliés par les facteurs de la
    /// convolution en cosinus) : R. Ramamoorthi et P. Hanrahan, « An Efficient Representation for Irradiance Environment
    /// Maps », SIGGRAPH 2001. Ciel équirectangulaire, chaque pixel pondéré par son angle solide.
    /// </summary>
    public static Vector4[] IrradianceSh(Vector3[] pixels, int width, int height)
    {
        var sh = new Vector3[9];
        var total = 0.0;
        for (var y = 0; y < height; y++)
        {
            var theta = (y + 0.5) / height * Math.PI;
            var weight = (float)(Math.Sin(theta) * (2 * Math.PI / width) * (Math.PI / height));
            for (var x = 0; x < width; x++)
            {
                var phi = (x + 0.5) / width * 2 * Math.PI;
                var d = new Vector3((float)(Math.Sin(theta) * Math.Cos(phi)), (float)Math.Cos(theta), (float)(Math.Sin(theta) * Math.Sin(phi)));
                var c = pixels[(y * width) + x] * weight;
                sh[0] += c * 0.282095f;
                sh[1] += c * 0.488603f * d.Y;
                sh[2] += c * 0.488603f * d.Z;
                sh[3] += c * 0.488603f * d.X;
                sh[4] += c * 1.092548f * d.X * d.Y;
                sh[5] += c * 1.092548f * d.Y * d.Z;
                sh[6] += c * 0.315392f * ((3f * d.Z * d.Z) - 1f);
                sh[7] += c * 1.092548f * d.X * d.Z;
                sh[8] += c * 0.546274f * ((d.X * d.X) - (d.Y * d.Y));
                total += weight;
            }
        }

        // Convolution par le cosinus (A0 = π, A1 = 2π/3, A2 = π/4), puis division par π : la luminance diffuse s'obtient
        // ensuite par albédo × Σ coefficient × Y(n).
        float[] band = [1f, 2f / 3f, 2f / 3f, 2f / 3f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f];
        _ = total;
        return [.. sh.Select((c, i) => new Vector4(c * band[i], 0f))];
    }

    /// <summary>Direction de la plus forte lumière du ciel (fenêtres, lustres), d'après la bande d'ordre 1 des harmoniques.</summary>
    public static Vector3 DominantDirection(Vector4[] sh)
    {
        var luminance = new Vector3(0.2126f, 0.7152f, 0.0722f);
        var x = Vector3.Dot(new Vector3(sh[3].X, sh[3].Y, sh[3].Z), luminance);
        var y = Vector3.Dot(new Vector3(sh[1].X, sh[1].Y, sh[1].Z), luminance);
        var z = Vector3.Dot(new Vector3(sh[2].X, sh[2].Y, sh[2].Z), luminance);
        var d = new Vector3(x, y, z);
        return d.LengthSquared() > 1e-12f ? Vector3.Normalize(d) : Vector3.UnitY;
    }

    private static Vector3 Decode(byte[] pixels, int at) =>
        new((pixels[at] / 127.5f) - 1f, (pixels[at + 1] / 127.5f) - 1f, (pixels[at + 2] / 127.5f) - 1f);

    private static float Linear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static float Srgb(float l) => l <= 0.0031308f ? l * 12.92f : (1.055f * MathF.Pow(l, 1f / 2.4f)) - 0.055f;
}
