using System.Collections.ObjectModel;
using Maus.Core;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>
/// Onglet Constats (demande du porteur, 05/10/2026) : d'abord ce qui demande une action, du plus grave au plus léger, tous
/// modules confondus ; ce qui va bien, les informations et les points illisibles seulement à la demande.
/// </summary>
public sealed partial class MainViewModel : IFindingActions
{
    private bool _showByModule;
    private bool _showConforming;
    private bool _showOthers;

    /// <summary>Problèmes, points à surveiller et optimisations, du plus grave au plus léger.</summary>
    public ObservableCollection<FindingViewModel> PriorityFindings { get; } = [];

    /// <summary>Le reste : conformes, informations (dont les constats « voulus ») et indéterminés.</summary>
    public ObservableCollection<FindingViewModel> OtherFindings { get; } = [];

    public bool HasPriorityFindings => PriorityFindings.Count > 0;

    /// <summary>Audit fait, et rien qui demande une action.</summary>
    public bool NothingToHandle => HasAudit && PriorityFindings.Count == 0;

    public string PriorityTitle => T("À traiter d'abord ({0})", PriorityFindings.Count);

    public string OthersLabel => T("Voir le reste ({0}) : conformes, informations, indéterminés", OtherFindings.Count);

    public bool HasOtherFindings => OtherFindings.Count > 0;

    /// <summary>Affichage par module (liste des modules à gauche) au lieu de la liste par importance.</summary>
    public bool ShowByModule
    {
        get => _showByModule;
        set
        {
            if (SetProperty(ref _showByModule, value))
            {
                OnPropertyChanged(nameof(ShowByImportance));
            }
        }
    }

    public bool ShowByImportance
    {
        get => !_showByModule;
        set => ShowByModule = !value;
    }

    /// <summary>Le reste est déplié sous la liste par importance.</summary>
    public bool ShowOthers
    {
        get => _showOthers;
        set => SetProperty(ref _showOthers, value);
    }

    /// <summary>Affichage par module : montrer aussi les constats conformes (cachés par défaut).</summary>
    public bool ShowConforming
    {
        get => _showConforming;
        set
        {
            if (SetProperty(ref _showConforming, value))
            {
                OnPropertyChanged(nameof(SelectedModuleFindings));
            }
        }
    }

    /// <summary>Constats du module choisi, sans les conformes sauf demande.</summary>
    public IReadOnlyList<FindingViewModel> SelectedModuleFindings =>
        SelectedModule?.Findings.Where(f => ShowConforming || f.Status != FindingStatus.Ok).ToList() ?? [];

    /// <summary>Nombre de constats conformes cachés dans le module choisi.</summary>
    public string HiddenConformingNote
    {
        get
        {
            var hidden = ShowConforming ? 0 : SelectedModule?.Findings.Count(f => f.Status == FindingStatus.Ok) ?? 0;
            return hidden == 0 ? string.Empty : T("{0} constat(s) conforme(s) masqué(s).", hidden);
        }
    }

    /// <summary>Range les constats du dernier audit : à traiter d'abord, puis le reste.</summary>
    private void RefreshFindingLists()
    {
        var order = Modules.Select((m, i) => (m.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        var all = _results
            .SelectMany(r => r.Findings.Select(f => (Result: r, Finding: f)))
            .OrderByDescending(x => x.Finding.Status.Rank())
            .ThenByDescending(x => x.Finding.Severity)
            .ThenBy(x => order.GetValueOrDefault(x.Result.ModuleId, int.MaxValue))
            .Select(x => new FindingViewModel(
                x.Finding,
                x.Result.ModuleId,
                this,
                Modules.FirstOrDefault(m => m.Id == x.Result.ModuleId)?.Header))
            .ToList();

        PriorityFindings.Clear();
        OtherFindings.Clear();
        var subjects = new Dictionary<string, FindingViewModel>(StringComparer.Ordinal);
        foreach (var finding in all)
        {
            if (!IsPriority(finding.Status))
            {
                OtherFindings.Add(finding);
                continue;
            }

            // Même sujet signalé par plusieurs modules (Secure Boot dans M01 et M08) : une seule carte, la plus grave.
            if (Maus.Core.Reporting.HealthScore.SubjectOf(finding.Id) is { } subject)
            {
                if (subjects.TryGetValue(subject, out var first))
                {
                    first.AddAlsoIn(finding.Module);
                    continue;
                }

                subjects[subject] = finding;
            }

            PriorityFindings.Add(finding);
        }

        OnPropertyChanged(nameof(HasPriorityFindings));
        OnPropertyChanged(nameof(NothingToHandle));
        OnPropertyChanged(nameof(PriorityTitle));
        OnPropertyChanged(nameof(OthersLabel));
        OnPropertyChanged(nameof(HasOtherFindings));
        OnPropertyChanged(nameof(SelectedModuleFindings));
        OnPropertyChanged(nameof(HiddenConformingNote));
    }

    Task IFindingActions.AcknowledgeAsync(string moduleId, Finding finding, bool acknowledge) => OnAcknowledgeAsync(moduleId, finding, acknowledge);

    bool IFindingActions.CanFix(Finding finding) => ChangesFor(finding).Any();

    /// <summary>
    /// « Corriger » sur un constat : ses corrections sont cochées, remontées en tête de liste et entourées, puis l'onglet
    /// Corrections s'ouvre. Rien n'est appliqué : l'utilisateur relit et clique lui-même sur « Appliquer ».
    /// </summary>
    void IFindingActions.Fix(Finding finding)
    {
        var matches = ChangesFor(finding).ToList();
        foreach (var change in Changes)
        {
            change.IsHighlighted = false;
        }

        for (var i = matches.Count - 1; i >= 0; i--)
        {
            matches[i].IsSelected = true;
            matches[i].IsHighlighted = true;
            Changes.Move(Changes.IndexOf(matches[i]), 0);
        }

        SelectedTab = TabFixes;
        StatusText = T("« {0} » : {1} correction(s) cochée(s), en tête de liste. Relisez-les, puis cliquez sur « Appliquer ».", finding.Title, matches.Count);
    }

    /// <summary>Corrections proposées pour un constat (même identifiant, ou constat d'origine déclaré par la correction).</summary>
    private IEnumerable<ChangeViewModel> ChangesFor(Finding finding) =>
        Changes.Where(c => string.Equals(c.Change.FindingId ?? c.Change.Id, finding.Id, StringComparison.Ordinal));

    private static bool IsPriority(FindingStatus status) =>
        status is FindingStatus.Problem or FindingStatus.Warning or FindingStatus.Improvable;
}
