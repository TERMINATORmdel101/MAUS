using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Maus.Bench.Cpu;
using Maus.Bench.Gpu;
using Maus.Bench.Platform;
using Maus.Bench.Render;
using Maus.Bench.Scenes;
using Maus.Bench.Ui;
using Maus.Core.Workshop.Benchmark;
using static Maus.Core.Localization.Texts;

namespace Maus.Bench;

/// <summary>
/// Déroulé complet en plein écran : présentation, scènes de la carte graphique (chargement, 3 s de mise en route non
/// mesurées, mesure), tests du processeur, puis bilan. Échap arrête à tout moment (bilan partiel, non enregistré
/// comme une passe complète).
/// </summary>
internal sealed class BenchmarkRunner : IDisposable
{
    private const double WarmupSeconds = 3;
    private readonly BenchOptions _options;
    private readonly BenchWindow _window;
    private readonly IGpuDevice _device;
    private readonly ShaderLibrary _shaders = new();
    private readonly PostProcess _post;
    private readonly FontAtlas _font;
    private readonly UiRenderer _ui;
    private readonly ITexture? _logo;
    private readonly FrameBuilder _builder = new();
    private readonly List<BenchmarkTestResult> _results = [];
    private readonly string _cpuName = CpuName();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly BenchmarkSensorSampler _sensors;
    private double _fps;
    private readonly Queue<float> _recentFrames = new();

    public BenchmarkRunner(BenchOptions options)
    {
        _options = options;
        _window = new BenchWindow("MAUS Benchmark");
        _device = BenchmarkProgram.CreateDevice(options.Api, _window.Handle, _window.Width, _window.Height);
        _post = new PostProcess(_device, _shaders, options.RenderSize);
        _font = FontAtlas.Create(_device);
        _ui = new UiRenderer(_device, _shaders, _font);
        _logo = LoadLogo(_device);
        _sensors = BenchmarkSensorSampler.ForThisPc(_device.AdapterName);
        _window.HideCursor();
    }

    public BenchmarkReport Run()
    {
        IReadOnlyList<BenchScene> scenes = _options.Only == "cpu" ? [] : BenchmarkProgram.CreateScenes(_options.Scenes);
        IReadOnlyList<CpuBenchTest> cpuTests = _options.Only == "gpu" ? [] : BenchmarkProgram.CreateCpuTests(_options.Scenes);
        var total = scenes.Count + cpuTests.Count;
        string? error = null;
        var completed = false;
        try
        {
            if (!Intro())
            {
                return Report(false, null);
            }

            var index = 0;
            foreach (var scene in scenes)
            {
                using (scene)
                {
                    if (!RunScene(scene, ++index, total))
                    {
                        return Finish(false, null);
                    }
                }
            }

            foreach (var test in cpuTests)
            {
                if (!RunCpuTest(test, ++index, total))
                {
                    return Finish(false, null);
                }
            }

            completed = true;
        }
        catch (DeviceLostException ex)
        {
            error = ex.Message;
        }

        return Finish(completed, error);
    }

    public void Dispose()
    {
        _sensors.Dispose();
        _logo?.Dispose();
        _ui.Dispose();
        _font.Dispose();
        _post.Dispose();
        _device.Dispose();
        _window.Dispose();
    }

    private BenchmarkReport Finish(bool completed, string? error)
    {
        var report = Report(completed, error);
        if (error is null)
        {
            ShowResults(report);
        }

        return report;
    }

    private BenchmarkReport Report(bool completed, string? error)
    {
        var gpu = BenchmarkScoring.Combine(_results.Where(r => r.Device == "gpu").Select(r => r.Score));
        var cpu = BenchmarkScoring.Combine(_results.Where(r => r.Device == "cpu").Select(r => r.Score));
        return new BenchmarkReport
        {
            Date = DateTimeOffset.Now,
            Api = _device.Api == GpuApi.Direct3D11 ? "Direct3D 11" : "Direct3D 12",
            Gpu = _device.AdapterName,
            Cpu = _cpuName,
            Threads = Environment.ProcessorCount,
            RenderResolution = string.Create(CultureInfo.InvariantCulture, $"{_options.RenderSize.Width}×{_options.RenderSize.Height}"),
            Tests = [.. _results],
            GpuScore = Math.Round(gpu),
            CpuScore = Math.Round(cpu),
            OverallScore = Math.Round(BenchmarkScoring.Overall(gpu, cpu)),
            Completed = completed,
            Error = error,
        };
    }

