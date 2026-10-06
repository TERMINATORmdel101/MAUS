using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Maus.Bench.Cpu;
using Maus.Bench.Gpu;
using Maus.Bench.Gpu.D3D11;
using Maus.Bench.Gpu.D3D12;
using Maus.Bench.Platform;
using Maus.Bench.Render;
using Maus.Bench.Scenes;
using Maus.Core.Workshop.Benchmark;

namespace Maus.Bench;

/// <summary>
/// Point d'entrée du benchmark visuel (MAUS.exe --benchmark, ou maus --benchmark pour les essais) :
///   --api d3d11|d3d12        interface graphique (par défaut : Direct3D 12)
///   --resolution 720p|1080p|1440p|4k   résolution de calcul (par défaut : 1080p)
///   --only gpu|cpu           seulement la carte graphique ou le processeur
///   --raytracing off         sans le test du lancer de rayons (Direct3D 12, cartes compatibles)
///   --scene ID[,ID]          seulement ces scènes
///   --capture DOSSIER        rendu hors écran de quelques images (--times 5,20,40 ; --size 1920x1080) en PNG
///   --duration-scale X       durées multipliées par X (essais rapides)
///   --result FICHIER         résultats en JSON
/// </summary>
public static partial class BenchmarkProgram
{
    public static int Run(string[] args)
    {
        _ = SetProcessDpiAwarenessContext(-4);
        var options = BenchOptions.Parse(args);
        Maus.Core.Localization.Texts.Use(options.Language);
        try
        {
            if (options.CaptureFolder is not null)
            {
                return Capture(options);
            }

            BenchmarkReport report;
            using (var runner = new BenchmarkRunner(options))
            {
                report = runner.Run();
            }

            // Les passes d'essai (accélérées ou fermées automatiquement) ne vont pas dans l'historique de l'utilisateur.
            if (report.Tests.Count > 0 && !options.IsTrial)
            {
                var store = BenchmarkHistoryStore.CreateDefault();
                store.Add(report);
                store.PruneImages();
            }

            if (options.ResultFile is { } file)
            {
                File.WriteAllText(file, BenchmarkHistoryStore.Serialize(report));
            }

            return report.Error is null ? 0 : 3;
        }
        catch (DeviceLostException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
    }

    internal static IReadOnlyList<BenchScene> CreateScenes(IReadOnlyCollection<string> only)
    {
        BenchScene[] all = [new RingScene(), new BattleScene(), new GalaxyScene(), new MaterialsScene(), new FractalScene(), new MirrorHallScene()];
        return only.Count == 0 ? all : all.Where(s => only.Contains(s.Id)).ToArray();
    }

    internal static IReadOnlyList<CpuBenchTest> CreateCpuTests(IReadOnlyCollection<string> only)
    {
        CpuBenchTest[] all = [new PathTracerTest(singleCore: false), new PathTracerTest(singleCore: true), new MandelbrotTest()];
        return only.Count == 0 ? all : all.Where(t => only.Contains(t.Id)).ToArray();
    }

    internal static IGpuDevice CreateDevice(GpuApi api, nint window, int width, int height) => api switch
    {
        GpuApi.Direct3D11 => new D3D11GpuDevice(window, width, height),
        _ => new D3D12GpuDevice(window, width, height),
    };

    /// <summary>Rendu hors écran de quelques instants de chaque scène (contrôle visuel, sans fenêtre).</summary>
    private static int Capture(BenchOptions options)
    {
        using var device = CreateDevice(options.Api, 0, options.OutputWidth, options.OutputHeight);
        Console.WriteLine($"{device.Api} : {device.AdapterName} ({device.DedicatedVideoMemory / (1024 * 1024)} Mo)");
        var shaders = new ShaderLibrary();
        using var post = new PostProcess(device, shaders, options.RenderSize);
        var context = new SceneContext(device, shaders, post) { Light = options.Light };
        var builder = new FrameBuilder();
        foreach (var scene in CreateScenes(options.Scenes))
        {
            using (scene)
            {
                var watch = Stopwatch.StartNew();
                device.BeginFrame();
                scene.Load(context);
                device.Present();
                device.WaitIdle();
                Console.WriteLine($"{scene.Id} : chargée en {watch.Elapsed.TotalSeconds:0.0} s");
                foreach (var time in options.Times)
                {
                    post.ResetHistory();
                    builder.Reset();
                    const int warmup = 16;
                    var gpu = new List<double>();
                    for (var k = 0; k < warmup; k++)
                    {
                        var t = Math.Max(0, time - ((warmup - 1 - k) / 60.0));
                        var frameWatch = Stopwatch.StartNew();
                        RenderFrame(device, context, post, builder, scene, t, 1f / 60f, options.CameraOverride);
                        if (k < warmup - 1)
                        {
                            device.Present();
                            device.WaitIdle();
                            gpu.Add(frameWatch.Elapsed.TotalMilliseconds);
                        }
                    }

                    var pixels = device.CaptureBackBuffer(out var w, out var h);
                    device.Present();
                    device.WaitIdle();
                    var file = Path.Combine(options.CaptureFolder!, $"{scene.Id}-{device.Api}-{time:000}.png");
                    ImageFile.SavePng(file, pixels, w, h);
                    var median = gpu.Skip(4).Order().ElementAt((gpu.Count - 4) / 2);
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  t = {time} s : {file} (carte : {median:0.0} ms par image)"));
                }
            }
        }

        return 0;
    }

    internal static void RenderFrame(IGpuDevice device, SceneContext context, PostProcess post, FrameBuilder builder, BenchScene scene, double time, float deltaTime, CameraPose? cameraOverride = null, float fade = 0f, ITexture? snapshot = null)
    {
        var state = scene.Evaluate(time);
        if (fade > 0f)
        {
            state = state with { Grade = state.Grade with { Fade = MathF.Max(state.Grade.Fade, fade) } };
        }

        if (cameraOverride is { } camera)
        {
            state = state with { Camera = camera };
        }

        if (state.Cut)
        {
            post.ResetHistory();
        }

        device.BeginFrame();
        var frame = builder.Build(state, context.Size, time, deltaTime);
        context.Frame = frame;
        var cmd = device.Commands;
        cmd.SetConstants(0, frame);
        scene.Render(context);
        cmd.SetConstants(0, frame);
        post.Run(cmd, state.Grade, state.Temporal, scene.HasOverlay ? target => scene.RenderOverlay(context, target) : null, snapshot);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessDpiAwarenessContext(nint value);
}

/// <summary>Options de la ligne de commande du benchmark.</summary>
internal sealed record BenchOptions
{
    public GpuApi Api { get; init; } = GpuApi.Direct3D12;

    public IReadOnlyCollection<string> Scenes { get; init; } = [];

    public string? CaptureFolder { get; init; }

    public IReadOnlyList<double> Times { get; init; } = [10];

    public int OutputWidth { get; init; } = 1920;

    public int OutputHeight { get; init; } = 1080;

    /// <summary>Résolution de calcul des scènes : 1080p par défaut (recommandé), 720p léger, 1440p ou 4K au choix.</summary>
    public RenderSize RenderSize { get; init; } = new(1920, 1080);

    /// <summary>Mode léger : 720p et scènes allégées (cartes intégrées, petits portables).</summary>
    public bool Light => RenderSize.Height < 1080;

    public double DurationScale { get; init; } = 1;

    public string? ResultFile { get; init; }

    public string? Language { get; init; }

    /// <summary>« gpu » : seulement les scènes de la carte graphique ; « cpu » : seulement les tests du processeur.</summary>
    public string? Only { get; init; }

    /// <summary>Bilan fermé tout seul après 4 secondes (essais automatiques).</summary>
    public bool AutoClose { get; init; }

    /// <summary>Test du lancer de rayons (Direct3D 12, cartes compatibles, score à part) : oui, sauf avec « --raytracing off ».</summary>
    public bool RayTracing { get; init; } = true;

    /// <summary>Passe d'essai (accélérée ou fermée automatiquement) : rien n'est gardé dans l'historique de l'utilisateur.</summary>
    public bool IsTrial => DurationScale < 1 || AutoClose;

    /// <summary>Dossier des captures de contrôle prises pendant une vraie passe (une par test et le bilan).</summary>
    public string? ScreenshotFolder { get; init; }

    /// <summary>Caméra imposée (repérages pour les captures) : x,y,z,cibleX,cibleY,cibleZ[,champ].</summary>
    public CameraPose? CameraOverride { get; init; }

    public static BenchOptions Parse(string[] args)
    {
        string? Value(string flag)
        {
            var i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var options = new BenchOptions();
        if (Value("--api") is { } api)
        {
            options = options with { Api = api.Contains("11", StringComparison.Ordinal) ? GpuApi.Direct3D11 : GpuApi.Direct3D12 };
        }

        if (Value("--scene") is { } scenes)
        {
            options = options with { Scenes = scenes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase) };
        }

        if (Value("--capture") is { } capture)
        {
            options = options with { CaptureFolder = capture };
        }

        if (Value("--times") is { } times)
        {
            options = options with { Times = times.Split(',').Select(t => double.Parse(t, CultureInfo.InvariantCulture)).ToArray() };
        }

        if (Value("--size") is { } size && size.Split('x') is [var w, var h])
        {
            options = options with { OutputWidth = int.Parse(w, CultureInfo.InvariantCulture), OutputHeight = int.Parse(h, CultureInfo.InvariantCulture) };
        }

        if (Value("--resolution") is { } resolution)
        {
            options = options with
            {
                RenderSize = resolution.ToLowerInvariant() switch
                {
                    "720p" => new RenderSize(1280, 720),
                    "1440p" => new RenderSize(2560, 1440),
                    "4k" or "2160p" => new RenderSize(3840, 2160),
                    _ => new RenderSize(1920, 1080),
                },
            };
        }

        if (Value("--duration-scale") is { } scale)
        {
            options = options with { DurationScale = double.Parse(scale, CultureInfo.InvariantCulture) };
        }

        if (Value("--camera") is { } camera)
        {
            var v = camera.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            options = options with
            {
                CameraOverride = new CameraPose(new System.Numerics.Vector3(v[0], v[1], v[2]), new System.Numerics.Vector3(v[3], v[4], v[5]), v.Length > 6 ? v[6] : 60f),
            };
        }

        return options with
        {
            ResultFile = Value("--result"),
            Language = Value("--lang"),
            ScreenshotFolder = Value("--screenshots"),
            Only = Value("--only")?.ToLowerInvariant(),
            AutoClose = args.Contains("--auto-close", StringComparer.OrdinalIgnoreCase),
            RayTracing = !string.Equals(Value("--raytracing"), "off", StringComparison.OrdinalIgnoreCase),
        };
    }
}
