using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Vos choix : audit automatique chaque semaine (tâche planifiée créée ou retirée à la demande).</summary>
public sealed partial class MainViewModel
{
    private ChoiceOption<DayOfWeek?>? _scheduleChoice;
    private bool _loadingSchedule;

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

    public ChoiceOption<DayOfWeek?> ScheduleChoice
    {
        get => _scheduleChoice ?? ScheduleOptions[0];
        set
        {
            if (value is null || value == ScheduleChoice || _loadingSchedule)
            {
                return;
            }

            _ = ChangeScheduleAsync(value);
        }
    }

    /// <summary>Lit l'état réel de la tâche (elle peut avoir été retirée dans le Planificateur de tâches).</summary>
    private async Task LoadScheduleAsync()
    {
        var (day, command) = await TaskSchedulerClient.GetAsync();
        if (day is not null && command is not null && TaskSchedulerClient.Refusal(command) is not null)
        {
            // Tâche créée par une version précédente depuis un dossier non protégé : à retirer.
            StatusText = T("La tâche d'audit automatique lance MAUS depuis un dossier non protégé ({0}) avec les droits administrateur : choisissez « Désactivé » pour la retirer, puis installez MAUS dans Program Files avant de la recréer.",
                System.IO.Path.GetDirectoryName(command) ?? command);
        }

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

    private async Task ChangeScheduleAsync(ChoiceOption<DayOfWeek?> choice)
    {
        string? error;
        if (choice.Value is { } day)
        {
            if (TaskSchedulerClient.Refusal() is { } refusal)
            {
                StatusText = T("La tâche n'a pas pu être créée : {0}", refusal);
                await LoadScheduleAsync();
                return;
            }

            if (!Confirm(T("Audit automatique chaque semaine ?"),
                T("MAUS va créer une tâche dans le Planificateur de tâches de Windows (« {0} ») : si vous êtes connecté à ce moment-là (sinon à la prochaine occasion), MAUS fait un audit en lecture seule, sans fenêtre et en priorité basse. Il ne s'affiche que s'il trouve un problème rouge. Rien n'est envoyé. Vous pouvez l'arrêter ici à tout moment.", choice.Label)
                + Environment.NewLine + Environment.NewLine + T("Continuer ?")))
            {
                OnPropertyChanged(nameof(ScheduleChoice));
                return;
            }

            error = await TaskSchedulerClient.CreateAsync(day);
            StatusText = error is null ? T("Audit automatique programmé (« {0} »).", choice.Label) : T("La tâche n'a pas pu être créée : {0}", error);
        }
        else
        {
            error = await TaskSchedulerClient.DeleteAsync();
            StatusText = error is null ? T("Audit automatique arrêté : la tâche planifiée a été retirée.") : T("La tâche n'a pas pu être retirée : {0}", error);
        }

        await LoadScheduleAsync();
    }
}
