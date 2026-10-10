using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Maus.Core;
using Maus.Core.Localization;
using Maus.Core.Workshop.Benchmark;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>Une ligne de test dans le dernier résultat (barre remplie selon les points).</summary>
public sealed record BenchmarkRow(string Name, string Points, string Detail, double Fill, double Reference);

/// <summary>Une passe de l'historique.</summary>
public sealed record BenchmarkRunRow(string When, string Summary);

/// <summary>Une ligne de la comparaison : test, vos points, les points de l'autre résultat, écart (+ = l'autre est plus rapide).</summary>
public sealed record BenchmarkCompareRow(string Name, string Mine, string Theirs, string Difference);

/// <summary>
/// Page Benchmark : lancement (processus séparé, MAUS.exe --benchmark, avec sa propre fenêtre plein écran : un plantage
/// du pilote graphique n'emporte pas MAUS), dernier résultat détaillé, historique des passes, copie du résultat.
/// </summary>
public sealed partial class WorkshopViewModel
{
    private const double BarScale = 20000;
    private bool _isBenchmarkRunning;
    private string? _benchmarkStatus;
    private TestOption<string>? _benchmarkApi;
    private TestOption<string>? _benchmarkResolution;
    private TestOption<string>? _benchmarkScope;
    private bool _benchmarkIncludeRayTracing = true;
    private ICommand? _startBenchmark;
    private ICommand? _copyBenchmark;
    private ICommand? _copyBenchmarkImage;
    private ICommand? _openBenchmarkImage;
    private ICommand? _showBenchmarkImage;
    private ICommand? _compareBenchmark;
    private ICommand? _compareWithPrevious;
    private string _benchmarkCompareText = "";
    private string? _benchmarkCompareStatus;
    private BenchmarkReport? _compared;
    private IReadOnlyList<BenchmarkReport>? _benchmarkHistory;

    /// <summary>Interfaces graphiques proposées : DirectX 12 recommandé (le plus moderne), DirectX 11 pour comparer.</summary>
    public IReadOnlyList<TestOption<string>> BenchmarkApis { get; } =
    [
        new(T("DirectX 12 (recommandé)"), "d3d12"),
        new(T("DirectX 11"), "d3d11"),
    ];

    public TestOption<string> BenchmarkApi
    {
        get => _benchmarkApi ?? BenchmarkApis[0];
        set
        {
            if (SetProperty(ref _benchmarkApi, value))
            {
                OnPropertyChanged(nameof(BenchmarkDuration));
            }
        }
    }

    /// <summary>
    /// Test du lancer de rayons (« Galerie des glaces », DirectX 12, cartes compatibles DXR 1.1) : proposé par défaut, son
    /// score est compté à part ; MAUS vérifie la carte et passe le test si elle ne le gère pas.
    /// </summary>
    public bool BenchmarkIncludeRayTracing
    {
        get => _benchmarkIncludeRayTracing;
        set
        {
            if (SetProperty(ref _benchmarkIncludeRayTracing, value))
            {
                OnPropertyChanged(nameof(BenchmarkDuration));
            }
        }
    }

    /// <summary>
    /// Résolution de calcul : 1080p natif recommandé (toutes les cartes) ; 720p léger pour les cartes intégrées et les petits
    /// portables (scènes allégées) ; 1440p et 4K pour les grosses cartes.
    /// </summary>
    public IReadOnlyList<TestOption<string>> BenchmarkResolutions { get; } =
    [
        new(T("1080p natif (recommandé)"), "1080p"),
        new(T("720p léger (cartes intégrées, petits portables)"), "720p"),
        new(T("1440p"), "1440p"),
        new(T("4K (très exigeant)"), "4k"),
    ];

    public TestOption<string> BenchmarkResolution
    {
        get => _benchmarkResolution ?? BenchmarkResolutions[0];
        set => SetProperty(ref _benchmarkResolution, value);
    }

