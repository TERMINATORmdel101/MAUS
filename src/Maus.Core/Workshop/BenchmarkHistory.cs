using System.Text.Json;

namespace Maus.Core.Workshop;

/// <summary>Un passage de test enregistré : type (« cpu-multi », « cpu-single », « ram »), score et date.</summary>
public sealed record BenchmarkEntry(string Kind, double Score, bool Stable, DateTimeOffset At, string? Detail = null);

/// <summary>
/// Historique local des tests, pour comparer un passage aux précédents sur le même PC (après un réglage, par exemple).
/// Stocké dans <c>%LOCALAPPDATA%\MAUS\benchmarks.json</c> : rien n'est envoyé (aucune télémétrie).
/// </summary>
public sealed class BenchmarkHistory(string path)
{
    private const int MaxEntries = 200;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static BenchmarkHistory CreateDefault() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MAUS", "benchmarks.json"));

    public IReadOnlyList<BenchmarkEntry> Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<BenchmarkEntry>>(File.ReadAllText(path), Options) ?? [] : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Add(BenchmarkEntry entry)
    {
        var entries = Load().Append(entry).OrderBy(e => e.At).TakeLast(MaxEntries).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(entries, Options));
    }

    /// <summary>Écart en % avec la moyenne des passages précédents du même type ; <c>null</c> sans passage précédent.</summary>
    public static double? CompareToPrevious(IReadOnlyList<BenchmarkEntry> previous, BenchmarkEntry current)
    {
        var same = previous.Where(e => e.Kind == current.Kind && e.At < current.At && e.Stable).Select(e => e.Score).ToList();
        return same.Count == 0 || same.Average() <= 0 ? null : Math.Round((current.Score / same.Average() - 1) * 100, 1);
    }
}
