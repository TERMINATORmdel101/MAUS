using System.Text.Json;
using System.Text.Json.Serialization;
using Maus.Core.Platform;

namespace Maus.Core.Workshop.Benchmark;

/// <summary>Résultat d'un test du benchmark visuel (une scène de la carte graphique ou un test du processeur).</summary>
/// <param name="Id">Identifiant stable du test (« fractal », « cpu-render »…).</param>
/// <param name="Device">« gpu », « cpu », ou « rt » pour le lancer de rayons (carte graphique, score compté à part).</param>
/// <param name="Capability">Capacité sollicitée (« compute », « geometry », « bandwidth », « multicore »…).</param>
/// <param name="Value">Mesure brute : images par seconde (carte graphique) ou unités de travail par seconde (processeur).</param>
/// <param name="Unit">Unité de la mesure brute, en clair.</param>
/// <param name="Score">Points du test : 10 000 = mesure de la machine de référence.</param>
/// <param name="Low1">Pour la carte graphique : images par seconde du 1 % des images les plus lentes.</param>
public sealed record BenchmarkTestResult(string Id, string Device, string Capability, double Value, string Unit, double Score, double? Low1 = null)
{
    /// <summary>Température, fréquence et puissance relevées pendant la mesure (absent si aucun capteur n'était lisible).</summary>
    public BenchmarkSensorSummary? Sensors { get; init; }
}

/// <summary>Bilan d'une passe complète du benchmark, enregistré dans l'historique.</summary>
public sealed record BenchmarkReport
{
    public DateTimeOffset Date { get; init; }

    public string Version { get; init; } = AppVersion.Display;

    /// <summary>« Direct3D 11 » ou « Direct3D 12 ».</summary>
    public string Api { get; init; } = "";

    public string Gpu { get; init; } = "";

    public string Cpu { get; init; } = "";

    public int Threads { get; init; }

    /// <summary>Résolution de calcul des scènes (fixe : les scores restent comparables d'un écran à l'autre).</summary>
    public string RenderResolution { get; init; } = "";

    public IReadOnlyList<BenchmarkTestResult> Tests { get; init; } = [];

    public double GpuScore { get; init; }

    public double CpuScore { get; init; }

    public double OverallScore { get; init; }

    /// <summary>
    /// Score du lancer de rayons (test « Galerie des glaces », Direct3D 12 et cartes compatibles DXR 1.1) : compté à part,
    /// pour que les cartes sans lancer de rayons restent comparables ; 0 s'il n'a pas tourné.
    /// </summary>
    public double RayTracingScore { get; init; }

    /// <summary>Pourquoi le lancer de rayons n'a pas été mesuré (interface ou carte qui ne le gère pas) ; absent sinon.</summary>
    public string? RayTracingNote { get; init; }

    /// <summary>Faux si la passe a été arrêtée (Échap) ou interrompue par une erreur : les scores sont alors partiels.</summary>
    public bool Completed { get; init; }

    public string? Error { get; init; }

    /// <summary>Image du résultat à partager (PNG : scores, vignettes des scènes, matériel ; aucune donnée personnelle).</summary>
    public string? Image { get; init; }
}

/// <summary>
/// Points du benchmark : chaque test vaut 10 000 points sur la machine de référence (Core i7-8700K et GeForce RTX 2080 Ti,
/// mesurée par le projet) et ses points suivent la vitesse mesurée (deux fois plus rapide = deux fois plus de points).
/// Score de la carte graphique et du processeur : moyenne géométrique de leurs tests (un point faible pèse autant qu'un
/// point fort). Score combiné : moyenne harmonique pondérée (75 % carte graphique, 25 % processeur), qui pénalise un
/// déséquilibre comme le fait un jeu limité par son composant le plus lent. Le lancer de rayons a son propre score, hors
/// du score combiné (toutes les cartes ne le gèrent pas).
/// </summary>
public static class BenchmarkScoring
{
    public const double ReferencePoints = 10000;
    public const double GpuWeight = 0.75;
    public const double CpuWeight = 0.25;

