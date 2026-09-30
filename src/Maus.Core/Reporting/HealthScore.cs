using static Maus.Core.Localization.Texts;

namespace Maus.Core.Reporting;

/// <summary>Les quatre familles de MAUS, une par lettre du nom : Maintenance, Audit, Updates (mises à jour), Sécurité.</summary>
public enum ModuleFamily
{
    Maintenance,
    Audit,
    Updates,
    Security,
}

/// <summary>Résumé d'une famille pour le tableau de bord.</summary>
/// <param name="Unchecked">Modules de la famille qui n'ont pas pu être vérifiés (erreur ou délai dépassé).</param>
public sealed record FamilySummary(ModuleFamily Family, string Letter, string Name, string Description, int Problems, int Warnings, int Improvements, int Unknown, int Unchecked = 0)
{
    /// <summary>
    /// Tout a été lu et tout est conforme. Un module non vérifié n'a produit aucun constat : sans ce contrôle, la famille
    /// paraîtrait « en ordre » alors qu'une partie n'a pas été lue (donnée illisible = gris, jamais un faux vert).
    /// </summary>
    public bool AllClear => Problems == 0 && Warnings == 0 && Improvements == 0 && Unknown == 0 && Unchecked == 0;
}

/// <summary>Un constat qui coûte des points, avec sa gravité retenue.</summary>
public sealed record ScoreLine(string FindingId, string Title, Severity Gravity, double Points);

/// <summary>Module qui n'a pas pu être vérifié (erreur ou délai dépassé) : ses constats manquent au score.</summary>
/// <param name="Reason">Message d'erreur du moteur d'audit (déjà dans la langue de l'utilisateur).</param>
public sealed record UncheckedModule(string ModuleId, string Title, string Reason);

/// <summary>Détail du score : ce qui a coûté des points, et pourquoi le score est ce qu'il est.</summary>
/// <param name="Lines">Constats de gravité critique, importante ou moyenne, du plus coûteux au moins coûteux.</param>
/// <param name="OptimisationCount">Nombre d'optimisations (gravité faible), comptées ensemble.</param>
/// <param name="OptimisationPoints">Points qu'elles retirent (au plus <see cref="HealthScore.OptimisationCap"/>).</param>
/// <param name="Cap">Plafond appliqué à cause du constat le plus grave, ou <c>null</c>.</param>
public sealed record ScoreBreakdown(int Score, double Points, IReadOnlyList<ScoreLine> Lines, int OptimisationCount, double OptimisationPoints, int? Cap)
{
    /// <summary>Modules non vérifiés : aucun point ne leur est retiré ni accordé, le score ne tient compte que du reste.</summary>
    public IReadOnlyList<UncheckedModule> Unchecked { get; init; } = [];

    /// <summary>Score partiel : au moins un module n'a pas pu être vérifié. Il n'est pas gardé dans l'historique.</summary>
    public bool IsPartial => Unchecked.Count > 0;
}

/// <summary>
/// Score de santé du PC (0 à 100) et résumé par famille. Barème de MAUS (choix de conception, révisé le 29/09/2026 à la
/// demande du porteur : les points retirés dépendent de la gravité) :
/// <list type="bullet">
/// <item>gravité de chaque constat : celle qu'il déclare, sinon celle de sa couleur (rouge = importante, orange = moyenne, bleu = faible) ;</item>
/// <item>points retirés : critique 20, importante 10, moyenne 4, faible 1, les optimisations (faibles) comptant au plus 10 points en tout ;</item>
/// <item>un même sujet contrôlé par deux modules (Secure Boot, modules 1 et 8) ne compte qu'une fois ;</item>
/// <item>le score baisse de moins en moins vite : 100 × e^(−points / 100), une longue liste de petits écarts ne mène pas à 0 ;</item>
/// <item>plafond selon le constat le plus grave : critique = 49 au plus (« à corriger en priorité »), importante = 74 au plus (« à améliorer ») ;</item>
/// <item>un indéterminé, une information ou un constat marqué « voulu » ne coûte rien ;</item>
/// <item>un module en erreur ou en délai dépassé n'a aucun constat : il ne coûte rien, mais le score est dit partiel
/// (<see cref="ScoreBreakdown.Unchecked"/>), n'est pas gardé dans l'historique, et sa famille est « non vérifiée ».</item>
/// </list>
/// </summary>
public static class HealthScore
{
    /// <summary>Points retirés en tout par les optimisations, quel que soit leur nombre.</summary>
    public const double OptimisationCap = 10;

