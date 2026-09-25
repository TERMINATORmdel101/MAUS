using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Maus.Core;
using Maus.Core.Engine;
using Maus.Core.Fixes;
using Maus.Core.Reporting;

namespace Maus.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly AuditEngine _engine = AuditEngine.CreateWithBuiltInModules();
    private ModuleViewModel? _selectedModule;
    private bool _isRunning;
    private string _statusText = "Prêt. Lancez l'audit : MAUS lit votre configuration sans rien modifier.";
    private string _systemSummary = string.Empty;
    private int _completed;
    private AuditContext? _lastContext;
    private string? _blockingReason;
    private bool _createRestorePoint = true;
    private bool _enableProtection;
    private string _fixReport = string.Empty;
    private bool _isApplying;

    public MainViewModel()
    {
        Modules = new ObservableCollection<ModuleViewModel>(_engine.Modules.Select(m => new ModuleViewModel(m)));
        SelectedModule = Modules.FirstOrDefault();
        RunAuditCommand = new AsyncCommand(RunAuditAsync);
        ApplyCommand = new AsyncCommand(ApplyAsync);
        RefreshJournalCommand = new AsyncCommand(RefreshJournalAsync);
        Profiles = FixProfile.All.Select(p => new ProfileViewModel(p, () => ApplyProfile(p))).ToList();
    }

    /// <summary>Demande de confirmation (titre, message) ; remplaçable pour les tests.</summary>
    public Func<string, string, bool> Confirm { get; init; } = (title, message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public ObservableCollection<ChangeViewModel> Changes { get; } = [];

    public ObservableCollection<SessionViewModel> Sessions { get; } = [];

    public IReadOnlyList<ProfileViewModel> Profiles { get; }

    public ICommand ApplyCommand { get; }

    public ICommand RefreshJournalCommand { get; }

    public int SelectedCount => Changes.Count(c => c.IsSelected);

    public bool CanApply => _lastContext is not null && !IsBlocked && !IsApplying && SelectedCount > 0;

    public string ApplyLabel => SelectedCount == 0 ? "Aucune correction cochée" : $"Appliquer la sélection ({SelectedCount})";

    public string ChangesSummary => _lastContext is null
        ? "Lancez d'abord l'audit : les corrections proposées apparaîtront ici."
        : Changes.Count == 0
            ? "Aucune correction à proposer : tout ce que MAUS sait corriger est déjà en ordre."
            : $"{Changes.Count} correction(s) proposée(s). Cochez celles que vous voulez, ou choisissez un profil. Rien n'est modifié sans votre accord.";

    public string? BlockingReason
    {
        get => _blockingReason;
        private set
        {
            if (SetProperty(ref _blockingReason, value))
            {
                OnPropertyChanged(nameof(IsBlocked));
                OnPropertyChanged(nameof(CanApply));
            }
        }
    }

    public bool IsBlocked => BlockingReason is not null;

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

    public string JournalSummary => Sessions.Count == 0
        ? "Aucune séance de corrections : MAUS n'a encore rien modifié sur ce PC."
        : "Chaque séance peut être annulée : MAUS remet les valeurs d'origine enregistrées avant d'écrire. " +
          "Une valeur que vous (ou Windows) avez changée depuis est laissée telle quelle.";

    public ObservableCollection<ModuleViewModel> Modules { get; }

    public ICommand RunAuditCommand { get; }

    public int ModuleCount => Modules.Count;

    public ModuleViewModel? SelectedModule
    {
        get => _selectedModule;
        set => SetProperty(ref _selectedModule, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    public int Completed
    {
        get => _completed;
        private set => SetProperty(ref _completed, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string SystemSummary
    {
        get => _systemSummary;
        private set => SetProperty(ref _systemSummary, value);
    }

    public string About { get; } = "Conçu et codé avec Claude, une IA d'Anthropic, sous la direction de son auteur · Logiciel libre sous licence GPL-3.0 · " +
                           $"version {typeof(AuditEngine).Assembly.GetName().Version?.ToString(3)}";

    private async Task RunAuditAsync()
    {
        IsRunning = true;
        Completed = 0;
        StatusText = "Lecture de la configuration…";
        try
        {
            var context = await Task.Run(AuditContext.CreateDefault);
            SystemSummary = $"{context.Windows.ProductName} {context.Windows.DisplayVersion} (build {context.Windows.FullBuild}) · " +
                            $"{Labels.Of(context.Hardware.FormFactor)} · {context.Hardware.Cpu.Name}";

            var byId = Modules.ToDictionary(m => m.Id);
            var progress = new Progress<ModuleResult>(result =>
            {
                if (byId.TryGetValue(result.ModuleId, out var module))
                {
                    module.SetResult(result);
                }

                Completed++;
                StatusText = $"Audit en cours… {Completed}/{ModuleCount}";
            });

            var results = await _engine.RunAsync(context, progress);
            var findings = results.SelectMany(r => r.Findings).ToList();
            StatusText = $"Audit terminé : {findings.Count(f => f.Status == FindingStatus.Problem)} problème(s), " +
                         $"{findings.Count(f => f.Status == FindingStatus.Warning)} à surveiller, " +
                         $"{findings.Count(f => f.Status == FindingStatus.Improvable)} optimisation(s) possible(s). Rien n'a été modifié.";
            OnPropertyChanged(nameof(SelectedModule));

            _lastContext = context;
            var plan = FixEngine.Plan(_engine, results, context);
            var fixContext = await Task.Run(() => FixContext.CreateDefault(context));
            BlockingReason = new FixEngine(fixContext).GetBlockingReason()
                ?? (fixContext.ElevatedAsAnotherUser
                    ? "MAUS a été lancé avec un autre compte administrateur : les réglages de votre profil (effets visuels, confidentialité, Game Bar) seront ignorés. Relancez MAUS depuis votre propre session."
                    : null);
            SetChanges(plan);
            await RefreshJournalAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"L'audit a échoué : {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void SetChanges(IReadOnlyList<PlannedChange> plan)
    {
        foreach (var change in Changes)
        {
            change.PropertyChanged -= OnChangeSelected;
        }

        Changes.Clear();
        foreach (var change in plan)
        {
            var item = new ChangeViewModel(change);
            item.PropertyChanged += OnChangeSelected;
            Changes.Add(item);
        }

        OnSelectionChanged();
        OnPropertyChanged(nameof(ChangesSummary));
    }

    private void OnChangeSelected(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChangeViewModel.IsSelected))
        {
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
    }

    private async Task ApplyAsync()
    {
        var selected = Changes.Where(c => c.IsSelected).Select(c => c.Change).ToList();
        if (_lastContext is null || selected.Count == 0 || IsBlocked)
        {
            return;
        }

        var warnings = selected.Where(c => c.Warning is not null).Select(c => $"• {c.Title} : {c.Warning}").ToList();
        var message = new StringBuilder()
            .AppendLine(CultureInfo.CurrentCulture, $"MAUS va appliquer {selected.Count} correction(s).")
            .AppendLine(CreateRestorePoint
                ? "Un point de restauration sera d'abord créé, puis vérifié."
                : "Aucun point de restauration ne sera créé (le journal permettra quand même d'annuler).")
            .AppendLine("Chaque valeur d'origine est enregistrée : l'onglet « Historique » permet de tout annuler.");
        if (warnings.Count > 0)
        {
            message.AppendLine().AppendLine("À lire avant de continuer :").AppendLine(string.Join(Environment.NewLine, warnings));
        }

        if (!Confirm("Appliquer les corrections ?", message.AppendLine().Append("Continuer ?").ToString()))
        {
            return;
        }

        IsApplying = true;
        FixReport = "Corrections en cours… (la création du point de restauration peut prendre une minute)";
        try
        {
            var context = _lastContext;
            var engine = new FixEngine(await Task.Run(() => FixContext.CreateDefault(context)));
            var options = new ApplyOptions { CreateRestorePoint = CreateRestorePoint, EnableProtectionIfNeeded = EnableProtection };
            var result = await Task.Run(() => engine.Apply(selected, options));

            if (result.Blocked && result.RestorePoint is { Succeeded: false } point &&
                Confirm("Point de restauration impossible", point.Message + Environment.NewLine + Environment.NewLine +
                    "Continuer sans point de restauration ? Le journal de MAUS permettra quand même d'annuler chaque correction."))
            {
                result = await Task.Run(() => engine.Apply(selected, options with { ProceedWithoutRestorePoint = true }));
            }

            FixReport = Describe(result);
            if (!result.Blocked)
            {
                // Vérification finale : un nouvel audit relit l'état effectif (une stratégie peut être ignorée sur Famille).
                await RunAuditAsync();
            }
        }
        catch (Exception ex)
        {
            FixReport = $"Les corrections ont été interrompues : {ex.Message}. Consultez l'onglet « Historique » pour annuler ce qui a été fait.";
        }
        finally
        {
            IsApplying = false;
        }
    }

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

    private async Task RevertAsync(SessionViewModel session)
    {
        if (!session.CanRevert || !Confirm("Annuler cette séance ?", $"MAUS va remettre les valeurs d'origine de la séance du {session.Header}.{Environment.NewLine}Continuer ?"))
        {
            return;
        }

        try
        {
            var context = _lastContext ?? await Task.Run(AuditContext.CreateDefault);
            var engine = new FixEngine(await Task.Run(() => FixContext.CreateDefault(context)));
            var result = await Task.Run(() => engine.Revert(session.Session.Id));
            FixReport = result.Error ?? "Annulation : " + Environment.NewLine + string.Join(Environment.NewLine,
                result.Entries.Select(e => $"• [{Labels.Of(e.Status)}] {e.Title} : {e.Message}"));
            await RunAuditAsync();
        }
        catch (Exception ex)
        {
            FixReport = $"L'annulation a échoué : {ex.Message}";
        }
    }

    private static string Describe(ApplyResult result)
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

        text.AppendLine(CultureInfo.CurrentCulture, $"{result.AppliedCount} correction(s) appliquée(s) et vérifiée(s).");
        foreach (var outcome in result.Changes)
        {
            text.AppendLine(CultureInfo.CurrentCulture, $"• [{Labels.Of(outcome.Status)}] {outcome.Title} : {outcome.Message}");
        }

        switch (result.RequiredEffect)
        {
            case ChangeEffect.Restart:
                text.AppendLine("Redémarrez le PC pour que toutes les corrections prennent effet.");
                break;
            case ChangeEffect.SignOut:
                text.AppendLine("Fermez puis rouvrez votre session pour que toutes les corrections prennent effet.");
                break;
            case ChangeEffect.ExplorerRestart:
                text.AppendLine("Certaines corrections de la barre des tâches seront complètes après redémarrage de l'Explorateur (ou fermeture de session).");
                break;
        }

        return text.ToString().TrimEnd();
    }
}

/// <summary>Bouton de profil en un clic.</summary>
public sealed class ProfileViewModel(FixProfile profile, Action apply)
{
    public string Name => profile.Name;

    public string Description => profile.Description;

    public ICommand Command { get; } = new AsyncCommand(() =>
    {
        apply();
        return Task.CompletedTask;
    });
}
