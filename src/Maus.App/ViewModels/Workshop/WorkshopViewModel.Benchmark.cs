using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Maus.Core;
using Maus.Core.Diagnostics;
using Maus.Core.Localization;
using Maus.Core.Workshop.Benchmark;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>Une ligne de test dans le dernier résultat (barre remplie selon les points).</summary>
public sealed record BenchmarkValue(string Label, string Value);

/// <summary>Un test dans le détail d'une carte de score : ses points, sa barre et tout ce qui a été mesuré.</summary>
public sealed record BenchmarkDetailRow(string Name, string Points, IReadOnlyList<BenchmarkValue> Values, double Fill, string Mark, bool IsStrong)
{
    public override string ToString() => Name;
}

/// <summary>Ce qu'affiche le détail d'une carte de score : « all » (score combiné), « gpu », « cpu » ou « rt ».</summary>
public sealed record BenchmarkDetail(string Kind, string Title, string Subtitle, string Score, IReadOnlyList<BenchmarkValue> Sensors, string Health, string Warning, IReadOnlyList<BenchmarkDetailRow> Rows, string Note, IReadOnlyList<double> Trend);

/// <summary>Une ligne du classement personnel : rang, date, points, écart avec la meilleure passe, et comparaison avec la dernière.</summary>
public sealed record BenchmarkRankRow(string Rank, string When, string Points, string Gap, string Settings, bool CanCompare, ICommand Compare)
{
    public override string ToString() => When;
}

/// <summary>
/// Une passe de l'historique ; <paramref name="Marker"/> dit ce qui a changé depuis la passe affichée juste en dessous (vide sinon),
/// <paramref name="EditionHeading"/> sépare les passes d'une autre édition du benchmark, à ne pas comparer (vide sinon).
/// </summary>
public sealed record BenchmarkRunRow(string When, string Summary, string Marker = "", string EditionHeading = "")
{
    public override string ToString() => When;

    public bool HasMarker => Marker.Length > 0;

    public bool HasEditionHeading => EditionHeading.Length > 0;
}

/// <summary>Une ligne de la comparaison : test, vos points, les points de l'autre résultat, écart (+ = l'autre est plus rapide).</summary>
public sealed record BenchmarkCompareRow(string Name, string Mine, string Theirs, string Difference)
{
    public override string ToString() => Name;
}

/// <summary>
/// Page Benchmark : lancement (processus séparé, MAUS.exe --benchmark, avec sa propre fenêtre plein écran : un plantage
/// du pilote graphique n'emporte pas MAUS), dernier résultat détaillé, historique des passes, copie du résultat.
/// </summary>
public sealed partial class WorkshopViewModel
{
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

    /// <summary>
    /// La première lecture se fait à côté : le fichier et l'image de la dernière passe ne ralentissent plus l'ouverture de MAUS.
    /// Les suivantes (après une passe) sont immédiates.
    /// </summary>
    private IReadOnlyList<BenchmarkReport> VisualRuns
    {
        get
        {
            if (_benchmarkHistory is { } known)
            {
                return known;
            }

            if (_benchmarkFirstRead == FirstRead.Done)
            {
                return _benchmarkHistory = BenchmarkHistoryStore.CreateDefault().Load();
            }

            if (_benchmarkFirstRead == FirstRead.NotStarted)
            {
                _benchmarkFirstRead = FirstRead.Reading;
                ReadBenchmarkHistoryAsync().Forget("benchmark : première lecture de l'historique");
            }

            return [];
        }
    }

    private enum FirstRead
    {
        NotStarted,
        Reading,
        Done,
    }

    private FirstRead _benchmarkFirstRead;

    private async Task ReadBenchmarkHistoryAsync()
    {
        try
        {
            _benchmarkHistory = await Task.Run(() => BenchmarkHistoryStore.CreateDefault().Load());
        }
        finally
        {
            _benchmarkFirstRead = FirstRead.Done;
        }

        NotifyBenchmark();
    }

    private BenchmarkReport? LastBenchmark => VisualRuns.Count > 0 ? VisualRuns[^1] : null;

    public bool HasBenchmark => LastBenchmark is not null;

    public bool HasNoBenchmark => LastBenchmark is null;

    public string BenchmarkOverall => BenchmarkTexts.Points(LastBenchmark?.OverallScore ?? 0);

    public string BenchmarkGpu => BenchmarkTexts.Points(LastBenchmark?.GpuScore ?? 0);

