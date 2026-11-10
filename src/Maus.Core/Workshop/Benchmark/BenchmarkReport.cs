using System.Text.Json;
using System.Text.Json.Serialization;
using Maus.Core.Platform;

namespace Maus.Core.Workshop.Benchmark;

/// <summary>Résultat d'un test du benchmark visuel (une scène de la carte graphique ou un test du processeur).</summary>
/// <param name="Id">Identifiant stable du test (« fractal », « cpu-render »…).</param>
/// <param name="Device">« gpu », « cpu », ou « rt » pour le lancer de rayons (carte graphique, score compté à part).</param>
/// <param name="Value">Mesure brute : images par seconde (carte graphique) ou unités de travail par seconde (processeur).</param>
/// <param name="Unit">Unité de la mesure brute telle qu'écrite ce jour-là : l'affichage passe par <see cref="BenchmarkTexts.Measure"/>, dans la langue du moment.</param>
/// <param name="Score">Points du test : 10 000 = mesure de la machine de référence.</param>
/// <param name="Low1">Pour la carte graphique : images par seconde du 1 % des images les plus lentes.</param>
public sealed record BenchmarkTestResult(string Id, string Device, double Value, string Unit, double Score, double? Low1 = null)
{
    /// <summary>Température, fréquence et puissance relevées pendant la mesure (absent si aucun capteur n'était lisible).</summary>
    public BenchmarkSensorSummary? Sensors { get; init; }
}

/// <summary>Bilan d'une passe complète du benchmark, enregistré dans l'historique.</summary>
public sealed record BenchmarkReport
{
    public DateTimeOffset Date { get; init; }

    public string Version { get; init; } = AppVersion.Display;

    /// <summary>
    /// Édition du benchmark qui a mesuré cette passe, écrite depuis l'édition 4. 0 = pas écrite (passe plus ancienne) : elle
    /// se déduit alors de la version, voir <see cref="BenchmarkScoring.EditionOf"/>. Pas de valeur par défaut : une ancienne
    /// passe relue ne doit pas passer pour une passe de l'édition actuelle.
    /// </summary>
    public int Edition { get; init; }

    /// <summary>« Direct3D 11 » ou « Direct3D 12 ».</summary>
    public string Api { get; init; } = "";

    public string Gpu { get; init; } = "";

    public string Cpu { get; init; } = "";

    /// <summary>Version du pilote de la carte graphique mesurée (absente pour les passes plus anciennes, ou si elle n'a pas pu être lue). Hors du code de partage.</summary>
    public string? GpuDriver { get; init; }

    /// <summary>Build de Windows pendant la passe, par exemple « 26100.7171 » (absent pour les passes plus anciennes, ou s'il n'a pas pu être lu). Hors du code de partage.</summary>
    public string? WindowsBuild { get; init; }

    public int Threads { get; init; }

    /// <summary>Résolution de calcul des scènes (fixe : les scores restent comparables d'un écran à l'autre).</summary>
    public string RenderResolution { get; init; } = "";

    /// <summary>
    /// Définition de l'écran pendant la mesure (vide pour les passes plus anciennes). Avant l'édition 4, l'image finale était
    /// dessinée à cette définition, ce qui pouvait changer un peu les points ; depuis, elle est seulement recopiée à l'écran.
    /// </summary>
    public string DisplayResolution { get; init; } = "";

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
    /// <summary>
    /// Éditions du benchmark : une nouvelle édition commence chaque fois que les scènes ou les valeurs de référence changent,
    /// et les points de deux éditions ne se comparent pas. Déduite de la version de MAUS du résultat (aussi pour l'historique
    /// et les codes partagés des versions précédentes) : 1 = de la 0.7.1 à la 0.7.4, 2 = de la 0.7.5 à la 0.8.5 (nouvelles scènes,
    /// étalonnage à vitesse réelle), 3 = versions d'essai de la 0.8.6 (références du processeur remesurées). À compléter à chaque changement de scène ou de référence.
    /// Depuis l'édition 4 (image finale à la résolution de calcul, couleur du test vectoriel sur tous les cœurs), l'édition est
    /// écrite dans le résultat (<see cref="BenchmarkReport.Edition"/>) : elle a changé sans changer de numéro de version.
    /// </summary>
    private static readonly (Version From, int Edition)[] Editions = [(new(0, 8, 6), 3), (new(0, 7, 5), 2), (new(0, 7, 1), 1)];

