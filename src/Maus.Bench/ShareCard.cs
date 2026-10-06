using System.Numerics;
using Maus.Bench.Gpu;
using Maus.Bench.Render;
using Maus.Bench.Ui;
using Maus.Core.Workshop.Benchmark;
using static Maus.Core.Localization.Texts;

namespace Maus.Bench;

/// <summary>Une vignette pour l'image du résultat : image RVBA 8 bits d'une scène ou d'un test du processeur.</summary>
internal sealed record SharePicture(string Id, byte[] Pixels, int Width, int Height);

/// <summary>
/// Image du résultat à partager (1920 × 1080, PNG) : score combiné, scores de la carte graphique et du processeur, une
/// vignette de chaque scène prise pendant la mesure, matériel et capteurs. Seulement le matériel : aucun nom
/// d'utilisateur ni nom de PC.
/// </summary>
internal static class ShareCard
{
    public const int Width = 1920;
    public const int Height = 1080;
    private const int CellWidth = 480;
    private const int CellHeight = 270;
    private const int Columns = 3;
    private const int Rows = 2;

    /// <summary>Dessine l'image (dans une image en cours de la carte) et la rend en RVBA 8 bits.</summary>
    public static byte[] Render(IGpuDevice device, UiRenderer ui, ITexture? logo, BenchmarkReport report, IReadOnlyList<SharePicture> pictures, string resolutionName)
    {
        var cells = pictures.Take(Columns * Rows).ToList();
        ITexture? atlas = null;
        if (cells.Count > 0)
        {
            var atlasPixels = Atlas(cells, out var atlasWidth, out var atlasHeight);
            atlas = device.CreateTexture(TextureDesc.Image(atlasWidth, atlasHeight, PixelFormat.Rgba8Unorm, "Vignettes du résultat"));
            device.UploadTexture(atlas, 0, 0, atlasPixels, atlasWidth * 4);
        }

        using var target = device.CreateTexture(TextureDesc.Target(Width, Height, PixelFormat.Rgba8Unorm, "Image du résultat"));
        var cmd = device.Commands;
        cmd.Clear(target, new ColorF(0.014f, 0.018f, 0.03f, 1f));
        ui.UseTarget(target);
        try
        {
            DrawBackground(ui);
            ui.Flush(cmd);
            if (atlas is not null)
            {
                DrawThumbnails(ui, atlas, cells, report);
                ui.Flush(cmd);
            }

            if (logo is not null)
            {
                ui.Image(logo, 52, 30, 96, 96);
            }

            DrawTexts(ui, report, cells, resolutionName);
            ui.Flush(cmd);
        }
        finally
        {
            ui.UseTarget(null);
        }

        var pixels = device.ReadTexture(target);
        atlas?.Dispose();
        return pixels;
    }

    private static void DrawBackground(UiRenderer ui)
    {
        for (var band = 0; band < 12; band++)
        {
            ui.Rect(0, Height * band / 12f, Width, (Height / 12f) + 1, new Vector4(0.08f, 0.1f, 0.2f, 0.016f * band));
        }

        // Halos de couleur derrière les blocs (lilas pour le score, bleu pour les vignettes).
        for (var i = 0; i < 6; i++)
        {
            var grow = i * 40f;
            ui.Rect(40 - grow, 150 - grow, 620 + (2 * grow), 300 + (2 * grow), UiColors.Lilac(0.012f), 150 + grow);
            ui.Rect(760 - grow, 150 - grow, 1120 + (2 * grow), 580 + (2 * grow), UiColors.Blue(0.008f), 120 + grow);
        }

        ui.Rect(0, 0, Width, 6, UiColors.Blue(0.9f));
        ui.Rect(Width * 0.25f, 0, Width * 0.25f, 6, UiColors.Rose(0.9f));
        ui.Rect(Width * 0.5f, 0, Width * 0.25f, 6, UiColors.Mint(0.9f));
        ui.Rect(Width * 0.75f, 0, Width * 0.25f, 6, UiColors.Sand(0.9f));
    }

    private static (float X, float Y) CellPosition(int index) =>
        (764 + ((index % Columns) * 378), 166 + ((index / Columns) * 292));

    private static void DrawThumbnails(UiRenderer ui, ITexture atlas, List<SharePicture> cells, BenchmarkReport report)
    {
        const float w = 360f;
        const float h = 202.5f;
        for (var i = 0; i < cells.Count; i++)
        {
            var (x, y) = CellPosition(i);
            ui.Rect(x - 3, y - 3, w + 6, h + 6, UiColors.White(0.14f), 10);
            var column = i % Columns;
            var row = i / Columns;
            var uv0 = new Vector2(column / (float)Columns, row / (float)Rows);
            var uv1 = new Vector2((column + 1) / (float)Columns, (row + 1) / (float)Rows);
            ui.ImagePart(atlas, x, y, w, h, uv0, uv1);
            var test = report.Tests.FirstOrDefault(t => t.Id == cells[i].Id);
            if (test is not null)
            {
                // Bandeau sombre en bas de la vignette, sous les points.
                ui.Rect(x, y + h - 44, w, 44, new Vector4(0, 0, 0, 0.55f));
            }
        }
    }

