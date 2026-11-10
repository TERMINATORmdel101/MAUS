using System.Collections.ObjectModel;
using System.Windows.Threading;
using Maus.Core;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Filtre de la liste « À traiter d'abord ».</summary>
public enum FindingFilter
{
    All,
    RedOnly,
    Fixable,
}

/// <summary>
/// Onglet Constats (demande du porteur, 05/10/2026) : d'abord ce qui demande une action, du plus grave au plus léger, tous
/// modules confondus ; ce qui va bien, les informations et les points illisibles seulement à la demande.
/// </summary>
public sealed partial class MainViewModel : IFindingActions
{
    private bool _showByModule;
    private bool _showConforming;
    private bool _showOthers;
    private int _priorityTotal;

    /// <summary>Constats fondus dans la carte d'un même sujet : ils comptent dans le score, pas dans les cartes.</summary>
    private int _priorityMerged;
    private bool _prioritySerious;
    private string _findingSearch = string.Empty;
    private DispatcherTimer? _findingSearchTimer;
    private ChoiceOption<FindingFilter>? _findingFilter;
    private ChoiceOption<Maus.Core.Reporting.ModuleFamily?>? _familyFilter;

    public IReadOnlyList<ChoiceOption<FindingFilter>> FindingFilterOptions { get; } =
    [
        new(T("Tout ce qui demande une action"), FindingFilter.All),
        new(T("Seulement les problèmes rouges"), FindingFilter.RedOnly),
        new(T("Seulement ce que MAUS peut corriger"), FindingFilter.Fixable),
    ];

    public IReadOnlyList<ChoiceOption<Maus.Core.Reporting.ModuleFamily?>> FamilyFilterOptions { get; } =
    [
        new(T("Toutes les familles"), null),
        .. Maus.Core.Reporting.HealthScore.Summaries([]).Select(f => new ChoiceOption<Maus.Core.Reporting.ModuleFamily?>(f.Name, f.Family)),
    ];

    public ChoiceOption<FindingFilter> FindingFilterChoice
    {
        get => _findingFilter ?? FindingFilterOptions[0];
        set
        {
            if (value is not null && value != FindingFilterChoice)
            {
                _findingFilter = value;
                OnPropertyChanged();
                RefreshFindingLists();
            }
        }
    }

    public ChoiceOption<Maus.Core.Reporting.ModuleFamily?> FamilyFilterChoice
    {
        get => _familyFilter ?? FamilyFilterOptions[0];
        set
        {
            if (value is not null && value != FamilyFilterChoice)
            {
                _familyFilter = value;
                OnPropertyChanged();
                RefreshFindingLists();
            }
        }
    }

    /// <summary>Le filtre choisi écarte tout : à dire, pour ne pas faire croire que tout va bien.</summary>
    public bool FilterHidesAll => _priorityTotal > 0 && PriorityFindings.Count == 0;

    /// <summary>Problèmes, points à surveiller et optimisations, du plus grave au plus léger.</summary>
    public ObservableCollection<FindingViewModel> PriorityFindings { get; } = [];

    /// <summary>Le reste : conformes, informations (dont les constats « voulus ») et indéterminés.</summary>
    public ObservableCollection<FindingViewModel> OtherFindings { get; } = [];

    public bool HasPriorityFindings => _priorityTotal > 0;

    /// <summary>Audit fait, et rien qui demande une action.</summary>
    public bool NothingToHandle => HasAudit && _priorityTotal == 0;

    /// <summary>Audit fait, ni problème ni point à surveiller : un PC sain doit pouvoir le lire même si des optimisations restent proposées.</summary>
    public bool OnlyOptionalLeft => HasAudit && _priorityTotal > 0 && !_prioritySerious;

    /// <summary>
    /// Tout n'a pas pu être lu : score partiel (audit limité, module non vérifié), module en erreur ou en délai dépassé, ou constat
    /// « indéterminé ». Les bandeaux rassurants le disent alors (« Dans ce qui a pu être lu : »), sans le vert.
    /// </summary>
    public bool SomethingUnread =>
        IsScorePartial || _results.Any(r => r.Error is not null || r.Findings.Any(f => f.Status == FindingStatus.Unknown));

    public bool ShowNothingToHandle => NothingToHandle && !SomethingUnread;

    public bool ShowNothingToHandleInRead => NothingToHandle && SomethingUnread;

    public bool ShowOnlyOptionalLeft => OnlyOptionalLeft && !SomethingUnread;

    public bool ShowOnlyOptionalLeftInRead => OnlyOptionalLeft && SomethingUnread;

    public string PriorityTitle => PriorityFindings.Count == _priorityTotal
        ? T("À traiter d'abord ({0})", _priorityTotal)
        : T("À traiter d'abord ({0} sur {1})", PriorityFindings.Count, _priorityTotal);

    /// <summary>Pourquoi « À traiter » compte moins de points que le score ne compte de constats : un même sujet ne fait qu'une carte.</summary>
    public string PriorityMergedNote => _priorityMerged == 0
        ? string.Empty
        : _priorityTotal > 1
            ? T("{0} constats pour {1} points à traiter : un même sujet signalé par plusieurs modules ne compte qu'une fois.", _priorityTotal + _priorityMerged, _priorityTotal)
            : T("{0} constats pour {1} point à traiter : un même sujet signalé par plusieurs modules ne compte qu'une fois.", _priorityTotal + _priorityMerged, _priorityTotal);

    public bool HasPriorityMergedNote => _priorityMerged > 0;

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

