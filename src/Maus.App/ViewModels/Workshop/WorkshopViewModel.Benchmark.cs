using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Maus.Core;
using Maus.Core.Localization;
using Maus.Core.Workshop.Benchmark;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>Une ligne de test dans le dernier résultat (barre remplie selon les points, repère des 10 000 points).</summary>
public sealed record BenchmarkRow(string Name, string Points, string Detail, double Fill, double Reference);

/// <summary>Une passe de l'historique.</summary>
public sealed record BenchmarkRunRow(string When, string Summary);

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
    private ICommand? _startBenchmark;
    private ICommand? _copyBenchmark;
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
        set => SetProperty(ref _benchmarkApi, value);
    }

    /// <summary>Résolution de calcul : 1080p natif recommandé (toutes les cartes) ; 1440p et 4K pour les grosses cartes.</summary>
    public IReadOnlyList<TestOption<string>> BenchmarkResolutions { get; } =
    [
        new(T("1080p natif (recommandé)"), "1080p"),
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

    public string BenchmarkDuration => BenchmarkScope.Value switch
    {
        "gpu" => T("Durée : environ 8 minutes"),
        "cpu" => T("Durée : environ 2 minutes"),
        _ => T("Durée : environ 10 minutes"),
    };

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

    public string BenchmarkWhen => LastBenchmark is { } last
        ? last.Date.ToLocalTime().ToString("g", Texts.Culture) + " · " + last.Api + " · " + last.RenderResolution + (last.Completed ? "" : T(" (passe arrêtée avant la fin, score partiel)"))
        : "";

    public string BenchmarkGpuName => LastBenchmark?.Gpu ?? "";

    public string BenchmarkCpuName => LastBenchmark is { } last ? last.Cpu + " · " + T("{0} fils de calcul", last.Threads) : "";

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
        BenchmarkStatus = T("Benchmark en cours dans sa propre fenêtre plein écran (environ dix minutes). Échap l'arrête à tout moment.");
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

            using var process = Process.Start(start);
            if (process is null)
            {
                BenchmarkStatus = T("Le benchmark n'a pas pu démarrer.");
                return;
            }

            await process.WaitForExitAsync().ConfigureAwait(true);
            BenchmarkStatus = process.ExitCode == 3
                ? T("La carte graphique a cessé de répondre pendant le benchmark (pilote réinitialisé par Windows). Une carte stable ne devrait jamais le faire : vérifiez sa température, son alimentation et ses réglages d'overclocking.")
                : "";
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
        })
        {
            OnPropertyChanged(name);
        }
    }

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
            t.Device == "gpu"
                ? T("{0} images/s", t.Value.ToString("0.0", culture)) + (t.Low1 is { } low ? " · " + T("1 % les plus lentes : {0}", low.ToString("0.0", culture)) : "")
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
        text.AppendLine(T("Processeur : {0} points ({1})", Points(report.CpuScore), report.Cpu));
        foreach (var test in report.Tests)
        {
            var value = test.Device == "gpu" ? T("{0} images/s", test.Value.ToString("0.0", Texts.Culture)) : test.Value.ToString("0.00", Texts.Culture) + " " + test.Unit;
            text.AppendLine("  • " + BenchmarkNames.Of(test.Id) + " : " + Points(test.Score) + " (" + value + ")");
        }

        text.Append(T("10 000 points = la machine de référence (Core i7-8700K et GeForce RTX 2080 Ti). Deux fois plus de points = deux fois plus rapide."));
        return text.ToString();
    }

    private static string Points(double value) => value > 0 ? value.ToString("N0", CultureInfo.CurrentCulture) : "—";
}

/// <summary>Noms des tests du benchmark, dans la langue choisie.</summary>
internal static class BenchmarkNames
{
    public static string Of(string id) => id switch
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
}