    private bool Intro()
    {
        var start = _clock.Elapsed.TotalSeconds;
        while (_clock.Elapsed.TotalSeconds - start < 5 * Math.Min(1, _options.DurationScale * 4))
        {
            if (!_window.Pump())
            {
                return false;
            }

            var t = _clock.Elapsed.TotalSeconds - start;
            _device.BeginFrame();
            var cmd = _device.Commands;
            cmd.Clear(_device.BackBuffer, new ColorF(0.012f, 0.014f, 0.022f, 1f));
            var s = _ui.Scale;
            var fade = (float)Math.Clamp(t / 0.8, 0, 1);
            var cx = _ui.Width / 2f;
            if (_logo is not null)
            {
                var size = 220 * s;
                _ui.Image(_logo, cx - (size / 2), (_ui.Height * 0.22f) - (12 * s), size, size, fade);
            }

            _ui.Text("MAUS BENCHMARK", cx, _ui.Height * 0.47f, 74 * s, UiColors.White(fade), bold: true, TextAlign.Center, glow: 0.35f);
            _ui.Text(T("Carte graphique et processeur poussés à fond, environ dix minutes"), cx, _ui.Height * 0.47f + (96 * s), 26 * s, UiColors.Grey(fade), align: TextAlign.Center);
            var api = _device.Api == GpuApi.Direct3D11 ? "Direct3D 11" : "Direct3D 12";
            _ui.Text($"{_device.AdapterName}  ·  {api}  ·  {_cpuName}", cx, _ui.Height * 0.47f + (150 * s), 20 * s, UiColors.Blue(fade), align: TextAlign.Center);
            _ui.Text(T("Ne touchez à rien pendant la mesure. Échap pour arrêter."), cx, _ui.Height - (90 * s), 19 * s, UiColors.Grey(0.8f * fade), align: TextAlign.Center);
            _ui.Flush(cmd);
            _device.Present();
        }

        return true;
    }

    private bool RunScene(BenchScene scene, int index, int total)
    {
        // Chargement (non mesuré), à l'intérieur d'une image : une scène peut y préparer ses données par la carte.
        var context = new SceneContext(_device, _shaders, _post) { Light = _options.Light };
        DrawMessage(T("Chargement : {0}", scene.Title), index, total, () => scene.Load(context));
        _device.WaitIdle();
        _post.ResetHistory();
        _builder.Reset();

        var duration = scene.Duration * _options.DurationScale;
        var start = _clock.Elapsed.TotalSeconds;
        var last = start;
        var frameTimes = new List<double>(8192);
        var measuredFrames = 0;
        var measuredStart = 0.0;
        while (true)
        {
            if (!_window.Pump())
            {
                return false;
            }

            var now = _clock.Elapsed.TotalSeconds;
            var elapsed = now - start;
            if (elapsed >= WarmupSeconds + duration)
            {
                break;
            }

            var frameSeconds = now - last;
            last = now;
            _fps = (_fps * 0.9) + (0.1 / Math.Max(frameSeconds, 1e-4));

            // Le temps de la scène suit l'horloge : une carte rapide montre plus d'images du même trajet.
            var sceneTime = Math.Max(0, elapsed - WarmupSeconds) / Math.Max(_options.DurationScale, 1e-3);
            if (elapsed >= WarmupSeconds)
            {
                if (measuredFrames == 0)
                {
                    measuredStart = now;
                    _sensors.BeginSegment();
                }
                else
                {
                    frameTimes.Add(frameSeconds * 1000);
                }

                measuredFrames++;
            }

            // Générique : la scène sort du noir pendant la mise en route et y retourne à la fin.
            var fadeIn = 1.0 - Math.Clamp(elapsed / 0.9, 0, 1);
            var fadeOut = Math.Clamp((elapsed - (WarmupSeconds + duration - 0.9)) / 0.9, 0, 1);
            BenchmarkProgram.RenderFrame(_device, context, _post, _builder, scene, sceneTime, (float)frameSeconds, fade: (float)Math.Max(fadeIn, fadeOut));
            var progress = Math.Clamp((elapsed - WarmupSeconds) / duration, 0, 1);
            RememberFrame(frameSeconds);
            if (elapsed < WarmupSeconds)
            {
                DrawTitleCard(scene.Title, scene.Subtitle, index, total, elapsed);
            }
            else
            {
                DrawHud(scene.Title, scene.Subtitle, index, total, progress, warmup: false, gpu: true);
            }
            _ui.Flush(_device.Commands);
            Screenshot(scene.Id, progress >= 0.5);
            _device.Present();
        }

        _device.WaitIdle();
        var seconds = _clock.Elapsed.TotalSeconds - measuredStart;
        var fps = measuredFrames > 1 ? (measuredFrames - 1) / seconds : 0;
        _results.Add(new BenchmarkTestResult(
            scene.Id,
            "gpu",
            scene.Capability.ToString().ToLowerInvariant(),
            Math.Round(fps, 2),
            T("images par seconde"),
            Math.Round(BenchmarkScoring.TestScore(fps, BenchReference.For(scene.Id, _options))),
            BenchmarkScoring.Low1Fps(frameTimes) is { } low ? Math.Round(low, 2) : null)
        {
            Sensors = _sensors.EndSegment("gpu"),
        });
        return true;
    }