    private static void DrawTexts(UiRenderer ui, BenchmarkReport report, List<SharePicture> cells, string resolutionName)
    {
        var culture = Culture;

        // En-tête.
        ui.Text("MAUS BENCHMARK", 168, 34, 46, UiColors.White(), bold: true, glow: 0.25f);
        var subtitle = string.Join("  ·  ", report.Date.ToLocalTime().ToString("g", culture), resolutionName, report.Api, "MAUS " + report.Version);
        ui.Text(subtitle, 170, 92, 21, UiColors.Grey());
        if (!report.Completed)
        {
            ui.Text(T("Passe arrêtée avant la fin : scores partiels"), Width - 56, 44, 20, UiColors.Rose(), bold: true, TextAlign.Right);
        }

        // Score principal.
        var (label, score) = report.OverallScore > 0
            ? (T("Score combiné"), report.OverallScore)
            : report.GpuScore > 0 ? (T("Score de la carte graphique"), report.GpuScore) : (T("Score du processeur"), report.CpuScore);
        ui.Text(label.ToUpper(culture), 60, 178, 22, UiColors.Lilac(), bold: true);
        ui.Text(Points(score), 52, 204, 150, UiColors.White(), bold: true, glow: 0.4f);
        ui.Text(T("10 000 points = Core i7-8700K et GeForce RTX 2080 Ti, à résolution égale"), 60, 384, 17, UiColors.Grey(0.9f));

        var y = 446f;
        y = DrawDevice(ui, T("Carte graphique"), report.GpuScore, report.Gpu, "gpu", report, UiColors.Blue(), y);
        y = DrawDevice(ui, T("Processeur"), report.CpuScore, report.Cpu + "  ·  " + T("{0} fils de calcul", report.Threads), "cpu", report, UiColors.Mint(), y);

        var (strongest, weakest) = BenchmarkScoring.Extremes(report.Tests);
        if (strongest is not null && weakest is not null)
        {
            ui.Text(T("Point fort : {0}", BenchmarkRunner.TestName(strongest.Id)), 60, y + 8, 20, UiColors.Mint(), bold: true);
            ui.Text(T("Point faible : {0}", BenchmarkRunner.TestName(weakest.Id)), 60, y + 40, 20, UiColors.Rose(), bold: true);
        }

        // Légendes des vignettes : nom du test, points, mesure.
        for (var i = 0; i < cells.Count; i++)
        {
            var (x, top) = CellPosition(i);
            var test = report.Tests.FirstOrDefault(t => t.Id == cells[i].Id);
            if (test is null)
            {
                continue;
            }

            ui.Text(Trim(ui, ShortName(test.Id), 18, 230), x + 12, top + 202.5f - 37, 18, UiColors.White(0.95f), bold: true);
            ui.Text(Points(test.Score), x + 348, top + 202.5f - 42, 26, UiColors.White(), bold: true, TextAlign.Right);
            var detail = test.Device == "gpu"
                ? T("{0} images/s", test.Value.ToString("0.0", culture))
                : test.Value.ToString("0.00", culture) + " " + test.Unit;
            ui.Text(Capability(test.Id) + "  ·  " + detail, x + 2, top + 202.5f + 12, 15, UiColors.Grey(0.9f));
        }

        // Tests sans vignette (processeur) : une ligne chacun sous les vignettes.
        var listY = 166 + (Rows * 292) + 8f;
        foreach (var test in report.Tests.Where(t => cells.TrueForAll(c => c.Id != t.Id)))
        {
            ui.Text(BenchmarkRunner.TestName(test.Id), 764, listY, 18, UiColors.White(0.9f));
            var value = test.Device == "gpu" ? T("{0} images/s", test.Value.ToString("0.0", culture)) : test.Value.ToString("0.00", culture) + " " + test.Unit;
            ui.Text(value, 1460, listY + 2, 15, UiColors.Grey(0.85f), align: TextAlign.Right);
            ui.Text(Points(test.Score), 1864, listY - 2, 22, UiColors.White(), bold: true, TextAlign.Right);
            listY += 34;
        }

        // Pied : d'où vient l'image, ce que valent les points.
        ui.Rect(0, Height - 64, Width, 64, new Vector4(0, 0, 0, 0.35f));
        ui.Text(T("Benchmark de MAUS, logiciel libre et gratuit : github.com/TERMINATORmdel101/MAUS"), 60, Height - 44, 18, UiColors.Grey(0.95f));
        ui.Text(T("Deux fois plus de points = deux fois plus rapide"), Width - 60, Height - 44, 18, UiColors.Grey(0.95f), align: TextAlign.Right);
    }

