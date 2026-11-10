using System.Windows;
using System.Windows.Threading;
using Maus.Core.Diagnostics;
using Maus.Core.Engine.Broker;

namespace Maus.App;

/// <summary>
/// Confirmation du moteur administrateur (0.8.6) : la question arrive sur un fil de fond du moteur, elle s'affiche dans la
/// fenêtre du moteur (<see cref="Views.EngineConfirmationWindow"/>) sur le fil de l'application WPF, et la réponse revient ici.
/// Sans application WPF dans ce processus, si son fil s'arrête, ou si la fenêtre ne peut pas s'afficher : « Non ».
/// </summary>
public sealed class EngineConfirmationPrompt : IEngineConfirmation
{
    private readonly Func<Dispatcher?> _dispatcher;
    private readonly Func<string, string, bool> _ask;

    public EngineConfirmationPrompt()
        : this(() => Application.Current?.Dispatcher, Views.EngineConfirmationWindow.Ask)
    {
    }

    /// <param name="dispatcher">Fil de l'application, s'il y en a une.</param>
    /// <param name="ask">Pose la question sur ce fil (les tests le remplacent : aucune fenêtre ne s'affiche).</param>
    internal EngineConfirmationPrompt(Func<Dispatcher?> dispatcher, Func<string, string, bool> ask)
    {
        _dispatcher = dispatcher;
        _ask = ask;
    }

    public bool Confirm(string title, string message)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            var dispatcher = _dispatcher();
            if (dispatcher is null || dispatcher.HasShutdownStarted)
            {
                Breadcrumbs.Add("moteur : aucune fenêtre pour la confirmation, réponse Non");
                return false;
            }

            // Un fil de l'application occupé au-delà du délai de réponse : « Non » (le délai court jusqu'au début de l'affichage).
            return dispatcher.CheckAccess()
                ? _ask(title, message)
                : dispatcher.Invoke(() => _ask(title, message), DispatcherPriority.Send, CancellationToken.None, EngineConfirmationGate.AnswerLimit);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Breadcrumbs.Add("moteur : fenêtre de confirmation impossible (" + ex.GetType().Name + "), réponse Non");
            return false;
        }
    }
}