    public string BenchmarkCpu => BenchmarkTexts.Points(LastBenchmark?.CpuScore ?? 0);

    public string BenchmarkRayTracing => BenchmarkTexts.Points(LastBenchmark?.RayTracingScore ?? 0);

    public bool HasBenchmarkRayTracing => LastBenchmark?.Tests.Any(t => t.Device == "rt") == true;

    /// <summary>Pourquoi le lancer de rayons n'a pas été mesuré lors de la dernière passe (vide s'il l'a été, ou s'il était écarté).</summary>
    public string BenchmarkRayTracingNote => LastBenchmark is { RayTracingNote: { } note } last && !last.Tests.Any(t => t.Device == "rt")
        ? T("Lancer de rayons non mesuré : {0}", note)
        : "";

    public bool HasBenchmarkRayTracingNote => BenchmarkRayTracingNote.Length > 0;

    public string BenchmarkWhen => LastBenchmark is { } last
        ? last.Date.ToLocalTime().ToString("g", Texts.Culture) + " · " + last.Api + " · " + last.RenderResolution + (last.Completed ? "" : T(" (mesure arrêtée avant la fin, score partiel)"))
        : "";

    /// <summary>Le dernier résultat vient d'une version précédente du benchmark : le dire tout de suite, sous son titre.</summary>
    public string BenchmarkOldEdition => LastBenchmark is { } last ? BenchmarkTexts.OldEditionWarning(last) : "";

    public bool HasBenchmarkOldEdition => BenchmarkOldEdition.Length > 0;

    public string BenchmarkGpuName => LastBenchmark?.Gpu ?? "";