    /// <summary>Plafond du score quand un constat critique est présent (sous « à améliorer »).</summary>
    public const int CriticalCap = 49;

    /// <summary>Plafond du score quand un constat de gravité importante est présent (sous « bon »).</summary>
    public const int HighCap = 74;

    /// <summary>Constats qui décrivent le même sujet que le premier de la liste : seul le plus grave compte.</summary>
    private static readonly string[][] SameSubject =
    [
        ["M01.secure-boot", "M08.secure-boot"],
    ];

    /// <summary>Points retirés par un constat selon sa gravité retenue.</summary>
    public static double PointsFor(Severity gravity) => gravity switch
    {
        Severity.Critical => 20,
        Severity.High => 10,
        Severity.Medium => 4,
        Severity.Low => 1,
        _ => 0,
    };

    /// <summary>
    /// Gravité retenue : celle du constat quand il en déclare une, sinon celle de sa couleur. Un constat conforme,
    /// informatif, indéterminé ou marqué « voulu » (passé en information) n'a pas de gravité.
    /// </summary>
    public static Severity GravityOf(Finding finding)
    {
        if (finding.Status is not (FindingStatus.Problem or FindingStatus.Warning or FindingStatus.Improvable))
        {
            return Severity.Info;
        }

        if (finding.Severity > Severity.Info)
        {
            return finding.Severity;
        }

        return finding.Status switch
        {
            FindingStatus.Problem => Severity.High,
            FindingStatus.Warning => Severity.Medium,
            _ => Severity.Low,
        };
    }

    /// <summary>Nom de la gravité, pour le détail du score.</summary>
    public static string GravityName(Severity gravity) => gravity switch
    {
        Severity.Critical => T("critique"),
        Severity.High => T("importante"),
        Severity.Medium => T("moyenne"),
        Severity.Low => T("faible"),
        _ => T("aucune"),
    };

    /// <summary>Score avant plafond : 100 × e^(−points / 100), arrondi.</summary>
    public static int Uncapped(double points) => (int)Math.Round(100 * Math.Exp(-points / 100), MidpointRounding.AwayFromZero);

    /// <summary>Modules dont la détection a échoué (erreur ou délai dépassé) : rien n'y a été lu.</summary>
    public static IReadOnlyList<UncheckedModule> UncheckedModules(IEnumerable<ModuleResult> results) =>
        results.Where(r => r.Error is not null).Select(r => new UncheckedModule(r.ModuleId, r.Title, r.Error!)).ToList();

    /// <summary>Phrase qui signale un score partiel, ou <c>null</c> quand tous les modules ont été vérifiés.</summary>
    public static string? PartialNote(ScoreBreakdown breakdown) => breakdown.Unchecked.Count switch
    {
        0 => null,
        1 => T("Score partiel : 1 module n'a pas pu être vérifié ({0}). Le score ne tient compte que du reste.", breakdown.Unchecked[0].Title),
        var count => T("Score partiel : {0} modules n'ont pas pu être vérifiés ({1}). Le score ne tient compte que du reste.", count,
            string.Join(", ", breakdown.Unchecked.Select(u => u.Title))),
    };

    /// <summary>
    /// Détail d'un score partiel pour « Pourquoi ce score ? » : la phrase de <see cref="PartialNote"/>, chaque module non
    /// vérifié avec sa raison, puis ce que cela change ; <c>null</c> quand tous les modules ont été vérifiés.
    /// </summary>
    public static string? PartialDetail(ScoreBreakdown breakdown)
    {
        if (PartialNote(breakdown) is not { } note)
        {
            return null;
        }

        var lines = new List<string> { note };
        lines.AddRange(breakdown.Unchecked.Select(u => "• " + T("{0} : {1}", u.Title, u.Reason)));
        lines.Add(T("Un module non vérifié ne retire ni n'ajoute de points : ses constats manquent. Ce score partiel n'est pas gardé dans l'historique, pour ne pas faire croire à une amélioration. Relancez l'audit pour obtenir un score complet."));
        return string.Join(Environment.NewLine, lines);
    }

