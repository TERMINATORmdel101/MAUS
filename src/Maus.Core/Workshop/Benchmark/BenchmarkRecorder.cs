using Maus.Core.Diagnostics;

namespace Maus.Core.Workshop.Benchmark;

/// <summary>Ce que le benchmark a fait de sa passe.</summary>
public enum BenchmarkRecord
{
    /// <summary>Gardée : dans l'historique (sans droits), ou dans le dossier de sortie du moteur.</summary>
    Kept,

    /// <summary>Rien à garder : passe d'essai, ou aucun test mesuré.</summary>
    Skipped,

    /// <summary>Droits administrateur sans dossier de sortie : rien n'est écrit dans le profil de l'utilisateur.</summary>
    NotKept,

    /// <summary>Écriture impossible (historique illisible, disque plein…).</summary>
    Failed,
}

/// <summary>
/// Où le benchmark garde sa passe et son image (0.8.6). Avec un dossier de sortie (<c>--output</c>, benchmark lancé par le
/// moteur) : seulement dans ce dossier. Avec les droits administrateur sans dossier de sortie : nulle part, l'historique et le
/// dossier des images ne sont même pas ouverts. Sans droits : dans l'historique du profil, comme avant.
/// </summary>
/// <param name="outputFolder">Dossier de sortie donné par le moteur, déjà validé (<see cref="BenchmarkOutput.IsValidFolder(string?)"/>).</param>
/// <param name="elevated">Le processus a les droits administrateur.</param>
/// <param name="trial">Passe d'essai : jamais gardée dans l'historique.</param>
/// <param name="screenshotFolder">Dossier des captures d'une passe d'essai (refusé avec les droits administrateur).</param>
/// <param name="history">Historique du profil, ouvert seulement quand il sert.</param>
public sealed class BenchmarkRecorder(string? outputFolder, bool elevated, bool trial, string? screenshotFolder, Func<BenchmarkHistoryStore> history)
{
    /// <summary>Chemin de l'image du résultat, ou <c>null</c> : elle n'est pas enregistrée.</summary>
    public string? ImagePathFor(DateTimeOffset date)
    {
        if (outputFolder is not null)
        {
            return BenchmarkOutput.IsExistingFolder(outputFolder) ? Path.Combine(outputFolder, BenchmarkOutput.ImageFileName) : null;
        }

        if (elevated)
        {
            return null;
        }

        if (trial)
        {
            return screenshotFolder is null ? null : Path.Combine(screenshotFolder, "share.png");
        }

        return history().ImagePathFor(date);
    }

    public BenchmarkRecord Record(BenchmarkReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Tests.Count == 0 || trial)
        {
            return BenchmarkRecord.Skipped;
        }

        try
        {
            if (outputFolder is not null)
            {
                if (!BenchmarkOutput.IsExistingFolder(outputFolder))
                {
                    // Dossier effacé par le moteur (demande abandonnée) ou remplacé : rien n'est écrit, ni ailleurs.
                    Breadcrumbs.Add("benchmark : dossier de sortie absent, passe non enregistrée");
                    return BenchmarkRecord.Failed;
                }

                BenchmarkOutput.WriteReport(outputFolder, report);
                return BenchmarkRecord.Kept;
            }

            if (elevated)
            {
                Breadcrumbs.Add("benchmark : droits administrateur sans dossier de sortie, passe non enregistrée");
                return BenchmarkRecord.NotKept;
            }

            var store = history();
            store.Add(report);
            store.PruneImages();
            return BenchmarkRecord.Kept;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Historique illisible ou plein : la passe reste affichée, ce n'est pas une erreur de Direct3D.
            Breadcrumbs.Add("benchmark : passe non enregistrée (" + ex.GetType().Name + ")");
            return BenchmarkRecord.Failed;
        }
    }
}
