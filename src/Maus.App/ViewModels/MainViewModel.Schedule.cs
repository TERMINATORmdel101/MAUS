using System.Windows.Input;
using Maus.Core.Diagnostics;
using Maus.Core.Engine;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Vos choix : audit automatique chaque semaine (tâche planifiée créée ou retirée à la demande).</summary>
public sealed partial class MainViewModel
{
    private ChoiceOption<DayOfWeek?>? _scheduleChoice;
    private bool _loadingSchedule;

    /// <summary>Jour de la tâche à remettre en place (dossier d'une version précédente du Store, ou droits d'une version précédente) ; <c>null</c> si tout va bien.</summary>
    private DayOfWeek? _outdatedDay;

    /// <summary>La tâche à remettre en place a encore les droits d'une version précédente, sans être sur un ancien dossier.</summary>
    private bool _outdatedRightsOnly;

    /// <summary>Désactivé (par défaut) ou un jour de la semaine, à midi.</summary>
    public IReadOnlyList<ChoiceOption<DayOfWeek?>> ScheduleOptions { get; } =
    [
        new(T("Désactivé"), null),
        new(T("Chaque lundi à midi"), DayOfWeek.Monday),
        new(T("Chaque mardi à midi"), DayOfWeek.Tuesday),
        new(T("Chaque mercredi à midi"), DayOfWeek.Wednesday),
        new(T("Chaque jeudi à midi"), DayOfWeek.Thursday),
        new(T("Chaque vendredi à midi"), DayOfWeek.Friday),
        new(T("Chaque samedi à midi"), DayOfWeek.Saturday),
        new(T("Chaque dimanche à midi"), DayOfWeek.Sunday),
    ];

    public IReadOnlyList<ChoiceOption<Maus.Core.Preferences.WeeklyNotification>> WeeklyNotificationOptions { get; } =
    [
        new(T("Seulement si un problème rouge est trouvé (recommandé)"), Maus.Core.Preferences.WeeklyNotification.RedOnly),
        new(T("À chaque audit, avec le score"), Maus.Core.Preferences.WeeklyNotification.Always),
        new(T("Jamais (le score reste dans l'historique)"), Maus.Core.Preferences.WeeklyNotification.Never),
    ];

    /// <summary>Dernière valeur connue : la lire par le moteur bloquerait le fil de l'interface à chaque affichage.</summary>
    private Maus.Core.Preferences.WeeklyNotification _weeklyNotification;

    /// <summary>Choix protégé : il s'enregistre par le moteur, et la liste est grisée en mode limité comme tous les choix protégés.</summary>
    public ChoiceOption<Maus.Core.Preferences.WeeklyNotification> WeeklyNotificationChoice
    {
        get => WeeklyNotificationOptions.FirstOrDefault(o => o.Value == _weeklyNotification) ?? WeeklyNotificationOptions[0];
        set
        {
            if (value is not null && value != WeeklyNotificationChoice)
            {
                SaveWeeklyNotificationAsync(value.Value).Forget("audit hebdomadaire : notification", ex => StatusText = Maus.Core.Diagnostics.ErrorText.For(ex));
            }
        }
    }

    private void ShowWeeklyNotification(Maus.Core.Preferences.WeeklyNotification level)
    {
        _weeklyNotification = level;
        OnPropertyChanged(nameof(WeeklyNotificationChoice));
    }

    /// <summary>Choix enregistré ou refusé : la liste reprend ce que le magasin a vraiment gardé.</summary>
    private async Task SaveWeeklyNotificationAsync(Maus.Core.Preferences.WeeklyNotification level)
    {
        try
        {
            await UpdatePreferencesAsync(p => p with { WeeklyNotification = level });
        }
        finally
        {
            ShowWeeklyNotification(await Task.Run(() => PreferencesStore.Load().WeeklyNotification));
        }
    }

    /// <summary>La tâche de la semaine, unique sur le PC, a été posée par un autre compte : ce choix-ci la remplacerait.</summary>
    private bool _scheduleOfAnotherAccount;

