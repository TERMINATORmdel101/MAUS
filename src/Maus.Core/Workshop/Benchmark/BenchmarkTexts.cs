using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop.Benchmark;

/// <summary>Textes d'un résultat du benchmark, dans la langue du moment : le benchmark et la page Benchmark de MAUS disent la même chose.</summary>
public static class BenchmarkTexts
{
    public static string Name(string id) => id switch
    {
        "fractal" => T("Forge fractale (calcul)"),
        "materials" => T("Cabinet de curiosités (textures)"),
        "galaxy" => T("Collision galactique (bande passante)"),
        "ring" => T("Anneau de la géante (géométrie)"),
        "battle" => T("Champ de bataille (effets)"),
        "raytracing" => T("Galerie des glaces (lancer de rayons)"),
        "cpu-render" => T("Rendu sur tous les cœurs"),
        "cpu-single" => T("Rendu sur un seul cœur"),
        "cpu-vector" => T("Calcul vectoriel"),
        _ => id,
    };

    /// <summary>Unité de la mesure d'un test, d'après son identifiant.</summary>
    public static string Unit(string id) => id switch
    {
        "cpu-render" or "cpu-single" => T("millions de rayons par seconde"),
        "cpu-vector" => T("milliards d'itérations par seconde"),
        _ => T("images par seconde"),
    };

    public static string Points(double score) => score > 0 ? score.ToString("N0", Culture) : "—";

    /// <summary>Phrase du bilan : à quelle édition appartiennent ces points.</summary>
    public static string EditionNote(BenchmarkReport report) =>
        BenchmarkScoring.EditionOf(report) < BenchmarkScoring.CurrentEdition
            ? T("Édition {0} du benchmark, une version précédente : ses points ne se comparent pas à ceux de l'édition actuelle ({1}).", BenchmarkScoring.EditionOf(report), BenchmarkScoring.CurrentEdition)
            : T("Édition {0} du benchmark : ses points ne se comparent pas à ceux des éditions précédentes.", BenchmarkScoring.EditionOf(report));

    /// <summary>Avertissement sous le dernier résultat, quand il vient d'une version précédente du benchmark ; vide sinon.</summary>
    public static string OldEditionWarning(BenchmarkReport report)
    {
        var edition = BenchmarkScoring.EditionOf(report);
        if (edition >= BenchmarkScoring.CurrentEdition)
        {
            return "";
        }

        var warning = T("Ce résultat vient d'une ancienne version du benchmark. Relancez-le : les nouveaux points ne se comparent pas à ceux-ci.");
        return edition < BenchmarkScoring.CpuRecalibratedEdition ? warning + " " + T("Le processeur y comptait environ 4 fois trop de points.") : warning;
    }

    /// <summary>« 34,1 images/s », ou la mesure d'un test du processeur avec son unité.</summary>
    public static string Measure(BenchmarkTestResult test)
    {
        ArgumentNullException.ThrowIfNull(test);

        if (test.Device != "cpu")
        {
            return T("{0} images/s", test.Value.ToString("0.0", Culture));
        }

        // Test inconnu de cette version (code collé d'une version plus récente) : l'unité écrite avec le résultat.
        var unit = test.Id is "cpu-render" or "cpu-single" or "cpu-vector" ? Unit(test.Id) : test.Unit;
        return test.Value.ToString("0.00", Culture) + " " + unit;
    }
}
