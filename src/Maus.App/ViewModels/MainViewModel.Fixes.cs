using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using Maus.Core;
using Maus.Core.Fixes;
using Maus.Core.Platform;
using Maus.Core.Reporting;

namespace Maus.App.ViewModels;

/// <summary>Onglet Corrections : sélection, application, vérification par un nouvel audit, Explorateur.</summary>
public sealed partial class MainViewModel
{
    private string? _blockingReason;
    private bool _createRestorePoint = true;
    private bool _enableProtection;
    private string _fixReport = string.Empty;
    private bool _isApplying;
    private bool _needsExplorerRestart;
    private AppliedSession? _lastApplied;

    public ObservableCollection<ChangeViewModel> Changes { get; } = [];

    public IReadOnlyList<ProfileViewModel> Profiles { get; }

    public ICommand ApplyCommand { get; }

    public ICommand RestartExplorerCommand { get; }

    public int SelectedCount => Changes.Count(c => c.IsSelected);

    public bool CanApply => _lastContext is not null && !IsBlocked && !IsApplying && SelectedCount > 0;

    public string ApplyLabel => SelectedCount == 0 ? "Aucune correction cochée" : $"Appliquer la sélection ({SelectedCount})";

    public string ChangesSummary => _lastContext is null
        ? "Lancez d'abord l'audit : les corrections proposées apparaîtront ici."
        : Changes.Count == 0
            ? "Aucune correction à proposer : tout ce que MAUS sait corriger est déjà en ordre (ou marqué « voulu »)."
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

    /// <summary>Des corrections de la barre des tâches attendent un redémarrage de l'Explorateur.</summary>
    public bool NeedsExplorerRestart
    {
        get => _needsExplorerRestart;
        private set => SetProperty(ref _needsExplorerRestart, value);
    }

    /// <summary>Recalcule les corrections en gardant les cases que l'utilisateur a déjà cochées ou décochées.</summary>
    private void Replan()
    {
        if (_lastContext is null)
        {
            return;
        }

        var previous = Changes.ToDictionary(c => c.Change.Id, c => c.IsSelected, StringComparer.Ordinal);
        foreach (var change in Changes)
        {
            change.PropertyChanged -= OnChangeSelected;
        }

        Changes.Clear();
        foreach (var change in FixEngine.Plan(_engine, _results, _lastContext))
        {
            var item = new ChangeViewModel(change);
            if (previous.TryGetValue(change.Id, out var selected))
            {
                item.IsSelected = selected;
            }

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
            .AppendLine("Chaque valeur d'origine est enregistrée : l'onglet « Historique » permet de tout annuler, ou une seule correction.");
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
            var before = AuditReport.Create(context, _results);
            var engine = new FixEngine(await Task.Run(() => FixContext.CreateDefault(context)));
            var options = new ApplyOptions { CreateRestorePoint = CreateRestorePoint, EnableProtectionIfNeeded = EnableProtection };
            var result = await Task.Run(() => engine.Apply(selected, options));

            if (result.Blocked && result.RestorePoint is { Succeeded: false } point &&
                Confirm("Point de restauration impossible", point.Message + Environment.NewLine + Environment.NewLine +
                    "Continuer sans point de restauration ? Le journal de MAUS permettra quand même d'annuler chaque correction."))
            {
                result = await Task.Run(() => engine.Apply(selected, options with { ProceedWithoutRestorePoint = true }));
            }

            if (result.Blocked)
            {
                FixReport = Describe(result, []);
                return;
            }

            // Verify, second niveau : un nouvel audit relit l'état effectif (une stratégie peut être ignorée sur Famille).
            FixReport = "Corrections faites. Vérification par un nouvel audit…";
            await RunAuditAsync();
            var verified = FixVerification.CompareWithAudit(selected, result, _results, _lastContext!.Windows);
            if (result.Session is { } session)
            {
                await Task.Run(() => engine.RecordVerification(session.Id, verified));
            }

            _lastApplied = new AppliedSession(before, result.Session?.Id, verified);
            NeedsExplorerRestart = verified.Any(v => v.Outcome.Status == ChangeStatus.Applied && v.Outcome.Effect == ChangeEffect.ExplorerRestart);
            FixReport = Describe(result, verified);
            await RefreshJournalAsync();
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

    private async Task RestartExplorerAsync()
    {
        if (!Confirm("Redémarrer l'Explorateur ?", ExplorerRestart.Warning + Environment.NewLine + Environment.NewLine + "Continuer ?"))
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

    private static string Describe(ApplyResult result, IReadOnlyList<VerifiedOutcome> verified)
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
        text.AppendLine(CultureInfo.CurrentCulture, $"{result.AppliedCount} correction(s) appliquée(s).");
        if (noEffect > 0)
        {
            text.AppendLine(CultureInfo.CurrentCulture, $"Attention : {noEffect} correction(s) écrite(s) mais sans effet d'après le nouvel audit (détails ci-dessous).");
        }

        foreach (var outcome in result.Changes)
        {
            var (label, detail) = byId.TryGetValue(outcome.ChangeId, out var check) && outcome.Status == ChangeStatus.Applied
                ? (check.Check switch
                {
                    EffectCheck.Confirmed => "Confirmé",
                    EffectCheck.PendingRestart => "En attente",
                    EffectCheck.NoEffect => "SANS EFFET",
                    _ => "Appliqué",
                }, check.Message)
                : (Labels.Of(outcome.Status), outcome.Message);
            text.AppendLine(CultureInfo.CurrentCulture, $"• [{label}] {outcome.Title} : {detail}");
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
                text.AppendLine("Certaines corrections de la barre des tâches seront visibles après redémarrage de l'Explorateur (bouton ci-dessous).");
                break;
        }

        text.AppendLine("Le bouton « Enregistrer le rapport » garde une trace avant/après de cette séance.");
        return text.ToString().TrimEnd();
    }

    /// <summary>Dernière séance appliquée pendant cette ouverture de MAUS, pour le rapport avant/après.</summary>
    private sealed record AppliedSession(AuditReport Before, string? SessionId, IReadOnlyList<VerifiedOutcome> Verified);
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