    public bool HasScheduleNotice => _scheduleOfAnotherAccount;

    public string? ScheduleNotice => _scheduleOfAnotherAccount
        ? T("L'audit automatique actuel n'a pas été programmé depuis ce compte : il tourne pour un autre compte de ce PC.")
        : null;

    public ChoiceOption<DayOfWeek?> ScheduleChoice
    {
        get => _scheduleChoice ?? ScheduleOptions[0];
        set
        {
            if (value is null || value == ScheduleChoice || _loadingSchedule)
            {
                return;
            }

            ChangeScheduleAsync(value).Forget("audit hebdomadaire : changement", ex => StatusText = T("L'audit automatique n'a pas pu être modifié : {0}", Maus.Core.Diagnostics.ErrorText.For(ex)));
        }
    }

    /// <summary>Chemin de MAUS que la tâche devrait lancer ; remplaçable pour les tests.</summary>
    internal string CurrentExecutable { get; set; } = TaskSchedulerClient.Executable;

    /// <summary>La tâche de la semaine n'est plus celle que cette version poserait : l'accueil le dit, et seul son bouton la remet en place.</summary>
    public bool IsScheduleOutdated => _outdatedDay is not null;

    public string ScheduleOutdatedText => _outdatedRightsOnly
        ? T("La tâche de l'audit de la semaine date d'une version précédente de MAUS, qui lui donnait les droits administrateur. Cette version la fait tourner sans ces droits.")
        : Ui.ScheduleOutdated;

    public ICommand MoveScheduleCommand { get; }

    /// <summary>Alerte de l'audit de la semaine restée sans réponse : dite à l'ouverture suivante, jusqu'au prochain audit.</summary>
    public string MissedAlert { get; private set; } = string.Empty;

    public bool HasMissedAlert => MissedAlert.Length > 0;

    private void LoadMissedAlert()
    {
        if (_histories.MissedAlert?.Take() is { } missed)
        {
            MissedAlert = missed.Problems > 1
                ? T("L'audit de la semaine du {0:d} a trouvé {1} problèmes, et son alerte est restée sans réponse. Lancez un audit pour les revoir.", missed.At.LocalDateTime, missed.Problems)
                : T("L'audit de la semaine du {0:d} a trouvé {1} problème, et son alerte est restée sans réponse. Lancez un audit pour le revoir.", missed.At.LocalDateTime, missed.Problems);
        }
    }

    /// <summary>
    /// À l'ouverture, version du Store seulement : lit la tâche sans rien recréer ni rien demander.
    /// Celui qui a choisi l'audit automatique pour ne plus y penser apprend ainsi qu'il ne se lance plus.
    /// </summary>
    internal async Task CheckScheduleAtOpeningAsync()
    {
        if (ScheduledAudit.IsInStoreFolder(CurrentExecutable))
        {
            await LoadScheduleAsync();
        }
    }

    /// <summary>Lit l'état réel de la tâche (elle peut avoir été retirée dans le Planificateur de tâches).</summary>
    private async Task LoadScheduleAsync()
    {
        var state = await Engine.GetScheduleAsync(CancellationToken.None);
        var day = state.Day;
        _scheduleOfAnotherAccount = state.OfAnotherAccount;
        OnPropertyChanged(nameof(ScheduleNotice));
        OnPropertyChanged(nameof(HasScheduleNotice));
        // La tâche d'un autre compte n'est jamais proposée ici : la remplacer passe par la liste, qui le dit.
        var oldFolder = state is { Command: { } command } && ScheduledAudit.IsOlderStoreFolder(command, CurrentExecutable);
        _outdatedDay = state is { Day: { } taskDay, OfAnotherAccount: false } && (oldFolder || state.KeepsOldRights) ? taskDay : null;
        _outdatedRightsOnly = _outdatedDay is not null && !oldFolder;
        OnPropertyChanged(nameof(IsScheduleOutdated));
        OnPropertyChanged(nameof(ScheduleOutdatedText));
        _loadingSchedule = true;
        try
        {
            _scheduleChoice = ScheduleOptions.FirstOrDefault(o => o.Value == day) ?? ScheduleOptions[0];
            OnPropertyChanged(nameof(ScheduleChoice));
        }
        finally
        {
            _loadingSchedule = false;
        }
    }

