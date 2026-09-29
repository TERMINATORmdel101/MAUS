using System.Collections.ObjectModel;
using System.Windows.Input;
using Maus.Core;
using Maus.Core.Fixes;
using Maus.Core.Reporting;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Onglet Historique : séances du journal, annulation d'une séance ou d'une seule correction.</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<SessionViewModel> Sessions { get; } = [];

    public ICommand RefreshJournalCommand { get; }

    public string JournalSummary => Sessions.Count == 0
        ? T("Aucune séance de corrections : MAUS n'a encore rien modifié sur ce PC.")
        : T("Chaque séance, ou chaque correction, peut être annulée : MAUS remet les valeurs d'origine enregistrées avant d'écrire. Une valeur que vous (ou Windows) avez changée depuis est laissée telle quelle.");

    private async Task RefreshJournalAsync()
    {
        IReadOnlyList<JournalSession> sessions;
        try
        {
            sessions = await Task.Run(() => FileJournalStore.CreateDefault().List());
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            sessions = [];
        }

        Sessions.Clear();
        foreach (var session in sessions)
        {
            Sessions.Add(new SessionViewModel(session, RevertAsync));
        }

        OnPropertyChanged(nameof(JournalSummary));
    }

    private async Task RevertAsync(SessionViewModel session, string? changeId)
    {
        if (IsApplying)
        {
            FixReport = T("Corrections en cours : attendez qu'elles soient finies avant d'annuler.");
            return;
        }

        var what = changeId is null
            ? T("MAUS va remettre les valeurs d'origine de la séance du {0}.", session.Header)
            : T("MAUS va remettre les valeurs d'origine de la correction « {0} ».", session.Session.Entries.First(e => e.ChangeId == changeId).ChangeTitle);
        if (!session.CanRevert || !Confirm(T("Annuler ?"), what + Environment.NewLine + T("Continuer ?")))
        {
            return;
        }

        try
        {
            var context = _lastContext ?? await Task.Run(AuditContext.CreateDefault);
            var engine = new FixEngine(await Task.Run(() => FixContext.CreateDefault(context)));
            var result = await Task.Run(() => engine.Revert(session.Session.Id, changeId: changeId));
            FixReport = result.Error ?? T("Annulation :") + Environment.NewLine + string.Join(Environment.NewLine,
                result.Entries.Select(e => $"• [{Labels.Of(e.Status)}] {e.Title} : {e.Message}"));
            await RunAuditAsync();
        }
        catch (Exception ex)
        {
            FixReport = T("L'annulation a échoué : {0}", ex.Message);
        }
    }
}
