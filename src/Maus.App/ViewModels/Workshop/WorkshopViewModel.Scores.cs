using System.Collections.ObjectModel;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>Atelier, Tests : historique des scores (courbe et derniers résultats), pour voir l'effet d'un réglage dans le temps.</summary>
public sealed partial class WorkshopViewModel
{
    private IReadOnlyList<BenchmarkEntry> _scoreEntries = [];
    private TestOption<string>? _scoreKind;
    private IReadOnlyList<double> _scoreSeries = [];
    private string _scoreTrend = string.Empty;

    public ObservableCollection<TestOption<string>> ScoreKinds { get; } = [];

    public ObservableCollection<string> ScoreLines { get; } = [];

    public TestOption<string>? ScoreKind
    {
        get => _scoreKind;
        set
        {
            if (SetProperty(ref _scoreKind, value))
            {
                ShowScores();
            }
        }
    }

    public IReadOnlyList<double> ScoreSeries
    {
        get => _scoreSeries;
        private set => SetProperty(ref _scoreSeries, value);
    }

    public string ScoreTrend
    {
        get => _scoreTrend;
        private set => SetProperty(ref _scoreTrend, value);
    }

    public bool HasScores => ScoreKinds.Count > 0;

    private void LoadScoreHistory()
    {
        _scoreEntries = BenchmarkHistory.CreateDefault().Load();
        var selected = ScoreKind?.Value;
        ScoreKinds.Clear();
        foreach (var kind in _scoreEntries.Where(e => e.Stable).Select(e => e.Kind).Distinct(StringComparer.Ordinal))
        {
            ScoreKinds.Add(new TestOption<string>(ScoreTrends.Label(kind), kind));
        }

        OnPropertyChanged(nameof(HasScores));
        ScoreKind = ScoreKinds.FirstOrDefault(k => k.Value == selected) ?? ScoreKinds.FirstOrDefault();
        ShowScores();
    }

    private void ShowScores()
    {
        ScoreLines.Clear();
        if (ScoreKind is not { } kind)
        {
            ScoreSeries = [];
            ScoreTrend = T("Pas encore de résultat.");
            return;
        }

        var series = ScoreTrends.Series(_scoreEntries, kind.Value);
        ScoreSeries = series.Select(e => e.Score).ToList();
        ScoreTrend = ScoreTrends.Describe(series);
        foreach (var entry in series.AsEnumerable().Reverse().Take(10))
        {
            ScoreLines.Add(T("{0:g} · {1:0.#} {2}{3}", entry.At.LocalDateTime, entry.Score, ScoreTrends.Unit(entry.Kind), entry.Detail is { Length: > 0 } detail ? " · " + detail : string.Empty));
        }
    }
}
