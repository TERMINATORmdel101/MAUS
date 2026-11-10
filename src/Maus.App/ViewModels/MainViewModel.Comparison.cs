using System.Collections.ObjectModel;
using Maus.App.Appearance;
using Maus.Core;
using Maus.Core.Diagnostics;
using Maus.Core.Platform;
using Maus.Core.Reporting;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Une ligne de la comparaison entre deux audits.</summary>
public sealed record AuditChangeRow(string Icon, Tone Tone, string Title, string Detail)
{
    public override string ToString() => Title;
}

/// <summary>Une ligne de la carte « Dernier audit » : l'état en mot et en couleur, et le titre du constat tel qu'il a été gardé.</summary>
public sealed record LastAuditRow(Tone Tone, string State, string Title)
{
    public override string ToString() => Title;
}

/// <summary>Une ligne de la carte « Avant une mise à jour ».</summary>
public sealed record ReadinessRow(string Icon, Tone Tone, string Text, string? ActionLabel = null, int Target = 0)
{
    public override string ToString() => Text;

    public bool HasAction => ActionLabel is not null;
}

/// <summary>
/// Comparaison entre deux audits et « ce que la mise à jour a changé » (0.8.6) : les constats de chaque audit sont gardés dans
/// le profil de l'utilisateur, seuls les changements d'état sont montrés. Carte « Avant une mise à jour » et choix des modules.
/// </summary>
public sealed partial class MainViewModel
{
    private List<AuditSnapshot> _snapshots = [];
    private AuditSnapshot? _currentSnapshot;
    private ChoiceOption<AuditSnapshot>? _comparisonChoice;
    private bool _isComparisonOpen;
    private bool _loadingComparison;

    public ObservableCollection<ChoiceOption<AuditSnapshot>> ComparisonOptions { get; } = [];

    public ObservableCollection<AuditChangeRow> AuditChanges { get; } = [];

    public bool HasComparison => _currentSnapshot is not null && ComparisonOptions.Count > 0;

    public string ComparisonTitle { get; private set; } = string.Empty;

    public bool IsComparisonOpen
    {
        get => _isComparisonOpen;
        set => SetProperty(ref _isComparisonOpen, value);
    }

    public ChoiceOption<AuditSnapshot>? ComparisonChoice
    {
        get => _comparisonChoice;
        set
        {
            if (SetProperty(ref _comparisonChoice, value) && !_loadingComparison)
            {
                RefreshComparison();
            }
        }
    }

    /// <summary>Windows a changé de version depuis le dernier audit : l'accueil propose d'en relancer un.</summary>
    public string BuildNotice { get; private set; } = string.Empty;

    public bool HasBuildNotice => BuildNotice.Length > 0;

    private void LoadSnapshots()
    {
        _snapshots = [.. _histories.Snapshots.Load()];
        ShowLastAuditCard();
        RefreshWhatChangedChoices();

        // L'audit de la semaine ne se compare pas : la mise à jour se mesure depuis le dernier audit lancé à la main.
        if (_snapshots.LastOrDefault(s => !s.Weekly) is not { } last)
        {
            return;
        }

        try
        {
            var build = WindowsInfo.Read(new WindowsRegistryReader()).FullBuild;
            if (!string.Equals(build, last.Build, StringComparison.Ordinal))
            {
                BuildNotice = T("Windows a été mis à jour depuis votre dernier audit (build {0} → {1}). Relancez l'audit : MAUS montrera ce que la mise à jour a changé dans vos réglages.", last.Build, build);
            }
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or System.IO.IOException or UnauthorizedAccessException or DataSourceUnavailableException)
        {
            Maus.Core.Diagnostics.Breadcrumbs.Add("version de Windows illisible à l'ouverture");
        }
    }

