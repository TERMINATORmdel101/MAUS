using Maus.Core.Platform;

namespace Maus.Core.Fixes;

public sealed record ExplorerRestartResult(bool Succeeded, string Message);

/// <summary>
/// Redémarrage de l'Explorateur, pour que les corrections de la barre des tâches prennent effet.
/// MAUS arrête l'Explorateur et laisse Windows le relancer (comme après un plantage), puis vérifie qu'il est revenu.
/// </summary>
public sealed class ExplorerRestart(IShellProcesses shell, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private const string Manual =
        "Pour le relancer vous-même : Ctrl+Maj+Échap, puis « Exécuter une nouvelle tâche », tapez explorer et validez. Ou fermez puis rouvrez votre session.";

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public static string Warning =>
        "La barre des tâches et le Bureau vont disparaître quelques secondes, et les fenêtres de dossiers ouvertes seront fermées. " +
        "Vos documents et applications ne sont pas touchés.";

    public async Task<ExplorerRestartResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!shell.AutoRestartEnabled())
        {
            return new(false, "Windows est réglé pour ne pas relancer l'Explorateur automatiquement (AutoRestartShell = 0) : MAUS ne l'arrête pas. " +
                              "Fermez puis rouvrez votre session pour appliquer les corrections.");
        }

        if (shell.Count() == 0)
        {
            return new(false, "L'Explorateur ne tourne pas dans cette session. " + Manual);
        }

        shell.StopShell();
        for (var waited = TimeSpan.Zero; waited < TimeSpan.FromSeconds(15); waited += TimeSpan.FromMilliseconds(500))
        {
            await _delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            if (shell.Count() > 0)
            {
                return new(true, "L'Explorateur a redémarré : les corrections de la barre des tâches sont appliquées.");
            }
        }

        return new(false, "L'Explorateur n'est pas revenu tout seul. " + Manual);
    }
}