    public static double TestScore(double measured, double reference) =>
        measured > 0 && reference > 0 ? ReferencePoints * measured / reference : 0;

    /// <summary>Moyenne géométrique des scores (0 si un score manque).</summary>
    public static double Combine(IEnumerable<double> scores)
    {
        var list = scores.ToList();
        if (list.Count == 0 || list.Exists(s => s <= 0))
        {
            return 0;
        }

        return Math.Exp(list.Average(Math.Log));
    }

    public static double Overall(double gpu, double cpu) =>
        gpu > 0 && cpu > 0 ? 1 / ((GpuWeight / gpu) + (CpuWeight / cpu)) : 0;

    /// <summary>Images par seconde du 1 % des images les plus lentes (à partir de 100 images).</summary>
    public static double? Low1Fps(IReadOnlyList<double> frameMilliseconds)
    {
        if (frameMilliseconds.Count < 100)
        {
            return null;
        }

        var sorted = frameMilliseconds.OrderByDescending(x => x).ToArray();
        var count = Math.Max(1, sorted.Length / 100);
        var slowest = sorted.Take(count).Average();
        return slowest > 0 ? 1000.0 / slowest : null;
    }

    /// <summary>Point fort et point faible : le test le plus au-dessus et le plus en dessous de la moyenne de son composant.</summary>
    public static (BenchmarkTestResult? Strongest, BenchmarkTestResult? Weakest) Extremes(IEnumerable<BenchmarkTestResult> tests)
    {
        var scored = tests.Where(t => t.Score > 0).ToList();
        if (scored.Count < 2)
        {
            return (null, null);
        }

        var mean = Combine(scored.Select(t => t.Score));
        var strongest = scored.MaxBy(t => t.Score / mean)!;
        var weakest = scored.MinBy(t => t.Score / mean)!;

        // Moins de 10 % d'écart : pas de point fort ni de point faible à signaler, la machine est équilibrée.
        return strongest.Score / weakest.Score < 1.1 ? (null, null) : (strongest, weakest);
    }
}

/// <summary>Historique des passes du benchmark (%LOCALAPPDATA%\MAUS\benchmark-history.json, 50 dernières passes).</summary>
public sealed class BenchmarkHistoryStore(string path)
{
    private const int Keep = 50;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static BenchmarkHistoryStore CreateDefault() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MAUS", "benchmark-history.json"));

    public string FilePath { get; } = path;

    /// <summary>Dossier des images du résultat (à côté de l'historique).</summary>
    public string ImagesFolder => Path.Combine(Path.GetDirectoryName(FilePath)!, "benchmark");

    /// <summary>Nom de l'image d'une passe : MAUS-benchmark-AAAAMMJJ-HHMMSS.png.</summary>
    public string ImagePathFor(DateTimeOffset date) =>
        Path.Combine(ImagesFolder, "MAUS-benchmark-" + date.ToLocalTime().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".png");

    /// <summary>
    /// Efface les images du résultat dont la passe n'est plus dans l'historique (50 dernières passes) : seulement les
    /// fichiers MAUS-benchmark-*.png du dossier des images de MAUS.
    /// </summary>
    public void PruneImages()
    {
        try
        {
            if (!Directory.Exists(ImagesFolder))
            {
                return;
            }

            var kept = Load().Select(r => r.Image).OfType<string>().Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(ImagesFolder, "MAUS-benchmark-*.png"))
            {
                if (!kept.Contains(Path.GetFullPath(file)))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Une image qui ne s'efface pas (ouverte ailleurs) le sera à la prochaine passe.
        }
    }

    public IReadOnlyList<BenchmarkReport> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<BenchmarkReport>>(File.ReadAllText(FilePath), Json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public void Add(BenchmarkReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var all = Load().Append(report).OrderBy(r => r.Date).TakeLast(Keep).ToList();
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(all, Json));
    }

    public static string Serialize(BenchmarkReport report) => JsonSerializer.Serialize(report, Json);

    public static BenchmarkReport? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<BenchmarkReport>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
