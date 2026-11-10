using System.Text.Json;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Un passage de test enregistré : type (« cpu-multi », « cpu-single », « ram »), score et date.</summary>
public sealed record BenchmarkEntry(string Kind, double Score, bool Stable, DateTimeOffset At, string? Detail = null);

/// <summary>
/// Historique local des tests, pour comparer un passage aux précédents sur le même PC (après un réglage, par exemple).
/// Stocké dans <c>%LOCALAPPDATA%\MAUS\benchmarks.json</c> : rien n'est envoyé (aucune télémétrie).
/// </summary>
/// <param name="wait">Attente entre deux tentatives de lecture (remplaçable pour les tests).</param>
public sealed class BenchmarkHistory(string path, Action<TimeSpan>? wait = null, int maxEntries = 200)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly JsonHistoryFile<BenchmarkEntry> _file = new(
        path,
        Options,
        maxEntries,
        e => e.At,
        error => T("L'historique des scores ({0}) est momentanément illisible : il n'est pas réécrit, pour ne rien perdre. {1}", path, error?.Message),
        wait,
        clean: Clean);

    public static BenchmarkHistory CreateDefault() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MAUS", "benchmarks.json"));

    /// <summary>Historique du score de santé, à part pour ne pas évincer les tests (un audit par jour suffit à le remplir).</summary>
    public static BenchmarkHistory CreateHealth() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MAUS", "health-history.json"));

    /// <summary>Mesures gardées dans le temps (disques, batterie, températures), à part : elles sont plus nombreuses.</summary>
    public static BenchmarkHistory CreateMeasures() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MAUS", "measures-history.json"), maxEntries: 2000);

    /// <summary>Passages enregistrés, pour l'affichage ; vide si l'historique est absent ou illisible.</summary>
    public IReadOnlyList<BenchmarkEntry> Load() => _file.Load();

    /// <summary>
    /// Ajoute un passage. L'historique n'est réécrit qu'à partir de ce qui a vraiment été lu : un fichier momentanément
    /// illisible n'est pas touché, et un fichier abîmé est copié à côté avant d'être remplacé.
    /// </summary>
    /// <exception cref="IOException">Historique illisible (laissé intact) ou écriture impossible.</exception>
    /// <exception cref="UnauthorizedAccessException">Écriture refusée.</exception>
    public void Add(BenchmarkEntry entry) => _file.Add(entry);

    /// <summary>Fichier modifiable par un programme du même compte : une valeur impossible n'est pas lue, plutôt que montrée.</summary>
    private static BenchmarkEntry? Clean(BenchmarkEntry entry) =>
        entry.Kind is null || !StoredValues.IsPastDate(entry.At) || !ScoreTrends.IsPossible(entry.Kind, entry.Score)
            ? null
            : entry with { Kind = StoredValues.Text(entry.Kind, 500), Detail = entry.Detail is null ? null : StoredValues.Text(entry.Detail, 500) };

    /// <summary>Écart en % avec la moyenne des passages précédents du même type ; <c>null</c> sans passage précédent.</summary>
    public static double? CompareToPrevious(IReadOnlyList<BenchmarkEntry> previous, BenchmarkEntry current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var same = previous.Where(e => e.Kind == current.Kind && e.At < current.At && e.Stable).Select(e => e.Score).ToList();
        return same.Count == 0 || same.Average() <= 0 ? null : Math.Round((current.Score / same.Average() - 1) * 100, 1);
    }
}
