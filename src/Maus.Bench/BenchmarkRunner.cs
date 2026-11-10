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
    private readonly List<SharePicture> _pictures = [];
    private readonly SharePictureStore _pictureStore = new();
    private readonly BenchmarkSafetyStop _safety = new();
    private readonly BenchmarkRecorder _recorder;
    private bool _recordDone;

    /// <summary>Ce que la passe est devenue (historique, dossier de sortie du moteur, rien) ; <c>null</c> avant la fin.</summary>
    private BenchmarkRecord? _record;

    /// <summary>Le benchmark s'est arrêté par sécurité : chaleur, ou température qui n'est plus lue (code de sortie à part pour l'Atelier).</summary>
    public bool StoppedForSafety { get; private set; }
    private readonly string _cpuName = CpuName();
    private readonly string? _windowsBuild = WindowsBuild();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly BenchmarkSensorSampler _sensors;
    private double _fps;
    private string? _rayTracingNote;
    private readonly Queue<float> _recentFrames = new();
    private List<(string Id, string Title)> _plan = [];

    public BenchmarkRunner(BenchOptions options)
    {
        _options = options;

        // Avec les droits administrateur, rien n'est écrit dans le profil de l'utilisateur : seulement dans le dossier de
        // sortie donné par le moteur (0.8.6).
        _recorder = new BenchmarkRecorder(options.OutputFolder, Environment.IsPrivilegedProcess, options.IsTrial, options.ScreenshotFolder, BenchmarkHistoryStore.CreateDefault);
        _window = new BenchWindow("MAUS Benchmark");
        _device = BenchmarkProgram.CreateDevice(options.Api, _window.Handle, _window.Width, _window.Height);
        _post = new PostProcess(_device, _shaders, options.RenderSize);
        _font = FontAtlas.Create(_device);
        _ui = new UiRenderer(_device, _shaders, _font);
        _logo = LoadLogo(_device);
        _sensors = BenchmarkSensorSampler.ForThisPc(_device.AdapterName, _cpuName);
        _window.HideCursor();
    }

    public BenchmarkReport Run()
    {
        IReadOnlyList<BenchScene> scenes = _options.Only == "cpu" ? [] : KeepRayTracingIfPossible(BenchmarkProgram.CreateScenes(_options.Scenes));
        IReadOnlyList<CpuBenchTest> cpuTests = _options.Only == "gpu" ? [] : BenchmarkProgram.CreateCpuTests(_options.Scenes);
        var total = scenes.Count + cpuTests.Count;
        _plan = [.. scenes.Select(s => (s.Id, s.Title)), .. cpuTests.Select(c => (c.Id, c.Title))];
        var minutes = ((scenes.Sum(s => s.Duration + WarmupSeconds + 2) + cpuTests.Sum(t => t.Duration + 2)) * _options.DurationScale) + 10;
        string? error = null;
        var completed = false;
        try
        {
            if (!Intro(Math.Max(1, (int)Math.Round(minutes / 60)), scenes.Count > 0, cpuTests.Count > 0))
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
        catch (SafetyStopException ex)
        {
            error = ex.Message;
            StoppedForSafety = true;
        }
        catch (SharpGen.Runtime.SharpGenException ex) when (DeviceLost.IsLoss(ex))
        {
            // Plantage du pilote signalé par un appel ordinaire : les résultats déjà mesurés sont gardés.
            error = DeviceLost.From(ex).Message;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Toute autre panne : les résultats déjà mesurés restent gardés, avec la raison de l'arrêt.
            Maus.Core.Diagnostics.CrashLog.Write(ex, "benchmark");
            error = ex.Message;
        }

        return Finish(completed, error);
    }

    /// <summary>
    /// Le lancer de rayons ne tourne qu'avec Direct3D 12, sur une carte qui le gère, et si l'utilisateur ne l'a pas écarté :
    /// sinon la scène est retirée (la raison est notée dans le bilan quand ce n'est pas un choix de l'utilisateur).
    /// </summary>
    private IReadOnlyList<BenchScene> KeepRayTracingIfPossible(IReadOnlyList<BenchScene> scenes)
    {
        if (scenes.OfType<MirrorHallScene>().FirstOrDefault() is not { } hall)
        {
            return scenes;
        }

        var reason = _options.RayTracing ? MirrorHallScene.Unavailable(_device) : null;
        if (_options.RayTracing && reason is null)
        {
            return scenes;
        }

        _rayTracingNote = reason;
        hall.Dispose();
        return [.. scenes.Where(s => !ReferenceEquals(s, hall))];
    }

    public void Dispose()
    {
        _pictureStore.Dispose();
        _sensors.Dispose();
        _logo?.Dispose();
        _ui.Dispose();
        _font.Dispose();
        _post.Dispose();
        _device.Dispose();
        _window.Dispose();
    }

    /// <summary>Arrêt de sécurité (voir <see cref="BenchmarkSafetyStop"/>) : la mesure en cours est abandonnée.</summary>
    private void ThrowIfTooHot()
    {
        if (_safety.Check(_sensors.Latest) is { } reason)
        {
            throw new SafetyStopException(reason);
        }
    }

    private string ApiName => _device.Api == GpuApi.Direct3D11 ? "Direct3D 11" : "Direct3D 12";

    private BenchmarkReport Finish(bool completed, string? error)
    {
        var report = Report(completed, error);
        var show = error is null;
        if (show)
        {
            try
            {
                report = report with { Image = PrepareImage(report) };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // La passe est finie : une image impossible à dessiner ne doit pas la perdre.
                Maus.Core.Diagnostics.CrashLog.Write(ex, "image du résultat du benchmark");
                show = false;
            }
        }

        // Enregistrée avant le bilan : il annonce des résultats enregistrés, et une carte perdue sur cet écran ne coûte rien.
        Record(report);
        if (show)
        {
            try
            {
                ShowResults(report);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Maus.Core.Diagnostics.CrashLog.Write(ex, "bilan du benchmark");
            }
        }

        return report;
    }

    /// <summary>
    /// Garde la passe, une seule fois : dans l'historique, ou dans le dossier de sortie du moteur (<see cref="BenchmarkRecorder"/>).
    /// Les passes d'essai (accélérées ou fermées automatiquement) ne sont pas gardées.
    /// </summary>
    private void Record(BenchmarkReport report)
    {
        if (_recordDone)
        {
            return;
        }

        _recordDone = true;
        _record = _recorder.Record(report);
    }

    private BenchmarkReport Report(bool completed, string? error)
    {
        var gpu = BenchmarkScoring.Combine(_results.Where(r => r.Device == "gpu").Select(r => r.Score));
        var cpu = BenchmarkScoring.Combine(_results.Where(r => r.Device == "cpu").Select(r => r.Score));
        var rayTracing = BenchmarkScoring.Combine(_results.Where(r => r.Device == "rt").Select(r => r.Score));
        return new BenchmarkReport
        {
            Date = DateTimeOffset.Now,
            Edition = BenchmarkScoring.CurrentEdition,
            Api = ApiName,
            Gpu = _device.AdapterName,
            Cpu = _cpuName,
            GpuDriver = _device.DriverVersion,
            WindowsBuild = _windowsBuild,
            Threads = Environment.ProcessorCount,
            RenderResolution = string.Create(CultureInfo.InvariantCulture, $"{_options.RenderSize.Width}×{_options.RenderSize.Height}"),
            DisplayResolution = string.Create(CultureInfo.InvariantCulture, $"{_device.OutputWidth}×{_device.OutputHeight}"),
            Tests = [.. _results],
            GpuScore = Math.Round(gpu),
            CpuScore = Math.Round(cpu),
            OverallScore = Math.Round(BenchmarkScoring.Overall(gpu, cpu)),
            RayTracingScore = Math.Round(rayTracing),
            RayTracingNote = _rayTracingNote,
            Completed = completed,
            Error = error,
        };
    }

    private bool Intro(int minutes, bool gpu, bool cpu)
    {
        var duration = gpu && cpu
            ? T("Carte graphique et processeur poussés à fond, environ {0} minutes", minutes)
            : gpu ? T("Carte graphique poussée à fond, environ {0} minutes", minutes) : T("Processeur poussé à fond, environ {0} minutes", minutes);
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
            _ui.Text(duration, cx, _ui.Height * 0.47f + (96 * s), 26 * s, UiColors.Grey(fade), align: TextAlign.Center);
            var api = ApiName;
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
        if (!Interlude(scene.Title, scene.Subtitle, index, total, gpu: true, () => scene.Load(context)))
        {
            return false;
        }

        _device.WaitIdle();
        _post.ResetHistory();
        _builder.Reset();

        var duration = scene.Duration * _options.DurationScale;
        var start = _clock.Elapsed.TotalSeconds;
        var last = start;
        var frameTimes = new List<double>(8192);
        using var snapshot = _device.CreateTexture(TextureDesc.Target(960, 540, PixelFormat.Rgba8Unorm, "Vignette de la scène"));
        var snapped = false;
        var measuredFrames = 0;
        var measuredStart = 0.0;
        while (true)
        {
            if (!_window.Pump())
            {
                return false;
            }

            ThrowIfTooHot();
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
            var progress = Math.Clamp((elapsed - WarmupSeconds) / duration, 0, 1);
            var snap = !snapped && progress >= 0.55;
            snapped |= snap;
            BenchmarkProgram.RenderFrame(_device, context, _post, _builder, scene, sceneTime, fade: (float)Math.Max(fadeIn, fadeOut), snapshot: snap ? snapshot : null);
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
        if (snapped)
        {
            // Lecture de la vignette pendant une image noire : la scène vient de finir par un fondu au noir.
            _device.BeginFrame();
            _device.Commands.Clear(_device.BackBuffer, new ColorF(0f, 0f, 0f, 1f));
            if (_pictureStore.Save(scene.Id, _device.ReadTexture(snapshot), snapshot.Desc.Width, snapshot.Desc.Height) is { } picture)
            {
                _pictures.Add(picture);
            }

            _device.Present();
            _device.WaitIdle();
        }
        _results.Add(new BenchmarkTestResult(
            scene.Id,
            scene is MirrorHallScene ? "rt" : "gpu",
            Math.Round(fps, 2),
            BenchmarkTexts.Unit(scene.Id),
            Math.Round(BenchmarkScoring.TestScore(fps, BenchReference.For(scene.Id, _options))),
            BenchmarkScoring.Low1Fps(frameTimes) is { } low ? Math.Round(low, 2) : null)
        {
            Sensors = _sensors.EndSegment("gpu"),
        });
        return true;
    }

    private bool RunCpuTest(CpuBenchTest test, int index, int total)
    {
        if (!Interlude(test.Title, test.Subtitle, index, total, gpu: false, null))
        {
            return false;
        }

        // Avant le calcul et le début de la mesure : la réservation des images ne compte pas dans le temps mesuré.
        test.Prepare();
        var picture = _device.CreateTexture(TextureDesc.Image(test.Width, test.Height, PixelFormat.Rgba8Unorm, test.Title));
        using var cancel = new CancellationTokenSource();
        var worker = Task.Factory.StartNew(() => test.Run(cancel.Token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var duration = test.Duration * _options.DurationScale;
        var start = _clock.Elapsed.TotalSeconds;
        var measureFrom = 2.0;
        var workAtStart = 0L;
        var measured = false;
        var aborted = false;
        // Résultat enregistré seulement si la mesure est allée jusqu'au bout : un arrêt (Échap, chaleur, carte perdue) ne
        // laisse jamais un résultat partiel passer pour un résultat complet.
        var finished = false;
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

                ThrowIfTooHot();
                var elapsed = _clock.Elapsed.TotalSeconds - start;
                if (!measured && elapsed >= measureFrom)
                {
                    workAtStart = test.Work;
                    measured = true;
                    _sensors.BeginSegment();
                }

                if (elapsed >= measureFrom + duration)
                {
                    finished = true;
                    break;
                }

                _device.BeginFrame();
                var cmd = _device.Commands;
                cmd.Clear(_device.BackBuffer, new ColorF(0.008f, 0.009f, 0.014f, 1f));

                // L'image du processeur est envoyée quatre fois par seconde : l'affichage ne lui vole presque rien.
                if (elapsed >= nextUpload)
                {
                    _device.UploadTexture(picture, 0, test.Image, test.Width * 4);
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
            if (finished && measured && seconds > 0)
            {
                if (test.Id == "cpu-render")
                {
                    if (_pictureStore.Save(test.Id, test.Image, test.Width, test.Height) is { } vignette)
                    {
                        _pictures.Add(vignette);
                    }

                }

                var value = (endWork - workAtStart) / seconds / test.UnitScale;
                _results.Add(new BenchmarkTestResult(
                    test.Id,
                    "cpu",
                    Math.Round(value, 3),
                    test.Unit,
                    Math.Round(BenchmarkScoring.TestScore(value, BenchReference.For(test.Id, _options))))
                {
                    Sensors = sensors,
                });
            }

            test.Release();
        }

        return !aborted;
    }

    /// <summary>
    /// Entre deux tests (rien n'est mesuré ici) : le titre du test qui arrive, les tests déjà passés avec leur image et leur score.
    /// L'écran arrive en une seconde, puis reste affiché pendant le chargement, qui bloque le dessin.
    /// </summary>
    private bool Interlude(string title, string subtitle, int index, int total, bool gpu, Action? during)
    {
        var pictures = new Dictionary<string, ITexture>();
        var length = 1.1 * Math.Min(1, _options.DurationScale * 4);
        var start = _clock.Elapsed.TotalSeconds;
        try
        {
            while (_clock.Elapsed.TotalSeconds - start < length)
            {
                if (!_window.Pump())
                {
                    return false;
                }

                ThrowIfTooHot();
                _device.BeginFrame();
                LoadPictures(pictures);
                DrawInterlude(title, subtitle, index, total, gpu, (float)((_clock.Elapsed.TotalSeconds - start) / length), pictures);
                _device.Present();
                Thread.Sleep(10);
            }

            if (!_window.Pump())
            {
                return false;
            }

            _device.BeginFrame();
            during?.Invoke();
            LoadPictures(pictures);
            DrawInterlude(title, subtitle, index, total, gpu, 1f, pictures);
            Screenshot("chargement-" + index.ToString(CultureInfo.InvariantCulture), true);
            _device.Present();
            return true;
        }
        finally
        {
            _device.WaitIdle();
            foreach (var picture in pictures.Values)
            {
                picture.Dispose();
            }
        }
    }

    private void LoadPictures(Dictionary<string, ITexture> pictures)
    {
        foreach (var picture in _pictures.Where(p => !pictures.ContainsKey(p.Id) && File.Exists(p.File)))
        {
            var pixels = File.ReadAllBytes(picture.File);
            if (pixels.Length == ShareCard.CellWidth * ShareCard.CellHeight * 4)
            {
                var texture = _device.CreateTexture(TextureDesc.Image(ShareCard.CellWidth, ShareCard.CellHeight, PixelFormat.Rgba8Unorm, "Vignette"));
                _device.UploadTexture(texture, 0, pixels, ShareCard.CellWidth * 4);
                pictures[picture.Id] = texture;
            }
        }
    }

    private void DrawInterlude(string title, string subtitle, int index, int total, bool gpu, float progress, Dictionary<string, ITexture> pictures)
    {
        var cmd = _device.Commands;
        cmd.Clear(_device.BackBuffer, new ColorF(0.014f, 0.018f, 0.03f, 1f));
        var s = _ui.Scale;
        var cx = _ui.Width / 2f;
        var ease = 1f - MathF.Pow(1f - Math.Clamp(progress * 1.6f, 0f, 1f), 3f);
        var accent = gpu ? UiColors.Blue() : UiColors.Mint();

        // Les couleurs de l'image du résultat, et un léger halo derrière le titre.
        for (var i = 0; i < 16; i++)
        {
            var grow = i * 22 * s;
            _ui.Rect(cx - (520 * s) - grow, (250 * s) - grow, (1040 * s) + (2 * grow), (280 * s) + (2 * grow), new Vector4(accent.X, accent.Y, accent.Z, 0.0045f * ease), (140 * s) + grow);
        }

        var quarter = _ui.Width / 4f;
        _ui.Rect(0, 0, quarter, 6 * s, UiColors.Blue(0.9f));
        _ui.Rect(quarter, 0, quarter, 6 * s, UiColors.Rose(0.9f));
        _ui.Rect(quarter * 2, 0, quarter, 6 * s, UiColors.Mint(0.9f));
        _ui.Rect(quarter * 3, 0, quarter, 6 * s, UiColors.Sand(0.9f));

        // Le test qui arrive : le titre monte en apparaissant.
        var y = (250 * s) + ((1f - ease) * 26 * s);
        _ui.Text(T("Test {0} sur {1}", index, total).ToUpperInvariant(), cx, y, 20 * s, new Vector4(accent.X, accent.Y, accent.Z, ease), bold: true, TextAlign.Center);
        _ui.Text(title, cx, y + (34 * s), 76 * s, UiColors.White(ease), bold: true, TextAlign.Center, glow: 0.35f);
        _ui.Text(subtitle, cx, y + (132 * s), 26 * s, UiColors.Grey(ease), align: TextAlign.Center);

        // La barre du déroulé : un segment par test, celui qui arrive se remplit.
        var barWidth = 720 * s;
        var gap = 6 * s;
        var segment = (barWidth - (gap * (total - 1))) / total;
        var barX = cx - (barWidth / 2);
        var barY = y + (196 * s);
        for (var i = 0; i < total; i++)
        {
            var x = barX + (i * (segment + gap));
            _ui.Rect(x, barY, segment, 6 * s, UiColors.White(0.13f), 3 * s);
            var fill = i < index - 1 ? 1f : i == index - 1 ? Math.Clamp(progress, 0f, 1f) : 0f;
            if (fill > 0f)
            {
                _ui.Rect(x, barY, segment * fill, 6 * s, i == index - 1 ? accent : UiColors.Mint(0.9f), 3 * s);
            }
        }

        _ui.Text(T("Préparation du test…"), cx, barY + (18 * s), 18 * s, UiColors.Grey(0.85f * ease), align: TextAlign.Center);

        // Les tests du déroulé : image et score de ceux qui sont passés, le nom seul pour les autres.
        const float Margin = 60f;
        var cellGap = 14 * s;
        var cellWidth = MathF.Min(280 * s, ((_ui.Width - (2 * Margin * s)) - (cellGap * (_plan.Count - 1))) / Math.Max(1, _plan.Count));
        var cellHeight = cellWidth * 9f / 16f;
        var rowWidth = (cellWidth * _plan.Count) + (cellGap * (_plan.Count - 1));
        var rowY = _ui.Height - (150 * s) - cellHeight;
        for (var i = 0; i < _plan.Count; i++)
        {
            var (id, name) = _plan[i];
            var x = cx - (rowWidth / 2) + (i * (cellWidth + cellGap));
            var appear = Math.Clamp((progress * 2f) - (i * 0.06f), 0f, 1f);
            var current = i == index - 1;
            _ui.Rect(x - (2 * s), rowY - (2 * s), cellWidth + (4 * s), cellHeight + (4 * s), current ? new Vector4(accent.X, accent.Y, accent.Z, 0.9f * appear) : UiColors.White(0.1f * appear), 9 * s);
            _ui.Rect(x, rowY, cellWidth, cellHeight, new Vector4(0.03f, 0.04f, 0.065f, appear), 8 * s);
            if (pictures.TryGetValue(id, out var picture))
            {
                _ui.Flush(cmd);
                _ui.Image(picture, x, rowY, cellWidth, cellHeight, appear);
                _ui.Flush(cmd);
            }

            var size = MathF.Min(16 * s, 16 * s * cellWidth / MathF.Max(1f, _ui.Measure(name, 16 * s) + (16 * s)));
            _ui.Text(name, x + (cellWidth / 2), rowY + cellHeight + (10 * s), size, (current ? UiColors.White(appear) : UiColors.Grey(0.8f * appear)), bold: current, TextAlign.Center);
            if (_results.FirstOrDefault(r => r.Id == id) is { } result)
            {
                _ui.Rect(x, rowY + cellHeight - (36 * s), cellWidth, 36 * s, UiColors.Ink(0.6f * appear));
                _ui.Text(result.Score.ToString("N0", Culture), x + cellWidth - (10 * s), rowY + cellHeight - (31 * s), 22 * s, UiColors.White(appear), bold: true, TextAlign.Right);
            }
        }

        // Bas de l'écran : le matériel et ce que lisent les capteurs pendant la pause.
        var bottom = _ui.Height - (58 * s);
        _ui.Text(T("Échap pour arrêter"), Margin * s, bottom, 16 * s, UiColors.Grey(0.7f * ease));
        var hardware = gpu ? _device.AdapterName : _cpuName;
        if (BenchmarkSensorText.Live(_sensors.Latest, gpu ? "gpu" : "cpu") is { } live)
        {
            hardware += "  ·  " + live;
        }

        _ui.Text(hardware, _ui.Width - (Margin * s), bottom, 16 * s, UiColors.Grey(0.7f * ease), align: TextAlign.Right);
        _ui.Flush(cmd);
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
        var hardware = gpu ? _device.AdapterName + "  ·  " + ApiName : _cpuName;
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

            _ui.Text(T("image entière : {0} ms", frames[^1].ToString("0.0", Culture)), gx, gy - (30 * s), 14 * s, UiColors.Grey(0.85f));
        }
    }

    /// <summary>Dessine et enregistre l'image du résultat, dans une image de la carte ; rend son chemin (ou <c>null</c>).</summary>
    private string? PrepareImage(BenchmarkReport report)
    {
        _device.BeginFrame();
        var image = SaveShareImage(report);
        _device.Commands.Clear(_device.BackBuffer, new ColorF(0.012f, 0.014f, 0.022f, 1f));
        _device.Present();
        return image;
    }

    /// <summary>Bilan à l'écran jusqu'à Entrée.</summary>
    private void ShowResults(BenchmarkReport report)
    {
        // Après Échap, le bilan partiel doit s'afficher : la demande d'arrêt a déjà servi.
        _window.ResetClose();
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
                ? (report.OverallScore, T("score combiné en {0}", ResolutionName(_options.RenderSize)))
                : report.GpuScore > 0
                    ? (report.GpuScore, T("score de la carte graphique en {0}", ResolutionName(_options.RenderSize)))
                    : (report.CpuScore, T("score du processeur"));
            _ui.Text(BenchmarkTexts.Points(headline * reveal), cx, y, 110 * s, UiColors.White(), bold: true, TextAlign.Center, glow: 0.4f);
            y += 130 * s;
            _ui.Text(caption, cx, y, 18 * s, UiColors.Grey(), align: TextAlign.Center);
            _ui.Text(BenchmarkTexts.EditionNote(report), cx, y + (26 * s), 15 * s, UiColors.Grey(0.8f), align: TextAlign.Center);
            y += 60 * s;

            // Carte graphique à gauche ; à droite le processeur, puis le lancer de rayons (score à part) en dessous.
            var colW = 520 * s;
            var gpuTests = report.Tests.Where(t => t.Device == "gpu").ToList();
            var cpuTests = report.Tests.Where(t => t.Device == "cpu").ToList();
            var rtTests = report.Tests.Where(t => t.Device == "rt").ToList();
            var both = gpuTests.Count > 0 && (cpuTests.Count > 0 || rtTests.Count > 0);
            var rightX = both ? cx + (30 * s) : cx - (colW / 2);
            DrawScoreColumn(T("Carte graphique"), report.GpuScore, gpuTests, both ? cx - colW - (30 * s) : cx - (colW / 2), y, colW, UiColors.Blue(), reveal);
            var below = DrawScoreColumn(T("Processeur"), report.CpuScore, cpuTests, rightX, y, colW, UiColors.Mint(), reveal);
            DrawScoreColumn(T("Lancer de rayons (score à part)"), report.RayTracingScore, rtTests, rightX, cpuTests.Count > 0 ? below + (18 * s) : y, colW, UiColors.Sand(), reveal);
            if (rtTests.Count == 0 && report.RayTracingNote is { } note)
            {
                _ui.Text(T("Lancer de rayons non mesuré : {0}", note), cx, _ui.Height - (88 * s), 15 * s, UiColors.Grey(0.8f), align: TextAlign.Center);
            }

            var bottom = _ui.Height - (150 * s);
            var warnings = new[] { ShortWarning([.. gpuTests, .. rtTests], "gpu"), ShortWarning(cpuTests, "cpu") }.OfType<string>().ToList();
            for (var w = 0; w < warnings.Count; w++)
            {
                _ui.Text(warnings[w], cx, bottom - (44 * s) - ((warnings.Count - 1 - w) * 28 * s), 18 * s, UiColors.Rose(0.95f), align: TextAlign.Center);
            }

            if (strongest is not null && weakest is not null && strongest.Id != weakest.Id)
            {
                _ui.Text(T("Le plus rapide : {0}", BenchmarkTexts.Name(strongest.Id)), cx, bottom, 22 * s, UiColors.Mint(), bold: true, TextAlign.Center);
                _ui.Text(T("Le moins rapide : {0}", BenchmarkTexts.Name(weakest.Id)), cx, bottom + (34 * s), 22 * s, UiColors.Sand(), bold: true, TextAlign.Center);
            }

            var hint = _record == BenchmarkRecord.Failed
                ? T("Entrée pour fermer. L'historique de MAUS est illisible : ces résultats n'y ont pas été enregistrés.")
                : _record == BenchmarkRecord.NotKept
                ? T("Entrée pour fermer. Avec les droits administrateur, ces résultats ne sont pas enregistrés : lancez le benchmark depuis MAUS pour les garder.")
                : report.Image is not null
                    ? T("Entrée pour fermer. Résultats et image à partager enregistrés dans MAUS (page Benchmark).")
                    : T("Entrée pour fermer. Les résultats sont enregistrés dans MAUS (page Benchmark).");
            _ui.Text(hint, cx, _ui.Height - (56 * s), 17 * s, UiColors.Grey(0.85f), align: TextAlign.Center);
            _ui.Flush(cmd);
            Screenshot("results", age > 2.2);
            _device.Present();
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// Image du résultat à partager : dans le dossier des images de MAUS pour une vraie passe, dans le dossier des captures
    /// pour un essai (rien n'est laissé dans les fichiers de l'utilisateur), dans le dossier de sortie quand le moteur l'a
    /// lancé, et nulle part avec les droits administrateur sans ce dossier (<see cref="BenchmarkRecorder.ImagePathFor"/>).
    /// </summary>
    private string? SaveShareImage(BenchmarkReport report)
    {
        var path = _recorder.ImagePathFor(report.Date);
        if (report.Tests.Count == 0 || path is null)
        {
            return null;
        }

        try
        {
            var pixels = ShareCard.Render(_device, _ui, _logo, report, _pictures, ResolutionName(_options.RenderSize));
            ImageFile.SavePng(path, pixels, ShareCard.Width, ShareCard.Height);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SharpGen.Runtime.SharpGenException)
        {
            return null;
        }
    }

    /// <summary>Colonne d'un composant (titre, score, une ligne par test) ; rend le bas de la colonne.</summary>
    private float DrawScoreColumn(string title, double score, List<BenchmarkTestResult> tests, float x, float y, float width, Vector4 accent, double reveal)
    {
        var s = _ui.Scale;
        if (tests.Count == 0)
        {
            return y;
        }

        // Bilan simple (demande du porteur) : le nom, les points et les images par seconde ; les capteurs et le détail des
        // images lentes restent dans la page Benchmark de MAUS.
        _ui.Rect(x, y, width, (86 * s) + (tests.Count * 74 * s), new Vector4(1, 1, 1, 0.05f), 18 * s);
        _ui.Text(title, x + (24 * s), y + (20 * s), 20 * s, accent, bold: true);
        _ui.Text(BenchmarkTexts.Points(score * reveal), x + width - (24 * s), y + (12 * s), 40 * s, UiColors.White(), bold: true, TextAlign.Right);
        var rowY = y + (70 * s);
        var max = Math.Max(15000, tests.Count > 0 ? tests.Max(t => t.Score) * 1.1 : 1);
        foreach (var test in tests)
        {
            _ui.Text(BenchmarkTexts.Name(test.Id), x + (24 * s), rowY, 17 * s, UiColors.White(0.92f));
            _ui.Text(BenchmarkTexts.Points(test.Score), x + width - (24 * s), rowY, 17 * s, UiColors.White(0.92f), bold: true, TextAlign.Right);
            _ui.Text(BenchmarkTexts.Measure(test), x + (24 * s), rowY + (40 * s), 14 * s, UiColors.Grey(0.85f));
            var barW = width - (48 * s);
            _ui.Rect(x + (24 * s), rowY + (26 * s), barW, 8 * s, UiColors.White(0.1f), 4 * s);
            _ui.Rect(x + (24 * s), rowY + (26 * s), (float)(barW * Math.Min(1, test.Score / max) * reveal), 8 * s, accent, 4 * s);
            rowY += 74 * s;
        }

        return y + (86 * s) + (tests.Count * 74 * s);
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


    internal static string ResolutionName(RenderSize size) => size.Height switch
    {
        >= 2160 => "4K",
        >= 1440 => "1440p",
        >= 1080 => "1080p",
        _ => T("720p léger"),
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
        device.UploadTexture(texture, 0, pixels, width * 4);
        return texture;
    }

    /// <summary>Lu une fois, avant toute mesure. Une lecture impossible n'arrête pas la passe : le bilan n'aura simplement pas ce champ.</summary>
    private static string? WindowsBuild()
    {
        try
        {
            return BenchmarkMarkers.WindowsBuildText(new Maus.Core.Platform.WindowsRegistryReader());
        }
        catch (Maus.Core.Platform.MausAccessDeniedException)
        {
            return null;
        }
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
/// comparent qu'à résolution égale. Valeurs mesurées par le projet sur cette machine (étalonnage du benchmark). Pour les
/// remesurer : une passe complète à vitesse réelle par résolution, dont on reprend la mesure brute de chaque test.
/// </summary>
internal static class BenchReference
{
    // Mesures du 06/10/2026 au soir sur la machine de référence (Direct3D 12), en passes à vitesse réelle et dans l'ordre
    // du benchmark : la carte et le processeur y chauffent comme chez l'utilisateur. Une passe accélérée surestimait les
    // tests de fin de passe (composants encore frais) et sous-estimait les galaxies (plus de pas de simulation par image).
    private static readonly Dictionary<string, double> At1080 = new(StringComparer.Ordinal)
    {
        ["ring"] = 35.42,
        ["battle"] = 36.85,
        ["galaxy"] = 39.63,
        ["materials"] = 60.27,
        ["fractal"] = 23.95,
        ["raytracing"] = 49.99,
    };

    // Mode léger (720p, scènes allégées) : pour les cartes intégrées ; points comparables entre passes légères seulement.
    private static readonly Dictionary<string, double> At720 = new(StringComparer.Ordinal)
    {
        ["ring"] = 158.67,
        ["battle"] = 199.06,
        ["galaxy"] = 184.06,
        ["materials"] = 327.6,
        ["fractal"] = 138.94,
        ["raytracing"] = 212.91,
    };

    private static readonly Dictionary<string, double> At2160 = new(StringComparer.Ordinal)
    {
        // Édition 4 : passes complètes du 02/11/2026 sur la machine de référence (moyenne de deux passes).
        ["ring"] = 30.73,
        ["battle"] = 11.91,
        ["galaxy"] = 15.67,
        ["materials"] = 16.53,
        ["fractal"] = 6.47,
        ["raytracing"] = 15.85,
    };

    /// <summary>1440p, et tests du processeur (indépendants de la résolution).</summary>
    private static readonly Dictionary<string, double> Values = new(StringComparer.Ordinal)
    {
        // Édition 4 : passe complète du 02/11/2026 sur la machine de référence, et cpu-vector, qui a changé avec elle.
        ["ring"] = 33.92,
        ["battle"] = 23.77,
        ["galaxy"] = 29.21,
        ["materials"] = 35.58,
        ["fractal"] = 16.91,
        ["raytracing"] = 31.81,
        // Processeur : passe complète du 19/10/2026 sur la machine de référence, à vitesse réelle et dans l'ordre du benchmark.
        // Revérifié le 01/11/2026 (26,69 ; 2,77 ; 4,45). Les anciennes valeurs donnaient environ 40 000 points à cette machine.
        ["cpu-render"] = 25.961,
        ["cpu-single"] = 2.778,
        ["cpu-vector"] = 8.74,
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