    private bool RunCpuTest(CpuBenchTest test, int index, int total)
    {
        var picture = _device.CreateTexture(TextureDesc.Image(test.Width, test.Height, PixelFormat.Rgba8Unorm, test.Title));
        using var cancel = new CancellationTokenSource();
        var worker = Task.Factory.StartNew(() => test.Run(cancel.Token), cancel.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var duration = test.Duration * _options.DurationScale;
        var start = _clock.Elapsed.TotalSeconds;
        var measureFrom = 2.0;
        var workAtStart = 0L;
        var measured = false;
        var aborted = false;
        var nextUpload = 0.0;
        try
        {
            while (true)
            {
                if (!_window.Pump())
                {
                    aborted = true;
                    break;
                }

                var elapsed = _clock.Elapsed.TotalSeconds - start;
                if (!measured && elapsed >= measureFrom)
                {
                    workAtStart = test.Work;
                    measured = true;
                    _sensors.BeginSegment();
                }

                if (elapsed >= measureFrom + duration)
                {
                    break;
                }

                _device.BeginFrame();
                var cmd = _device.Commands;
                cmd.Clear(_device.BackBuffer, new ColorF(0.008f, 0.009f, 0.014f, 1f));

                // L'image du processeur est envoyée quatre fois par seconde : l'affichage ne lui vole presque rien.
                if (elapsed >= nextUpload)
                {
                    _device.UploadTexture(picture, 0, 0, test.Image, test.Width * 4);
                    nextUpload = elapsed + 0.25;
                }

                var scale = MathF.Min(_ui.Width / test.Width, _ui.Height / test.Height);
                var w = test.Width * scale;
                var h = test.Height * scale;
                _ui.Image(picture, (_ui.Width - w) / 2, (_ui.Height - h) / 2, w, h);
                var rate = measured ? (test.Work - workAtStart) / Math.Max(elapsed - measureFrom, 1e-3) : 0;
                var progress = Math.Clamp((elapsed - measureFrom) / duration, 0, 1);
                DrawHud(test.Title, test.Subtitle, index, total, progress, !measured, gpu: false,
                    cpuRate: measured ? (rate / test.UnitScale).ToString("0.00", Culture) + " " + test.Unit : null, threads: test.Threads);
                _ui.Flush(cmd);
                Screenshot(test.Id, progress >= 0.6);
                _device.Present();
                Thread.Sleep(15);
            }
        }
        finally
        {
            var endWork = test.Work;
            var seconds = _clock.Elapsed.TotalSeconds - start - measureFrom;
            cancel.Cancel();
            var sensors = _sensors.EndSegment("cpu");
            worker.Wait();
            _device.WaitIdle();
            picture.Dispose();
            if (!aborted && measured && seconds > 0)
            {
                var value = (endWork - workAtStart) / seconds / test.UnitScale;
                _results.Add(new BenchmarkTestResult(
                    test.Id,
                    "cpu",
                    test.Capability,
                    Math.Round(value, 3),
                    test.Unit,
                    Math.Round(BenchmarkScoring.TestScore(value, BenchReference.For(test.Id, _options))))
                {
                    Sensors = sensors,
                });
            }
        }

        return !aborted;
    }

    private void DrawMessage(string message, int index, int total, Action? during = null)
    {
        _window.Pump();
        _device.BeginFrame();
        during?.Invoke();
        var cmd = _device.Commands;
        cmd.Clear(_device.BackBuffer, new ColorF(0.012f, 0.014f, 0.022f, 1f));
        var s = _ui.Scale;
        _ui.Text(message, _ui.Width / 2, (_ui.Height / 2) - (20 * s), 34 * s, UiColors.White(), bold: true, TextAlign.Center);
        _ui.Text(T("Test {0} sur {1}", index, total), _ui.Width / 2, (_ui.Height / 2) + (30 * s), 20 * s, UiColors.Grey(), align: TextAlign.Center);
        _ui.Flush(cmd);
        _device.Present();
    }

    /// <summary>Titre de la scène en grand au centre, comme le générique d'un film, pendant la mise en route.</summary>
    private void DrawTitleCard(string title, string subtitle, int index, int total, double elapsed)
    {
        var s = _ui.Scale;
        var alpha = (float)(Math.Clamp(elapsed / 0.6, 0, 1) * Math.Clamp((WarmupSeconds - elapsed) / 0.6, 0, 1));
        var cx = _ui.Width / 2f;
        var cy = _ui.Height / 2f;
        _ui.Rect(0, cy - (120 * s), _ui.Width, 240 * s, new Vector4(0, 0, 0, 0.32f * alpha));
        _ui.Text(T("Test {0} sur {1}", index, total).ToUpperInvariant(), cx, cy - (92 * s), 18 * s, UiColors.Blue(alpha), bold: true, TextAlign.Center);
        _ui.Text(title, cx, cy - (62 * s), 76 * s, UiColors.White(alpha), bold: true, TextAlign.Center, glow: 0.45f);
        _ui.Text(subtitle, cx, cy + (36 * s), 24 * s, UiColors.Grey(alpha * 0.95f), align: TextAlign.Center);
        _ui.Text(T("Mise en route (non mesurée)"), cx, cy + (82 * s), 16 * s, UiColors.Grey(alpha * 0.7f), align: TextAlign.Center);
    }

    private void RememberFrame(double seconds)
    {
        _recentFrames.Enqueue((float)(seconds * 1000));
        while (_recentFrames.Count > 180)
        {
            _recentFrames.Dequeue();
        }
    }

    private void DrawHud(string title, string subtitle, int index, int total, double progress, bool warmup, bool gpu, string? cpuRate = null, int threads = 0)
    {
        var s = _ui.Scale;
        var margin = 40 * s;

        // Bandeau du haut : titre du test, ce qu'il sollicite.
        _ui.Rect(0, 0, _ui.Width, 168 * s, new Vector4(0, 0, 0, 0.35f));
        _ui.Text("MAUS  ·  BENCHMARK", margin, margin * 0.7f, 15 * s, UiColors.Blue(0.9f), bold: true);
        _ui.Text(title, margin, (margin * 0.7f) + (24 * s), 40 * s, UiColors.White(), bold: true, glow: 0.25f);
        _ui.Text(subtitle, margin, (margin * 0.7f) + (76 * s), 19 * s, UiColors.Grey(0.95f));

        // Mesure en direct, à droite.
        var right = _ui.Width - margin;
        if (gpu)
        {
            _ui.Text(_fps.ToString("0", Culture), right, margin * 0.55f, 64 * s, UiColors.White(), bold: true, TextAlign.Right, glow: 0.2f);
            _ui.Text(T("images/s"), right, (margin * 0.55f) + (78 * s), 17 * s, UiColors.Grey(), align: TextAlign.Right);
            var ms = _device.LastGpuFrameMilliseconds;
            if (ms > 0)
            {
                _ui.Text(T("carte : {0} ms par image", ms.ToString("0.0", Culture)), right, (margin * 0.55f) + (100 * s), 15 * s, UiColors.Grey(0.8f), align: TextAlign.Right);
            }

            if (BenchmarkSensorText.Live(_sensors.Latest, "gpu") is { } live)
            {
                _ui.Text(live, right, (margin * 0.55f) + (121 * s), 15 * s, UiColors.Blue(0.9f), align: TextAlign.Right);
            }
        }
        else if (cpuRate is not null)
        {
            _ui.Text(cpuRate, right, margin * 0.9f, 26 * s, UiColors.White(), bold: true, TextAlign.Right);
            _ui.Text(T("{0} fils de calcul", threads), right, (margin * 0.9f) + (38 * s), 17 * s, UiColors.Grey(), align: TextAlign.Right);
            if (BenchmarkSensorText.Live(_sensors.Latest, "cpu") is { } live)
            {
                _ui.Text(live, right, (margin * 0.9f) + (62 * s), 15 * s, UiColors.Mint(0.9f), align: TextAlign.Right);
            }
        }

        // Bas de l'écran : progression, numéro du test, matériel.
        var barY = _ui.Height - (46 * s);
        _ui.Rect(0, barY - (34 * s), _ui.Width, 80 * s, new Vector4(0, 0, 0, 0.35f));
        _ui.Rect(margin, barY, _ui.Width - (2 * margin), 6 * s, UiColors.White(0.15f), 3 * s);
        _ui.Rect(margin, barY, (float)((_ui.Width - (2 * margin)) * progress), 6 * s, gpu ? UiColors.Blue() : UiColors.Mint(), 3 * s);
        var state = warmup ? T("Mise en route (non mesurée)") : T("Mesure en cours");
        _ui.Text(T("Test {0} sur {1}", index, total) + "  ·  " + state, margin, barY - (28 * s), 16 * s, UiColors.Grey());
        var hardware = gpu ? _device.AdapterName + "  ·  " + (_device.Api == GpuApi.Direct3D11 ? "Direct3D 11" : "Direct3D 12") : _cpuName;
        _ui.Text(hardware, right, barY - (28 * s), 16 * s, UiColors.Grey(), align: TextAlign.Right);

        // Temps des 180 dernières images : une barre par image, les à-coups sautent aux yeux.
        if (gpu && _recentFrames.Count > 10)
        {
            var graphW = 300 * s;
            var graphH = 54 * s;
            var gx = right - graphW;
            var gy = barY - (52 * s) - graphH;
            _ui.Rect(gx - (10 * s), gy - (10 * s), graphW + (20 * s), graphH + (20 * s), new Vector4(0, 0, 0, 0.35f), 10 * s);
            var frames = _recentFrames.ToArray();
            // Une image régulière remplit la moitié de la hauteur ; un à-coup (1,5 fois l'image médiane) passe en rose.
            var median = frames.Order().ElementAt(frames.Length / 2);
            var top = MathF.Max(MathF.Max(frames.Max(), median * 2f), 1f);
            var width = graphW / 180f;
            for (var i = 0; i < frames.Length; i++)
            {
                var h = graphH * MathF.Min(frames[i] / top, 1f);
                var color = frames[i] > median * 1.5f ? UiColors.Rose(0.9f) : UiColors.Blue(0.75f);
                _ui.Rect(gx + ((180 - frames.Length + i) * width), gy + graphH - h, MathF.Max(width - (0.5f * s), 1f), h, color);
            }

            _ui.Text(T("carte : {0} ms par image", frames[^1].ToString("0.0", Culture)), gx, gy - (30 * s), 14 * s, UiColors.Grey(0.85f));
        }
    }

    private void ShowResults(BenchmarkReport report)
    {
        var (strongest, weakest) = BenchmarkScoring.Extremes(report.Tests);
        var shown = _clock.Elapsed.TotalSeconds;
        while (_window.Pump())
        {
            if (_window.ConfirmPressed || (_options.AutoClose && _clock.Elapsed.TotalSeconds - shown > 4))
            {
                break;
            }

            _device.BeginFrame();
            var cmd = _device.Commands;
            cmd.Clear(_device.BackBuffer, new ColorF(0.012f, 0.014f, 0.022f, 1f));
            var s = _ui.Scale;
            var cx = _ui.Width / 2f;
            var age = _clock.Elapsed.TotalSeconds - shown;
            var reveal = 1.0 - Math.Pow(1.0 - Math.Clamp(age / 1.8, 0, 1), 3.0);
            for (var band = 0; band < 12; band++)
            {
                _ui.Rect(0, _ui.Height * band / 12f, _ui.Width, (_ui.Height / 12f) + 1, new Vector4(0.08f, 0.1f, 0.2f, 0.018f * band));
            }

            var pulse = 0.5f + (0.5f * MathF.Sin((float)age * 1.4f));
            _ui.Rect(cx - (380 * s), 95 * s, 760 * s, 210 * s, UiColors.Blue(0.05f + (0.03f * pulse)), 105 * s);
            var y = 70 * s;
            _ui.Text(report.Completed ? T("Résultats") : T("Résultats partiels (benchmark arrêté)"), cx, y, 30 * s, UiColors.Grey(), bold: true, TextAlign.Center);
            y += 50 * s;
            // Passe partielle (carte graphique seule ou processeur seul) : le score du composant testé passe au premier plan.
            var (headline, caption) = report.OverallScore > 0
                ? (report.OverallScore, T("score combiné en {0}  ·  10 000 = Core i7-8700K et RTX 2080 Ti", ResolutionName(_options.RenderSize)))
                : report.GpuScore > 0
                    ? (report.GpuScore, T("score de la carte graphique en {0}  ·  10 000 = RTX 2080 Ti", ResolutionName(_options.RenderSize)))
                    : (report.CpuScore, T("score du processeur  ·  10 000 = Core i7-8700K"));
            _ui.Text(Points(headline * reveal), cx, y, 110 * s, UiColors.White(), bold: true, TextAlign.Center, glow: 0.4f);
            y += 130 * s;
            _ui.Text(caption, cx, y, 18 * s, UiColors.Grey(), align: TextAlign.Center);
            y += 60 * s;

            var colW = 520 * s;
            var gpuTests = report.Tests.Where(t => t.Device == "gpu").ToList();
            var cpuTests = report.Tests.Where(t => t.Device == "cpu").ToList();
            var both = gpuTests.Count > 0 && cpuTests.Count > 0;
            DrawScoreColumn(T("Carte graphique"), report.GpuScore, gpuTests, both ? cx - colW - (30 * s) : cx - (colW / 2), y, colW, UiColors.Blue(), reveal);
            DrawScoreColumn(T("Processeur"), report.CpuScore, cpuTests, both ? cx + (30 * s) : cx - (colW / 2), y, colW, UiColors.Mint(), reveal);

            var bottom = _ui.Height - (150 * s);
            var warnings = new[] { ShortWarning(gpuTests, "gpu"), ShortWarning(cpuTests, "cpu") }.OfType<string>().ToList();
            for (var w = 0; w < warnings.Count; w++)
            {
                _ui.Text(warnings[w], cx, bottom - (44 * s) - ((warnings.Count - 1 - w) * 28 * s), 18 * s, UiColors.Rose(0.95f), align: TextAlign.Center);
            }

            if (strongest is not null && weakest is not null && strongest.Id != weakest.Id)
            {
                _ui.Text(T("Point fort : {0}", TestName(strongest.Id)), cx, bottom, 22 * s, UiColors.Mint(), bold: true, TextAlign.Center);
                _ui.Text(T("Point faible : {0}", TestName(weakest.Id)), cx, bottom + (34 * s), 22 * s, UiColors.Rose(), bold: true, TextAlign.Center);
            }

            _ui.Text(T("Entrée pour fermer. Les résultats sont enregistrés dans MAUS (page Benchmark)."), cx, _ui.Height - (56 * s), 17 * s, UiColors.Grey(0.85f), align: TextAlign.Center);
            _ui.Flush(cmd);
            Screenshot("results", age > 2.2);
            _device.Present();
            Thread.Sleep(10);
        }
    }

    private void DrawScoreColumn(string title, double score, List<BenchmarkTestResult> tests, float x, float y, float width, Vector4 accent, double reveal)
    {
        var s = _ui.Scale;
        if (tests.Count == 0)
        {
            return;
        }

        _ui.Rect(x, y, width, (108 * s) + (tests.Count * 74 * s), new Vector4(1, 1, 1, 0.05f), 18 * s);
        _ui.Text(title, x + (24 * s), y + (20 * s), 20 * s, accent, bold: true);
        var device = tests[0].Device;
        if (BenchmarkSensorText.Describe(BenchmarkSensorSummary.Merge(tests.Select(t => t.Sensors)), device) is { } sensors)
        {
            _ui.Text(sensors, x + (24 * s), y + (62 * s), 14 * s, UiColors.Grey(0.85f));
        }
        _ui.Text(Points(score * reveal), x + width - (24 * s), y + (12 * s), 40 * s, UiColors.White(), bold: true, TextAlign.Right);
        var rowY = y + (92 * s);
        var max = Math.Max(15000, tests.Count > 0 ? tests.Max(t => t.Score) * 1.1 : 1);
        foreach (var test in tests)
        {
            _ui.Text(TestName(test.Id), x + (24 * s), rowY, 17 * s, UiColors.White(0.92f));
            _ui.Text(Points(test.Score), x + width - (24 * s), rowY, 17 * s, UiColors.White(0.92f), bold: true, TextAlign.Right);
            var detail = test.Device == "gpu"
                ? T("{0} images/s", test.Value.ToString("0.0", Culture)) + (test.Low1 is { } low ? "  ·  " + T("1 % les plus lentes : {0}", low.ToString("0.0", Culture)) : "")
                : test.Value.ToString("0.00", Culture) + " " + test.Unit;
            _ui.Text(detail, x + (24 * s), rowY + (40 * s), 14 * s, UiColors.Grey(0.85f));
            var barW = width - (48 * s);
            _ui.Rect(x + (24 * s), rowY + (26 * s), barW, 8 * s, UiColors.White(0.1f), 4 * s);
            _ui.Rect(x + (24 * s), rowY + (26 * s), (float)(barW * Math.Min(1, test.Score / max) * reveal), 8 * s, accent, 4 * s);
            // Repère des 10 000 points de la machine de référence.
            _ui.Rect(x + (24 * s) + (float)(barW * BenchmarkScoring.ReferencePoints / max), rowY + (22 * s), 2 * s, 16 * s, UiColors.White(0.6f));
            rowY += 74 * s;
        }
    }

    /// <summary>Capture de contrôle (option --screenshots) : une image par test, prise une seule fois.</summary>
    private void Screenshot(string name, bool when)
    {
        if (_options.ScreenshotFolder is null || !when || !_screenshots.Add(name))
        {
            return;
        }

        var pixels = _device.CaptureBackBuffer(out var width, out var height);
        ImageFile.SavePng(Path.Combine(_options.ScreenshotFolder, name + ".png"), pixels, width, height);
    }

    private readonly HashSet<string> _screenshots = [];

    /// <summary>Avertissement court du bilan (la page Benchmark de MAUS donne le conseil complet).</summary>
    private static string? ShortWarning(List<BenchmarkTestResult> tests, string device)
    {
        var summary = BenchmarkSensorSummary.Merge(tests.Select(t => t.Sensors));
        if (summary is null || BenchmarkSensorText.Warning(summary, device) is null)
        {
            return null;
        }

        return device == "gpu"
            ? summary.ThermalSlowdownPercent is > 0 and var thermal
                ? T("Carte graphique ralentie par la chaleur pendant {0} % de la mesure", thermal.ToString("0.#", Culture))
                : T("Carte graphique freinée par sa protection matérielle pendant {0} % de la mesure", (summary.HardwareBrakePercent ?? 0).ToString("0.#", Culture))
            : T("Processeur à sa limite de température ({0} °C) : il a ralenti pour se protéger", summary.LimitTemperatureC ?? 0);
    }

    private static string Points(double score) => score > 0 ? score.ToString("N0", Culture) : "—";

    internal static string ResolutionName(RenderSize size) => size.Height switch
    {
        >= 2160 => "4K",
        >= 1440 => "1440p",
        >= 1080 => "1080p",
        _ => T("720p léger"),
    };

    internal static string TestName(string id) => id switch
    {
        "fractal" => T("Forge fractale (calcul)"),
        "galaxy" => T("Collision galactique (bande passante)"),
        "ring" => T("Anneau de la géante (géométrie)"),
        "battle" => T("Champ de bataille (effets)"),
        "cpu-render" => T("Rendu sur tous les cœurs"),
        "cpu-single" => T("Rendu sur un seul cœur"),
        "cpu-vector" => T("Calcul vectoriel"),
        _ => id,
    };

    private static ITexture? LoadLogo(IGpuDevice device)
    {
        using var stream = typeof(BenchmarkRunner).Assembly.GetManifestResourceStream("Maus.Bench.logo.png");
        if (stream is null)
        {
            return null;
        }

        var (pixels, width, height) = ImageFile.LoadRgba(stream);
        var texture = device.CreateTexture(TextureDesc.Image(width, height, PixelFormat.Rgba8Unorm, "Logo"));
        device.UploadTexture(texture, 0, 0, pixels, width * 4);
        return texture;
    }

    private static string CpuName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? T("Processeur");
        }
        catch (System.Security.SecurityException)
        {
            return T("Processeur");
        }
    }
}

