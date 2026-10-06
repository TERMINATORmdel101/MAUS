using Vortice.WIC;

namespace Maus.Bench.Render;

/// <summary>Lecture et écriture d'images par WIC (composant d'images de Windows) : PNG, JPEG.</summary>
internal static class ImageFile
{
    /// <summary>Enregistre une image RVBA 8 bits en PNG (captures de contrôle et bilan).</summary>
    public static void SavePng(string path, byte[] rgba, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var bgra = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i];
            bgra[i + 3] = 255;
        }

        using var factory = new IWICImagingFactory();
        using var stream = File.Create(path);
        using var encoder = factory.CreateEncoder(ContainerFormat.Png, stream);
        using var frame = encoder.CreateNewFrame(out var properties);
        frame.Initialize(properties);
        frame.SetSize((uint)width, (uint)height);
        var format = PixelFormat.Format32bppBGRA;
        frame.SetPixelFormat(ref format);
        frame.WritePixels((uint)height, (uint)(width * 4), (ReadOnlySpan<byte>)bgra);
        frame.Commit();
        encoder.Commit();
        properties?.Dispose();
    }

    /// <summary>Lit une image (PNG, JPEG…) en RVBA 8 bits non prémultiplié.</summary>
    public static (byte[] Pixels, int Width, int Height) LoadRgba(Stream source)
    {
        using var factory = new IWICImagingFactory();
        using var decoder = factory.CreateDecoderFromStream(source);
        using var frame = decoder.GetFrame(0);
        using var converter = factory.CreateFormatConverter();
        converter.Initialize(frame, PixelFormat.Format32bppRGBA);
        var size = converter.Size;
        var pixels = new byte[size.Width * size.Height * 4];
        converter.CopyPixels((uint)(size.Width * 4), pixels);
        return (pixels, size.Width, size.Height);
    }
}
