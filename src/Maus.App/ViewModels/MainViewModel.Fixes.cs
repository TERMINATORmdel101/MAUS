using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using Maus.App.Appearance;
using Maus.Core;
using Maus.Core.Diagnostics;
using Maus.Core.Engine.Broker;
using Maus.Core.Fixes;
using Maus.Core.Modules.M20Software;
using Maus.Core.Platform;
using Maus.Core.Preferences;
using Maus.Core.Reporting;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Onglet Corrections : sélection, application, vérification par un nouvel audit, Explorateur.</summary>
public sealed partial class MainViewModel
{
    private string? _blockingReason;
    private bool _showMoreChanges;
    private bool _createRestorePoint = true;
    private bool _enableProtection;
    private string _fixReport = string.Empty;
    private bool _isApplying;
    private bool _needsExplorerRestart;
    private AppliedSession? _lastApplied;

    public ObservableCollection<ChangeViewModel> Changes { get; } = [];

    /// <summary>Corrections recommandées, affichées d'emblée (les mêmes que celles déjà cochées).</summary>
    public ObservableCollection<ChangeViewModel> ImportantChanges { get; } = [];

    /// <summary>Le reste, au choix : derrière un simple lien, ouvert dès qu'une de ces corrections est cochée.</summary>
    public ObservableCollection<ChangeViewModel> MoreChanges { get; } = [];

    public bool HasMoreChanges => MoreChanges.Count > 0;

    public string MoreChangesLabel => MoreChanges.Count == 1
        ? T("Voir 1 autre correction, facultative")
        : T("Voir {0} autres corrections, facultatives", MoreChanges.Count);

    public bool ShowMoreChanges
    {
        get => _showMoreChanges;
        set => SetProperty(ref _showMoreChanges, value);
    }

    /// <summary>Idées reçues que MAUS ne suit pas, chacune avec sa source (onglet Corrections).</summary>
    public IReadOnlyList<MythViewModel> Myths { get; } = Maus.Core.Reporting.OptimizationMyths.All.Select(m => new MythViewModel(m)).ToList();

    public string MythsTitle => T("Idées reçues que MAUS ne suit pas ({0})", Myths.Count);

    public IReadOnlyList<ProfileViewModel> Profiles { get; }

    public ICommand ApplyCommand { get; }

    public ICommand RestartExplorerCommand { get; }

    public int SelectedCount => Changes.Count(c => c.IsSelected);

    public bool CanApply => _lastContext is not null && !IsBlocked && !IsApplying && !IsRunning && SelectedCount > 0;

    public string ApplyLabel => SelectedCount == 0 ? T("Aucune correction cochée") : T("Appliquer la sélection ({0})", SelectedCount);

    public string ChangesSummary => _lastContext is null
        ? T("Lancez d'abord l'audit : les corrections proposées apparaîtront ici.")
        : IsPlainLimitedAudit
        ? T("Cet audit s'est fait sans droits administrateur : MAUS n'a préparé aucune correction. Cliquez sur « Toutes les fonctionnalités », puis relancez l'audit pour les voir.")
        : IsLimitedAudit
        ? T("Cet audit s'est fait sans droits administrateur : MAUS n'a préparé aucune correction.")
        : Changes.Count == 0
            ? T("Aucune correction à proposer : tout ce que MAUS sait corriger est déjà en ordre (ou marqué « voulu »).")
            : Changes.Count == 1
                ? T("1 correction proposée. Cochez-la si vous la voulez : rien n'est modifié sans votre accord.")
                : T("{0} corrections proposées. Cochez celles que vous voulez, ou choisissez un profil. Rien n'est modifié sans votre accord.", Changes.Count);

    public string? BlockingReason
    {
        get => _blockingReason;
        private set
        {
            if (SetProperty(ref _blockingReason, value))
            {
                OnPropertyChanged(nameof(IsBlocked));
                OnPropertyChanged(nameof(ShowBlockingBanner));
                OnPropertyChanged(nameof(CanApply));
            }
        }
    }

