using Maus.Core.Reporting;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Évolution des scores dans le temps : santé du PC après chaque audit, et résultats des tests de l'atelier.</summary>
public static class ScoreTrends
{
    /// <summary>
    /// Score de santé au barème selon la gravité (depuis le 29/09/2026). Les scores de l'ancien barème (« health ») restent
    /// dans l'historique mais ne sont pas mélangés à la courbe : ils ne se comparent pas.
    /// </summary>
    public const string HealthKind = "health-v2";

    /// <summary>Ancien barème (un problème = 12 points, un point à surveiller = 5), avant le 29/09/2026.</summary>
    public const string LegacyHealthKind = "health";

    /// <summary>Score de santé d'un audit, avec le nombre de problèmes / points à surveiller / optimisations en détail.</summary>
    public static BenchmarkEntry HealthEntry(IReadOnlyCollection<ModuleResult> results, DateTimeOffset at)
    {
        var findings = results.SelectMany(r => r.Findings).ToList();
        return new BenchmarkEntry(HealthKind, HealthScore.Compute(results), true, at,
            string.Join('/', findings.Count(f => f.Status == FindingStatus.Problem), findings.Count(f => f.Status == FindingStatus.Warning), findings.Count(f => f.Status == FindingStatus.Improvable)));
    }

    public static string Label(string kind) => kind switch
    {
        HealthKind => T("Score de santé"),
        LegacyHealthKind => T("Score de santé (ancien barème)"),
        "cpu-multi" => T("Processeur (tous les cœurs)"),
        "cpu-int" => T("Processeur (tous les cœurs, calculs entiers)"),
        "cpu-sse" => T("Processeur (tous les cœurs, SSE2)"),
        "cpu-avx" => T("Processeur (tous les cœurs, AVX)"),
        "cpu-fma" => T("Processeur (tous les cœurs, AVX2 et FMA)"),
        "cpu-avx512" => T("Processeur (tous les cœurs, AVX-512)"),
        "cpu-memory" => T("Processeur (tous les cœurs, caches et mémoire)"),
        "cpu-single" => T("Processeur (un cœur)"),
        "ram" => T("Mémoire vive (débit de copie)"),
        "vram" => T("Mémoire vidéo (débit de relecture)"),
        "disk-read" => T("Disque (lecture)"),
        _ => kind,
    };

    /// <summary>Type de score du test processeur : un par charge, car leurs tours n'ont pas la même durée.</summary>
    public static string CpuKind(CpuStressMode mode) => mode switch
    {
        CpuStressMode.Scalar => "cpu-int",
        CpuStressMode.Sse => "cpu-sse",
        CpuStressMode.Avx => "cpu-avx",
        CpuStressMode.Fma => "cpu-fma",
        CpuStressMode.Avx512 => "cpu-avx512",
        CpuStressMode.Memory => "cpu-memory",
        _ => "cpu-multi",
    };

    public static string Unit(string kind) => kind switch
    {
        HealthKind or LegacyHealthKind => "/100",
        "ram" or "vram" => T("Go/s"),
        "disk-read" => T("Mo/s"),
        _ => T("points"),
    };

    /// <summary>Scores d'un type, du plus ancien au plus récent (les 50 derniers).</summary>
    public static IReadOnlyList<BenchmarkEntry> Series(IEnumerable<BenchmarkEntry> entries, string kind) =>
        entries.Where(e => e.Kind == kind && e.Stable).OrderBy(e => e.At).TakeLast(50).ToList();

    /// <summary>« 85 /100 le 25/09/2026 · 72 → 85 depuis le 20/09/2026 (+18 %) », ou le seul score connu.</summary>
    public static string Describe(IReadOnlyList<BenchmarkEntry> series)
    {
        if (series.Count == 0)
        {
            return T("Pas encore de résultat.");
        }

        var last = series[^1];
        var unit = Unit(last.Kind);
        var text = T("{0:0.#} {1} le {2:d}", last.Score, unit, last.At.LocalDateTime);
        if (series.Count < 2)
        {
            return text;
        }

        var first = series[0];
        var change = first.Score > 0 ? (last.Score / first.Score - 1) * 100 : 0;
        return text + T(" · {0:0.#} → {1:0.#} depuis le {2:d} ({3:+0;-0;0} %)", first.Score, last.Score, first.At.LocalDateTime, change);
    }
}
