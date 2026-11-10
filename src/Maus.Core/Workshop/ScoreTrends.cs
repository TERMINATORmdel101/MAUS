using Maus.Core.Reporting;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Évolution des scores dans le temps : santé du PC après chaque audit, et résultats des tests de l'atelier.</summary>
public static class ScoreTrends
{
    /// <summary>
    /// Score de santé du barème de la 0.8.6 (un sujet vu par deux modules ne compte plus qu'une fois). Les scores des barèmes
    /// précédents restent dans l'historique mais ne sont pas mélangés à la courbe : la même situation n'y vaut pas les mêmes
    /// points, et une version de MAUS passerait pour un progrès du PC.
    /// </summary>
    public const string HealthKind = "health-v3";

    /// <summary>Barème selon la gravité, du 29/09/2026 à la 0.8.5.</summary>
    public const string PreviousHealthKind = "health-v2";

    /// <summary>
    /// Score de l'audit de la semaine (0.8.6) : sans réseau, la liste des logiciels à mettre à jour lui manque. Sa courbe est à
    /// part, pour ne pas faire croire à une amélioration à côté de ceux des audits lancés à la main.
    /// </summary>
    public const string WeeklyHealthKind = "health-weekly";

    /// <summary>Ancien barème (un problème = 12 points, un point à surveiller = 5), avant le 29/09/2026.</summary>
    public const string LegacyHealthKind = "health";

    /// <summary>
    /// Score de santé d'un audit, avec le nombre de problèmes / points à surveiller / optimisations en détail ; <c>null</c>
    /// pour un score partiel (un module en erreur ou en délai dépassé) : ses constats manquent, le score serait meilleur que
    /// la réalité et la courbe montrerait une fausse amélioration. Il n'est donc pas enregistré (les fichiers d'historique
    /// existants restent lisibles tels quels).
    /// </summary>
    public static BenchmarkEntry? HealthEntry(IReadOnlyCollection<ModuleResult> results, DateTimeOffset at)
    {
        var breakdown = HealthScore.Explain(results);
        if (breakdown.IsPartial)
        {
            return null;
        }

        var findings = results.SelectMany(r => r.Findings).ToList();
        return new BenchmarkEntry(HealthKind, breakdown.Score, true, at,
            string.Join('/', findings.Count(f => f.Status == FindingStatus.Problem), findings.Count(f => f.Status == FindingStatus.Warning), findings.Count(f => f.Status == FindingStatus.Improvable)));
    }

    public static string Label(string kind) => kind switch
    {
        HealthKind => T("Score de santé"),
        WeeklyHealthKind => T("Score de santé (audit de la semaine, sans réseau)"),
        PreviousHealthKind => T("Score de santé (barème précédent)"),
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
        _ when kind.StartsWith(DiskKindPrefix, StringComparison.Ordinal) => T("Disque (lecture)") + " · " + kind[DiskKindPrefix.Length..],
        _ when kind.StartsWith(VramKindPrefix, StringComparison.Ordinal) => T("Mémoire vidéo (débit de relecture)") + " · " + kind[VramKindPrefix.Length..],
        _ => kind,
    };

    private const string DiskKindPrefix = "disk-read:";

    private const string VramKindPrefix = "vram:";

    /// <summary>Type de score d'un disque : chacun a sa courbe. Les anciennes entrées restent lisibles sous « disk-read ».</summary>
    public static string DiskKind(string root) => DiskKindPrefix + root;

    /// <summary>Type de score d'une carte graphique : chacune a sa courbe. Les anciennes entrées restent lisibles sous « vram ».</summary>
    public static string VramKind(string adapter) => VramKindPrefix + adapter;

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
        _ when MeasureHistory.IsMeasure(kind) => MeasureHistory.Unit(kind),
        HealthKind or PreviousHealthKind or LegacyHealthKind or WeeklyHealthKind => "/100",
        "ram" or "vram" => T("Go/s"),
        "disk-read" => T("Mo/s"),
        _ when kind.StartsWith(VramKindPrefix, StringComparison.Ordinal) => T("Go/s"),
        _ when kind.StartsWith(DiskKindPrefix, StringComparison.Ordinal) => T("Mo/s"),
        _ => T("points"),
    };

    /// <summary>Valeur que MAUS a pu noter pour ce type : un score de santé tient sur 100, une mesure en pourcentage ou en degrés reste à taille humaine.</summary>
    public static bool IsPossible(string kind, double score)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return kind switch
        {
            HealthKind or PreviousHealthKind or LegacyHealthKind or WeeklyHealthKind => Platform.StoredValues.IsWithin(score, 0, 100),
            _ when MeasureHistory.IsMeasure(kind) => Platform.StoredValues.IsWithin(score, -100, 1000),
            _ => Platform.StoredValues.IsWithin(score, 0, 1e12),
        };
    }

    /// <summary>Scores d'un type, du plus ancien au plus récent (les 50 derniers).</summary>
    public static IReadOnlyList<BenchmarkEntry> Series(IEnumerable<BenchmarkEntry> entries, string kind) =>
        entries.Where(e => e.Kind == kind && e.Stable).OrderBy(e => e.At).TakeLast(50).ToList();

    /// <summary>« 85 /100 le 25/09/2026 · 72 → 85 depuis le 20/09/2026 (+18 %) », ou le seul score connu.</summary>
    /// <summary>
    /// Tendance du score de santé en une phrase simple, en points (et non en pourcentage) : « Score stable depuis le
    /// 05/10/2026 », « En hausse de 6 points depuis le … », « En baisse de 4 points depuis le … ».
    /// </summary>
    public static string DescribeHealth(IReadOnlyList<BenchmarkEntry> series)
    {
        ArgumentNullException.ThrowIfNull(series);

        if (series.Count == 0)
        {
            return T("Pas encore de résultat.");
        }

        var first = series[0];
        var last = series[^1];
        if (series.Count < 2)
        {
            return T("Premier score enregistré le {0:d}.", first.At.LocalDateTime);
        }

        var points = (int)Math.Round(last.Score - first.Score);
        return points switch
        {
            0 => T("Score stable depuis le {0:d} ({1} audits).", first.At.LocalDateTime, series.Count),
            1 => T("En hausse d'1 point depuis le {0:d}.", first.At.LocalDateTime),
            > 1 => T("En hausse de {0} points depuis le {1:d}.", points, first.At.LocalDateTime),
            -1 => T("En baisse d'1 point depuis le {0:d}.", first.At.LocalDateTime),
            _ => T("En baisse de {0} points depuis le {1:d}.", -points, first.At.LocalDateTime),
        };
    }

    /// <summary>
    /// Tendance de la courbe d'après tout l'historique : tant qu'elle ne compte pas deux scores, dit pourquoi elle repart alors
    /// que des audits ont déjà été notés avec le barème précédent.
    /// </summary>
    public static string DescribeHealthHistory(IReadOnlyCollection<BenchmarkEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var series = Series(entries, HealthKind);
        var text = DescribeHealth(series);
        return series.Count < 2 && entries.Any(e => e.Kind == PreviousHealthKind && e.Stable)
            ? text + " " + T("Le barème a changé avec la 0.8.6 : les scores d'avant ne se comparent pas.")
            : text;
    }

    public static string Describe(IReadOnlyList<BenchmarkEntry> series)
    {
        ArgumentNullException.ThrowIfNull(series);

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