    /// <summary>Édition des passes mesurées par ce benchmark.</summary>
    public const int CurrentEdition = 4;

    /// <summary>Première édition aux références du processeur remesurées : avant, il comptait environ 4 fois trop de points.</summary>
    public const int CpuRecalibratedEdition = 3;

    /// <summary>Première édition où la définition de l'écran ne compte plus : l'image finale est calculée à la résolution de calcul.</summary>
    public const int ScreenIndependentEdition = 4;

    public static int EditionOf(BenchmarkReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Edition > 0)
        {
            return report.Edition;
        }

        var text = report.Version.Split('+', '-', ' ')[0];
        if (!Version.TryParse(text, out var version))
        {
            return 0;
        }

        foreach (var (from, edition) in Editions)
        {
            if (version >= from)
            {
                return edition;
            }
        }

        return 0;
    }

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
        ArgumentNullException.ThrowIfNull(frameMilliseconds);

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

        // Chaque composant a son échelle : un processeur à 40 000 points ne dit rien d'une carte graphique à 10 000.
        var means = scored.GroupBy(t => t.Device).ToDictionary(g => g.Key, g => Combine(g.Select(t => t.Score)));
        double Relative(BenchmarkTestResult test) => test.Score / means[test.Device];
        var strongest = scored.MaxBy(Relative)!;
        var weakest = scored.MinBy(Relative)!;

        // Moins de 10 % d'écart : pas de point fort ni de point faible à signaler, la machine est équilibrée.
        return Relative(strongest) / Relative(weakest) < 1.1 ? (null, null) : (strongest, weakest);
    }
}

/// <summary>Historique des passes du benchmark (%LOCALAPPDATA%\MAUS\benchmark-history.json, 50 dernières passes).</summary>
/// <param name="path">Chemin de l'historique.</param>
/// <param name="wait">Attente entre deux tentatives de lecture (remplaçable pour les tests).</param>
public sealed class BenchmarkHistoryStore(string path, Action<TimeSpan>? wait = null)
{
    private const int Keep = 50;

    /// <summary>Même borne que le code de partage : mille fois la machine de référence.</summary>
    private const double HighestPoints = 1e7;

    private const double HighestMeasure = 1e12;

    private const int HighestEdition = 1000;

    private const int HighestThreads = 4096;

    private const int LongestText = 500;

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly JsonHistoryFile<BenchmarkReport> _file = new(
        path,
        Json,
        Keep,
        r => r.Date,
        _ => Maus.Core.Localization.Texts.T("L'historique du benchmark ({0}) est momentanément illisible : il n'est pas réécrit, pour ne rien perdre.", path),
        wait,
        clean: Clean);

    public static BenchmarkHistoryStore CreateDefault() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MAUS", "benchmark-history.json"));

    public string FilePath { get; } = path;

    /// <summary>Dossier des images du résultat (à côté de l'historique).</summary>
    public string ImagesFolder => Path.Combine(Path.GetDirectoryName(FilePath)!, "benchmark");