    /// <summary>Après un audit : garde ses constats et ses mesures, puis montre ce qui a changé depuis le précédent.</summary>
    private async Task RecordSnapshotAsync(string build, bool otherAccount)
    {
        var now = DateTimeOffset.Now;
        var snapshot = AuditSnapshot.From(_results, build, now) with { OtherAccount = otherAccount };
        var results = _results;
        var kept = await Task.Run(() =>
        {
            try
            {
                var history = _histories.Measures;
                foreach (var entry in MeasureHistory.FromAudit(results, history.Load(), now))
                {
                    history.Add(entry);
                }

                return _histories.Snapshots.Add(snapshot);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                // Rien de grave : la comparaison se fera sur ce qui est déjà gardé, sans gêner l'audit.
                return null;
            }
        });

        _snapshots = kept is null ? [.. _snapshots, snapshot] : [.. kept];
        _currentSnapshot = snapshot;
        RefreshWhatChangedChoices();
        BuildNotice = string.Empty;
        OnPropertyChanged(nameof(BuildNotice));
        OnPropertyChanged(nameof(HasBuildNotice));
        MissedAlert = string.Empty;
        OnPropertyChanged(nameof(MissedAlert));
        OnPropertyChanged(nameof(HasMissedAlert));

        _loadingComparison = true;
        ComparisonOptions.Clear();
        foreach (var older in _snapshots.Where(s => !s.Weekly && s.At < snapshot.At).OrderByDescending(s => s.At))
        {
            ComparisonOptions.Add(new(older.IsPartial ? T("{0:g} · score {1} (audit partiel)", older.At.LocalDateTime, older.Score) : T("{0:g} · score {1}", older.At.LocalDateTime, older.Score), older));
        }

        ComparisonChoice = ComparisonOptions.FirstOrDefault();
        _loadingComparison = false;
        RefreshComparison();

        // Windows a changé depuis l'audit précédent : la comparaison est ouverte d'office, c'est ce que l'utilisateur vient voir.
        IsComparisonOpen = ComparisonChoice?.Value.Build is { } previous && !string.Equals(previous, build, StringComparison.Ordinal) && AuditChanges.Count > 0;
    }

    /// <summary>Audit sans droits : il n'est pas gardé et ne se compare à rien ; la comparaison précédente ne vaut plus pour ce qui est affiché.</summary>
    private void ForgetComparison()
    {
        _currentSnapshot = null;
        BuildNotice = string.Empty;
        OnPropertyChanged(nameof(BuildNotice));
        OnPropertyChanged(nameof(HasBuildNotice));
        _loadingComparison = true;
        ComparisonOptions.Clear();
        ComparisonChoice = null;
        _loadingComparison = false;
        IsComparisonOpen = false;
        AuditChanges.Clear();
        RefreshComparison();
        RefreshWhatChanged();
    }

    private void RefreshComparison()
    {
        AuditChanges.Clear();
        if (_currentSnapshot is { } current && ComparisonChoice?.Value is { } older)
        {
            foreach (var change in AuditComparison.Compare(older, current))
            {
                var bad = change.Kind is AuditChangeKind.Worse or AuditChangeKind.New;
                AuditChanges.Add(change.Kind == AuditChangeKind.NotComparable
                    ? new AuditChangeRow("\uE9CE", Tone.Grey, T(change.Title), Describe(change))
                    : new AuditChangeRow(bad ? "\uE74B" : "\uE74A", bad ? Tone.Rose : Tone.Mint, T(change.Title), Describe(change)));
            }

            var worse = AuditChanges.Count(c => c.Tone == Tone.Rose);
            var better = AuditChanges.Count(c => c.Tone == Tone.Mint);
            var when = older.At.LocalDateTime;
            ComparisonTitle = !string.Equals(older.Build, current.Build, StringComparison.Ordinal)
                ? T("Ce que la mise à jour de Windows a changé (build {0} → {1}) : {2}, {3}", older.Build, current.Build, Plural(worse, T("{0} aggravé"), T("{0} aggravés")), Plural(better, T("{0} amélioré"), T("{0} améliorés")))
                : AuditChanges.Count == 0
                    ? T("Aucun changement d'état depuis l'audit du {0:g}", when)
                    : T("Depuis l'audit du {0:g} : {1}, {2}", when, Plural(worse, T("{0} aggravé"), T("{0} aggravés")), Plural(better, T("{0} amélioré"), T("{0} améliorés")));
        }

        foreach (var name in new[] { nameof(HasComparison), nameof(ComparisonTitle) })
        {
            OnPropertyChanged(name);
        }
    }

    private static string Describe(AuditChange change) => change.Kind switch
    {
        AuditChangeKind.New => T("Nouveau : {0} · {1}", Labels.Of(change.After!.Value), change.ModuleId),
        AuditChangeKind.Gone => T("Plus signalé (était : {0}) · {1}", Labels.Of(change.Before!.Value), change.ModuleId),
        AuditChangeKind.NotComparable when change.After is { } after => T("Seulement dans cet audit ({0}) : c'est peut-être un contrôle ajouté par une nouvelle version de MAUS, pas un changement du PC · {1}", Labels.Of(after), change.ModuleId),
        AuditChangeKind.NotComparable => T("Plus dans cet audit (était : {0}) : le contrôle a peut-être changé avec MAUS, pas forcément votre PC · {1}", Labels.Of(change.Before!.Value), change.ModuleId),
        _ => T("{0} → {1} · {2}", Labels.Of(change.Before!.Value), Labels.Of(change.After!.Value), change.ModuleId),
    };

