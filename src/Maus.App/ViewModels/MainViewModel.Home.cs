using Maus.Core.Preferences;
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
    public override string ToString() => CompactLabel;

    public string Letter => summary.Letter;

    public string Name => summary.Name;

    public string Description => summary.Description;

    public IReadOnlyList<ChipViewModel> Chips { get; } = audited ? BuildChips(summary) : [new(T("pas encore audité"), Tone.Grey)];

    /// <summary>Accueil simple : nombre de points à voir (« 3 à voir », « en ordre », « — » avant l'audit).</summary>
    public string CompactText { get; } = !audited ? "—"
        : summary.Problems + summary.Warnings + summary.Improvements is var n and > 0 ? T("{0} à voir", n)
        : summary.Unchecked > 0 ? T("non vérifié")
        : summary.AllClear ? T("en ordre") : T("indéterminé");

    /// <summary>Couleur du plus grave : rouge (problème), sable (à surveiller), bleu (optimisation), menthe (en ordre).</summary>
    public Tone CompactTone { get; } = !audited ? Tone.Grey
        : summary.Problems > 0 ? Tone.Rose
        : summary.Warnings > 0 ? Tone.Sand
        : summary.Improvements > 0 ? Tone.Blue
        : summary.AllClear && summary.Unchecked == 0 ? Tone.Mint : Tone.Grey;

    /// <summary>Nom lu par les lecteurs d'écran (« Maintenance : 3 à voir »).</summary>
    public string CompactLabel => T("{0} : {1}", Name, CompactText);

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
            chips.Add(s.AllClear ? new(T("tout est en ordre"), Tone.Mint) : new(Plural(s.Unknown, T("{0} indéterminé"), T("{0} indéterminés")), Tone.Grey));
        }

        return chips;
    }
}

/// <summary>Pastille colorée (« 3 optimisations »).</summary>
public sealed record ChipViewModel(string Text, Tone Tone)
{
    public override string ToString() => Text;
}

/// <summary>Accueil (tableau de bord) et navigation entre les grandes parties de la fenêtre.</summary>
public sealed partial class MainViewModel
{
    public const int TabFindings = 1;
    public const int TabFixes = 2;
    public const int TabWorkshop = 3;
    public const int TabHistory = 4;
    public const int TabBenchmark = 5;
    public const int TabFollow = 6;

    private int _selectedTab;
    private WorkshopViewModel? _workshop;

    public WorkshopViewModel Workshop => _workshop ??= new WorkshopViewModel(Confirm, PreferencesStore) { OpenModuleFindings = ShowModuleFindings, InventoryRead = inventory => RefreshNewPartAsync(inventory) };

