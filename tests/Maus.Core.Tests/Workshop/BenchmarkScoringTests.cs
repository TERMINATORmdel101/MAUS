using Maus.Core.Workshop.Benchmark;

namespace Maus.Core.Tests.Workshop;

public class BenchmarkScoringTests
{
    [Fact]
    public void ReferenceSpeedIsWorthTenThousandPoints()
    {
        Assert.Equal(10000, BenchmarkScoring.TestScore(18, 18));
        Assert.Equal(20000, BenchmarkScoring.TestScore(36, 18));
        Assert.Equal(0, BenchmarkScoring.TestScore(0, 18));
        Assert.Equal(0, BenchmarkScoring.TestScore(18, 0));
    }

    [Fact]
    public void ComponentScoreIsTheGeometricMean()
    {
        Assert.Equal(10000, BenchmarkScoring.Combine([5000, 20000]), 6);
        Assert.Equal(0, BenchmarkScoring.Combine([5000, 0]));
        Assert.Equal(0, BenchmarkScoring.Combine([]));
    }

    [Fact]
    public void OverallScorePenalizesImbalance()
    {
        Assert.Equal(10000, BenchmarkScoring.Overall(10000, 10000), 6);

        // Même moyenne arithmétique, mais un processeur faible tire le score combiné vers le bas.
        var balanced = BenchmarkScoring.Overall(15000, 15000);
        var unbalanced = BenchmarkScoring.Overall(20000, 5000);
        Assert.True(unbalanced < balanced);
        Assert.Equal(0, BenchmarkScoring.Overall(10000, 0));
    }

    [Fact]
    public void LowOnePercentNeedsEnoughFrames()
    {
        Assert.Null(BenchmarkScoring.Low1Fps([.. Enumerable.Repeat(16.7, 50)]));

        // 99 images à 10 ms et une à 100 ms : le 1 % le plus lent est l'image de 100 ms, soit 10 images/s.
        var frames = Enumerable.Repeat(10.0, 99).Append(100.0).ToList();
        Assert.Equal(10, BenchmarkScoring.Low1Fps(frames)!.Value, 6);
    }

    [Fact]
    public void ExtremesNeedARealGap()
    {
        var even = new[] { Result("a", 10000), Result("b", 10500) };
        Assert.Equal((null, null), BenchmarkScoring.Extremes(even));

        var uneven = new[] { Result("a", 16000), Result("b", 9000), Result("c", 6000) };
        var (strongest, weakest) = BenchmarkScoring.Extremes(uneven);
        Assert.Equal("a", strongest!.Id);
        Assert.Equal("c", weakest!.Id);
    }

    [Fact]
    public void HistoryKeepsReportsAndSurvivesABrokenFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "maus-bench-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new BenchmarkHistoryStore(Path.Combine(folder, "history.json"));
            Assert.Empty(store.Load());
            store.Add(new BenchmarkReport { Date = DateTimeOffset.Now, Gpu = "Carte", Tests = [Result("fractal", 9000)], OverallScore = 9000, Completed = true });
            var loaded = Assert.Single(store.Load());
            Assert.Equal("Carte", loaded.Gpu);
            Assert.Equal(9000, Assert.Single(loaded.Tests).Score);

            File.WriteAllText(store.FilePath, "{ pas du JSON");
            Assert.Empty(store.Load());
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    private static BenchmarkTestResult Result(string id, double score) => new(id, "gpu", "compute", 10, "images par seconde", score);
}