    /// <summary>Recherche dans tous les constats, conformes compris : pour savoir où en est l'antivirus sans deviner le module.</summary>
    public string FindingSearch
    {
        get => _findingSearch;
        set
        {
            if (SetProperty(ref _findingSearch, value ?? string.Empty))
            {
                // Pas à chaque lettre : ranger toutes les cartes de constats a un coût.
                _findingSearchTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => ApplyFindingSearch(), Dispatcher.CurrentDispatcher);
                _findingSearchTimer.Stop();
                _findingSearchTimer.Start();
            }
        }
    }

    /// <summary>Rien ne contient le texte cherché : l'écran le dit, plutôt qu'une page vide.</summary>
    public bool FindingSearchFindsNothing => FindingSearch.Trim().Length > 0 && PriorityFindings.Count == 0 && OtherFindings.Count == 0;

    internal void ApplyFindingSearch()
    {
        _findingSearchTimer?.Stop();
        ShowOthers |= FindingSearch.Trim().Length > 0;
        RefreshFindingLists();
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
            return hidden == 0 ? string.Empty : Plural(hidden, T("{0} constat conforme masqué."), T("{0} constats conformes masqués."));
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
        _priorityTotal = 0;
        _priorityMerged = 0;
        _prioritySerious = all.Exists(f => f.Status is FindingStatus.Problem or FindingStatus.Warning);
        var subjects = new Dictionary<string, FindingViewModel>(StringComparer.Ordinal);
        var cards = new List<(FindingViewModel Card, List<string> ModuleIds)>();
        foreach (var finding in all)
        {
            if (!IsPriority(finding.Status))
            {
                if (Maus.Core.Reporting.FindingSearch.Matches(finding.Finding, FindingSearch))
                {
                    OtherFindings.Add(finding);
                }

                continue;
            }

            // Même sujet signalé par plusieurs modules (Secure Boot dans M01 et M08) : une seule carte, la plus grave.
            if (Maus.Core.Reporting.HealthScore.SubjectOf(finding.Id) is { } subject)
            {
                if (subjects.TryGetValue(subject, out var first))
                {
                    first.AddAlsoIn(finding.Module);
                    cards.First(c => c.Card == first).ModuleIds.Add(finding.ModuleId);
                    _priorityMerged++;
                    continue;
                }

                subjects[subject] = finding;
            }

            cards.Add((finding, [finding.ModuleId]));
        }

        // Après la boucle : une carte fondue reste dans chaque famille que l'un de ses modules concerne.
        _priorityTotal = cards.Count;
        foreach (var (card, moduleIds) in cards)
        {
            if (PassesFilter(card, moduleIds))
            {
                PriorityFindings.Add(card);
            }
        }

        OnPropertyChanged(nameof(FindingSearchFindsNothing));
        OnPropertyChanged(nameof(FilterHidesAll));
        OnPropertyChanged(nameof(HasPriorityFindings));
        OnPropertyChanged(nameof(NothingToHandle));
        OnPropertyChanged(nameof(OnlyOptionalLeft));
        OnPropertyChanged(nameof(SomethingUnread));
        OnPropertyChanged(nameof(ShowNothingToHandle));
        OnPropertyChanged(nameof(ShowNothingToHandleInRead));
        OnPropertyChanged(nameof(ShowOnlyOptionalLeft));
        OnPropertyChanged(nameof(ShowOnlyOptionalLeftInRead));
        OnPropertyChanged(nameof(PriorityTitle));
        OnPropertyChanged(nameof(PriorityMergedNote));
        OnPropertyChanged(nameof(HasPriorityMergedNote));
        OnPropertyChanged(nameof(OthersLabel));
        OnPropertyChanged(nameof(HasOtherFindings));
        OnPropertyChanged(nameof(SelectedModuleFindings));
        OnPropertyChanged(nameof(HiddenConformingNote));
    }

    private bool PassesFilter(FindingViewModel finding, List<string> moduleIds) =>
        Maus.Core.Reporting.FindingSearch.Matches(finding.Finding, FindingSearch)
        && (FamilyFilterChoice.Value is not { } family || moduleIds.Exists(id => Maus.Core.Reporting.HealthScore.FamilyOf(id) == family))
        && FindingFilterChoice.Value switch
        {
            FindingFilter.RedOnly => finding.Status == FindingStatus.Problem,
            FindingFilter.Fixable => finding.CanFix,
            _ => true,
        };

    Task IFindingActions.AcknowledgeAsync(string moduleId, Finding finding, bool acknowledge) => OnAcknowledgeAsync(moduleId, finding, acknowledge);

    bool IFindingActions.CanFix(Finding finding) => ChangesFor(finding).Any();

    /// <summary>
    /// « Corriger » sur un constat : ses corrections sont cochées et entourées, la page défile jusqu'à elles, et l'onglet
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
            ShowMoreChanges |= MoreChanges.Contains(matches[i]);
            Changes.Move(Changes.IndexOf(matches[i]), 0);
        }

        SelectedTab = TabFixes;
        StatusText = T("« {0} » : {1}", finding.Title, Plural(matches.Count, T("{0} correction cochée, entourée dans la liste. Relisez-la, puis cliquez sur « Appliquer »."), T("{0} corrections cochées, entourées dans la liste. Relisez-les, puis cliquez sur « Appliquer ».")));
    }

    /// <summary>Corrections proposées pour un constat (même identifiant, ou constat d'origine déclaré par la correction).</summary>
    private IEnumerable<ChangeViewModel> ChangesFor(Finding finding) =>
        Changes.Where(c => string.Equals(c.Change.FindingId ?? c.Change.Id, finding.Id, StringComparison.Ordinal));

    private static bool IsPriority(FindingStatus status) =>
        status is FindingStatus.Problem or FindingStatus.Warning or FindingStatus.Improvable;
}
