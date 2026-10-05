using System.Collections.ObjectModel;
using System.Windows.Input;
using Maus.App.Appearance;
using Maus.App.ViewModels.Workshop;
using Maus.Core;
using Maus.Core.Reporting;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Une famille M·A·U·S sur le tableau de bord, avec ses pastilles de comptage.</summary>
/// <param name="audited">Faux avant le premier audit : la famille est présentée, sans compte.</param>
public sealed class FamilyViewModel(FamilySummary summary, bool audited = true)
{
    public string Letter => summary.Letter;

    public string Name => summary.Name;

    public string Description => summary.Description;

    public IReadOnlyList<ChipViewModel> Chips { get; } = audited ? BuildChips(summary) : [new(T("pas encore audité"), Tone.Grey)];

    private static List<ChipViewModel> BuildChips(FamilySummary s)
    {
        var chips = new List<ChipViewModel>();
        if (s.Problems > 0)
        {
            chips.Add(new(s.Problems == 1 ? T("1 problème") : T("{0} problèmes", s.Problems), Tone.Rose));
        }

        if (s.Warnings > 0)
        {
            chips.Add(new(T("{0} à surveiller", s.Warnings), Tone.Sand));
        }

        if (s.Improvements > 0)
        {
            chips.Add(new(s.Improvements == 1 ? T("1 optimisation") : T("{0} optimisations", s.Improvements), Tone.Blue));
        }

        // Module en erreur ou en délai dépassé : rien n'y a été lu, la famille n'est donc pas « en ordre » (gris, jamais un faux vert).
        if (s.Unchecked > 0)
        {
            chips.Add(new(s.Unchecked == 1 ? T("1 module non vérifié") : T("{0} modules non vérifiés", s.Unchecked), Tone.Grey));
        }

        if (chips.Count == 0)
        {
            chips.Add(s.AllClear ? new(T("tout est en ordre"), Tone.Mint) : new(T("{0} indéterminé(s)", s.Unknown), Tone.Grey));
        }

        return chips;
    }
}