    /// <summary>Tests à lancer : tout (recommandé), la carte graphique seule ou le processeur seul.</summary>
    public IReadOnlyList<TestOption<string>> BenchmarkScopes { get; } =
    [
        new(T("Complet : carte graphique et processeur (recommandé)"), "all"),
        new(T("Carte graphique seule"), "gpu"),
        new(T("Processeur seul"), "cpu"),
    ];

    public TestOption<string> BenchmarkScope
    {
        get => _benchmarkScope ?? BenchmarkScopes[0];
        set
        {
            if (SetProperty(ref _benchmarkScope, value))
            {
                OnPropertyChanged(nameof(BenchmarkDuration));
            }
        }
    }

    /// <summary>Durée annoncée : cinq scènes de 1 min 36 (8 min 30 avec les chargements), 1 min de lancer de rayons, 2 min de processeur.</summary>
    public string BenchmarkDuration
    {
        get
        {
            var rayTracing = BenchmarkIncludeRayTracing && BenchmarkApi.Value == "d3d12" ? 1.1 : 0;
            var minutes = BenchmarkScope.Value switch
            {
                "gpu" => 8.5 + rayTracing,
                "cpu" => 2.1,
                _ => 10.6 + rayTracing,
            };
            return T("Durée : environ {0} minutes", Math.Round(minutes).ToString("0", Texts.Culture));
        }
    }

    public bool IsBenchmarkRunning
    {
        get => _isBenchmarkRunning;
        private set
        {
            if (SetProperty(ref _isBenchmarkRunning, value))
            {
                OnPropertyChanged(nameof(IsNotBenchmarkRunning));
            }
        }
    }

    public bool IsNotBenchmarkRunning => !IsBenchmarkRunning;

    /// <summary>Message du moment (benchmark en cours, erreur, copie), sous le bouton de lancement.</summary>
    public string BenchmarkStatus
    {
        get => _benchmarkStatus ?? "";
        private set => SetProperty(ref _benchmarkStatus, value);
    }

    private IReadOnlyList<BenchmarkReport> VisualRuns => _benchmarkHistory ??= BenchmarkHistoryStore.CreateDefault().Load();

    private BenchmarkReport? LastBenchmark => VisualRuns.Count > 0 ? VisualRuns[^1] : null;

    public bool HasBenchmark => LastBenchmark is not null;

    public bool HasNoBenchmark => LastBenchmark is null;

    public string BenchmarkOverall => Points(LastBenchmark?.OverallScore ?? 0);

    public string BenchmarkGpu => Points(LastBenchmark?.GpuScore ?? 0);

    public string BenchmarkCpu => Points(LastBenchmark?.CpuScore ?? 0);

    public string BenchmarkRayTracing => Points(LastBenchmark?.RayTracingScore ?? 0);

    public bool HasBenchmarkRayTracing => LastBenchmark?.Tests.Any(t => t.Device == "rt") == true;

    /// <summary>Pourquoi le lancer de rayons n'a pas été mesuré lors de la dernière passe (vide s'il l'a été, ou s'il était écarté).</summary>
    public string BenchmarkRayTracingNote => LastBenchmark is { RayTracingNote: { } note } last && !last.Tests.Any(t => t.Device == "rt")
        ? T("Lancer de rayons non mesuré : {0}", note)
        : "";

    public bool HasBenchmarkRayTracingNote => BenchmarkRayTracingNote.Length > 0;

    public IReadOnlyList<BenchmarkRow> BenchmarkRayTracingTests => Rows("rt");

    public string BenchmarkRayTracingSensors => SensorLine("rt");

    public bool HasBenchmarkRayTracingSensors => BenchmarkRayTracingSensors.Length > 0;

    public string BenchmarkWhen => LastBenchmark is { } last
        ? last.Date.ToLocalTime().ToString("g", Texts.Culture) + " · " + last.Api + " · " + last.RenderResolution + (last.Completed ? "" : T(" (passe arrêtée avant la fin, score partiel)"))
        : "";

    public string BenchmarkGpuName => LastBenchmark?.Gpu ?? "";

