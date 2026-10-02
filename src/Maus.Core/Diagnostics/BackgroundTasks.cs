namespace Maus.Core.Diagnostics;

/// <summary>
/// Tâches lancées sans être attendues (un choix dans une liste déroulante, un onglet qui s'ouvre) : leur erreur ne disparaît
/// plus en silence. Elle est notée dans les dernières actions et dans le journal des erreurs, puis signalée à l'écran par
/// l'appelant.
/// </summary>
public static class BackgroundTasks
{
    /// <summary>Laisse la tâche se terminer seule, sans jamais perdre son erreur.</summary>
    /// <param name="task">La tâche lancée.</param>
    /// <param name="what">Ce qu'elle fait (texte technique pour le journal, non traduit).</param>
    /// <param name="onError">
    /// Affiche l'erreur à l'utilisateur (zone de statut concernée) ; appelé sur le fil de l'appelant, donc sur celui de
    /// l'interface quand la tâche est lancée depuis la fenêtre.
    /// </param>
    public static void Forget(this Task task, string what, Action<Exception>? onError = null) =>
        _ = ObserveAsync(task, what, onError, CrashLog.Write);

    /// <summary>Attend la tâche ; ne se termine jamais en erreur (tout est noté, rien n'est relancé).</summary>
    internal static async Task ObserveAsync(Task task, string what, Action<Exception>? onError, Func<Exception, string, string?> log)
    {
        ArgumentNullException.ThrowIfNull(task);
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Annulée à la demande (fermeture, arrêt) : ce n'est pas une erreur.
            Breadcrumbs.Add(what + " : annulé");
        }
        catch (Exception ex)
        {
            Breadcrumbs.Add(what + " : erreur imprévue (" + ex.GetType().Name + ")");
            log(ex, what + " : erreur imprévue, MAUS continue");
            try
            {
                onError?.Invoke(ex);
            }
            catch (Exception reportError)
            {
                // L'affichage du message a lui-même échoué (fenêtre fermée entre-temps…) : la trace suffit.
                log(reportError, what + " : message d'erreur impossible à afficher");
            }
        }
    }
}