/// <summary>Pastille colorée (« 3 optimisations »).</summary>
public sealed record ChipViewModel(string Text, Tone Tone);

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

    /// <summary>Les quatre familles M·A·U·S, présentées dès l'ouverture puis comptées après chaque audit.</summary>
    public ObservableCollection<FamilyViewModel> Families { get; } = new(HealthScore.Summaries([]).Select(f => new FamilyViewModel(f, audited: false)));

    /// <summary>Score de santé sur 100, ou -1 avant le premier audit.</summary>
    public int Score { get; private set; } = -1;

    public bool HasScore => Score >= 0;

    public string ScoreText => HasScore ? Score.ToString(Culture) : "—";

    public string ScoreWord => HasScore ? HealthScore.Describe(Score) : T("lancez l'audit");

    public string ScoreDetail { get; private set; } = T("Le score apparaîtra après le premier audit. L'audit ne modifie rien.");

    /// <summary>Phrase « Score partiel : … » quand un module n'a pas pu être vérifié, sinon vide.</summary>
    public string ScorePartialNote { get; private set; } = string.Empty;

    public bool IsScorePartial => ScorePartialNote.Length > 0;

    public string Greeting { get; } = DateTime.Now.Hour switch
    {
        < 5 or >= 18 => T("Bonsoir"),
        _ => T("Bonjour"),
    };

    public string HomeAuditLabel => HasAudit ? T("Relancer l'audit") : T("Lancer l'audit");

    public string FixCardDetail => _lastContext is null
        ? T("Après l'audit : les corrections recommandées, réversibles, avec point de restauration vérifié.")
        : T("{0} correction(s) proposée(s), réversibles, avec point de restauration vérifié.", Changes.Count);

    /// <summary>Carte « prochaine étape » de l'accueil : l'audit d'abord, puis, dès qu'il est fini, les corrections.</summary>
    public string NextStepTitle => !HasAudit
        ? T("Première étape : l'audit")
        : Changes.Count > 1
            ? T("{0} corrections proposées", Changes.Count)
            : Changes.Count == 1
                ? T("1 correction proposée")
                : T("Rien à corriger pour l'instant");

    public string NextStepText => !HasAudit
        ? T("MAUS lit la configuration de votre PC sans rien modifier, puis vous propose des corrections. Chacune ne s'applique qu'avec votre accord et peut être annulée.")
        : Changes.Count > 0
            ? T("Réversibles, avec un point de restauration vérifié. Vous choisissez lesquelles appliquer.")
            : T("MAUS ne propose aucune correction. Les constats restent à lire dans l'onglet Constats.");

    public Tone NextStepTone => !HasAudit ? Tone.Blue : Changes.Count > 0 ? Tone.Mint : Tone.Grey;

    /// <summary>L'audit est fait et propose au moins une correction : le bouton « Corriger maintenant » apparaît.</summary>
    public bool HasProposedChanges => HasAudit && Changes.Count > 0;

    public string FixNowLabel => T("Corriger maintenant ({0})", Changes.Count);

    /// <summary>Navigation depuis l'accueil : paramètre = numéro d'onglet, ou 30 + sous-partie de l'atelier.</summary>
    public ICommand GoToCommand => _goTo ??= new ParameterCommand(parameter =>
    {
        if (parameter is not null && int.TryParse(parameter.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var tab))
        {
            if (tab >= 30)
            {
                OpenWorkshop(tab - 30);
                return;
            }

            SelectedTab = tab;
        }
    });

    private ICommand? _goTo;

    /// <summary>« Pourquoi mon PC est lent ? » : ouvre les processus de l'atelier et lance une minute de mesures.</summary>
    public ICommand SlowPcCommand => _slowPc ??= new ParameterCommand(_ =>
        OpenWorkshop(WorkshopViewModel.SectionProcesses, () => Workshop.DiagnoseCommand.Execute(null)));

    /// <summary>
    /// Ouvre l'atelier sur une sous-partie. À son affichage, la barre d'onglets de l'atelier revient d'elle-même au premier
    /// onglet (« Mon PC ») : la sous-partie est donc choisie une fois l'atelier affiché, sinon toutes les cartes de l'accueil
    /// menaient au même endroit.
    /// </summary>
    private void OpenWorkshop(int section, Action? then = null)
    {
        Workshop.Open(section);
        SelectedTab = TabWorkshop;
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
        {
            dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                Workshop.Open(section);
                then?.Invoke();
            });
        }
        else
        {
            then?.Invoke();
        }
    }

    private ICommand? _slowPc;

    /// <summary>Affichage du résumé pour demander de l'aide ; remplaçable pour les tests.</summary>
    public Action<string> ShowHelpSummary { get; init; } = summary =>
        new Views.HelpSummaryWindow(summary) { Owner = System.Windows.Application.Current?.MainWindow }.ShowDialog();

    /// <summary>« Demander de l'aide » : résumé du dernier audit, relu et complété par l'utilisateur avant de le copier.</summary>
    public ICommand HelpSummaryCommand => _helpSummary ??= new ParameterCommand(_ =>
    {
        if (_lastContext is { } context)
        {
            ShowHelpSummary(HelpSummary.Build(CurrentReport(context), PrivacyFilter.ForCurrentUser()));
        }
    });

    private ICommand? _helpSummary;

    /// <summary>Fermeture de la fenêtre : plus aucune mesure ni aucun test ne doit tourner en arrière-plan.</summary>
    public void Shutdown()
    {
        CloseSideWindows();
        _workshop?.Stop();
    }

    private IReadOnlyList<double> _healthTrend = [];
    private string _healthTrendText = string.Empty;

    /// <summary>Scores de santé des audits précédents (courbe de l'accueil).</summary>
    public IReadOnlyList<double> HealthTrend
    {
        get => _healthTrend;
        private set
        {
            if (SetProperty(ref _healthTrend, value))
            {
                OnPropertyChanged(nameof(HasHealthTrend));
                OnPropertyChanged(nameof(ShowWelcome));
            }
        }
    }

    public bool HasHealthTrend => HealthTrend.Count >= 2;

    /// <summary>Tout premier lancement (aucun audit ni score enregistré) : carte « Bienvenue » en trois étapes.</summary>
    public bool ShowWelcome => !HasAudit && HealthTrend.Count == 0;

    public string HealthTrendText
    {
        get => _healthTrendText;
        private set => SetProperty(ref _healthTrendText, value);
    }

    /// <summary>Courbe des audits précédents, affichée dès l'ouverture.</summary>
    private void LoadHealthTrend()
    {
        var series = Maus.Core.Workshop.ScoreTrends.Series(Maus.Core.Workshop.BenchmarkHistory.CreateHealth().Load(), Maus.Core.Workshop.ScoreTrends.HealthKind);
        HealthTrend = series.Select(e => e.Score).ToList();
        HealthTrendText = Maus.Core.Workshop.ScoreTrends.DescribeHealth(series);
    }

    /// <summary>
    /// Enregistre le score de cet audit (sur ce PC seulement) et met à jour la courbe. Un score partiel (module en erreur ou
    /// en délai dépassé) n'est pas enregistré : il paraîtrait meilleur que la réalité et la courbe monterait à tort.
    /// </summary>
    private async Task RecordHealthAsync()
    {
        var entry = Maus.Core.Workshop.ScoreTrends.HealthEntry(_results, DateTimeOffset.Now);
        var series = await Task.Run(() =>
        {
            var history = Maus.Core.Workshop.BenchmarkHistory.CreateHealth();
            try
            {
                if (entry is not null)
                {
                    history.Add(entry);
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                // Historique impossible à écrire : la courbe reste celle d'avant, sans gêner l'audit.
            }

            return Maus.Core.Workshop.ScoreTrends.Series(history.Load(), Maus.Core.Workshop.ScoreTrends.HealthKind);
        });
        HealthTrend = series.Select(e => e.Score).ToList();
        HealthTrendText = Maus.Core.Workshop.ScoreTrends.DescribeHealth(series);
    }

    private ScoreBreakdown? _breakdown;

    private ICommand? _showScoreDetail;

    /// <summary>« Pourquoi ce score ? » : ce qui a coûté des points et le barème.</summary>
    public ICommand ShowScoreDetailCommand => _showScoreDetail ??= new AsyncCommand(() =>
    {
        if (_breakdown is { } breakdown)
        {
            new Views.ScoreWindow(breakdown) { Owner = System.Windows.Application.Current?.MainWindow }.ShowDialog();
        }

        return Task.CompletedTask;
    });

    /// <summary>« 1 problème · 4 à surveiller · 20 optimisations », accordé au singulier ou au pluriel.</summary>
    private static string CountsText(IReadOnlyCollection<Finding> findings)
    {
        static string Count(int n, string one, string many) => string.Format(Culture, n > 1 ? many : one, n);
        return string.Join(" · ",
            Count(findings.Count(f => f.Status == FindingStatus.Problem), T("{0} problème"), T("{0} problèmes")),
            Count(findings.Count(f => f.Status == FindingStatus.Warning), T("{0} à surveiller"), T("{0} à surveiller")),
            Count(findings.Count(f => f.Status == FindingStatus.Improvable), T("{0} optimisation"), T("{0} optimisations")));
    }

    /// <summary>Met à jour le score et les familles après un audit ou un changement de constat.</summary>
    private void RefreshDashboard()
    {
        _breakdown = HealthScore.Explain(_results);
        Score = _breakdown.Score;
        var findings = _results.SelectMany(r => r.Findings).ToList();
        ScoreDetail = CountsText(findings);
        ScorePartialNote = HealthScore.PartialNote(_breakdown) ?? string.Empty;
        RefreshFindingLists();
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
        OnPropertyChanged(nameof(ScorePartialNote));
        OnPropertyChanged(nameof(IsScorePartial));
        OnPropertyChanged(nameof(HomeAuditLabel));
        OnPropertyChanged(nameof(FixCardDetail));
        OnPropertyChanged(nameof(NextStepTitle));
        OnPropertyChanged(nameof(NextStepText));
        OnPropertyChanged(nameof(NextStepTone));
        OnPropertyChanged(nameof(HasProposedChanges));
        OnPropertyChanged(nameof(FixNowLabel));
    }
}
