using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Maus.Core.Workshop.Benchmark;

/// <summary>
/// Code de partage d'un résultat : une ligne « MAUS-BENCH-1:… » ajoutée au texte copié, que MAUS sait relire pour
/// comparer deux machines test par test. Il ne contient que le matériel et les mesures (aucun nom d'utilisateur, aucun nom
/// de PC) ; rien ne passe par un serveur : l'utilisateur colle lui-même le texte de son ami.
/// </summary>
public static partial class BenchmarkShareCode
{
    public const string Prefix = "MAUS-BENCH-1:";

    // Au plus 64 Kio une fois décompressé : un texte collé ne peut pas faire gonfler la mémoire de MAUS.
    private const int MaxJson = 64 * 1024;
    private const int MaxText = 200;
    private const int MaxTests = 24;

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 8,
    };

    public static string Encode(BenchmarkReport report)
    {
        var payload = new Payload(
            report.Version,
            report.Date,
            report.Api,
            report.RenderResolution,
            report.Gpu,
            report.Cpu,
            report.Threads,
            report.GpuScore,
            report.CpuScore,
            report.OverallScore,
            report.Completed,
            [.. report.Tests.Select(t => new PayloadTest(t.Id, t.Device, t.Value, t.Score, t.Low1, t.Sensors?.MaxTemperatureC, t.Sensors?.AverageClockMhz))]);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(bytes);
        }

        return Prefix + Convert.ToBase64String(output.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Retrouve un code dans un texte collé (le message entier d'un ami) ; <c>null</c> s'il n'y en a pas de valable.</summary>
    public static BenchmarkReport? Decode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || CodePattern().Match(text) is not { Success: true } match)
        {
            return null;
        }

        try
        {
            var data = match.Groups[1].Value.Replace('-', '+').Replace('_', '/');
            data = data.PadRight(data.Length + ((4 - (data.Length % 4)) % 4), '=');
            using var input = new MemoryStream(Convert.FromBase64String(data));
            using var inflate = new DeflateStream(input, CompressionMode.Decompress);
            using var json = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = inflate.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (json.Length + read > MaxJson)
                {
                    return null;
                }

                json.Write(buffer, 0, read);
            }

            var payload = JsonSerializer.Deserialize<Payload>(json.ToArray(), Json);
            return payload is null ? null : ToReport(payload);
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static BenchmarkReport? ToReport(Payload p)
    {
        if (p.Tests is null || p.Tests.Count > MaxTests || !Valid(p.GpuScore) || !Valid(p.CpuScore) || !Valid(p.OverallScore) || p.Threads is < 0 or > 4096)
        {
            return null;
        }

        var tests = new List<BenchmarkTestResult>();
        foreach (var t in p.Tests)
        {
            if (t is null || !Valid(t.Value) || !Valid(t.Score) || (t.Low1 is { } low && !Valid(low)) || Clean(t.Id) is not { Length: > 0 } id || t.Device is not ("gpu" or "cpu"))
            {
                return null;
            }

            tests.Add(new BenchmarkTestResult(id, t.Device, "", t.Value, "", t.Score, t.Low1)
            {
                Sensors = t.MaxTemperatureC is null && t.AverageClockMhz is null
                    ? null
                    : new BenchmarkSensorSummary { Samples = 1, MaxTemperatureC = Finite(t.MaxTemperatureC), AverageClockMhz = Finite(t.AverageClockMhz) },
            });
        }

        return new BenchmarkReport
        {
            Version = Clean(p.Version) ?? "",
            Date = p.Date,
            Api = Clean(p.Api) ?? "",
            RenderResolution = Clean(p.RenderResolution) ?? "",
            Gpu = Clean(p.Gpu) ?? "",
            Cpu = Clean(p.Cpu) ?? "",
            Threads = p.Threads,
            GpuScore = p.GpuScore,
            CpuScore = p.CpuScore,
            OverallScore = p.OverallScore,
            Completed = p.Completed,
            Tests = tests,
        };
    }

    private static bool Valid(double value) => double.IsFinite(value) && value is >= 0 and < 1e7;

    private static double? Finite(double? value) => value is { } v && Valid(v) ? v : null;

    /// <summary>Texte affichable : sans caractères de contrôle, raccourci.</summary>
    private static string? Clean(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var clean = new string([.. text.Where(c => !char.IsControl(c))]).Trim();
        return clean.Length > MaxText ? clean[..MaxText] : clean;
    }

    [GeneratedRegex(@"MAUS-BENCH-1:([A-Za-z0-9_\-]{16,8000})", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    private sealed record Payload(
        string? Version,
        DateTimeOffset Date,
        string? Api,
        string? RenderResolution,
        string? Gpu,
        string? Cpu,
        int Threads,
        double GpuScore,
        double CpuScore,
        double OverallScore,
        bool Completed,
        List<PayloadTest?>? Tests);

    private sealed record PayloadTest(string? Id, string? Device, double Value, double Score, double? Low1, double? MaxTemperatureC, double? AverageClockMhz);
}

/// <summary>Une ligne de comparaison : points de l'utilisateur, points de l'autre résultat, écart en %.</summary>
/// <param name="Id">« overall », « gpu », « cpu » ou l'identifiant d'un test.</param>
/// <param name="Difference">Écart de l'autre résultat par rapport au vôtre, en % (+ = plus rapide) ; <c>null</c> si l'un manque.</param>
public sealed record BenchmarkComparisonRow(string Id, double Mine, double Theirs, double? Difference);

/// <summary>Comparaison de deux passes du benchmark, test par test (même barème : 10 000 = la machine de référence).</summary>
public static class BenchmarkComparison
{
    public static IReadOnlyList<BenchmarkComparisonRow> Compare(BenchmarkReport mine, BenchmarkReport theirs)
    {
        var rows = new List<BenchmarkComparisonRow>
        {
            Row("overall", mine.OverallScore, theirs.OverallScore),
            Row("gpu", mine.GpuScore, theirs.GpuScore),
            Row("cpu", mine.CpuScore, theirs.CpuScore),
        };
        var ids = mine.Tests.Select(t => t.Id).Concat(theirs.Tests.Select(t => t.Id)).Distinct(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            rows.Add(Row(id, mine.Tests.FirstOrDefault(t => t.Id == id)?.Score ?? 0, theirs.Tests.FirstOrDefault(t => t.Id == id)?.Score ?? 0));
        }

        return rows;
    }

    /// <summary>Les points ne se comparent qu'à résolution de calcul égale (et en mode léger avec le mode léger).</summary>
    public static bool Comparable(BenchmarkReport a, BenchmarkReport b) =>
        string.Equals(a.RenderResolution, b.RenderResolution, StringComparison.Ordinal);

    private static BenchmarkComparisonRow Row(string id, double mine, double theirs) =>
        new(id, mine, theirs, mine > 0 && theirs > 0 ? Math.Round(((theirs / mine) - 1) * 100, 1) : null);
}