    private static float DrawDevice(UiRenderer ui, string title, double score, string name, string device, BenchmarkReport report, Vector4 accent, float y)
    {
        if (score <= 0)
        {
            return y;
        }

        ui.Rect(52, y, 620, 112, new Vector4(1, 1, 1, 0.05f), 16);
        ui.Text(title, 74, y + 14, 22, accent, bold: true);
        ui.Text(Points(score), 650, y + 6, 44, UiColors.White(), bold: true, TextAlign.Right);
        ui.Text(Trim(ui, name, 17, 560), 74, y + 58, 17, UiColors.Grey(0.95f));
        var tests = report.Tests.Where(t => t.Device == device).ToList();
        if (BenchmarkSensorText.Describe(BenchmarkSensorSummary.Merge(tests.Select(t => t.Sensors)), device) is { } sensors)
        {
            ui.Text(Trim(ui, sensors, 15, 560), 74, y + 84, 15, UiColors.Grey(0.8f));
        }

        return y + 128;
    }

    /// <summary>Coupe un texte trop long pour sa place (points de suspension).</summary>
    private static string Trim(UiRenderer ui, string text, float size, float width)
    {
        if (ui.Measure(text, size) <= width)
        {
            return text;
        }

        while (text.Length > 4 && ui.Measure(text + "…", size) > width)
        {
            text = text[..^1];
        }

        return text.TrimEnd() + "…";
    }

    private static string Points(double score) => score > 0 ? score.ToString("N0", Culture) : "—";

    /// <summary>Nom court d'un test sur sa vignette (le titre de la scène).</summary>
    private static string ShortName(string id) => id switch
    {
        "ring" => T("Anneau de la géante"),
        "battle" => T("Champ de bataille"),
        "galaxy" => T("Collision galactique"),
        "fractal" => T("Forge fractale"),
        _ => BenchmarkRunner.TestName(id),
    };

    /// <summary>Ce que le test sollicite, sous sa vignette.</summary>
    private static string Capability(string id) => id switch
    {
        "ring" => T("Géométrie"),
        "battle" => T("Effets"),
        "galaxy" => T("Bande passante"),
        "fractal" => T("Calcul"),
        _ => T("Processeur"),
    };

    /// <summary>Vignettes réduites et réunies dans une seule image (trois colonnes, deux lignes).</summary>
    private static byte[] Atlas(List<SharePicture> cells, out int width, out int height)
    {
        width = CellWidth * Columns;
        height = CellHeight * Rows;
        var atlas = new byte[width * height * 4];
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = Fit(cells[i]);
            var ox = (i % Columns) * CellWidth;
            var oy = (i / Columns) * CellHeight;
            for (var row = 0; row < CellHeight; row++)
            {
                cell.AsSpan(row * CellWidth * 4, CellWidth * 4).CopyTo(atlas.AsSpan((((oy + row) * width) + ox) * 4));
            }
        }

        return atlas;
    }

    /// <summary>
    /// Ramène une image à la taille d'une vignette (480 × 270) : recadrée au format 16/9 puis réduite en faisant la moyenne
    /// des pixels de chaque bloc (pas de scintillement des détails fins).
    /// </summary>
    internal static byte[] Fit(SharePicture picture)
    {
        var (w, h, pixels) = (picture.Width, picture.Height, picture.Pixels);
        var cropH = Math.Min(h, w * CellHeight / CellWidth);
        var cropW = Math.Min(w, cropH * CellWidth / CellHeight);
        var x0 = (w - cropW) / 2;
        var y0 = (h - cropH) / 2;
        var result = new byte[CellWidth * CellHeight * 4];
        for (var y = 0; y < CellHeight; y++)
        {
            var sy0 = y0 + (y * cropH / CellHeight);
            var sy1 = Math.Max(sy0 + 1, y0 + ((y + 1) * cropH / CellHeight));
            for (var x = 0; x < CellWidth; x++)
            {
                var sx0 = x0 + (x * cropW / CellWidth);
                var sx1 = Math.Max(sx0 + 1, x0 + ((x + 1) * cropW / CellWidth));
                int r = 0, g = 0, b = 0, n = 0;
                for (var sy = sy0; sy < sy1; sy++)
                {
                    for (var sx = sx0; sx < sx1; sx++)
                    {
                        var p = ((sy * w) + sx) * 4;
                        r += pixels[p];
                        g += pixels[p + 1];
                        b += pixels[p + 2];
                        n++;
                    }
                }

                var d = ((y * CellWidth) + x) * 4;
                result[d] = (byte)(r / n);
                result[d + 1] = (byte)(g / n);
                result[d + 2] = (byte)(b / n);
                result[d + 3] = 255;
            }
        }

        return result;
    }
}