    // ----- Dernier audit, retrouvé à l'ouverture -----

    private const int LastAuditRowsShown = 6;

    private AuditSnapshot? _lastAudit;
    private int _pastScore = AuditSnapshot.NoScore;

    /// <summary>Aucun audit depuis l'ouverture, mais un audit gardé : sa carte s'affiche, datée, jamais comme l'état actuel.</summary>
    public bool ShowLastAudit => !HasAudit && _lastAudit is not null;

    /// <summary>L'anneau montre le score d'un audit passé : en gris, et la date est écrite dessous.</summary>
    public bool IsPastScore => !HasScore && _pastScore >= 0;

    public string LastAuditTitle { get; private set; } = string.Empty;

    public string LastAuditScore { get; private set; } = string.Empty;

    public string LastAuditCounts { get; private set; } = string.Empty;

    /// <summary>Titres des constats rouges puis orange ; les explications ne sont pas gardées, le détail demande un nouvel audit.</summary>
    public IReadOnlyList<LastAuditRow> LastAuditRows { get; private set; } = [];

    public string LastAuditMore { get; private set; } = string.Empty;

    public bool HasLastAuditMore => LastAuditMore.Length > 0;

    /// <summary>Remplit la carte depuis le résumé gardé : aucune lecture de Windows, aucun moteur, aucune autorisation.</summary>
    private void ShowLastAuditCard()
    {
        if (_snapshots.LastOrDefault() is not { } last)
        {
            return;
        }

        _lastAudit = last;
        var at = last.At.LocalDateTime;
        LastAuditTitle = last.Weekly ? T("Dernier audit : audit de la semaine du {0:g}", at) : T("Dernier audit : le {0:g}", at);
        LastAuditScore = !last.HasScore ? T("Pas de score : cet audit s'est fait sans droits administrateur, certains contrôles n'ont pas été lus.")
            : last.IsPartial ? T("Score : {0} sur 100 (audit partiel).", last.Score)
            : T("Score : {0} sur 100.", last.Score);

        // Même sujet vu par deux modules : une seule ligne, comme dans l'onglet Constats. Un constat « voulu » n'est plus signalé.
        var kept = last.Findings
            .Where(f => !f.Acknowledged)
            .OrderByDescending(f => f.Status.Rank())
            .DistinctBy(f => HealthScore.SubjectOf(f.Id) ?? f.Id, StringComparer.Ordinal)
            .ToList();
        LastAuditCounts = CountsText([.. kept.Select(f => f.Status)]);
        if (kept.Count(f => f.Status == FindingStatus.Unknown) is > 0 and var unread)
        {
            LastAuditCounts += " · " + Plural(unread, T("{0} indéterminé"), T("{0} indéterminés")).Replace(' ', '\u00A0');
        }

        var important = kept.Where(f => f.Status is FindingStatus.Problem or FindingStatus.Warning).ToList();
        LastAuditRows = [.. important.Take(LastAuditRowsShown).Select(f => new LastAuditRow(f.Status == FindingStatus.Problem ? Tone.Rose : Tone.Sand, Labels.Of(f.Status), T(f.Title)))];
        LastAuditMore = important.Count > LastAuditRowsShown ? Plural(important.Count - LastAuditRowsShown, T("… et {0} autre."), T("… et {0} autres.")) : string.Empty;

        // L'anneau garde le dernier score connu, avec sa date : l'audit de la semaine sans droits n'en a pas.
        if (_snapshots.LastOrDefault(s => s.HasScore) is not { } scored)
        {
            ScoreDetail = T("Pas encore de score : relancez l'audit pour voir l'état actuel. L'audit ne modifie rien.");
            return;
        }

        _pastScore = Math.Clamp(scored.Score, 0, 100);
        ScoreDetail = scored.IsPartial
            ? T("Score partiel de l'audit du {0:g}. Ce n'est pas l'état actuel : relancez l'audit pour le voir.", scored.At.LocalDateTime)
            : T("Score de l'audit du {0:g}. Ce n'est pas l'état actuel : relancez l'audit pour le voir.", scored.At.LocalDateTime);
    }

    // ----- Avant une mise à jour -----

