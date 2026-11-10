using System.Runtime.InteropServices;

namespace Maus.Core.Engine.Broker;

/// <summary>
/// Confirmation affichée par le moteur administrateur lui-même (0.8.4, rapport de sécurité du 29/10/2026, point 5) pour les
/// actions les plus sensibles : fermer un programme, toucher au pilote graphique, marquer un constat « voulu », remettre une
/// valeur sensible, toucher à un logiciel de sécurité au démarrage. L'interface tourne sans droits : un programme malveillant du même compte pourrait s'y introduire et
/// envoyer des demandes signées. Il ne peut pas, en revanche, cliquer dans une fenêtre d'un processus administrateur :
/// Windows l'en empêche (isolation des privilèges de l'interface, UIPI, Microsoft Learn « Windows Integrity Mechanism Design »).
/// Depuis la 0.8.6, la question s'affiche dans une fenêtre propre au moteur (<c>Maus.App</c>), dont les boutons ne répondent
/// qu'après un délai (<see cref="EngineConfirmationGate"/>) : une frappe ou un clic destiné à une autre fenêtre ne répond pas.
/// </summary>
public interface IEngineConfirmation
{
    /// <summary>Vrai seulement si l'utilisateur répond « Oui » (« Non » est le choix par défaut).</summary>
    bool Confirm(string title, string message);
}

/// <summary>Aucune fenêtre ne peut poser la question (pas d'application WPF dans ce processus) : la réponse est toujours « Non ».</summary>
public sealed class RefusingEngineConfirmation : IEngineConfirmation
{
    public bool Confirm(string title, string message) => false;
}

/// <summary>
/// Ce que la fenêtre de confirmation du moteur accepte à chaque instant, sans la fenêtre elle-même (0.8.6) : les tests le
/// pilotent avec leur propre horloge. Les deux boutons ne répondent qu'après <see cref="ArmingDelay"/> au premier plan, délai
/// compté de nouveau à chaque retour au premier plan. Au clavier, « Oui » demande en plus que la dernière touche date d'au
/// moins <see cref="ArmingDelay"/> : une frappe en cours ne peut pas y répondre. Sans réponse au bout de
/// <see cref="AnswerLimit"/>, ou à la fermeture de la fenêtre, la réponse est « Non ».
/// </summary>
public sealed class EngineConfirmationGate
{
    /// <summary>Temps au premier plan avant que les boutons répondent.</summary>
    public static TimeSpan ArmingDelay { get; } = TimeSpan.FromSeconds(2);

    /// <summary>Au-delà, la question se ferme d'elle-même sur « Non ».</summary>
    public static TimeSpan AnswerLimit { get; } = TimeSpan.FromMinutes(2);

    private readonly Func<TimeSpan> _clock;
    private readonly TimeSpan _shownAt;

    /// <summary>Retour au premier plan ; <c>null</c> tant que la fenêtre n'y est pas.</summary>
    private TimeSpan? _foregroundSince;
    private TimeSpan _lastKeyAt;

    /// <param name="clock">Temps écoulé, toujours croissant (un chronomètre).</param>
    public EngineConfirmationGate(Func<TimeSpan> clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        _shownAt = clock();
        _lastKeyAt = _shownAt;
    }

    /// <summary><c>true</c> pour « Oui », <c>false</c> pour « Non », <c>null</c> tant que personne n'a répondu.</summary>
    public bool? Answer { get; private set; }

    /// <summary>La fenêtre est au premier plan (son délai est en cours ou passé).</summary>
    public bool IsForeground => _foregroundSince is not null;

    /// <summary>Les boutons répondent : la fenêtre est au premier plan depuis au moins <see cref="ArmingDelay"/>.</summary>
    public bool ButtonsEnabled => Answer is null && _foregroundSince is { } since && _clock() - since >= ArmingDelay;

    /// <summary>Secondes avant que les boutons répondent (0 : ils répondent) ; <c>null</c> : fenêtre au second plan.</summary>
    public int? SecondsBeforeButtons => _foregroundSince is { } since ? SecondsLeft(since) : null;

    /// <summary>Secondes avant que « Oui » réponde au clavier (Espace ou Entrée) ; <c>null</c> : fenêtre au second plan.</summary>
    public int? SecondsBeforeKeyboardYes => SecondsBeforeButtons is { } buttons ? Math.Max(buttons, SecondsLeft(_lastKeyAt)) : null;

    /// <summary>« Oui » accepte Espace ou Entrée : boutons actifs, et aucune touche depuis <see cref="ArmingDelay"/>.</summary>
    public bool AcceptsKeyboardYes => ButtonsEnabled && _clock() - _lastKeyAt >= ArmingDelay;

    /// <summary>La fenêtre revient au premier plan : le délai repart de zéro.</summary>
    public void Activated() => _foregroundSince = _clock();

    /// <summary>La fenêtre passe au second plan : les boutons ne répondent plus.</summary>
    public void Deactivated() => _foregroundSince = null;

    /// <summary>Une touche a été pressée dans la fenêtre : « Oui » au clavier attend de nouveau <see cref="ArmingDelay"/>.</summary>
    public void KeyPressed() => _lastKeyAt = _clock();

    /// <summary>« Oui » choisi (clic, ou touche acceptée par <see cref="AcceptsKeyboardYes"/>).</summary>
    /// <returns>Vrai si la réponse est bien « Oui » ; faux si les boutons ne répondaient pas encore.</returns>
    public bool Yes()
    {
        if (ButtonsEnabled)
        {
            Answer = true;
        }

        return Answer == true;
    }

    /// <summary>« Non », Échap ou fermeture de la fenêtre ; sans effet après une réponse.</summary>
    public void No() => Answer ??= false;

    /// <summary>À appeler régulièrement : passé <see cref="AnswerLimit"/> sans réponse, la réponse devient « Non ».</summary>
    /// <returns>La réponse, s'il y en a une.</returns>
    public bool? Tick()
    {
        if (Answer is null && _clock() - _shownAt >= AnswerLimit)
        {
            Answer = false;
        }

        return Answer;
    }

    private int SecondsLeft(TimeSpan from) =>
        (int)Math.Ceiling(Math.Max(0, (from + ArmingDelay - _clock()).TotalSeconds));
}

/// <summary>Côté interface : autorise le moteur à passer au premier plan pour afficher sa confirmation.</summary>
public static partial class EngineForeground
{
    /// <summary><c>AllowSetForegroundWindow</c> (Microsoft Learn) : sans effet si l'interface n'est pas elle-même au premier plan.</summary>
    public static void Allow(int processId)
    {
        try
        {
            _ = AllowSetForegroundWindow((uint)processId);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Hors de Windows : rien à faire.
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);
}