    /// <summary>
    /// Image enregistrée par le benchmark : un fichier MAUS-benchmark-*.png directement dans le dossier des images. Le
    /// chemin vient d'un fichier du profil : tout autre emplacement n'est ni décodé ni ouvert.
    /// </summary>
    public bool IsOwnImage(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(ImagesFolder), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("MAUS-benchmark-", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetExtension(full), ".png", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Nom de l'image d'une passe : MAUS-benchmark-AAAAMMJJ-HHMMSS.png.</summary>
    public string ImagePathFor(DateTimeOffset date) =>
        Path.Combine(ImagesFolder, "MAUS-benchmark-" + date.ToLocalTime().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".png");

    /// <summary>Le chemin vient d'un fichier du profil : un chemin invalide ne désigne aucune image, et ne fait pas perdre la passe.</summary>
    private static string? FullPathOrNull(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

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

            // Historique illisible : aucune image ne peut être dite orpheline. Une passe qui n'est pas montrée garde la sienne.
            if (!_file.TryReadAll(out var reports))
            {
                return;
            }

            var kept = reports.Select(r => r.Image).OfType<string>().Select(FullPathOrNull).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
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

    public IReadOnlyList<BenchmarkReport> Load() => _file.Load();

    /// <summary>
    /// Fichier modifiable par un programme du même compte : une passe avec un chiffre ou une date impossible n'est pas montrée, ses
    /// scores ne valent rien. Un texte ou une liste vidés ne doivent pas faire tomber ceux qui les lisent.
    /// </summary>
    internal static BenchmarkReport? Clean(BenchmarkReport report)
    {
        if (!StoredValues.IsPastDate(report.Date)
            || report.Edition is < 0 or > HighestEdition || report.Threads is < 0 or > HighestThreads
            || !IsPoints(report.GpuScore) || !IsPoints(report.CpuScore) || !IsPoints(report.OverallScore) || !IsPoints(report.RayTracingScore))
        {
            return null;
        }

        var tests = (report.Tests ?? []).OfType<BenchmarkTestResult>().Where(t => t.Id is not null && t.Device is not null).ToList();
        if (tests.Exists(t => !IsPoints(t.Score) || !StoredValues.IsWithin(t.Value, 0, HighestMeasure) || (t.Low1 is { } low && !StoredValues.IsWithin(low, 0, HighestMeasure))))
        {
            return null;
        }

        return report with
        {
            Version = StoredValues.Text(report.Version, LongestText),
            Api = StoredValues.Text(report.Api, LongestText),
            Gpu = StoredValues.Text(report.Gpu, LongestText),
            Cpu = StoredValues.Text(report.Cpu, LongestText),
            GpuDriver = Optional(report.GpuDriver),
            WindowsBuild = Optional(report.WindowsBuild),
            RenderResolution = StoredValues.Text(report.RenderResolution, LongestText),
            DisplayResolution = StoredValues.Text(report.DisplayResolution, LongestText),
            RayTracingNote = Optional(report.RayTracingNote),
            Error = Optional(report.Error),
            Tests = [.. tests.Select(t => t with { Id = StoredValues.Text(t.Id, LongestText), Unit = StoredValues.Text(t.Unit, LongestText), Sensors = Readable(t.Sensors) })],
        };
    }

    private static bool IsPoints(double value) => StoredValues.IsWithin(value, 0, HighestPoints - 1);

    private static string? Optional(string? text) => text is null ? null : StoredValues.Text(text, LongestText);

    /// <summary>Un capteur impossible redevient « non lu » : la passe, elle, a bien eu lieu.</summary>
    private static BenchmarkSensorSummary? Readable(BenchmarkSensorSummary? sensors) => sensors is null ? null : sensors with
    {
        MaxTemperatureC = StoredValues.Within(sensors.MaxTemperatureC, -50, 200),
        AverageClockMhz = StoredValues.Within(sensors.AverageClockMhz, 0, 20000),
        AveragePowerWatts = StoredValues.Within(sensors.AveragePowerWatts, 0, 5000),
        LimitTemperatureC = sensors.LimitTemperatureC is > 0 and <= 200 ? sensors.LimitTemperatureC : null,
        ThermalSlowdownPercent = StoredValues.Within(sensors.ThermalSlowdownPercent, 0, 100),
        HardwareBrakePercent = StoredValues.Within(sensors.HardwareBrakePercent, 0, 100),
    };

    /// <exception cref="IOException">Historique momentanément illisible (laissé intact) ou écriture impossible.</exception>
    /// <exception cref="UnauthorizedAccessException">Écriture refusée.</exception>
    public void Add(BenchmarkReport report) => _file.Add(report);

    public static string Serialize(BenchmarkReport report) => JsonSerializer.Serialize(report, Json);
}