    public ObservableCollection<ReadinessRow> ReadinessRows { get; } = [];

    public bool ShowReadiness => ReadinessRows.Count > 0;

    private void RefreshReadiness()
    {
        ReadinessRows.Clear();
        var findings = _results.SelectMany(r => r.Findings).ToList();
        if (UpdateReadiness.IsDue(findings))
        {
            ReadinessRows.Add(Row(findings, "M19.restore-points", value => T("Point de restauration : {0}", value), T("Historique"), TabHistory));
            ReadinessRows.Add(Row(findings, "M19.personal-files", value => T("Sauvegarde : {0}", value)));
            ReadinessRows.Add(new("\uE823", Tone.Grey, T("Prévoyez 30 minutes, PC branché, travail enregistré.")));
            ReadinessRows.Add(new("\uE896", Tone.Grey, T("Gardez le pilote du fabricant à portée de main, au cas où l'affichage en aurait besoin après la mise à jour."),
                T("Page des pilotes"), 30 + Maus.App.ViewModels.Workshop.WorkshopViewModel.SectionDrivers));
        }

        OnPropertyChanged(nameof(ShowReadiness));
    }

    private static ReadinessRow Row(List<Finding> findings, string id, Func<string, string> text, string? action = null, int target = 0)
    {
        var finding = findings.FirstOrDefault(f => f.Id == id);
        var fine = finding?.Status is FindingStatus.Ok or FindingStatus.Info;
        return new(fine ? "\uE73E" : "\uE7BA", fine ? Tone.Mint : finding is null ? Tone.Grey : Tone.Sand,
            text(finding?.Current ?? Labels.Of(FindingStatus.Unknown)), action, target);
    }

    // ----- Choix des modules -----

    public string ModulesChoiceLabel => T("Modules de l'audit ({0} sur {1})", Modules.Count(m => m.IsAudited), Modules.Count);

    /// <summary>Affiche le choix enregistré, sans déclencher d'écriture.</summary>
    private void ShowSkippedModules(IReadOnlyList<string> skipped)
    {
        foreach (var module in Modules)
        {
            module.ShowAudited(!skipped.Contains(module.Id, StringComparer.Ordinal));
        }

        OnPropertyChanged(nameof(ModulesChoiceLabel));
    }

    void IFindingActions.SetModuleAudited(string moduleId, bool audited) =>
        SetModuleAuditedAsync(moduleId, audited).Forget("choix des modules", ReportChoiceError);

    private async Task SetModuleAuditedAsync(string moduleId, bool audited)
    {
        var saved = false;
        try
        {
            saved = await UpdatePreferencesAsync(p => p with
            {
                SkippedModules = audited
                    ? [.. p.SkippedModules.Where(id => !string.Equals(id, moduleId, StringComparison.Ordinal))]
                    : [.. p.SkippedModules.Where(id => !string.Equals(id, moduleId, StringComparison.Ordinal)), moduleId],
            });
        }
        finally
        {
            // Relu : si le moteur a refusé (confirmation déclinée) ou si l'écriture a échoué, la case revient à son état.
            var kept = await Task.Run(() => PreferencesStore.Load().SkippedModules);
            ShowSkippedModules(kept);
            if (saved)
            {
                StatusText = kept.Contains(moduleId, StringComparer.Ordinal) != audited
                    ? T("Choix des modules enregistré : il compte à partir du prochain audit.")
                    : T("Choix des modules annulé : rien n'a changé.");
            }
        }
    }

    /// <summary>« Mon PC est lent » : ouvre les constats du module concerné par une cause.</summary>
    private void ShowModuleFindings(string moduleId)
    {
        ShowByModule = true;
        SelectedModule = Modules.FirstOrDefault(m => m.Id == moduleId) ?? SelectedModule;
        SelectedTab = TabFindings;
    }

    // ----- Ligne du temps des corrections -----

    private int _applyStep;

    public bool ShowApplySteps => _applyStep > 0;

    public IReadOnlyList<JourneyStepRow> ApplySteps =>
    [
        new(1, T("Audit"), _applyStep),
        new(2, CreateRestorePoint ? T("Point de restauration et corrections") : T("Corrections"), _applyStep),
        new(3, T("Vérification par un nouvel audit"), _applyStep),
        new(4, T("Terminé"), _applyStep),
    ];

    private void SetApplyStep(int step)
    {
        _applyStep = step;
        OnPropertyChanged(nameof(ShowApplySteps));
        OnPropertyChanged(nameof(ApplySteps));
    }
}
