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
public sealed record FamilySummary(ModuleFamily Family, string Letter, string Name, string Description, int Problems, int Warnings, int Improvements, int Unknown);

/// <summary>
/// Score de santé du PC (0 à 100) et résumé par famille. Choix de conception de MAUS : un problème retire 12 points,
/// un point à surveiller 5, une optimisation 1 (au plus 20 points pour l'ensemble des optimisations) ; un indéterminé ne coûte rien.
/// </summary>
public static class HealthScore
{
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

    public static int Compute(IEnumerable<ModuleResult> results)
    {
        var findings = results.SelectMany(r => r.Findings).ToList();
        var penalty = (12 * findings.Count(f => f.Status == FindingStatus.Problem))
                      + (5 * findings.Count(f => f.Status == FindingStatus.Warning))
                      + Math.Min(20, findings.Count(f => f.Status == FindingStatus.Improvable));
        return Math.Clamp(100 - penalty, 0, 100);
    }

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
            var findings = list.Where(r => FamilyOf(r.ModuleId) == family).SelectMany(r => r.Findings).ToList();
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
                findings.Count(f => f.Status == FindingStatus.Unknown));
        }).ToList();
    }
}
