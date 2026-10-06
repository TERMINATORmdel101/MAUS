using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using Maus.Core.Localization;
using Maus.Core.Workshop.Benchmark;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>
/// Atelier, onglet Tests : benchmark visuel. Lancé dans un processus séparé (MAUS.exe --benchmark) avec sa propre
/// fenêtre plein écran : un plantage du pilote graphique n'emporte pas MAUS. Le bilan est relu dans l'historique.
/// </summary>
public sealed partial class WorkshopViewModel
{
    private bool _isBenchmarkRunning;
    private string? _benchmarkStatus;
    private TestOption<string>? _benchmarkApi;
    private ICommand? _startBenchmark;

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

    public string BenchmarkStatus
    {
        get => _benchmarkStatus ??= DescribeLastBenchmark();
        private set => SetProperty(ref _benchmarkStatus, value);
    }

    public ICommand StartBenchmarkCommand => _startBenchmark ??= new AsyncCommand(RunBenchmarkAsync);

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
            start.ArgumentList.Add("--benchmark");
            start.ArgumentList.Add("--api");
            start.ArgumentList.Add(BenchmarkApi.Value);
            start.ArgumentList.Add("--lang");
            start.ArgumentList.Add(Texts.Language);
            using var process = Process.Start(start);
            if (process is null)
            {
                BenchmarkStatus = T("Le benchmark n'a pas pu démarrer.");
                return;
            }

            await process.WaitForExitAsync().ConfigureAwait(true);
            BenchmarkStatus = process.ExitCode == 3
                ? T("La carte graphique a cessé de répondre pendant le benchmark (pilote réinitialisé par Windows). Une carte stable ne devrait jamais le faire : vérifiez sa température, son alimentation et ses réglages d'overclocking.")
                    + Environment.NewLine + Environment.NewLine + DescribeLastBenchmark()
                : DescribeLastBenchmark();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            BenchmarkStatus = T("Le benchmark n'a pas pu démarrer : {0}", ex.Message);
        }
        finally
        {
            IsBenchmarkRunning = false;
        }
    }

    private static string DescribeLastBenchmark()
    {
        var history = BenchmarkHistoryStore.CreateDefault().Load();
        var last = history.Count > 0 ? history[^1] : null;
        if (last is null)
        {
            return T("Aucun benchmark lancé pour l'instant. Comptez environ dix minutes : 8 minutes de carte graphique, puis 2 à 3 minutes de processeur.");
        }

        var culture = Texts.Culture;
        var text = new StringBuilder();
        text.Append(T("Dernier résultat ({0}) : {1} points", last.Date.ToLocalTime().ToString("g", culture), Points(last.OverallScore)));
        if (!last.Completed)
        {
            text.Append(T(" (passe arrêtée avant la fin, score partiel)"));
        }

        text.AppendLine();
        text.AppendLine(T("Carte graphique : {0} points · Processeur : {1} points · {2}", Points(last.GpuScore), Points(last.CpuScore), last.Api));
        text.AppendLine(last.Gpu + " · " + last.Cpu);
        foreach (var test in last.Tests)
        {
            var value = test.Device == "gpu"
                ? T("{0} images/s", test.Value.ToString("0.0", culture))
                : test.Value.ToString("0.00", culture) + " " + test.Unit;
            text.AppendLine("  • " + BenchmarkNames.Of(test.Id) + " : " + Points(test.Score) + " (" + value + ")");
        }

        var (strongest, weakest) = BenchmarkScoring.Extremes(last.Tests);
        if (strongest is not null && weakest is not null)
        {
            text.AppendLine(T("Point fort : {0} · Point faible : {1}", BenchmarkNames.Of(strongest.Id), BenchmarkNames.Of(weakest.Id)));
        }

        text.Append(T("10 000 points = la machine de référence (Core i7-8700K et GeForce RTX 2080 Ti). Deux fois plus de points = deux fois plus rapide."));
        return text.ToString();

        static string Points(double value) => value > 0 ? value.ToString("N0", CultureInfo.CurrentCulture) : "—";
    }
}

/// <summary>Noms des tests du benchmark, dans la langue choisie.</summary>
internal static class BenchmarkNames
{
    public static string Of(string id) => id switch
    {
        "fractal" => T("Forge fractale (calcul)"),
        "cpu-render" => T("Rendu sur tous les cœurs"),
        "cpu-single" => T("Rendu sur un seul cœur"),
        "cpu-vector" => T("Calcul vectoriel"),
        _ => id,
    };
}