    /// <summary>Partie affichée : Accueil, Constats, Corrections, Atelier ou Historique.</summary>
    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetProperty(ref _selectedTab, value))
            {
                Workshop.IsActive = value == TabWorkshop;
                if (value == TabFindings)
                {
                    MarkFindingsSeen();
                }

                if (value == TabHistory)
                {
                    Maus.Core.Diagnostics.BackgroundTasks.Forget(LoadKeptRefreshRatesAsync(), "fréquences d'écran gardées");
                }
            }
        }
    }

    /// <summary>Les quatre familles M·A·U·S, présentées dès l'ouverture puis comptées après chaque audit.</summary>
    public ObservableCollection<FamilyViewModel> Families { get; } = new(HealthScore.Summaries([]).Select(f => new FamilyViewModel(f, audited: false)));

    /// <summary>Score de santé sur 100, ou -1 avant le premier audit.</summary>
    public int Score { get; private set; } = -1;

    public bool HasScore => Score >= 0;

    public string ScoreText => HasScore ? Math.Round(Math.Max(0, _displayedScore)).ToString(Culture) : IsPastScore ? _pastScore.ToString(Culture) : "—";

    public string ScoreWord => HasScore ? (IsScorePartial ? T("partiel") : HealthScore.Describe(Score)) : ShowLastAudit ? T("dernier audit") : IsRunning ? T("audit en cours") : T("lancez l'audit");

    /// <summary>Sous le score : les totaux de l'audit ; pendant un tout premier audit, ce qui se passe.</summary>
    public string ScoreDetail
    {
        get => IsRunning && !HasScore && !ShowLastAudit ? T("Le score apparaîtra à la fin de l'audit. L'audit ne modifie rien.") : _scoreDetail;
        private set => _scoreDetail = value;
    }

    private string _scoreDetail = T("Le score apparaîtra après le premier audit. L'audit ne modifie rien.");

    /// <summary>Phrase « Score partiel : … » quand un module n'a pas pu être vérifié, sinon vide.</summary>
    public string ScorePartialNote { get; private set; } = string.Empty;

    public bool IsScorePartial => ScorePartialNote.Length > 0;

    public string Greeting { get; } = DateTime.Now.Hour switch
    {
        < 5 or >= 18 => T("Bonsoir"),
        _ => T("Bonjour"),
    };

    public string HomeAuditLabel => HasAudit || _lastAudit is not null ? T("Relancer l'audit") : T("Lancer l'audit");

    private bool _findingsSeen;

    /// <summary>
    /// Parcours « première fois » (0.8.6) : 1 lancer l'audit, 2 lire les constats, 3 appliquer les corrections recommandées ;
    /// 0 quand il est fini (constats lus et rien à appliquer, ou corrections déjà appliquées dans cette ouverture), ou pour un
    /// habitué qui rouvre MAUS sans audit dans cette fenêtre : un audit est déjà gardé, ce n'est plus l'« étape 1 ».
    /// </summary>
    public int JourneyStep => !HasAudit ? (_lastAudit is null ? 1 : 0) : _lastApplied is not null ? 0 : !_findingsSeen ? 2 : HasProposedChanges ? 3 : 0;

    public bool ShowJourney => JourneyStep > 0;

    public IReadOnlyList<JourneyStepRow> JourneySteps =>
    [
        new(1, T("Lancer l'audit"), JourneyStep),
        new(2, T("Lire les constats"), JourneyStep),
        new(3, T("Appliquer les corrections recommandées"), JourneyStep),
    ];

    /// <summary>Étape 2 : le bouton principal mène aux constats.</summary>
    public bool ShowReadFindingsButton => JourneyStep == 2;

    /// <summary>Carte « prochaine étape » de l'accueil : l'audit d'abord, puis les constats, puis les corrections.</summary>
    public string NextStepTitle => JourneyStep switch
    {
        1 => T("Étape 1 sur 3 : l'audit"),
        2 => T("Étape 2 sur 3 : lire les constats"),
        3 => T("Étape 3 sur 3 : appliquer les corrections recommandées"),
        _ when !HasAudit => T("Relancez l'audit pour voir l'état actuel"),
        _ when IsLimitedAudit => T("Pour les corrections : toutes les fonctionnalités"),
        _ => Changes.Count > 1
            ? T("{0} corrections proposées", Changes.Count)
            : Changes.Count == 1
                ? T("1 correction proposée")
                : T("Rien à corriger pour l'instant"),
    };

    public string NextStepText => JourneyStep switch
    {
        1 => T("MAUS lit la configuration de votre PC sans rien modifier, puis vous propose des corrections. Chacune ne s'applique qu'avec votre accord et peut être annulée, sauf si elle signale le contraire."),
        2 => _priorityTotal > 0
            ? Plural(_priorityTotal, T("L'audit a relevé {0} point à traiter. Chaque constat dit pourquoi il compte ; les détails techniques restent repliés."), T("L'audit a relevé {0} points à traiter. Chaque constat dit pourquoi il compte ; les détails techniques restent repliés."))
            : T("L'audit n'a rien relevé qui demande une action. Les constats conformes restent à lire dans l'onglet Constats."),
        3 => T("Réversibles, sauf ce qu'elles signalent, avec un point de restauration vérifié (option recommandée). Vous choisissez lesquelles appliquer."),
        _ when !HasAudit => T("MAUS lit la configuration de votre PC sans rien modifier. Le dernier audit gardé reste affiché, daté, jusqu'au nouveau."),
        _ when IsLimitedAudit => T("Cet audit s'est fait sans droits administrateur : MAUS n'a préparé aucune correction. Cliquez sur « Toutes les fonctionnalités », puis relancez l'audit pour les voir."),
        _ => Changes.Count > 0
            ? T("Réversibles, sauf ce qu'elles signalent, avec un point de restauration vérifié (option recommandée). Vous choisissez lesquelles appliquer.")
            : T("MAUS ne propose aucune correction. Les constats restent à lire dans l'onglet Constats."),
    };

    public Tone NextStepTone => JourneyStep switch
    {
        1 => Tone.Blue,
        2 => Tone.Lilac,
        3 => Tone.Mint,
        _ when !HasAudit => Tone.Blue,
        _ => Changes.Count > 0 ? Tone.Mint : Tone.Grey,
    };

    /// <summary>L'audit est fait et propose au moins une correction : le bouton « Appliquer les corrections » apparaît.</summary>
    public bool HasProposedChanges => HasAudit && Changes.Count > 0;

    /// <summary>Le bouton des corrections s'efface le temps de l'étape 2, pour que les constats soient lus d'abord.</summary>
    public bool ShowFixNowButton => HasProposedChanges && JourneyStep != 2;

    public string FixNowLabel => Changes.Count(c => c.Change.Recommended && !c.Change.Advanced) is > 0 and var recommended
        ? T("Voir les corrections recommandées ({0})", recommended)
        : T("Voir les corrections");

    /// <summary>Les constats ont été ouverts après l'audit : l'étape 2 est faite.</summary>
    private void MarkFindingsSeen()
    {
        if (HasAudit && !_findingsSeen)
        {
            _findingsSeen = true;
            NotifyJourney();
        }
    }

    private void NotifyJourney()
    {
        foreach (var name in new[] { nameof(JourneyStep), nameof(ShowJourney), nameof(JourneySteps), nameof(ShowReadFindingsButton), nameof(ShowFixNowButton), nameof(NextStepTitle), nameof(NextStepText), nameof(NextStepTone) })
        {
            OnPropertyChanged(name);
        }
    }

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
    public Action<string, string> ShowHelpSummary { get; init; } = (summary, anonymization) =>
        new Views.HelpSummaryWindow(summary, anonymization) { Owner = System.Windows.Application.Current?.MainWindow }.ShowDialog();

    /// <summary>« Demander de l'aide » : résumé du dernier audit, relu et complété par l'utilisateur avant de le copier.</summary>
    public ICommand HelpSummaryCommand => _helpSummary ??= new ParameterCommand(_ =>
    {
        if (_lastContext is { } context)
        {
            var privacy = PrivacyFilter.ForCurrentUser();
            var summary = HelpSummary.Build(CurrentReport(context), privacy);
            ShowHelpSummary(summary, HelpSummary.AnonymizationNote(privacy));
        }
    });

    private ICommand? _helpSummary;

    /// <summary>Fermeture de la fenêtre : plus aucune mesure ni aucun test ne doit tourner en arrière-plan.</summary>
    /// <summary>Rouvre l'atelier ou le benchmark, seuls onglets utiles sans audit ; les autres reviennent à l'accueil.</summary>
    public void RestoreView(Maus.Core.Preferences.UserPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ShowWeeklyNotification(preferences.WeeklyNotification);

        // Lancé en administrateur, aucun changement de droits ne viendra remettre les cases sur le choix enregistré.
        if (EngineHost.IsElevated)
        {
            try
            {
                ShowSkippedModules(PreferencesStore.Load().SkippedModules);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or Maus.Core.Fixes.JournalUnsafeException or Maus.Core.Engine.Broker.EngineException)
            {
                Maus.Core.Diagnostics.Breadcrumbs.Add("choix des modules non relus à l'ouverture");
            }
        }

        if (preferences.LastTab == TabWorkshop)
        {
            OpenWorkshop(Math.Clamp(preferences.LastWorkshopSection, 0, 6));
        }
        else if (preferences.LastTab == TabBenchmark)
        {
            SelectedTab = TabBenchmark;
        }
    }

    /// <summary>
    /// Note l'onglet ouvert et la place de la fenêtre, à la fermeture. Un échec est sans conséquence, et un moteur qui ne
    /// répond plus ne doit pas retenir la fenêtre : l'attente est bornée, puis l'enregistrement est abandonné.
    /// </summary>
    public void RememberView(Maus.Core.Preferences.WindowPlacement placement)
    {
        var (tab, section, zoom) = (SelectedTab, Workshop.Section, Appearance.AppearanceManager.Current.UiZoomUsed);
        var save = Task.Run(() =>
        {
            try
            {
                PreferencesStore.Update(p => p with { MainWindow = placement, LastTab = tab, LastWorkshopSection = section, UiZoom = zoom });
            }
            catch (Exception ex) when (ex is Maus.Core.Fixes.JournalUnsafeException or Maus.Core.Engine.Broker.EngineException or System.IO.IOException or UnauthorizedAccessException)
            {
                Maus.Core.Diagnostics.Breadcrumbs.Add("place de la fenêtre non enregistrée");
            }
        });
        if (!save.Wait(TimeSpan.FromSeconds(2)))
        {
            Maus.Core.Diagnostics.Breadcrumbs.Add("place de la fenêtre : enregistrement abandonné après 2 secondes");
        }
    }

    /// <summary>Droits accordés : lance l'audit si l'utilisateur l'a choisi. Ne peut pas provoquer de demande d'autorisation : le moteur tourne déjà.</summary>
    private void OnElevationChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(JournalSummary));
        if (!EngineHost.IsElevated)
        {
            return;
        }

        // Ces choix se lisent par le moteur : jamais sur le fil de l'interface, qui doit se redessiner tout de suite.
        Maus.Core.Diagnostics.BackgroundTasks.Forget(Task.Run(() =>
        {
            var preferences = PreferencesStore.Load();
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                ShowSkippedModules(preferences.SkippedModules);
                ShowWeeklyNotification(preferences.WeeklyNotification);
                if (EngineHost.IsElevated && (!HasAudit || IsLimitedAudit) && !IsRunning && !IsApplying && preferences.AuditWhenElevated)
                {
                        Maus.Core.Diagnostics.BackgroundTasks.Forget(RunAuditAsync(), "audit après les droits administrateur", ex => StatusText = Maus.Core.Diagnostics.ErrorText.For(ex));
                }
            });
        }), "choix protégés après les droits administrateur", _ => { });
    }

    public void Shutdown()
    {
        EngineHost.ElevationChanged -= OnElevationChanged;
        CloseSideWindows();
        UnhookAppearance();
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
            }
        }
    }

    public bool HasHealthTrend => HealthTrend.Count >= 2;

    public string HealthTrendText
    {
        get => _healthTrendText;
        private set => SetProperty(ref _healthTrendText, value);
    }

    /// <summary>Courbe des audits précédents, affichée dès l'ouverture.</summary>
    private void LoadHealthTrend()
    {
        var entries = _histories.Health.Load();
        HealthTrend = Maus.Core.Workshop.ScoreTrends.Series(entries, Maus.Core.Workshop.ScoreTrends.HealthKind).Select(e => e.Score).ToList();
        HealthTrendText = Maus.Core.Workshop.ScoreTrends.DescribeHealthHistory(entries);
    }

    /// <summary>
    /// Enregistre le score de cet audit (sur ce PC seulement) et met à jour la courbe. Un score partiel (module en erreur ou
    /// en délai dépassé) n'est pas enregistré : il paraîtrait meilleur que la réalité et la courbe monterait à tort.
    /// </summary>
    private async Task RecordHealthAsync()
    {
        var entry = Maus.Core.Workshop.ScoreTrends.HealthEntry(_results, DateTimeOffset.Now);
        var entries = await Task.Run(() =>
        {
            var history = _histories.Health;
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

            return history.Load();
        });
        HealthTrend = Maus.Core.Workshop.ScoreTrends.Series(entries, Maus.Core.Workshop.ScoreTrends.HealthKind).Select(e => e.Score).ToList();
        HealthTrendText = Maus.Core.Workshop.ScoreTrends.DescribeHealthHistory(entries);
    }

    private ScoreBreakdown? _breakdown;

    private ICommand? _showScoreDetail;

    /// <summary>« Pourquoi ce score ? » : ce qui a coûté des points et le barème.</summary>
    public ICommand ShowScoreDetailCommand => _showScoreDetail ??= new AsyncCommand(() =>
    {
        if (_breakdown is { } breakdown)
        {
            new Views.ScoreWindow(breakdown, IsLimitedAudit ? HealthScore.LimitedNote(_results) : null) { Owner = System.Windows.Application.Current?.MainWindow }.ShowDialog();
        }
    });

    /// <summary>« 1 problème · 4 à surveiller · 20 optimisations », accordé au singulier ou au pluriel.</summary>
    private static string CountsText(IReadOnlyCollection<FindingStatus> statuses)
    {
        // Espaces insécables : un compte ne se coupe pas en fin de ligne.
        static string Count(int n, string one, string many) => Plural(n, one, many).Replace(' ', '\u00A0');
        return string.Join(" · ",
            Count(statuses.Count(s => s == FindingStatus.Problem), T("{0} problème"), T("{0} problèmes")),
            Count(statuses.Count(s => s == FindingStatus.Warning), T("{0} à surveiller"), T("{0} à surveiller")),
            Count(statuses.Count(s => s == FindingStatus.Improvable), T("{0} optimisation"), T("{0} optimisations")));
    }

    /// <summary>Met à jour le score et les familles après un audit ou un changement de constat.</summary>
    private void RefreshDashboard()
    {
        _breakdown = HealthScore.Explain(_results);
        Score = _breakdown.Score;
        AnimateScore(Score);
        RefreshGlance();
        var findings = _results.SelectMany(r => r.Findings).ToList();
        ScoreDetail = CountsText([.. findings.Select(f => f.Status)]);
        ScorePartialNote = string.Join(" ", new[] { IsLimitedAudit ? HealthScore.LimitedNote(_results) : null, HealthScore.PartialNote(_breakdown) }.OfType<string>());
        RefreshFindingLists();
        RefreshHomeInsights();
        RefreshReadiness();
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
        OnPropertyChanged(nameof(NextStepTitle));
        OnPropertyChanged(nameof(NextStepText));
        OnPropertyChanged(nameof(NextStepTone));
        OnPropertyChanged(nameof(HasProposedChanges));
        OnPropertyChanged(nameof(FixNowLabel));
        OnPropertyChanged(nameof(ShowSeeFindingsButton));
        OnPropertyChanged(nameof(ShowRerunButton));
        OnPropertyChanged(nameof(ShowLastAudit));
        OnPropertyChanged(nameof(IsPastScore));
        OnPropertyChanged(nameof(Symptoms));
        NotifyJourney();
    }
}

/// <summary>Une étape du parcours de l'accueil : faite, en cours ou à venir.</summary>
public sealed record JourneyStepRow(int Number, string Label, int CurrentStep)
{
    public override string ToString() => AccessibleName;

    public bool IsDone => Number < CurrentStep;

    public bool IsCurrent => Number == CurrentStep;

    public string Icon => IsDone ? "\uE73E" : IsCurrent ? "\uE768" : "\uE9CE";

    public Tone Tone => IsDone ? Tone.Mint : IsCurrent ? Tone.Blue : Tone.Grey;

    public string Text => Number.ToString(Culture) + ". " + Label;

    public string AccessibleName => Text + " : " + (IsDone ? T("fait") : IsCurrent ? T("en cours") : T("à venir"));
}
