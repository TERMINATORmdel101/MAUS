using System.Collections.ObjectModel;
using Maus.Core;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>
/// Onglet Constats (demande du porteur, 05/10/2026) : d'abord ce qui demande une action, du plus grave au plus léger, tous
/// modules confondus ; ce qui va bien, les informations et les points illisibles seulement à la demande.
/// </summary>
public sealed partial class MainViewModel
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
                (finding, acknowledge) => OnAcknowledgeAsync(x.Result.ModuleId, finding, acknowledge),
                Modules.FirstOrDefault(m => m.Id == x.Result.ModuleId)?.Header))
            .ToList();

        PriorityFindings.Clear();
        OtherFindings.Clear();
        foreach (var finding in all)
        {
            (IsPriority(finding.Status) ? PriorityFindings : OtherFindings).Add(finding);
        }

        OnPropertyChanged(nameof(HasPriorityFindings));
        OnPropertyChanged(nameof(NothingToHandle));
        OnPropertyChanged(nameof(PriorityTitle));
        OnPropertyChanged(nameof(OthersLabel));
        OnPropertyChanged(nameof(HasOtherFindings));
        OnPropertyChanged(nameof(SelectedModuleFindings));
        OnPropertyChanged(nameof(HiddenConformingNote));
    }

    private static bool IsPriority(FindingStatus status) =>
        status is FindingStatus.Problem or FindingStatus.Warning or FindingStatus.Improvable;
}