    public string BenchmarkCpuName => LastBenchmark is { } last ? last.Cpu + " · " + T("{0} fils de calcul", last.Threads) : "";

    /// <summary>Température, fréquence et puissance relevées pendant les tests de la carte graphique (vide si illisibles).</summary>
    public string BenchmarkGpuSensors => SensorLine("gpu");

    public bool HasBenchmarkGpuSensors => BenchmarkGpuSensors.Length > 0;

    public string BenchmarkCpuSensors => SensorLine("cpu");

    public bool HasBenchmarkCpuSensors => BenchmarkCpuSensors.Length > 0;

    /// <summary>Ralentissement signalé par la carte elle-même (chaleur, protection matérielle), avec le conseil.</summary>
    public string BenchmarkGpuWarning => BenchmarkSensorText.Warning(DeviceSensors("gpu"), "gpu") ?? "";

    public bool HasBenchmarkGpuWarning => BenchmarkGpuWarning.Length > 0;

    /// <summary>Processeur arrivé à sa limite de température (lue dans la puce), avec le conseil.</summary>
    public string BenchmarkCpuWarning => BenchmarkSensorText.Warning(DeviceSensors("cpu"), "cpu") ?? "";

    public bool HasBenchmarkCpuWarning => BenchmarkCpuWarning.Length > 0;

    public string BenchmarkVerdict
    {
        get
        {
            if (LastBenchmark is not { } last)
            {
                return "";
            }

            var (strongest, weakest) = BenchmarkScoring.Extremes(last.Tests);
            return strongest is not null && weakest is not null
                ? T("Point fort : {0} · Point faible : {1}", BenchmarkNames.Of(strongest.Id), BenchmarkNames.Of(weakest.Id))
                : T("Machine équilibrée : aucun test ne s'écarte de plus de 10 % des autres.");
        }
    }

    public IReadOnlyList<BenchmarkRow> BenchmarkGpuTests => Rows("gpu");

    public IReadOnlyList<BenchmarkRow> BenchmarkCpuTests => Rows("cpu");

    /// <summary>Scores combinés des passes complètes, pour la courbe de l'historique.</summary>
    public IReadOnlyList<double> BenchmarkTrend => [.. VisualRuns.Where(r => r.OverallScore > 0).TakeLast(20).Select(r => r.OverallScore)];

    public IReadOnlyList<BenchmarkRunRow> BenchmarkRuns => [.. VisualRuns.Reverse().Take(12).Select(r => new BenchmarkRunRow(
        r.Date.ToLocalTime().ToString("g", Texts.Culture),
        T("{0} · {1} · {2} : {3} points (carte graphique {4}, processeur {5})", r.Api, r.RenderResolution, r.Gpu, Points(r.OverallScore), Points(r.GpuScore), Points(r.CpuScore))))];

    public ICommand StartBenchmarkCommand => _startBenchmark ??= new AsyncCommand(RunBenchmarkAsync);

    /// <summary>Texte collé par l'utilisateur (le message d'un ami), qui contient un code MAUS-BENCH-1.</summary>
    public string BenchmarkCompareText
    {
        get => _benchmarkCompareText;
        set => SetProperty(ref _benchmarkCompareText, value ?? "");
    }

    public string BenchmarkCompareStatus
    {
        get => _benchmarkCompareStatus ?? "";
        private set => SetProperty(ref _benchmarkCompareStatus, value);
    }

    public bool HasBenchmarkComparison => _compared is not null && LastBenchmark is not null;

    public string BenchmarkCompareTitle => _compared is { } other
        ? T("Comparé à : {0} · {1} ({2}, {3}, {4})", other.Gpu, other.Cpu, other.RenderResolution, other.Api, other.Date.ToLocalTime().ToString("d", Texts.Culture))
        : "";

    /// <summary>Les points ne se comparent qu'à résolution égale : avertissement sinon.</summary>
    public string BenchmarkCompareWarning => _compared is { } other && LastBenchmark is { } mine && !BenchmarkComparison.Comparable(mine, other)
        ? T("Attention : résolutions différentes ({0} contre {1}), les points ne se comparent pas vraiment.", mine.RenderResolution, other.RenderResolution)
        : "";