    public bool IsBlocked => BlockingReason is not null;

    /// <summary>Audit sans droits sur un PC qui n'est pas géré par une organisation : le mode limité, qui n'est pas une anomalie.</summary>
    private bool IsPlainLimitedAudit => IsLimitedAudit && _lastContext?.Hardware is not ({ IsManaged: true } or { ManagementUnknown: true });

    /// <summary>
    /// Bandeau rouge : un vrai blocage. En mode limité, le résumé en haut de la page dit déjà quoi faire ; les corrections
    /// restent bloquées de la même façon, seul le bandeau disparaît.
    /// </summary>
    public bool ShowBlockingBanner => IsBlocked && !IsPlainLimitedAudit;

    public bool CreateRestorePoint
    {
        get => _createRestorePoint;
        set => SetProperty(ref _createRestorePoint, value);
    }

    public bool EnableProtection
    {
        get => _enableProtection;
        set => SetProperty(ref _enableProtection, value);
    }

    public string FixReport
    {
        get => _fixReport;
        private set
        {
            if (SetProperty(ref _fixReport, value))
            {
                OnPropertyChanged(nameof(HasFixReport));
            }
        }
    }

    public bool HasFixReport => FixReport.Length > 0;

    public bool IsApplying
    {
        get => _isApplying;
        private set
        {
            if (SetProperty(ref _isApplying, value))
            {
                OnPropertyChanged(nameof(CanApply));
            }
        }
    }

    /// <summary>Des corrections de la barre des tâches attendent un redémarrage de l'Explorateur.</summary>
    public bool NeedsExplorerRestart
    {
        get => _needsExplorerRestart;
        private set => SetProperty(ref _needsExplorerRestart, value);
    }

    /// <summary>Recalcule les corrections (par le moteur) en gardant les cases que l'utilisateur a déjà cochées ou décochées.</summary>
    private async Task ReplanAsync()
    {
        if (_lastContext is null)
        {
            return;
        }

        var plan = await Engine.PlanAsync(CancellationToken.None);

        var previous = Changes.ToDictionary(c => c.Change.Id, c => c.IsSelected, StringComparer.Ordinal);
        foreach (var change in Changes)
        {
            change.PropertyChanged -= OnChangeSelected;
        }

        Changes.Clear();
        ImportantChanges.Clear();
        MoreChanges.Clear();
        foreach (var change in plan)
        {
            var item = new ChangeViewModel(change, Modules.FirstOrDefault(m => m.Id == change.ModuleId)?.Header);
            if (previous.TryGetValue(change.Id, out var selected))
            {
                item.IsSelected = selected;
            }

            item.PropertyChanged += OnChangeSelected;
            Changes.Add(item);
            (change.Recommended && !change.Advanced ? ImportantChanges : MoreChanges).Add(item);
        }

        ShowMoreChanges |= MoreChanges.Any(c => c.IsSelected);
        NotifyPlan();
    }

    /// <summary>Audit sans droits : aucune correction préparée, et celles d'un audit précédent ne valent plus pour ce qui est affiché.</summary>
    private void ForgetPlan()
    {
        foreach (var change in Changes)
        {
            change.PropertyChanged -= OnChangeSelected;
        }

        Changes.Clear();
        ImportantChanges.Clear();
        MoreChanges.Clear();
        _lastApplied = null;
        NotifyPlan();
    }

    private void NotifyPlan()
    {
        OnPropertyChanged(nameof(ShowBlockingBanner));
        OnPropertyChanged(nameof(HasMoreChanges));
        OnPropertyChanged(nameof(MoreChangesLabel));
        OnSelectionChanged();
        OnPropertyChanged(nameof(ChangesSummary));
    }

