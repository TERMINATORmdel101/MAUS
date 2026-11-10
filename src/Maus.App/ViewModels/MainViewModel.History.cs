using System.Collections.ObjectModel;
using System.Windows.Input;
using Maus.Core.Fixes;
using Maus.Core.Preferences;
using Maus.Core.Reporting;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Une fréquence d'écran gardée, et le bouton pour y revenir.</summary>
public sealed record KeptRefreshRow(string Text, string UndoLabel, ICommand Undo)
{
    public override string ToString() => Text;
}

/// <summary>Onglet Historique : séances du journal, annulation d'une séance ou d'une seule correction.</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<SessionViewModel> Sessions { get; } = [];

    /// <summary>Fréquences d'écran gardées après un essai : chacune peut revenir à l'ancienne valeur.</summary>
    public ObservableCollection<KeptRefreshRow> KeptRefreshRates { get; } = [];

    public bool HasKeptRefreshRates => KeptRefreshRates.Count > 0;

    public string KeptRefreshStatus { get; private set; } = string.Empty;

    private async Task LoadKeptRefreshRatesAsync()
    {
        var kept = await Task.Run(() => PreferencesStore.Load().KeptRefreshRates);
        KeptRefreshRates.Clear();
        foreach (var entry in kept)
        {
            KeptRefreshRates.Add(new KeptRefreshRow(
                T("{0} : {1} → {2} Hz, le {3:d}", entry.Label, entry.FromHz, entry.ToHz, entry.At.LocalDateTime),
                T("Revenir à {0} Hz", entry.FromHz),
                new AsyncCommand(() => UndoRefreshRateAsync(entry))));
        }

        OnPropertyChanged(nameof(HasKeptRefreshRates));
    }

    private async Task UndoRefreshRateAsync(Maus.Core.Preferences.KeptRefreshRate kept)
    {
        if (!Confirm(T("Revenir à {0} Hz ?", kept.FromHz), ConfirmationText.Build(T("MAUS va remettre {0} à {1} Hz au lieu de {2} Hz.", kept.Label, kept.FromHz, kept.ToHz), undo: T("« Essayer la fréquence maximale » reste proposé dans les constats."))))
        {
            return;
        }

        var outcome = Maus.Core.Modules.M14Display.RefreshRateUndo.Run(kept);
        KeptRefreshStatus = outcome.Message;
        OnPropertyChanged(nameof(KeptRefreshStatus));
        if (outcome.Finished)
        {
            await Task.Run(() => PreferencesStore.Update(p => p with { KeptRefreshRates = [.. p.KeptRefreshRates.Where(k => k != kept)] }));
            await LoadKeptRefreshRatesAsync();
        }
    }

    public ICommand RefreshJournalCommand { get; }

    public string JournalSummary
    {
        get
        {
            var summary = !_journalRead
                ? CanReadJournalSilently()
                    ? T("Le journal n'a pas encore été lu : il s'affichera après un audit ou une correction.")
                    : T("Le journal se lit avec les droits administrateur : cliquez sur « Toutes les fonctionnalités », en haut de la fenêtre, puis sur « Actualiser ».")
                : Sessions.Count == 0 && _journalNotice is null
                ? T("Aucune séance de corrections : MAUS n'a encore rien modifié sur ce PC.")
                : T("Chaque séance, ou chaque correction, peut être annulée : MAUS remet les valeurs d'origine enregistrées avant d'écrire. Une valeur que vous (ou Windows) avez changée depuis est laissée telle quelle.");
            return _journalNotice is null ? summary : summary + "\n" + _journalNotice;
        }
    }

    private static string RevertQuestion(List<SessionViewModel> sessions, bool today)
    {
        if (today)
        {
            return Plural(sessions.Count, T("MAUS va remettre les valeurs d'origine de la séance de corrections d'aujourd'hui."), T("MAUS va remettre les valeurs d'origine des {0} séances de corrections d'aujourd'hui, de la plus récente à la plus ancienne."));
        }

        var dates = string.Join(", ", sessions.Select(s => s.Session.CreatedAt.ToLocalTime().ToString("d", Culture)).Distinct());
        return sessions.Count > 1
            ? T("MAUS va remettre les valeurs d'origine des {0} séances de corrections, de la plus récente à la plus ancienne : {1}.", sessions.Count, dates)
            : T("MAUS va remettre les valeurs d'origine de la séance de corrections du {0}.", dates);
    }

    /// <summary>Audit en échec après une annulation : les constats affichés sont encore ceux d'avant, il faut le dire.</summary>
    private static string AuditFailedNote() =>
        Environment.NewLine + Environment.NewLine + T("L'audit de vérification a échoué : les constats affichés sont encore ceux d'avant l'annulation. Relancez l'audit pour voir l'état actuel.");

    /// <summary>Séances du journal qui n'ont pas pu être lues (laissées intactes), ou journal illisible.</summary>
    private string? _journalNotice;

    private bool _reverting;
    private bool _journalRead;
    private string? _accountSid;

    /// <summary>Écritures en cours : corrections ou annulation. Fermer MAUS à ce moment-là pose la question.</summary>
    public bool IsWriting => IsApplying || _reverting;

    /// <param name="withoutAsking">Rien n'a été demandé par l'utilisateur : moteur arrêté, le journal reste tel qu'il était lu, sans aucune autorisation demandée.</param>
    private async Task RefreshJournalAsync(bool withoutAsking = false)
    {
        IReadOnlyList<JournalSession> sessions;
        try
        {
            var listing = withoutAsking ? await BrowseJournalIfRunningAsync() : await Engine.BrowseJournalAsync(CancellationToken.None);
            if (listing is null)
            {
                return;
            }

            sessions = listing.Sessions;
            _journalNotice = listing.UnreadableNotice;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or JournalUnsafeException
            || (ex is Maus.Core.Engine.Broker.EngineException and not Maus.Core.Engine.Broker.EngineDeclinedException and not Maus.Core.Engine.Broker.EngineUnavailableException))
        {
            sessions = [];
            _journalNotice = T("Le journal des corrections n'a pas pu être lu : {0}", Maus.Core.Diagnostics.ErrorText.For(ex));
        }

        // Le journal est commun au PC : chaque séance est lue par rapport au compte de cette session Windows.
        _accountSid ??= await Task.Run(Maus.Core.Platform.SessionUser.Sid);

        var comeBack = MarkComeBack(sessions);
        Sessions.Clear();
        foreach (var session in sessions)
        {
            Sessions.Add(new SessionViewModel(session, RevertAsync, _accountSid, comeBack));
        }

        // Seulement ici : un journal jamais lu (autorisation refusée, moteur tombé) ne doit pas passer pour un journal vide.
        _journalRead = true;

        // Un retour arrière raté ne doit jamais passer inaperçu : la modification est peut-être encore en place.
        // Dans le profil d'un autre compte, ce compte-ci n'y peut rien : l'Historique le dit, sans alerte à chaque audit.
        if (sessions.Any(s => s.HasFailedRollback && s.CanRevertAs(_accountSid)))
        {
            StatusText = T("Attention : une correction a échoué et son retour arrière aussi. La valeur modifiée est peut-être encore en place : ouvrez l'Historique et cliquez sur « Annuler » pour réessayer.");
        }

        await LoadKeptRefreshRatesAsync();
        OnPropertyChanged(nameof(JournalSummary));
        OnPropertyChanged(nameof(HasRevertibleToday));
        OnPropertyChanged(nameof(RevertTodayLabel));
        OnPropertyChanged(nameof(HasRevertibleEarlier));
        OnPropertyChanged(nameof(RevertEverythingLabel));
        if (_windowsUndidShown)
        {
            await UpdateWindowsUndidAsync();
        }
    }

    /// <summary>Le journal, seulement s'il se lit sans rien demander ; <c>null</c> : moteur arrêté. MAUS lancé en administrateur n'a pas de moteur à part.</summary>
    private async Task<Maus.Core.Fixes.JournalListing?> BrowseJournalIfRunningAsync() =>
        EngineHost.OnDemand is { } onDemand
            ? await onDemand.BrowseJournalIfRunningAsync(CancellationToken.None)
            : await Engine.BrowseJournalAsync(CancellationToken.None);

    /// <summary>
    /// Corrections écrites un jour et que le plan propose de nouveau : « en place » serait faux, et les cartes le disent.
    /// Renvoie leurs identifiants.
    /// </summary>
    private HashSet<string> MarkComeBack(IReadOnlyList<JournalSession> sessions)
    {
        var applied = sessions
            .SelectMany(s => s.Entries.Where(e => e.State == EntryState.Applied).Select(e => (e.ChangeId, s.CreatedAt)))
            .GroupBy(x => x.ChangeId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Max(x => x.CreatedAt), StringComparer.Ordinal);
        var comeBack = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in Changes)
        {
            change.AppliedOn = applied.TryGetValue(change.Change.Id, out var date) ? date : null;
            if (change.AppliedOn is not null)
            {
                comeBack.Add(change.Change.Id);
            }
        }

        OnPropertyChanged(nameof(HasComeBackChanges));
        OnPropertyChanged(nameof(RecheckComeBackLabel));
        return comeBack;
    }

    public bool HasComeBackChanges => Changes.Any(c => c.HasComeBack);

    public string RecheckComeBackLabel => T("Re-cocher ce qui a été remis ({0})", Changes.Count(c => c.HasComeBack));

    private ICommand? _recheckComeBack;

    public ICommand RecheckComeBackCommand => _recheckComeBack ??= new AsyncCommand(() =>
    {
        foreach (var change in Changes.Where(c => c.HasComeBack))
        {
            change.IsSelected = true;
            ShowMoreChanges |= MoreChanges.Contains(change);
        }
    });

    /// <summary>
    /// Séances qu'une annulation groupée peut défaire, la plus récente d'abord : on défait dans l'ordre inverse. Celles d'un
    /// autre compte n'en font pas partie : elles ne s'annulent qu'une par une, après l'avertissement. Il en va de même des
    /// valeurs sensibles (<see cref="SensitiveSettings"/>), dont chacune est montrée avant d'être remise.
    /// </summary>
    private List<SessionViewModel> RevertibleSessions(bool today) => SessionsInScope(today)
        .Where(s => SensitiveSettings.OrdinaryChanges(s.Session).Count > 0)
        .OrderByDescending(s => s.Session.CreatedAt)
        .ToList();

    private IEnumerable<SessionViewModel> SessionsInScope(bool today) => Sessions
        .Where(s => s.CanRevert && !s.IsFromAnotherAccount && (!today || s.Session.CreatedAt.ToLocalTime().Date == DateTime.Today));

    /// <summary>Ce qu'une annulation groupée laisse de côté, à dire avant et après ; <c>null</c> s'il n'y a rien.</summary>
    private string? SensitiveLeftOutNote(bool today)
    {
        var count = SessionsInScope(today).Sum(s => SensitiveSettings.Pending(s.Session).Count);
        return count == 0 ? null : Plural(count, T("{0} valeur sensible laissée de côté : elle s'annule à part dans cet onglet, où elle est montrée avant d'être remise."), T("{0} valeurs sensibles laissées de côté : elles s'annulent une par une dans cet onglet, où chacune est montrée avant d'être remise."));
    }

    public bool HasRevertibleToday => RevertibleSessions(today: true).Count > 0;

    public string RevertTodayLabel => T("Tout annuler d'aujourd'hui ({0})", RevertibleSessions(today: true).Count);

    /// <summary>Il reste des séances d'avant aujourd'hui : « Tout annuler » va plus loin que « Tout annuler d'aujourd'hui ».</summary>
    public bool HasRevertibleEarlier => RevertibleSessions(today: false).Count > RevertibleSessions(today: true).Count;

    public string RevertEverythingLabel => T("Tout annuler ({0} séances)", RevertibleSessions(today: false).Count);

    private ICommand? _revertToday;

    private ICommand? _revertEverything;

    public ICommand RevertTodayCommand => _revertToday ??= new AsyncCommand(RevertTodayAsync);

    public ICommand RevertEverythingCommand => _revertEverything ??= new AsyncCommand(RevertEverythingAsync);

    internal Task RevertTodayAsync() => RevertManyAsync(today: true);

    /// <summary>Toutes les séances, pour quitter MAUS proprement : leur nombre et leurs dates sont dans la question.</summary>
    internal Task RevertEverythingAsync() => RevertManyAsync(today: false);

    /// <summary>
    /// Annule plusieurs séances, chacune par la voie habituelle du moteur (une valeur changée entre-temps n'est pas écrasée).
    /// Au premier échec on s'arrête : les séances plus anciennes reposent sur celle qui n'a pas pu être défaite.
    /// </summary>
    private async Task RevertManyAsync(bool today)
    {
        if (IsApplying)
        {
            FixReport = T("Corrections en cours : attendez qu'elles soient finies avant d'annuler.");
            return;
        }

        var sessions = RevertibleSessions(today);
        var leftOut = SensitiveLeftOutNote(today);
        if (sessions.Count == 0 || !Confirm(T("Annuler ?"), RevertQuestion(sessions, today) + Environment.NewLine + (leftOut is null ? string.Empty : leftOut + Environment.NewLine) + T("Continuer ?")))
        {
            return;
        }

        var lines = new List<string> { T("Annulation :") };
        _reverting = true;
        try
        {
            var stopped = false;
            foreach (var session in sessions.TakeWhile(_ => !stopped))
            {
                // Avec une valeur sensible dans la séance, ses autres corrections sont annulées une à une ; sinon, la séance d'un coup.
                var requests = SensitiveSettings.Pending(session.Session).Count == 0 ? new string?[] { null } : SensitiveSettings.OrdinaryChanges(session.Session).ToArray<string?>();
                foreach (var changeId in requests)
                {
                    var result = await Engine.RevertAsync(session.Session.Id, changeId, force: false, CancellationToken.None);
                    lines.AddRange(result.Entries.Select(e => $"• [{Labels.Of(e.Status)}] {e.Title} : {e.Message}"));
                    if (result.Error is { } error)
                    {
                        lines.Add(T("Arrêt à la séance du {0} : {1}", session.Header, error));
                        stopped = true;
                        break;
                    }

                    // Les séances plus anciennes reposent sur celle qui n'a pas pu être défaite entièrement.
                    if (result.Entries.Any(e => e.Status == RevertStatus.Failed))
                    {
                        lines.Add(T("Arrêt à la séance du {0} : une valeur n'a pas pu être remise, les séances plus anciennes restent en place.", session.Header));
                        stopped = true;
                        break;
                    }
                }
            }

            if (leftOut is not null)
            {
                lines.Add(leftOut);
            }

            FixReport = string.Join(Environment.NewLine, lines);
            if (!await RunAuditAsync())
            {
                FixReport += AuditFailedNote();
            }
        }
        catch (Maus.Core.Engine.Broker.EngineDeclinedException declined)
        {
            FixReport = string.Join(Environment.NewLine, lines) + Environment.NewLine + declined.Message;
        }
        catch (Exception ex)
        {
            Maus.Core.Diagnostics.CrashLog.Write(ex, "annulation");
            FixReport = string.Join(Environment.NewLine, lines) + Environment.NewLine + T("L'annulation a échoué : {0}", Maus.Core.Diagnostics.ErrorText.For(ex));
        }
        finally
        {
            _reverting = false;
        }

        await RefreshJournalUnlessEngineStoppedAsync();
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
        if (session.IsFromAnotherAccount)
        {
            what += Environment.NewLine + JournalSession.AnotherAccountNotice;
        }

        // Ce que la correction avait retiré peut être justement ce qui va revenir : la valeur est montrée, pas seulement « d'origine ».
        if (SensitiveSettings.Pending(session.Session, changeId) is { Count: > 0 } sensitive)
        {
            what += Environment.NewLine + SensitiveSettings.Question(sensitive);
        }

        if (!session.CanRevert || !Confirm(T("Annuler ?"), what + Environment.NewLine + T("Continuer ?")))
        {
            return;
        }

        try
        {
            _reverting = true;
            var result = await Engine.RevertAsync(session.Session.Id, changeId, force: false, CancellationToken.None);
            var report = result.Entries.Select(e => $"• [{Labels.Of(e.Status)}] {e.Title} : {e.Message}").ToList();
            if (result.Error is null || report.Count > 0)
            {
                report.Insert(0, T("Annulation :"));
            }

            if (result.Error is { } error)
            {
                report.Add(error);
            }

            FixReport = string.Join(Environment.NewLine, report);

            // Audit en échec : les constats affichés sont encore ceux d'avant l'annulation, il faut le dire.
            if (!await RunAuditAsync())
            {
                FixReport += AuditFailedNote();
            }
        }
        catch (Maus.Core.Engine.Broker.EngineDeclinedException declined)
        {
            FixReport = declined.Message;
        }
        catch (Exception ex)
        {
            Maus.Core.Diagnostics.CrashLog.Write(ex, "annulation");
            FixReport = T("L'annulation a échoué : {0}", Maus.Core.Diagnostics.ErrorText.For(ex));
        }
        finally
        {
            _reverting = false;
        }

        // Même si l'audit qui suit échoue, la liste des séances doit dire ce qui vient d'être annulé.
        await RefreshJournalUnlessEngineStoppedAsync();
    }

    /// <summary>Après un audit ou une annulation : moteur arrêté entre-temps, relire le journal demanderait une autorisation que personne n'a voulue.</summary>
    private Task RefreshJournalUnlessEngineStoppedAsync() => RefreshJournalAsync(withoutAsking: true);
}