    public bool HasBenchmarkCompareWarning => BenchmarkCompareWarning.Length > 0;

    public IReadOnlyList<BenchmarkCompareRow> BenchmarkCompareRows => _compared is { } other && LastBenchmark is { } mine
        ? [.. BenchmarkComparison.Compare(mine, other).Select(r => new BenchmarkCompareRow(
            r.Id switch
            {
                "overall" => T("Score combiné"),
                "gpu" => T("Carte graphique"),
                "cpu" => T("Processeur"),
                "rt" => T("Lancer de rayons"),
                _ => BenchmarkNames.Of(r.Id),
            },
            Points(r.Mine),
            Points(r.Theirs),
            r.Difference is { } d ? (d > 0 ? "+" : "") + d.ToString("0.0", Texts.Culture) + " %" : "—"))]
        : [];

    public ICommand CompareBenchmarkCommand => _compareBenchmark ??= new AsyncCommand(() =>
    {
        if (BenchmarkShareCode.Decode(BenchmarkCompareText) is { } other)
        {
            ShowComparison(other, "");
        }
        else
        {
            BenchmarkCompareStatus = T("Aucun code MAUS-BENCH-1 valable dans ce texte : demandez à votre ami le texte de son bouton « Copier le résultat ».");
        }

        return Task.CompletedTask;
    });

    public ICommand CompareWithPreviousCommand => _compareWithPrevious ??= new AsyncCommand(() =>
    {
        if (VisualRuns.Count >= 2)
        {
            ShowComparison(VisualRuns[^2], "");
        }
        else
        {
            BenchmarkCompareStatus = T("Il faut au moins deux passes dans l'historique pour comparer.");
        }

        return Task.CompletedTask;
    });

    /// <summary>Image du résultat de la dernière passe (fichier PNG enregistré par le benchmark), si elle existe encore.</summary>
    private string? BenchmarkImagePath => LastBenchmark?.Image is { } path && File.Exists(path) ? path : null;

    public bool HasBenchmarkImage => BenchmarkImagePath is not null;

    /// <summary>Aperçu de l'image (lue en mémoire : le fichier n'est pas verrouillé).</summary>
    public ImageSource? BenchmarkImageSource => BenchmarkImagePath is { } path ? LoadImage(path) : null;

    public ICommand CopyBenchmarkImageCommand => _copyBenchmarkImage ??= new AsyncCommand(() =>
    {
        if (BenchmarkImagePath is { } path && LoadImage(path) is { } image)
        {
            Clipboard.SetImage(image);
            BenchmarkStatus = T("Image copiée : collez-la où vous voulez (Discord, forum, message).");
        }

        return Task.CompletedTask;
    });

    public ICommand OpenBenchmarkImageCommand => _openBenchmarkImage ??= new AsyncCommand(() =>
    {
        if (BenchmarkImagePath is { } path)
        {
            ShellLauncher.OpenFile(path);
        }

        return Task.CompletedTask;
    });

    public ICommand ShowBenchmarkImageCommand => _showBenchmarkImage ??= new AsyncCommand(() =>
    {
        if (BenchmarkImagePath is { } path)
        {
            ShellLauncher.ShowInFolder(path);
        }

        return Task.CompletedTask;
    });

    /// <summary>Copie un résumé du dernier résultat (à coller sur un forum) : aucune donnée personnelle, seulement le matériel.</summary>
    public ICommand CopyBenchmarkCommand => _copyBenchmark ??= new AsyncCommand(() =>
    {
        if (LastBenchmark is { } last)
        {
            Clipboard.SetText(Summary(last));
            BenchmarkStatus = T("Résultat copié : collez-le où vous voulez (forum, message).");
        }

        return Task.CompletedTask;
    });