    public static ScoreBreakdown Explain(IEnumerable<ModuleResult> results)
    {
        var all = results.ToList();
        var costly = all.SelectMany(r => r.Findings)
            .Select(f => (Finding: f, Gravity: GravityOf(f)))
            .Where(x => x.Gravity > Severity.Info)
            .ToList();

        // Même sujet vu par deux modules : on garde le constat le plus grave, une seule fois.
        foreach (var group in SameSubject)
        {
            var duplicates = costly.Where(x => group.Contains(x.Finding.Id, StringComparer.Ordinal)).OrderByDescending(x => x.Gravity).Skip(1).ToList();
            costly.RemoveAll(x => duplicates.Contains(x));
        }

        var lines = costly.Where(x => x.Gravity >= Severity.Medium)
            .Select(x => new ScoreLine(x.Finding.Id, x.Finding.Title, x.Gravity, PointsFor(x.Gravity)))
            .OrderByDescending(l => l.Points)
            .ThenBy(l => l.Title, StringComparer.CurrentCulture)
            .ToList();
        var optimisations = costly.Count(x => x.Gravity == Severity.Low);
        var optimisationPoints = Math.Min(OptimisationCap, optimisations * PointsFor(Severity.Low));
        var points = lines.Sum(l => l.Points) + optimisationPoints;

        var score = Uncapped(points);
        int? cap = lines.Any(l => l.Gravity == Severity.Critical) ? CriticalCap
            : lines.Any(l => l.Gravity == Severity.High) ? HighCap
            : null;
        if (cap is { } limit && score > limit)
        {
            score = limit;
        }
        else
        {
            cap = null;
        }

        // Un module non vérifié ne retire ni n'accorde de points (aucune donnée inventée) : il est seulement signalé.
        return new ScoreBreakdown(Math.Clamp(score, 0, 100), points, lines, optimisations, optimisationPoints, cap)
        {
            Unchecked = UncheckedModules(all),
        };
    }

    private static readonly Dictionary<string, ModuleFamily> Families = new(StringComparer.Ordinal)
    {
        ["M01"] = ModuleFamily.Audit,
        ["M02"] = ModuleFamily.Maintenance,
        ["M03"] = ModuleFamily.Updates,
        ["M04"] = ModuleFamily.Security,
        ["M05"] = ModuleFamily.Maintenance,
        ["M06"] = ModuleFamily.Maintenance,
        ["M07"] = ModuleFamily.Maintenance,
        ["M08"] = ModuleFamily.Updates,
        ["M09"] = ModuleFamily.Updates,
        ["M10"] = ModuleFamily.Maintenance,
        ["M11"] = ModuleFamily.Maintenance,
        ["M12"] = ModuleFamily.Maintenance,
        ["M13"] = ModuleFamily.Security,
        ["M14"] = ModuleFamily.Maintenance,
        ["M15"] = ModuleFamily.Updates,
        ["M16"] = ModuleFamily.Maintenance,
        ["M17"] = ModuleFamily.Maintenance,
        ["M18"] = ModuleFamily.Maintenance,
        ["M19"] = ModuleFamily.Security,
        ["M20"] = ModuleFamily.Updates,
    };

    public static ModuleFamily FamilyOf(string moduleId) => Families.GetValueOrDefault(moduleId, ModuleFamily.Maintenance);

    public static int Compute(IEnumerable<ModuleResult> results) => Explain(results).Score;

    /// <summary>Mot qui qualifie le score, pour l'afficher sous le chiffre.</summary>
    public static string Describe(int score) => score switch
    {
        >= 90 => T("excellent"),
        >= 75 => T("bon"),
        >= 50 => T("à améliorer"),
        _ => T("à corriger en priorité"),
    };

    public static IReadOnlyList<FamilySummary> Summaries(IEnumerable<ModuleResult> results)
    {
        var list = results.ToList();
        return Enum.GetValues<ModuleFamily>().Select(family =>
        {
            var modules = list.Where(r => FamilyOf(r.ModuleId) == family).ToList();
            var findings = modules.SelectMany(r => r.Findings).ToList();
            var (letter, name, description) = family switch
            {
                ModuleFamily.Maintenance => ("M", T("Maintenance"), T("Démarrage, santé du matériel, périphériques, réseau, alimentation, mémoire")),
                ModuleFamily.Audit => ("A", T("Audit"), T("Modifications risquées faites par des scripts ou des logiciels")),
                ModuleFamily.Updates => ("U", T("Mises à jour"), T("Windows Update, logiciels, pilote graphique, BIOS et microcode")),
                _ => ("S", T("Sécurité"), T("Protections du processeur, intégrité de la mémoire, confidentialité, sauvegardes")),
            };
            return new FamilySummary(family, letter, name, description,
                findings.Count(f => f.Status == FindingStatus.Problem),
                findings.Count(f => f.Status == FindingStatus.Warning),
                findings.Count(f => f.Status == FindingStatus.Improvable),
                findings.Count(f => f.Status == FindingStatus.Unknown),
                modules.Count(r => r.Error is not null));
        }).ToList();
    }
}
