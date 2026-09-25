using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using Maus.App.Controls;
using Maus.App.ViewModels.Workshop;
using Maus.Core;
using Maus.Core.Reporting;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Une famille M·A·U·S sur le tableau de bord, avec ses pastilles de comptage.</summary>
public sealed class FamilyViewModel(FamilySummary summary)
{
    public string Letter => summary.Letter;

    public string Name => summary.Name;

    public string Description => summary.Description;

    public IReadOnlyList<ChipViewModel> Chips { get; } = BuildChips(summary);

    private static List<ChipViewModel> BuildChips(FamilySummary s)
    {
        var chips = new List<ChipViewModel>();
        if (s.Problems > 0)
        {
            chips.Add(new(s.Problems == 1 ? T("1 problème") : T("{0} problèmes", s.Problems), Palette.Red));
        }

        if (s.Warnings > 0)
        {
            chips.Add(new(T("{0} à surveiller", s.Warnings), Palette.Gold));
        }

        if (s.Improvements > 0)
        {
            chips.Add(new(s.Improvements == 1 ? T("1 optimisation") : T("{0} optimisations", s.Improvements), Palette.Blue));
        }

        if (chips.Count == 0)
        {
            chips.Add(s.Unknown > 0 ? new(T("{0} indéterminé(s)", s.Unknown), Palette.Grey) : new(T("tout est en ordre"), Palette.Green));
        }

        return chips;
    }
}

/// <summary>Pastille colorée (« 3 optimisations »).</summary>
public sealed record ChipViewModel(string Text, Brush Background);

/// <summary>Accueil (tableau de bord) et navigation entre les grandes parties de la fenêtre.</summary>
public sealed partial class MainViewModel
{
    public const int TabHome = 0;
    public const int TabFindings = 1;
    public const int TabFixes = 2;
    public const int TabWorkshop = 3;
    public const int TabHistory = 4;

    private int _selectedTab;
    private WorkshopViewModel? _workshop;

    public WorkshopViewModel Workshop => _workshop ??= new WorkshopViewModel(Confirm, () => _lastContext, PreferencesStore);

    /// <summary>Partie affichée : Accueil, Constats, Corrections, Atelier ou Historique.</summary>
    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetProperty(ref _selectedTab, value))
            {
                Workshop.IsActive = value == TabWorkshop;
            }
        }
    }

    public ObservableCollection<FamilyViewModel> Families { get; } = [];

    /// <summary>Score de santé sur 100, ou -1 avant le premier audit.</summary>
    public int Score { get; private set; } = -1;

    public bool HasScore => Score >= 0;

    public string ScoreText => HasScore ? Score.ToString(Culture) : "—";

    public string ScoreWord => HasScore ? HealthScore.Describe(Score) : T("lancez l'audit");

    public string ScoreDetail { get; private set; } = T("Le score apparaîtra après le premier audit. L'audit ne modifie rien.");

    public string Greeting { get; } = DateTime.Now.Hour switch
    {
        < 5 or >= 18 => T("Bonsoir"),
        _ => T("Bonjour"),
    };

    public string HomeAuditLabel => HasAudit ? T("Relancer l'audit") : T("Lancer l'audit");

    public string FixCardDetail => _lastContext is null
        ? T("Après l'audit : les corrections recommandées, réversibles, avec point de restauration vérifié.")
        : T("{0} correction(s) proposée(s), réversibles, avec point de restauration vérifié.", Changes.Count);

    /// <summary>Navigation depuis l'accueil : paramètre = numéro d'onglet, ou 30 + sous-partie de l'atelier.</summary>
    public ICommand GoToCommand => _goTo ??= new ParameterCommand(parameter =>
    {
        if (parameter is not null && int.TryParse(parameter.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var tab))
        {
            if (tab >= 30)
            {
                Workshop.Open(tab - 30);
                tab = TabWorkshop;
            }

            SelectedTab = tab;
        }
    });

    private ICommand? _goTo;

    /// <summary>« Pourquoi mon PC est lent ? » : ouvre les processus de l'atelier et lance une minute de mesures.</summary>
    public ICommand SlowPcCommand => _slowPc ??= new ParameterCommand(_ =>
    {
        Workshop.Open(WorkshopViewModel.SectionProcesses);
        SelectedTab = TabWorkshop;
        Workshop.DiagnoseCommand.Execute(null);
    });

    private ICommand? _slowPc;

    /// <summary>Fermeture de la fenêtre : plus aucune mesure ni aucun test ne doit tourner en arrière-plan.</summary>
    public void Shutdown() => _workshop?.Stop();

    /// <summary>Met à jour le score et les familles après un audit ou un changement de constat.</summary>
    private void RefreshDashboard()
    {
        Score = HealthScore.Compute(_results);
        var findings = _results.SelectMany(r => r.Findings).ToList();
        ScoreDetail = T("{0} problème(s) · {1} à surveiller · {2} optimisation(s)",
            findings.Count(f => f.Status == FindingStatus.Problem),
            findings.Count(f => f.Status == FindingStatus.Warning),
            findings.Count(f => f.Status == FindingStatus.Improvable));
        Families.Clear();
        foreach (var family in HealthScore.Summaries(_results))
        {
            Families.Add(new FamilyViewModel(family));
        }

        OnPropertyChanged(nameof(Score));
        OnPropertyChanged(nameof(HasScore));
        OnPropertyChanged(nameof(ScoreText));
        OnPropertyChanged(nameof(ScoreWord));
        OnPropertyChanged(nameof(ScoreDetail));
        OnPropertyChanged(nameof(HomeAuditLabel));
        OnPropertyChanged(nameof(FixCardDetail));
    }
}