    private async Task RunBenchmarkAsync()
    {
        if (IsBenchmarkRunning || Environment.ProcessPath is not { } exe)
        {
            return;
        }

        IsBenchmarkRunning = true;
        BenchmarkStatus = T("Benchmark en cours dans sa propre fenêtre plein écran. Échap l'arrête à tout moment.");
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var argument in new[] { "--benchmark", "--api", BenchmarkApi.Value, "--resolution", BenchmarkResolution.Value, "--lang", Texts.Language })
            {
                start.ArgumentList.Add(argument);
            }

            if (BenchmarkScope.Value != "all")
            {
                start.ArgumentList.Add("--only");
                start.ArgumentList.Add(BenchmarkScope.Value);
            }

            if (!BenchmarkIncludeRayTracing)
            {
                start.ArgumentList.Add("--raytracing");
                start.ArgumentList.Add("off");
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                BenchmarkStatus = T("Le benchmark n'a pas pu démarrer.");
                return;
            }

            await process.WaitForExitAsync().ConfigureAwait(true);
            BenchmarkStatus = process.ExitCode switch
            {
                0 => "",
                3 => T("La carte graphique a cessé de répondre pendant le benchmark (pilote réinitialisé par Windows). Une carte stable ne devrait jamais le faire : vérifiez sa température, son alimentation et ses réglages d'overclocking."),
                4 => T("Le benchmark s'est arrêté : Direct3D ou Windows a refusé une opération. Mettez à jour le pilote de la carte graphique, ou essayez l'autre interface (DirectX 11 / DirectX 12)."),
                var code => T("Le benchmark s'est arrêté sur une erreur (code {0}). Le détail est dans l'Observateur d'événements de Windows, journal Application, source « .NET Runtime ».", code),
            };
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            BenchmarkStatus = T("Le benchmark n'a pas pu démarrer : {0}", ex.Message);
        }
        finally
        {
            IsBenchmarkRunning = false;
            RefreshBenchmark();
        }
    }

    /// <summary>Relit l'historique et met à jour toute la page.</summary>
    private void RefreshBenchmark()
    {
        _benchmarkHistory = null;
        foreach (var name in new[]
        {
            nameof(HasBenchmark), nameof(HasNoBenchmark), nameof(BenchmarkOverall), nameof(BenchmarkGpu), nameof(BenchmarkCpu),
            nameof(BenchmarkWhen), nameof(BenchmarkGpuName), nameof(BenchmarkCpuName), nameof(BenchmarkVerdict),
            nameof(BenchmarkGpuTests), nameof(BenchmarkCpuTests), nameof(BenchmarkTrend), nameof(BenchmarkRuns),
            nameof(BenchmarkGpuSensors), nameof(HasBenchmarkGpuSensors), nameof(BenchmarkCpuSensors), nameof(HasBenchmarkCpuSensors),
            nameof(BenchmarkGpuWarning), nameof(HasBenchmarkGpuWarning), nameof(BenchmarkCpuWarning), nameof(HasBenchmarkCpuWarning),
            nameof(HasBenchmarkImage), nameof(BenchmarkImageSource),
            nameof(BenchmarkRayTracing), nameof(HasBenchmarkRayTracing), nameof(BenchmarkRayTracingNote), nameof(HasBenchmarkRayTracingNote),
            nameof(BenchmarkRayTracingTests), nameof(BenchmarkRayTracingSensors), nameof(HasBenchmarkRayTracingSensors),
            nameof(HasBenchmarkComparison), nameof(BenchmarkCompareTitle), nameof(BenchmarkCompareWarning), nameof(HasBenchmarkCompareWarning), nameof(BenchmarkCompareRows),
        })
        {
            OnPropertyChanged(name);
        }
    }

    private BenchmarkSensorSummary? DeviceSensors(string device) =>
        LastBenchmark is { } last ? BenchmarkSensorSummary.Merge(last.Tests.Where(t => t.Device == device).Select(t => t.Sensors)) : null;

    private string SensorLine(string device) =>
        BenchmarkSensorText.Describe(DeviceSensors(device), device) is { } text ? T("Pendant la mesure : {0}", text) : "";

    private List<BenchmarkRow> Rows(string device)
    {
        if (LastBenchmark is not { } last)
        {
            return [];
        }

        var culture = Texts.Culture;
        return [.. last.Tests.Where(t => t.Device == device).Select(t => new BenchmarkRow(
            BenchmarkNames.Of(t.Id),
            Points(t.Score),
            t.Device != "cpu"
                ? T("{0} images/s", t.Value.ToString("0.0", culture))
                : t.Value.ToString("0.00", culture) + " " + t.Unit,
            Math.Min(1, t.Score / BarScale),
            BenchmarkScoring.ReferencePoints / BarScale))];
    }

    private static string Summary(BenchmarkReport report)
    {
        var text = new StringBuilder();
        text.AppendLine(T("MAUS Benchmark {0} : {1}", AppVersion.Display, report.Date.ToLocalTime().ToString("g", Texts.Culture)));
        text.AppendLine(T("Score combiné : {0} ({1}, {2})", Points(report.OverallScore), report.RenderResolution, report.Api));
        text.AppendLine(T("Carte graphique : {0} points ({1})", Points(report.GpuScore), report.Gpu));
        AppendSensors(text, report, "gpu");
        text.AppendLine(T("Processeur : {0} points ({1})", Points(report.CpuScore), report.Cpu));
        AppendSensors(text, report, "cpu");
        if (report.RayTracingScore > 0)
        {
            text.AppendLine(T("Lancer de rayons (score à part) : {0} points", Points(report.RayTracingScore)));
        }

        foreach (var test in report.Tests)
        {
            var value = test.Device != "cpu" ? T("{0} images/s", test.Value.ToString("0.0", Texts.Culture)) : test.Value.ToString("0.00", Texts.Culture) + " " + test.Unit;
            text.AppendLine("  • " + BenchmarkNames.Of(test.Id) + " : " + Points(test.Score) + " (" + value + ")");
        }

        text.AppendLine(T("Deux fois plus de points = deux fois plus rapide (à résolution égale)."));
        text.Append(T("Code à coller dans MAUS (page Benchmark) pour comparer : {0}", BenchmarkShareCode.Encode(report)));
        return text.ToString();
    }

    private void ShowComparison(BenchmarkReport other, string status)
    {
        _compared = other;
        BenchmarkCompareStatus = status;
        foreach (var name in new[] { nameof(HasBenchmarkComparison), nameof(BenchmarkCompareTitle), nameof(BenchmarkCompareWarning), nameof(HasBenchmarkCompareWarning), nameof(BenchmarkCompareRows) })
        {
            OnPropertyChanged(name);
        }
    }

    private static BitmapImage? LoadImage(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private static void AppendSensors(StringBuilder text, BenchmarkReport report, string device)
    {
        var summary = BenchmarkSensorSummary.Merge(report.Tests.Where(t => t.Device == device).Select(t => t.Sensors));
        if (BenchmarkSensorText.Describe(summary, device) is { } line)
        {
            text.AppendLine("  " + T("Pendant la mesure : {0}", line));
        }
    }

    private static string Points(double value) => value > 0 ? value.ToString("N0", CultureInfo.CurrentCulture) : "—";
}

/// <summary>Noms des tests du benchmark, dans la langue choisie.</summary>
internal static class BenchmarkNames
{
    public static string Of(string id) => id switch
    {
        "fractal" => T("Forge fractale (calcul)"),
        "materials" => T("Cabinet de curiosités (textures)"),
        "galaxy" => T("Collision galactique (bande passante)"),
        "ring" => T("Anneau de la géante (géométrie)"),
        "battle" => T("Champ de bataille (effets)"),
        "raytracing" => T("Galerie des glaces (lancer de rayons)"),
        "cpu-render" => T("Rendu sur tous les cœurs"),
        "cpu-single" => T("Rendu sur un seul cœur"),
        "cpu-vector" => T("Calcul vectoriel"),
        _ => id,
    };
}