    private void OnChangeSelected(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChangeViewModel.IsSelected))
        {
            // Rien de coché ne reste caché : cette liste s'ouvre dès qu'une de ses corrections est choisie.
            ShowMoreChanges |= sender is ChangeViewModel { IsSelected: true } change && MoreChanges.Contains(change);
            OnSelectionChanged();
        }
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(ApplyLabel));
        OnPropertyChanged(nameof(CanApply));
    }

    private void ApplyProfile(FixProfile profile)
    {
        foreach (var change in Changes)
        {
            change.IsSelected = profile.Selects(change.Change);
        }

        var chosen = Changes.Where(c => c.IsSelected).ToList();
        StatusText = T("Profil « {0} » : {1}, dont {2} avec avertissement. Rien n'est appliqué avant votre confirmation.",
            profile.Name, Plural(chosen.Count, T("{0} correction cochée"), T("{0} corrections cochées")), chosen.Count(c => c.Change.Warning is not null));
    }

    internal async Task ApplyAsync()
    {
        var selected = Changes.Where(c => c.IsSelected).Select(c => c.Change).ToList();
        if (_lastContext is null || selected.Count == 0 || IsBlocked)
        {
            return;
        }

        var warnings = selected.Where(c => c.Warning is not null).Select(c => $"• {c.Title} : {c.Warning}").ToList();
        var message = new StringBuilder()
            .AppendLine(Plural(selected.Count, T("MAUS va appliquer {0} correction."), T("MAUS va appliquer {0} corrections.")))
            .AppendLine();

        // Récapitulatif : ce qui change, regroupé selon le moment où l'effet sera complet.
        const int Shown = 12;
        foreach (var group in selected.GroupBy(c => c.Effect).OrderBy(g => g.Key))
        {
            message.AppendLine(group.Key == ChangeEffect.Immediate ? T("Effet immédiat :") : char.ToUpper(Labels.Of(group.Key)[0], Culture) + Labels.Of(group.Key)[1..] + " :");
            foreach (var change in group.Take(Shown))
            {
                message.Append("  • ").AppendLine(change.Title);
            }

            if (group.Count() > Shown)
            {
                message.AppendLine("  " + Plural(group.Count() - Shown, T("… et {0} autre."), T("… et {0} autres.")));
            }
        }

        message.AppendLine()
            .AppendLine(CreateRestorePoint
                ? T("Un point de restauration sera d'abord créé, puis vérifié.")
                : T("Aucun point de restauration ne sera créé (le journal permettra quand même d'annuler)."))
;
        if (warnings.Count > 0)
        {
            message.AppendLine().AppendLine(T("À lire avant de continuer :")).AppendLine(string.Join(Environment.NewLine, warnings));
        }

        if (!Confirm(T("Appliquer les corrections ?"), Maus.Core.Reporting.ConfirmationText.Build(message.ToString(),
                undo: T("chaque valeur d'origine est enregistrée ; l'onglet « Historique » annule tout, ou une seule correction."),
                reminder: Maus.Core.Legal.Disclaimer.ChangeReminder)))
        {
            return;
        }

        IsApplying = true;
        SetApplyStep(2);
        FixReport = T("Corrections en cours… (la création du point de restauration peut prendre une minute)");
        try
        {
            var context = _lastContext;
            var before = AuditReport.Create(context, _results);
            var scoreBefore = Score;
            var ids = selected.Select(c => c.Id).ToList();
            var options = new ApplyOptions { CreateRestorePoint = CreateRestorePoint, EnableProtectionIfNeeded = EnableProtection };
            var result = await Engine.ApplyAsync(ids, options, CancellationToken.None);

            // Protection simplement coupée : le bon choix est proposé avant de renoncer au point de restauration.
            if (result.Blocked && result.RestorePoint is { Status: RestorePointStatus.ProtectionDisabled } &&
                Confirm(T("Protection du système coupée"), T("Activer la protection du système et continuer ?") + Environment.NewLine + Environment.NewLine +
                    T("Elle occupe en général 3 à 5 % du disque. « Annuler » ne la recoupera pas : elle se règle ensuite dans les paramètres de Windows.")))
            {
                options = options with { EnableProtectionIfNeeded = true };
                result = await Engine.ApplyAsync(ids, options, CancellationToken.None);
            }

            if (result.Blocked && result.RestorePoint is { Succeeded: false } point &&
                Confirm(T("Point de restauration impossible"), point.Message + Environment.NewLine + Environment.NewLine +
                    T("Continuer sans point de restauration ? Le journal de MAUS permettra quand même d'annuler chaque correction.")))
            {
                result = await Engine.ApplyAsync(ids, options with { CreateRestorePoint = false, ProceedWithoutRestorePoint = true }, CancellationToken.None);
            }

            if (result.Blocked)
            {
                SetApplyStep(0);
                FixReport = Describe(result, []);
                return;
            }

            // Verify, second niveau : un nouvel audit relit l'état effectif (une stratégie peut être ignorée sur Famille).
            SetApplyStep(3);
            FixReport = T("Corrections faites. Vérification par un nouvel audit…");

            // Audit de vérification en échec : les résultats affichés sont encore ceux d'AVANT les corrections. Les comparer
            // ferait passer chaque correction pour « sans effet », et le journal le garderait : elles restent « non vérifiées ».
            // Moteur arrêté entre-temps : cet audit sans droits ne vérifie rien non plus, et noter le journal redemanderait une autorisation.
            var audited = await RunAuditAsync() && !IsLimitedAudit;
            var verified = audited
                ? FixVerification.CompareWithAudit(selected, result, _results, _lastContext!.Windows)
                : FixVerification.AuditFailed(result);
            if (result.Session is { } session && !IsLimitedAudit && CanReadJournalSilently())
            {
                try
                {
                    await Engine.RecordVerificationAsync(session.Id, verified, CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Simple annotation du journal : son échec ne dit rien des corrections, déjà faites.
                    Maus.Core.Diagnostics.CrashLog.Write(ex, "note d'effet des corrections");
                }
            }

            _lastApplied = new AppliedSession(before, result.Session, verified);
            NotifyJourney();
            NeedsExplorerRestart = verified.Any(v => v.Outcome.Status == ChangeStatus.Applied && v.Outcome.Effect == ChangeEffect.ExplorerRestart);
            FixReport = Describe(result, verified, verificationFailed: !audited);
            if (result.RequiredEffect == ChangeEffect.Restart)
            {
                RememberRestart(DateTimeOffset.Now);
            }

            SetApplyStep(5);
            if (audited && ScoreChange(scoreBefore, Score, IsScorePartial) is { } change)
            {
                // Avant / après, en tête du compte rendu et sur l'accueil.
                FixReport = change + Environment.NewLine + Environment.NewLine + FixReport;
                StatusText = change;
                if (!IsScorePartial)
                {
                    ShowScoreDelta(scoreBefore, Score);
                }
            }

            if (!IsLimitedAudit)
            {
                await RefreshJournalUnlessEngineStoppedAsync();
            }
        }
        catch (Maus.Core.Engine.Broker.EngineDeclinedException declined)
        {
            SetApplyStep(0);
            FixReport = declined.Message;
        }
        catch (Exception ex)
        {
            Maus.Core.Diagnostics.CrashLog.Write(ex, "corrections");
            SetApplyStep(0);
            FixReport = T("Les corrections ont été interrompues : {0}. Consultez l'onglet « Historique » pour annuler ce qui a été fait.", Maus.Core.Diagnostics.ErrorText.For(ex));
        }
        finally
        {
            IsApplying = false;
        }
    }

    /// <summary>
    /// Score de santé avant et après les corrections (« 80 → 86 »). Rien si l'un des deux manque ; un score partiel (module
    /// non vérifié) est dit tel quel, pour ne pas faire croire à un gain qui n'en est pas un.
    /// </summary>
    internal static string? ScoreChange(int before, int after, bool partial)
    {
        if (before < 0 || after < 0)
        {
            return null;
        }

        var text = after > before
            ? T("Score de santé : {0} → {1} (+{2}).", before, after, after - before)
            : after < before
                ? T("Score de santé : {0} → {1} ({2}).", before, after, after - before)
                : T("Score de santé : {0} → {1} (inchangé).", before, after);
        return partial ? text + " " + T("Score partiel : un module n'a pas pu être vérifié.") : text;
    }

    /// <summary>« Réparer les fichiers de Windows » : DISM puis SFC dans une console visible, après confirmation.</summary>
    public ICommand RepairWindowsCommand => _repairWindows ??= new AsyncCommand(async () =>
    {
        var message = T("MAUS va lancer deux outils officiels de Microsoft et afficher leur progression :") + Environment.NewLine
            + T("• DISM répare la copie de référence de Windows en la comparant à Windows Update (connexion Internet conseillée) ;") + Environment.NewLine
            + T("• SFC remplace ensuite les fichiers système abîmés par une copie saine.") + Environment.NewLine + Environment.NewLine
            + T("Vos fichiers, vos applications et vos réglages ne sont pas touchés. Comptez 10 à 30 minutes : ne fermez pas la fenêtre, gardez le PC branché, puis redémarrez-le.");
        if (_results.SelectMany(r => r.Findings).Any(f => f.Id == "M02.pending-reboot" && f.Status is FindingStatus.Warning or FindingStatus.Problem))
        {
            message += Environment.NewLine + Environment.NewLine
                + T("Attention : un redémarrage est en attente. Redémarrez d'abord le PC, sinon la réparation risque d'échouer ou de donner un faux résultat.");
        }

        if (!Confirm(T("Réparer les fichiers de Windows ?"), Maus.Core.Reporting.ConfirmationText.Build(message, undo: T("rien à annuler : DISM et SFC ne font que remettre des fichiers de Windows dans leur état d'origine."), reminder: Maus.Core.Legal.Disclaimer.OperationReminder)))
        {
            return;
        }

        var outcome = await Views.ToolWindow.RunAsync(T("Réparation de Windows"), progress => Engine.RunToolAsync(new ToolRequest(ToolKind.Repair), progress, CancellationToken.None));
        FixReport = (FixReport.Length > 0 ? FixReport + Environment.NewLine + Environment.NewLine : string.Empty)
            + (outcome.Message ?? T("L'outil n'a pas pu démarrer."));
    });

    private ICommand? _repairWindows;
    private ICommand? _updateSoftware;

    /// <summary>Choix des logiciels à mettre à jour ; <c>null</c> si l'utilisateur renonce. Remplaçable pour les tests.</summary>
    public Func<IReadOnlyList<SoftwareUpdate>, IReadOnlyList<SoftwareUpdate>?> PickSoftwareUpdates { get; init; } = updates =>
    {
        var window = new Views.SoftwareUpdatesWindow(updates) { Owner = System.Windows.Application.Current?.MainWindow };
        return window.ShowDialog() == true ? window.Selected : null;
    };

    /// <summary>
    /// Liste des logiciels à mettre à jour, lue par cette fenêtre et jamais par le moteur (0.8.6) : winget n'est jamais lancé avec les
    /// droits administrateur. Remplaçable pour les tests.
    /// </summary>
    public Func<CancellationToken, Task<SoftwareListing>> ListSoftwareUpdates { get; init; } = SoftwareListing.ReadHereAsync;

    /// <summary>« Mettre à jour les logiciels » : liste winget (lecture seule), choix de l'utilisateur, puis console visible.</summary>
    public ICommand UpdateSoftwareCommand => _updateSoftware ??= new AsyncCommand(UpdateSoftwareAsync);

    /// <summary>Recherche winget en cours (barre de progression de l'onglet Corrections).</summary>
    public bool IsCheckingSoftware
    {
        get => _isCheckingSoftware;
        private set => SetProperty(ref _isCheckingSoftware, value);
    }

    private bool _isCheckingSoftware;

    private void AddToFixReport(string text) =>
        FixReport = (FixReport.Length > 0 ? FixReport + Environment.NewLine + Environment.NewLine : string.Empty) + text;

    private async Task UpdateSoftwareAsync()
    {
        StatusText = T("Recherche des mises à jour des logiciels (winget), jusqu'à une minute et demie…");
        IReadOnlyList<SoftwareUpdate> updates;
        IsCheckingSoftware = true;
        try
        {
            var listing = await ListSoftwareUpdates(CancellationToken.None);
            if (!listing.WingetFound)
            {
                StatusText = T("winget est absent : réinstallez « Programme d'installation d'application » depuis le Microsoft Store.");
                return;
            }

            updates = listing.Updates;
        }
        catch (Exception ex) when (ex is DataSourceUnavailableException or MausAccessDeniedException or InvalidOperationException or EngineException)
        {
            StatusText = T("La liste des mises à jour n'a pas pu être lue : {0}", Maus.Core.Diagnostics.ErrorText.For(ex));
            return;
        }
        finally
        {
            IsCheckingSoftware = false;
        }

        if (updates.Count == 0)
        {
            StatusText = T("Tous les logiciels suivis par winget sont à jour.");
            return;
        }

        StatusText = Plural(updates.Count, T("{0} logiciel peut être mis à jour."), T("{0} logiciels peuvent être mis à jour."));
        if (PickSoftwareUpdates(updates) is not { Count: > 0 } chosen)
        {
            return;
        }

        var outcome = await Task.Run(() => Maus.Core.Modules.M20Software.SoftwareConsole.Upgrade(chosen));
        AddToFixReport(outcome.Ran
            ? Plural(chosen.Count, T("Mise à jour de {0} logiciel lancée dans une fenêtre de commande. Quand elle est terminée, relancez l'audit."), T("Mise à jour de {0} logiciels lancée dans une fenêtre de commande. Quand elle est terminée, relancez l'audit."))
            : outcome.Message ?? T("La fenêtre de commande n'a pas pu s'ouvrir."));
    }

    private async Task RestartExplorerAsync()
    {
        if (!Confirm(T("Redémarrer l'Explorateur ?"), Maus.Core.Reporting.ConfirmationText.Build(ExplorerRestart.Warning, undo: T("l'Explorateur redémarre de lui-même ; seules les fenêtres de dossiers ouvertes se ferment."), reminder: Maus.Core.Legal.Disclaimer.OperationReminder)))
        {
            return;
        }

        var restart = new ExplorerRestart(new WindowsShellProcesses(new WindowsRegistryReader()));
        var result = await Task.Run(() => restart.RunAsync());
        FixReport = (FixReport.Length > 0 ? FixReport + Environment.NewLine + Environment.NewLine : string.Empty) + result.Message;
        if (result.Succeeded)
        {
            NeedsExplorerRestart = false;
        }
    }

    /// <summary>Le redémarrage attendu a eu lieu si le PC a démarré après les corrections. Une date à venir (fichier retouché) n'en réclamerait un pour toujours.</summary>
    internal static bool IsRestartStillDue(DateTimeOffset? since, DateTimeOffset lastBoot) => since is { } date && date > lastBoot && Maus.Core.Platform.StoredValues.IsPastDate(date);

    private static DateTimeOffset LastBoot => DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>Le rappel du compte rendu se perd à la fermeture de MAUS, et un audit peut dire « Confirmé » avant que l'effet soit là.</summary>
    public bool HasRestartPending => IsRestartStillDue(_restartPendingSince, LastBoot);

    public string RestartPendingNote => _restartPendingSince is { } since
        ? T("Redémarrage à faire : les corrections du {0} ne prennent tout leur effet qu'après un redémarrage du PC. Jusque-là, un audit peut les dire confirmées alors que l'effet n'est pas encore là.", since.ToLocalTime().ToString("d", Culture))
        : string.Empty;

    private DateTimeOffset? _restartPendingSince = AppearanceManager.Current.RestartPendingSince;

    private void RememberRestart(DateTimeOffset? since)
    {
        _restartPendingSince = since;
        AppearanceManager.Apply(AppearanceManager.Current with { RestartPendingSince = since });
        OnPropertyChanged(nameof(HasRestartPending));
        OnPropertyChanged(nameof(RestartPendingNote));
        Task.Run(() =>
        {
            try
            {
                PreferencesStore.Update(p => p with { RestartPendingSince = since });
            }
            catch (Exception ex) when (ex is Maus.Core.Fixes.JournalUnsafeException or System.IO.IOException or UnauthorizedAccessException or Maus.Core.Engine.Broker.EngineException)
            {
                // Le rappel vaut pour cette ouverture de MAUS.
            }
        }).Forget("rappel de redémarrage : enregistrement");
    }

    /// <summary>À l'ouverture : le PC a redémarré depuis, plus rien à rappeler.</summary>
    private void ForgetRestartIfDone()
    {
        if (_restartPendingSince is not null && !HasRestartPending)
        {
            RememberRestart(null);
        }
    }

    /// <param name="verificationFailed">L'audit de vérification a échoué : l'effet des corrections n'a pas pu être relu.</param>
    private static string Describe(ApplyResult result, IReadOnlyList<VerifiedOutcome> verified, bool verificationFailed = false)
    {
        var text = new StringBuilder();
        if (result.RestorePoint is { } point)
        {
            text.AppendLine(point.Message);
        }

        if (result.Blocked)
        {
            return text.Append(result.BlockedReason).ToString();
        }

        var byId = verified.ToDictionary(v => v.Outcome.ChangeId, StringComparer.Ordinal);
        var noEffect = verified.Count(v => v.Check == EffectCheck.NoEffect);
        text.AppendLine(Plural(result.AppliedCount, T("{0} correction appliquée."), T("{0} corrections appliquées.")));
        if (verificationFailed)
        {
            text.AppendLine(T("L'audit de vérification a échoué : l'effet des corrections n'a pas pu être relu. Elles restent dans le journal (onglet « Historique ») et peuvent être annulées. Relancez l'audit pour voir leur effet."));
        }

        if (noEffect > 0)
        {
            text.AppendLine(Plural(noEffect, T("Attention : {0} correction écrite mais sans effet d'après le nouvel audit (détails ci-dessous)."), T("Attention : {0} corrections écrites mais sans effet d'après le nouvel audit (détails ci-dessous).")));
        }

        foreach (var outcome in result.Changes)
        {
            var (label, detail) = byId.TryGetValue(outcome.ChangeId, out var check) && outcome.Status == ChangeStatus.Applied
                ? (check.Check switch
                {
                    EffectCheck.Confirmed => T("Confirmé"),
                    EffectCheck.PendingRestart => T("En attente"),
                    EffectCheck.NoEffect => T("SANS EFFET"),
                    EffectCheck.NotChecked => T("Non vérifié"),
                    _ => T("Appliqué"),
                }, check.Message)
                : (Labels.Of(outcome.Status), outcome.Message);
            text.AppendLine(CultureInfo.InvariantCulture, $"• [{label}] {outcome.Title} : {detail}");
        }

        switch (result.RequiredEffect)
        {
            case ChangeEffect.Restart:
                text.AppendLine(T("Redémarrez le PC pour que toutes les corrections prennent effet."));
                break;
            case ChangeEffect.SignOut:
                text.AppendLine(T("Fermez puis rouvrez votre session pour que toutes les corrections prennent effet."));
                break;
            case ChangeEffect.ExplorerRestart:
                text.AppendLine(T("Certaines corrections de la barre des tâches seront visibles après redémarrage de l'Explorateur (bouton ci-dessous)."));
                break;
        }

        text.AppendLine(T("Le bouton « Enregistrer le rapport » garde une trace avant/après de cette séance."));
        return text.ToString().TrimEnd();
    }

    /// <summary>Dernière séance appliquée pendant cette ouverture de MAUS, pour le rapport avant/après.</summary>
    private sealed record AppliedSession(AuditReport Before, JournalSession? Session, IReadOnlyList<VerifiedOutcome> Verified);
}

/// <summary>Bouton de profil en un clic.</summary>
public sealed class ProfileViewModel(FixProfile profile, Action apply)
{
    public override string ToString() => Name;

    public string Name => profile.Name;

    public string Description => profile.Description;

    public ICommand Command { get; } = new AsyncCommand(() =>
    {
        apply();
    });
}