    public string BenchmarkCpuName => LastBenchmark is { } last ? last.Cpu + " · " + T("{0} threads", last.Threads) : "";

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
                ? T("Le plus rapide : {0} · Le moins rapide : {1}", BenchmarkTexts.Name(strongest.Id), BenchmarkTexts.Name(weakest.Id))
                : last.Tests.Count(t => t.Score > 0) >= 2 ? T("Machine équilibrée : aucun test ne s'écarte de plus de 10 % des autres.") : "";
        }
    }

    /// <summary>Détail d'une carte de score, ou <c>null</c> s'il n'y a pas encore de résultat.</summary>
    public BenchmarkDetail? BenchmarkDetailOf(string kind)
    {
        if (LastBenchmark is not { } last)
        {
            return null;
        }

        return kind switch
        {
            "gpu" => DeviceDetail(last, kind, T("Carte graphique"), last.Gpu, BenchmarkTexts.Points(last.GpuScore)),
            "cpu" => DeviceDetail(last, kind, T("Processeur"), BenchmarkCpuName, BenchmarkTexts.Points(last.CpuScore)),
            "rt" => DeviceDetail(last, kind, T("Lancer de rayons (score à part)"), last.Gpu, BenchmarkTexts.Points(last.RayTracingScore)),
            _ => new(
                "all",
                T("Score combiné"),
                BenchmarkWhen,
                BenchmarkTexts.Points(last.OverallScore),
                [],
                "",
                "",
                CombinedRows(last),
                BenchmarkVerdict + " " + T("Le score combiné compte la carte graphique pour {0} % et le processeur pour {1} %.", BenchmarkScoring.GpuWeight * 100, BenchmarkScoring.CpuWeight * 100) + " " + BenchmarkTexts.EditionNote(last),
                BenchmarkTrend),
        };
    }

    private BenchmarkDetail DeviceDetail(BenchmarkReport last, string kind, string title, string subtitle, string score)
    {
        var sensors = DeviceSensors(kind);
        var warning = BenchmarkSensorText.Warning(sensors, kind) ?? "";
        return new(
            kind,
            title,
            subtitle,
            score,
            SensorValues(sensors, kind, withLimit: true),
            warning.Length > 0 ? "" : Health(sensors, kind),
            warning,
            TestRows(last, kind),
            string.Join(" ", new[] { Spread(last, kind), HeavyMoments(last, kind), T("Deux fois plus de points = deux fois plus rapide (à résolution égale).") }.Where(s => s.Length > 0)),
            []);
    }

    /// <summary>Scores combinés des passes complètes, pour la courbe de l'historique.</summary>
    /// <summary>Évolution du score combiné : seulement les passes complètes comparables à la dernière (même édition, même résolution).</summary>
    public IReadOnlyList<double> BenchmarkTrend => [.. TrendRuns.Select(r => r.OverallScore)];

    private List<BenchmarkReport> TrendRuns => LastBenchmark is not { } latest
        ? []
        : [.. VisualRuns.Where(r => r.OverallScore > 0 && r.Completed && BenchmarkComparison.Comparable(latest, r)).TakeLast(20)];

    /// <summary>Les points de la courbe précédés d'un changement de pilote ou de build de Windows (trait vertical entre les deux points).</summary>
    public IReadOnlyList<int> BenchmarkTrendMarks
    {
        get
        {
            var runs = TrendRuns;
            return [.. Enumerable.Range(1, Math.Max(0, runs.Count - 1)).Where(i => BenchmarkMarkers.Between(runs[i - 1], runs[i]) is not null)];
        }
    }

    /// <summary>Les repères de la courbe, avec leurs dates : elle remonte plus loin que la liste des passes. <c>null</c> s'il n'y en a pas (pas de bulle).</summary>
    public string? BenchmarkTrendMarksText
    {
        get
        {
            var runs = TrendRuns;
            var lines = BenchmarkTrendMarks
                .Select(i => T("Du {0} au {1} : {2}", runs[i - 1].Date.ToLocalTime().ToString("d", Texts.Culture), runs[i].Date.ToLocalTime().ToString("d", Texts.Culture), BenchmarkMarkers.Between(runs[i - 1], runs[i])!.Text))
                .ToList();
            return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : null;
        }
    }

    public IReadOnlyList<BenchmarkRunRow> BenchmarkRuns => RunRows(VisualRuns);

    /// <summary>Le rappel qui accompagne les repères n'apparaît qu'avec eux.</summary>
    public bool HasBenchmarkMarkers => BenchmarkTrendMarks.Count > 0 || BenchmarkRuns.Any(r => r.HasMarker);

    /// <summary>
    /// Les 12 dernières passes, de la plus récente à la plus ancienne. Le repère va entre deux lignes affichées : la plus ancienne
    /// de la liste n'en a pas, puisque l'autre passe de la phrase ne serait pas à l'écran.
    /// </summary>
    internal static List<BenchmarkRunRow> RunRows(IReadOnlyList<BenchmarkReport> runs)
    {
        var shown = runs.TakeLast(12).ToList();
        var rows = shown.Select((r, i) => new BenchmarkRunRow(
            r.Date.ToLocalTime().ToString("g", Texts.Culture),
            T("{0} · {1} · {2} : {3} points (carte graphique {4}, processeur {5})", r.Api, r.RenderResolution, r.Gpu, BenchmarkTexts.Points(r.OverallScore), BenchmarkTexts.Points(r.GpuScore), BenchmarkTexts.Points(r.CpuScore)),
            i > 0 ? BenchmarkMarkers.Between(shown[i - 1], r)?.Text ?? "" : "",
            EditionHeading(i < shown.Count - 1 ? shown[i + 1] : null, r))).ToList();
        rows.Reverse();
        return rows;
    }

    /// <summary>Titre posé au-dessus d'une passe : la liste commence par une édition ancienne, ou l'édition change par rapport à la ligne du dessus.</summary>
    private static string EditionHeading(BenchmarkReport? above, BenchmarkReport run)
    {
        var edition = BenchmarkScoring.EditionOf(run);
        var starts = above is null ? edition < BenchmarkScoring.CurrentEdition : BenchmarkScoring.EditionOf(above) != edition;
        return starts ? T("Passes d'une autre version du benchmark : leurs points ne se comparent pas aux autres.") : "";
    }

    /// <summary>Mes passes de la meilleure à la moins bonne : comparer deux réglages, sans avoir besoin d'un ami.</summary>
    public IReadOnlyList<BenchmarkRankRow> BenchmarkRanking
    {
        get
        {
            var ranking = BenchmarkComparison.Ranking(VisualRuns);
            if (ranking.Count == 0)
            {
                return [];
            }

            var best = ranking[0].OverallScore;
            var latest = VisualRuns.Last(r => r.Completed && r.OverallScore > 0);
            return [.. ranking.Take(10).Select((run, index) => new BenchmarkRankRow(
                (index + 1).ToString(Texts.Culture),
                run.Date.ToLocalTime().ToString("g", Texts.Culture),
                BenchmarkTexts.Points(run.OverallScore),
                index == 0 ? T("meilleure") : T("{0:0.0} % de moins", (best - run.OverallScore) / best * 100),
                T("{0} · {1}", run.Api, run.RenderResolution) + (ReferenceEquals(run, latest) ? " · " + T("dernière mesure") : string.Empty),
                !ReferenceEquals(run, latest),
                new AsyncCommand(() =>
                {
                    ShowComparison(run, "");
                })))];
        }
    }

    public bool HasBenchmarkRanking => BenchmarkRanking.Count >= 2;

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

    /// <summary>Tout ce qui empêche ou limite la comparaison (édition, résolution, interface, passe interrompue, tests).</summary>
    public string BenchmarkCompareWarning => _compared is { } other && LastBenchmark is { } mine
        ? string.Join(" ", BenchmarkComparison.Differences(mine, other).Select(n => T("Attention :") + " " + n))
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
                _ => BenchmarkTexts.Name(r.Id),
            },
            BenchmarkTexts.Points(r.Mine),
            BenchmarkTexts.Points(r.Theirs),
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
    });

    /// <summary>Image du résultat de la dernière passe (fichier PNG enregistré par le benchmark), si elle existe encore.</summary>
    private string? BenchmarkImagePath =>
        LastBenchmark?.Image is { } path && BenchmarkHistoryStore.CreateDefault().IsOwnImage(path) && File.Exists(path) ? path : null;

    public bool HasBenchmarkImage => BenchmarkImagePath is not null;

    /// <summary>Aperçu de l'image (lue en mémoire : le fichier n'est pas verrouillé).</summary>
    public ImageSource? BenchmarkImageSource => BenchmarkImagePath is { } path ? LoadImage(path) : null;

    /// <summary>L'image en taille réelle, seulement quand on en a besoin : la vue en grand et la copie à partager.</summary>
    public ImageSource? BenchmarkFullImage() => BenchmarkImagePath is { } path ? LoadImage(path, null) : null;

    public ICommand CopyBenchmarkImageCommand => _copyBenchmarkImage ??= new AsyncCommand(() =>
    {
        if (BenchmarkImagePath is { } path && LoadImage(path, null) is { } image)
        {
            BenchmarkStatus = ClipboardText.TryCopyImage(image)
                ? T("Image copiée : collez-la où vous voulez (Discord, forum, message).")
                : ClipboardText.Busy;
        }
    });

    public ICommand OpenBenchmarkImageCommand => _openBenchmarkImage ??= new AsyncCommand(() =>
    {
        if (BenchmarkImagePath is { } path)
        {
            ShellLauncher.OpenFile(path);
        }
    });

    public ICommand ShowBenchmarkImageCommand => _showBenchmarkImage ??= new AsyncCommand(() =>
    {
        if (BenchmarkImagePath is { } path)
        {
            ShellLauncher.ShowInFolder(path);
        }
    });

    /// <summary>Copie un résumé du dernier résultat (à coller sur un forum) : aucune donnée personnelle, seulement le matériel.</summary>
    public ICommand CopyBenchmarkCommand => _copyBenchmark ??= new AsyncCommand(() =>
    {
        if (LastBenchmark is { } last)
        {
            // Seulement le matériel et les mesures : rien à masquer, et un masque pourrait abîmer le code de comparaison.
            BenchmarkStatus = ClipboardText.TryCopy(Summary(last), mask: false)
                ? T("Résultat copié : collez-le où vous voulez (forum, message).")
                : ClipboardText.Busy;
        }
    });

    private async Task RunBenchmarkAsync()
    {
        if (IsBenchmarkRunning)
        {
            return;
        }

        if (Busy)
        {
            BenchmarkStatus = T("Un test est en cours : arrêtez-le d'abord.");
            return;
        }

        IsBenchmarkRunning = true;
        var startedAt = DateTimeOffset.Now;
        BenchmarkStatus = T("Benchmark en cours dans sa propre fenêtre plein écran. Échap l'arrête à tout moment.");
        try
        {
            // Avec PawnIO, la température du processeur (arrêt de sécurité) se lit avec les droits administrateur. En mode limité,
            // le benchmark part sans cette mesure, sans demande d'autorisation (seul le bouton « Toutes les fonctionnalités » la
            // déclenche, demande du porteur du 29/10/2026).
            if (!Maus.Core.Workshop.PawnIo.State(new Maus.Core.Platform.WindowsRegistryReader()).Installed)
            {
                BenchmarkStatus = T("Benchmark lancé sans le pilote PawnIO : la température du processeur n'est pas suivie. Échap l'arrête à tout moment.");
            }
            else if (EngineHost.OnDemand is { IsElevatedRunning: false })
            {
                BenchmarkStatus = T("Benchmark lancé en mode limité : la température du processeur n'est pas suivie (elle demande toutes les fonctionnalités). Échap l'arrête à tout moment.");
            }

            // Lancé par le moteur, qui vérifie les réglages : il lit aussi la température du processeur pour le rapport. Avec ses
            // droits, le benchmark n'écrit rien dans le profil : il rend sa passe, que cette fenêtre garde elle-même, sans droits.
            var outcome = await EngineHost.Current.RunBenchmarkAsync(
                new Maus.Core.Engine.Broker.BenchmarkLaunch(BenchmarkApi.Value, BenchmarkResolution.Value, BenchmarkScope.Value, BenchmarkIncludeRayTracing, Texts.Language),
                CancellationToken.None);
            var notKept = await Task.Run(() => BenchmarkOutput.Keep(outcome, BenchmarkHistoryStore.CreateDefault(), Environment.IsPrivilegedProcess));
            var ended = outcome.ExitCode switch
            {
                0 => "",
                3 => T("La carte graphique a cessé de répondre pendant le benchmark (pilote réinitialisé par Windows). Une carte stable ne devrait jamais le faire : vérifiez sa température, son alimentation et ses réglages d'overclocking."),
                5 => SafetyStopText(outcome.Report ?? ReloadedLastRun(), startedAt),
                4 => T("Le benchmark s'est arrêté : Direct3D ou Windows a refusé une opération. Mettez à jour le pilote de la carte graphique, ou essayez l'autre interface (DirectX 11 / DirectX 12)."),
                var code => T("Le benchmark s'est arrêté sur une erreur (code {0}). Le détail est dans l'Observateur d'événements de Windows, journal Application, source « .NET Runtime ».", code),
            };
            BenchmarkStatus = notKept is null ? ended : ended.Length == 0 ? notKept : ended + " " + notKept;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or Maus.Core.Engine.Broker.EngineException)
        {
            BenchmarkStatus = T("Le benchmark n'a pas pu démarrer : {0}", Maus.Core.Diagnostics.ErrorText.For(ex));
        }
        finally
        {
            IsBenchmarkRunning = false;
            RefreshBenchmark();
        }
    }

    private BenchmarkReport? ReloadedLastRun()
    {
        _benchmarkHistory = null;
        return LastBenchmark;
    }

    /// <summary>
    /// Pourquoi le benchmark s'est arrêté par sécurité : la raison écrite avec cette passe (température trop haute, ou température
    /// qui n'est plus lue) ; à défaut (historique illisible, par exemple), une phrase qui couvre les deux cas.
    /// </summary>
    internal static string SafetyStopText(BenchmarkReport? last, DateTimeOffset startedAt) =>
        last is { Completed: false, Error.Length: > 0 } && last.Date >= startedAt
            ? last.Error
            : T("Benchmark arrêté par sécurité : la carte graphique ou le processeur a atteint sa limite de température, ou cette température n'est plus lue. Vérifiez le refroidissement (ventilation du boîtier, poussière) avant de recommencer.");

    /// <summary>Relit l'historique et met à jour toute la page.</summary>
    private void RefreshBenchmark()
    {
        _benchmarkHistory = null;
        NotifyBenchmark();
    }

    /// <summary>Pour les tests : montre cet historique sans lire le profil de l'utilisateur.</summary>
    internal void UseBenchmarkHistory(IReadOnlyList<BenchmarkReport> runs)
    {
        _benchmarkHistory = runs;
        NotifyBenchmark();
    }

    private void NotifyBenchmark()
    {
        foreach (var name in new[]
        {
            nameof(HasBenchmark), nameof(HasNoBenchmark), nameof(BenchmarkOverall), nameof(BenchmarkGpu), nameof(BenchmarkCpu),
            nameof(BenchmarkWhen), nameof(BenchmarkGpuName), nameof(BenchmarkCpuName), nameof(BenchmarkVerdict), nameof(BenchmarkOldEdition), nameof(HasBenchmarkOldEdition),
            nameof(BenchmarkTrend), nameof(BenchmarkTrendMarks), nameof(BenchmarkTrendMarksText), nameof(HasBenchmarkMarkers), nameof(BenchmarkRuns), nameof(BenchmarkRanking), nameof(HasBenchmarkRanking),
            nameof(BenchmarkGpuWarning), nameof(HasBenchmarkGpuWarning), nameof(BenchmarkCpuWarning), nameof(HasBenchmarkCpuWarning),
            nameof(HasBenchmarkImage), nameof(BenchmarkImageSource),
            nameof(BenchmarkRayTracing), nameof(HasBenchmarkRayTracing), nameof(BenchmarkRayTracingNote), nameof(HasBenchmarkRayTracingNote),
            nameof(HasBenchmarkComparison), nameof(BenchmarkCompareTitle), nameof(BenchmarkCompareWarning), nameof(HasBenchmarkCompareWarning), nameof(BenchmarkCompareRows),
        })
        {
            OnPropertyChanged(name);
        }
    }

    private BenchmarkSensorSummary? DeviceSensors(string device) =>
        LastBenchmark is { } last ? BenchmarkSensorSummary.Merge(last.Tests.Where(t => t.Device == device).Select(t => t.Sensors)) : null;

    /// <summary>Ce que les capteurs ont lu, rien de plus : une valeur illisible n'apparaît pas.</summary>
    internal static List<BenchmarkValue> SensorValues(BenchmarkSensorSummary? sensors, string device, bool withLimit)
    {
        var culture = Texts.Culture;
        var values = new List<BenchmarkValue>();
        if (sensors?.MaxTemperatureC is { } temperature)
        {
            values.Add(new(T("Température maximale"), temperature.ToString("0", culture) + " °C"));
        }

        if (withLimit && sensors?.LimitTemperatureC is { } limit)
        {
            values.Add(new(T("Limite donnée par la puce"), limit.ToString(culture) + " °C"));
        }

        if (sensors?.AverageClockMhz is { } clock)
        {
            values.Add(new(T("Fréquence moyenne"), device == "cpu" && clock >= 1000 ? (clock / 1000).ToString("0.00", culture) + " GHz" : clock.ToString("0", culture) + " MHz"));
        }

        if (sensors?.AveragePowerWatts is { } watts)
        {
            values.Add(new(T("Puissance moyenne"), watts.ToString("0", culture) + " W"));
        }

        return values;
    }

    /// <summary>
    /// Une phrase rassurante seulement quand les capteurs le prouvent : la carte dit elle-même si elle a ralenti, le processeur
    /// donne sa limite de température. Sans ces mesures, MAUS ne dit rien plutôt que de deviner.
    /// </summary>
    internal static string Health(BenchmarkSensorSummary? sensors, string device) => device == "cpu"
        ? sensors is { MaxTemperatureC: { } max, LimitTemperatureC: { } limit } && max < limit
            ? T("Le processeur est resté sous sa limite de température pendant toute la mesure : il a tourné normalement.")
            : ""
        : sensors is { ThermalSlowdownPercent: 0, HardwareBrakePercent: 0 }
            ? T("La carte n'a signalé ni surchauffe ni freinage pendant la mesure : elle a tourné normalement.")
            : "";

    /// <summary>L'écart entre les tests, dit tel qu'il est mesuré.</summary>
    internal static string Spread(BenchmarkReport report, string device)
    {
        var scores = report.Tests.Where(t => t.Device == device && t.Score > 0).Select(t => t.Score).ToList();
        if (scores.Count < 2)
        {
            return "";
        }

        var gap = (scores.Max() - scores.Min()) / scores.Max() * 100;
        return gap < 10
            ? T("Résultats réguliers : {0} % d'écart entre le meilleur et le moins bon test.", gap.ToString("0", Texts.Culture))
            : T("{0} % d'écart entre le meilleur et le moins bon test : chacun sollicite une partie différente de la machine.", gap.ToString("0", Texts.Culture));
    }

    /// <summary>
    /// Scène au 1 % le plus lent très loin de sa moyenne. Vu sur toutes les passes de la machine de référence (Collision galactique :
    /// 4,9 à 5,5 images/s pour 39 de moyenne, 06/10 au 19/10/2026) : la valeur reste affichée, et cette phrase évite d'y voir une panne.
    /// </summary>
    internal static string HeavyMoments(BenchmarkReport report, string device) =>
        report.Tests.Any(t => t.Device == device && t.Low1 is { } low && t.Value > 0 && low / t.Value < 0.4)
            ? T("Le 1 % le plus lent reste bas sur certaines scènes à cause de leurs moments les plus lourds : c'est aussi le cas sur la machine de référence.")
            : "";

    /// <summary>Les tests d'un appareil : la barre la plus longue est le meilleur score de la liste.</summary>
    internal static List<BenchmarkDetailRow> TestRows(BenchmarkReport report, string device)
    {
        var culture = Texts.Culture;
        var tests = report.Tests.Where(t => t.Device == device).ToList();
        var best = tests.Count > 0 ? tests.Max(t => t.Score) : 0;
        var (strongest, weakest) = BenchmarkScoring.Extremes(report.Tests);
        return [.. tests.Select(t =>
        {
            var values = new List<BenchmarkValue>();
            if (t.Device == "cpu")
            {
                values.Add(new(T("Résultat"), BenchmarkTexts.Measure(t)));
            }
            else
            {
                values.Add(new(T("Images par seconde"), t.Value.ToString("0.0", culture)));
            }

            if (t.Low1 is { } low)
            {
                values.Add(new(T("1 % les plus lentes"), T("{0} images/s", low.ToString("0.0", culture))));
            }

            values.AddRange(SensorValues(t.Sensors, device, withLimit: false));
            return new BenchmarkDetailRow(
                BenchmarkTexts.Name(t.Id),
                BenchmarkTexts.Points(t.Score),
                values,
                best > 0 ? t.Score / best : 0,
                ReferenceEquals(t, strongest) ? T("Le plus rapide") : ReferenceEquals(t, weakest) ? T("Le moins rapide") : "",
                ReferenceEquals(t, strongest));
        })];
    }

    /// <summary>Le score combiné : carte graphique, processeur et, à part, lancer de rayons.</summary>
    internal static List<BenchmarkDetailRow> CombinedRows(BenchmarkReport report)
    {
        var scores = new List<(string Name, double Score, string Hardware)>();
        if (report.GpuScore > 0)
        {
            scores.Add((T("Carte graphique"), report.GpuScore, report.Gpu));
        }

        if (report.CpuScore > 0)
        {
            scores.Add((T("Processeur"), report.CpuScore, report.Cpu));
        }

        if (report.RayTracingScore > 0)
        {
            scores.Add((T("Lancer de rayons (score à part)"), report.RayTracingScore, report.Gpu));
        }

        var best = scores.Count > 0 ? scores.Max(s => s.Score) : 0;
        return [.. scores.Select(s => new BenchmarkDetailRow(s.Name, BenchmarkTexts.Points(s.Score), [new(T("Matériel"), s.Hardware)], best > 0 ? s.Score / best : 0, "", false))];
    }

    private static string Summary(BenchmarkReport report)
    {
        var text = new StringBuilder();
        text.AppendLine(T("MAUS Benchmark {0} : {1}", AppVersion.Display, report.Date.ToLocalTime().ToString("g", Texts.Culture)));
        text.AppendLine(T("Score combiné : {0} ({1}, {2})", BenchmarkTexts.Points(report.OverallScore), report.RenderResolution, report.Api));
        text.AppendLine(T("Carte graphique : {0} points ({1})", BenchmarkTexts.Points(report.GpuScore), report.Gpu));
        AppendSensors(text, report, "gpu");
        text.AppendLine(T("Processeur : {0} points ({1})", BenchmarkTexts.Points(report.CpuScore), report.Cpu));
        AppendSensors(text, report, "cpu");
        if (report.RayTracingScore > 0)
        {
            text.AppendLine(T("Lancer de rayons (score à part) : {0} points", BenchmarkTexts.Points(report.RayTracingScore)));
        }

        foreach (var test in report.Tests)
        {
            text.AppendLine("  • " + BenchmarkTexts.Name(test.Id) + " : " + BenchmarkTexts.Points(test.Score) + " (" + BenchmarkTexts.Measure(test) + ")");
        }

        text.AppendLine(T("Deux fois plus de points = deux fois plus rapide (à résolution égale)."));
        text.AppendLine(BenchmarkTexts.EditionNote(report));
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

    private static BitmapImage? LoadImage(string path, int? width = 800)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            // L'aperçu de la page, sur 400 pixels de large : décodé à 800 au plus (écrans à forte densité), pas en 1920 × 1080.
            image.DecodePixelWidth = width ?? 0;
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

}