    /// <summary>« Remettre en place » : même jour, par le moteur (autorisation de Windows), après une question.</summary>
    private async Task MoveScheduleAsync()
    {
        if (_outdatedDay is not { } day)
        {
            return;
        }

        var question = _outdatedRightsOnly
            ? T("La tâche de l'audit de la semaine a encore les droits administrateur que lui donnait une version précédente de MAUS. MAUS va la recréer sur cette version, le même jour et sans ces droits. Windows peut demander votre autorisation.")
            : T("La tâche de l'audit de la semaine pointe encore vers le dossier de l'ancienne version de MAUS. MAUS va la recréer sur cette version, le même jour et sans droits administrateur. Windows peut demander votre autorisation.");
        if (!Confirm(T("Audit automatique chaque semaine ?"), Maus.Core.Reporting.ConfirmationText.Build(question, undo: T("choisissez « Désactivé » dans la même liste : la tâche est retirée du Planificateur."))))
        {
            return;
        }

        try
        {
            var error = await Engine.SetScheduleAsync(day, CancellationToken.None);
            StatusText = error is null ? T("L'audit de la semaine est remis en place sur cette version de MAUS.") : T("La tâche n'a pas pu être créée : {0}", error);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception or System.Security.SecurityException or Maus.Core.Engine.Broker.EngineException)
        {
            StatusText = T("L'audit automatique n'a pas pu être modifié : {0}", Maus.Core.Diagnostics.ErrorText.For(ex));
        }
        finally
        {
            await LoadScheduleAsync();
        }
    }

    private async Task ChangeScheduleAsync(ChoiceOption<DayOfWeek?> choice)
    {
        try
        {
            if (_scheduleOfAnotherAccount
                && !Confirm(T("Tâche d'un autre compte"), T("L'audit automatique actuel n'a pas été programmé depuis ce compte : Windows n'en garde qu'un par PC. Si vous continuez, il sera remplacé ou retiré, et l'autre compte n'aura plus le sien.") + Environment.NewLine + T("Continuer ?")))
            {
                return;
            }

            if (choice.Value is not null
                && !Confirm(T("Audit automatique chaque semaine ?"), Maus.Core.Reporting.ConfirmationText.Build(T("MAUS va créer une tâche dans le Planificateur de tâches de Windows (« {0} ») : si vous êtes connecté à ce moment-là (sinon à la prochaine occasion), MAUS fait un audit en lecture seule, sans fenêtre, en priorité basse et sans réseau : il lit ce que Windows Update a déjà trouvé, et la liste des logiciels à mettre à jour attend votre prochain audit. Il ne s'affiche que s'il trouve un problème rouge. Rien n'est envoyé. Vous pouvez l'arrêter ici à tout moment.", choice.Label), undo: T("choisissez « Désactivé » dans la même liste : la tâche est retirée du Planificateur."))))
            {
                return;
            }

            var error = await Engine.SetScheduleAsync(choice.Value, CancellationToken.None);
            if (choice.Value is not null)
            {
                StatusText = error is null ? T("Audit automatique programmé (« {0} »).", choice.Label) : T("La tâche n'a pas pu être créée : {0}", error);
            }
            else
            {
                StatusText = error is null ? T("Audit automatique arrêté : la tâche planifiée a été retirée.") : T("La tâche n'a pas pu être retirée : {0}", error);
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception or System.Security.SecurityException or Maus.Core.Engine.Broker.EngineException)
        {
            StatusText = T("L'audit automatique n'a pas pu être modifié : {0}", Maus.Core.Diagnostics.ErrorText.For(ex));
        }
        finally
        {
            // Toujours l'état réel : la liste ne doit jamais afficher un jour pour lequel aucune tâche n'existe.
            await LoadScheduleAsync();
        }
    }
}