/// <summary>
/// Mesures de la machine de référence (Core i7-8700K, GeForce RTX 2080 Ti, Direct3D 12) : chaque test y vaut 10 000
/// points. Les scènes de la carte graphique ont une référence par résolution (1080p, 1440p, 4K) : les points ne se
/// comparent qu'à résolution égale. Valeurs mesurées par le projet sur cette machine (étalonnage du benchmark).
/// </summary>
internal static class BenchReference
{
    // Mesures du 06/10/2026 sur la machine de référence (Direct3D 12, passes accélérées qui parcourent les mêmes trajets).
    // Le test standard (1080p) tourne à plus de 24 images par seconde sur cette machine : une carte modeste le fait tourner
    // correctement ; la 4K reste là pour mettre les très grosses cartes à genoux.
    private static readonly Dictionary<string, double> At1080 = new(StringComparer.Ordinal)
    {
        ["ring"] = 36.0,
        ["battle"] = 37.6,
        ["galaxy"] = 39.2,
        ["fractal"] = 28.6,
    };

    // Mode léger (720p, scènes allégées) : pour les cartes intégrées ; points comparables entre passes légères seulement.
    private static readonly Dictionary<string, double> At720 = new(StringComparer.Ordinal)
    {
        ["ring"] = 164,
        ["battle"] = 200,
        ["galaxy"] = 179,
        ["fractal"] = 141,
    };

    private static readonly Dictionary<string, double> At2160 = new(StringComparer.Ordinal)
    {
        ["ring"] = 30.8,
        ["battle"] = 10.6,
        ["galaxy"] = 13.8,
        ["fractal"] = 6.36,
    };

    /// <summary>1440p, et tests du processeur (indépendants de la résolution).</summary>
    private static readonly Dictionary<string, double> Values = new(StringComparer.Ordinal)
    {
        ["ring"] = 34.2,
        ["battle"] = 22.4,
        ["galaxy"] = 26.0,
        ["fractal"] = 16.9,
        ["cpu-render"] = 6.79,
        ["cpu-single"] = 0.693,
        ["cpu-vector"] = 1.28,
    };

    public static double For(string id, BenchOptions options)
    {
        var table = options.RenderSize.Height switch
        {
            >= 2160 => At2160,
            >= 1440 => Values,
            >= 1080 => At1080,
            _ => At720,
        };
        return table.TryGetValue(id, out var value) || Values.TryGetValue(id, out value) ? value : 0;
    }
}
