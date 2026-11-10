using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Maus.App.Views;
using Maus.Core;
using Maus.Core.Engine;
using Maus.Core.Engine.Broker;
using Maus.Core.Fixes;
using Maus.Core.Preferences;
using Maus.Core.Reporting;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Ce que MAUS garde sur ce PC après chaque audit : score, mesures, constats. Ailleurs pendant les tests.</summary>
internal sealed record AuditHistories(Maus.Core.Workshop.BenchmarkHistory Health, Maus.Core.Workshop.BenchmarkHistory Measures, AuditSnapshotStore Snapshots, MissedAlertNote? MissedAlert = null, Maus.Core.Follow.HealthLogStore? HealthLog = null)
{
    public static AuditHistories Default => new(Maus.Core.Workshop.BenchmarkHistory.CreateHealth(), Maus.Core.Workshop.BenchmarkHistory.CreateMeasures(), AuditSnapshotStore.CreateDefault(), MissedAlertNote.CreateDefault(), Maus.Core.Follow.HealthLogStore.CreateDefault());
}

/// <summary>Fenêtre principale : audit (ce fichier), corrections, historique et choix de l'utilisateur (fichiers partiels).</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly AuditHistories _histories;
    /// <summary>Liste des modules (titres, ordre) : l'audit lui-même est fait par le moteur.</summary>
    private readonly AuditEngine _engine = AuditEngine.CreateWithBuiltInModules();
    private List<ModuleResult> _results = [];
    private ModuleViewModel? _selectedModule;
    private bool _isRunning;
    private string _statusText = T("Prêt. Lancez l'audit : MAUS lit votre configuration sans rien modifier.");
    private string _systemSummary = string.Empty;
    private int _completed;
    /// <summary>Ce que le moteur a dit du PC au dernier audit ; <c>null</c> avant le premier.</summary>
    private EngineSnapshot? _lastContext;
    private bool _laptopQuestionAsked;

    public MainViewModel()
        : this(AuditHistories.Default)
    {
        NewPartStore = Maus.Core.Follow.PartsCardStore.CreateDefault();
    }

    internal MainViewModel(AuditHistories histories)
    {
        _histories = histories;
        Modules = new ObservableCollection<ModuleViewModel>(_engine.Modules.Select(m => new ModuleViewModel(m, this)));
        SelectedModule = Modules.FirstOrDefault();
        // Pas d'audit lancé par le bouton pendant les corrections : elles relancent elles-mêmes l'audit de vérification.
        EngineHost.ElevationChanged += OnElevationChanged;
        RunAuditCommand = new AsyncCommand(() => IsApplying ? Task.CompletedTask : RunAuditAsync());
        RerunAuditForLanguageCommand = new AsyncCommand(() => IsApplying ? Task.CompletedTask : RunAuditAsync(forLanguage: true));
        ApplyCommand = new AsyncCommand(ApplyAsync);
        RefreshJournalCommand = new AsyncCommand(() => IsWriting ? Task.CompletedTask : RefreshJournalAsync());
        SaveReportCommand = new AsyncCommand(SaveReportAsync);
        SaveCheckReportCommand = new AsyncCommand(SaveCheckReportAsync);
        RestartExplorerCommand = new AsyncCommand(RestartExplorerAsync);
        Profiles = FixProfile.All.Select(p => new ProfileViewModel(p, () => ApplyProfile(p))).ToList();
        _gameBarChoice = GameBarOptions[0];
        _laptopChoice = LaptopOptions[0];
        LoadHealthTrend();
        LoadSnapshots();
        LoadMissedAlert();
        MoveScheduleCommand = new AsyncCommand(MoveScheduleAsync);
        HookAppearance();
        ForgetRestartIfDone();

        // Mode limité ou fichier des choix illisible au démarrage : l'utilisateur le sait dès l'ouverture.
        if (PreferencesStore.Notice is { } notice)
        {
            _statusText = notice;
        }
    }

    /// <summary>Demande de confirmation (titre, message) ; remplaçable pour les tests.</summary>
    public Func<string, string, bool> Confirm { get; init; } = (title, message) =>
        Views.QuestionWindow.Ask(title, message, Views.QuestionTone.Question);

    /// <summary>Préférences enregistrées (profil Game Bar, alimentation, constats « voulus ») ; remplaçable pour les tests.</summary>
    public IPreferencesStore PreferencesStore { get; init; } = EngineHost.Preferences;

    /// <summary>Le moteur (audit, corrections, journal) ; remplaçable pour les tests.</summary>
    public IMausEngine Engine { get; init; } = EngineHost.Current;

    /// <summary>Contrôle technique du PC : le dernier audit et les résultats du bilan complet, sur une page sans donnée personnelle.</summary>
    private async Task SaveCheckReportAsync()
    {
        if (_lastContext is not { } context || Workshop.FullCheckLines.Count == 0)
        {
            StatusText = T("Lancez d'abord un audit puis « Tout tester » : le contrôle technique réunit leurs résultats.");
            Workshop.ShowCheckHint(StatusText);
            return;
        }

        if (PickReportPath(T("MAUS-controle-technique") + $"-{DateTime.Now:yyyy-MM-dd-HHmm}.html") is not { } path)
        {
            return;
        }

        try
        {
            var html = HtmlReport.Build(new HtmlReportInput { After = CurrentReport(context), TestLines = [.. Workshop.FullCheckLines], Privacy = PrivacyFilter.ForCurrentUser() });
            await File.WriteAllTextAsync(path, html, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Workshop.ShowCheckHint(T("Rapport enregistré : {0} (ouvrez-le avec votre navigateur).", path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Workshop.ShowCheckHint(T("Le rapport n'a pas pu être enregistré : {0}", Maus.Core.Diagnostics.ErrorText.For(ex)));
        }
    }

    /// <summary>Choix de l'emplacement du rapport HTML (nom proposé) ; <c>null</c> si l'utilisateur renonce.</summary>
    public Func<string, string?> PickReportPath { get; init; } = suggested =>
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = suggested,
            DefaultExt = ".html",
            Filter = T("Page web") + " (*.html)|*.html|" + T("Données JSON") + " (*.json)|*.json|" + T("Tableau CSV") + " (*.csv)|*.csv",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    };

    /// <summary>Question du premier lancement sur un portable ; <c>null</c> = « plus tard ».</summary>
    public Func<LaptopPowerChoice?> AskLaptopChoice { get; init; } = () =>
    {
        var window = new LaptopChoiceWindow { Owner = Application.Current?.MainWindow };
        return window.ShowDialog() == true ? window.Choice : null;
    };

    public ObservableCollection<ModuleViewModel> Modules { get; }

    public ICommand RunAuditCommand { get; }

    /// <summary>Le même audit, relancé pour retraduire la fenêtre après un changement de langue.</summary>
    public ICommand RerunAuditForLanguageCommand { get; }

    public ICommand SaveReportCommand { get; }

    public ICommand SaveCheckReportCommand { get; }

    public int ModuleCount => Modules.Count;

    public bool HasAudit => _lastContext is not null;

    /// <summary>Pas encore d'audit : le bouton « Lancer l'audit » est le bouton principal de l'accueil.</summary>
    public bool IsFirstAudit => !HasAudit;

    /// <summary>
    /// Le dernier audit s'est fait sans droits administrateur (moteur arrêté) : son score est partiel, il n'est pas gardé, et
    /// les corrections ne se préparent pas d'après lui. Le moteur peut avoir démarré depuis : c'est l'audit qui compte.
    /// </summary>
    public bool IsLimitedAudit => _lastContext is { IsElevated: false };

    public ModuleViewModel? SelectedModule
    {
        get => _selectedModule;
        set
        {
            if (SetProperty(ref _selectedModule, value))
            {
                OnPropertyChanged(nameof(SelectedModuleFindings));
                OnPropertyChanged(nameof(HiddenConformingNote));
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            SetProperty(ref _isRunning, value);
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(ScoreWord));
            OnPropertyChanged(nameof(ScoreDetail));
        }
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

    public string About { get; } = T("Conçu et codé avec Claude, une IA d'Anthropic, sous la direction de son auteur · Logiciel libre sous licence GPL-3.0, fourni sans garantie · version {0}",
        AppVersion.Display);

    /// <summary>
    /// Lance l'audit. Renvoie vrai quand de nouveaux résultats remplacent les précédents : sinon (audit en échec), les
    /// constats affichés sont ceux d'avant, et rien ne doit être vérifié ni conclu sur leur base.
    /// </summary>
    /// <param name="forLanguage">Relancé par un changement de langue : le PC n'a rien de neuf à dire, le carnet de santé n'a pas de ligne à ajouter.</param>
    internal async Task<bool> RunAuditAsync(bool forLanguage = false)
    {
        var fresh = false;
        if (IsRunning)
        {
            // Un audit tourne déjà : deux audits en parallèle écraseraient les résultats l'un de l'autre.
            return false;
        }

        IsRunning = true;
        Completed = 0;
        _findingsSeen = false;
        StatusText = T("Lecture de la configuration…");
        StartAuditClock();
        foreach (var module in Modules)
        {
            module.AuditState = AuditState.Running;
        }

        try
        {
            var byId = Modules.ToDictionary(m => m.Id);
            var progress = new Progress<ModuleResult>(result =>
            {
                if (byId.TryGetValue(result.ModuleId, out var module))
                {
                    module.SetResult(result);
                    module.AuditState = AuditState.Done;
                }

                Completed++;
                StatusText = T("Audit en cours… {0}/{1}", Completed, ModuleCount);
            });

            var run = await Engine.RunAuditAsync(progress, CancellationToken.None);
            var context = run.Snapshot;
            SystemSummary = $"{context.Windows.ProductName} {context.Windows.DisplayVersion} (build {context.Windows.FullBuild}) · " +
                            $"{Labels.Of(context.Hardware.FormFactor)} · {context.Hardware.Cpu.DisplayName}";
            _results = [.. run.Results];
            var findings = _results.SelectMany(r => r.Findings).ToList();
            // Les comptes sont déjà sous le score et dans « À traiter d'abord » : ici, seulement l'heure de l'audit.
            StatusText = context.IsElevated ? T("Audit terminé à {0:t}.", DateTime.Now) : T("Audit sans droits administrateur terminé à {0:t}.", DateTime.Now);
            OnPropertyChanged(nameof(SelectedModule));

            _lastContext = context;
            fresh = true;

            // Sans droits : tout ce qui suit et passe par le moteur demanderait une autorisation que personne n'a voulue.
            if (!IsLimitedAudit && CanReadJournalSilently())
            {
                await RebindAcknowledgementsAsync(findings);
            }

            OnPropertyChanged(nameof(HasAudit));
            OnPropertyChanged(nameof(IsFirstAudit));
            OnPropertyChanged(nameof(IsLimitedAudit));
            context = await WithCurrentChoicesAsync(_lastContext);
            LoadChoices(context);
            await RefreshNewPartAsync(null, afterAudit: true);

            // « Refermer les droits » après la fin de l'audit : la suite passerait par le moteur, et ferait revenir l'invite de
            // Windows juste après la fermeture demandée. Cet audit finit donc comme un audit sans droits ; un clic le refera.
            var rightsClosed = !IsLimitedAudit && !CanReadJournalSilently();
            if (rightsClosed)
            {
                context = context with { IsElevated = false };
                _lastContext = context;
                OnPropertyChanged(nameof(IsLimitedAudit));
                StatusText = T("Audit terminé à {0:t}, droits refermés juste après : il n'est pas gardé et ne prépare aucune correction.", DateTime.Now);
            }

            if (IsLimitedAudit)
            {
                BlockingReason = context.Hardware is { IsManaged: true } or { ManagementUnknown: true } ? context.BlockingReason : T("Les corrections demandent les droits administrateur : cliquez sur « Toutes les fonctionnalités », en haut de la fenêtre, puis relancez l'audit.");
                ForgetPlan();
                ForgetComparison();
                RefreshDashboard();
                await RecordHealthLogAsync(run.Snapshot.IsElevated, forLanguage);
                return fresh;
            }

            BlockingReason = context.BlockingReason
                ?? (context.ElevatedAsAnotherUser
                    ? T("MAUS a été lancé avec un autre compte administrateur : les réglages de votre profil (effets visuels, confidentialité, Game Bar) seront ignorés. Relancez MAUS depuis votre propre session.")
                    : null);
            await ReplanAsync();
            RefreshDashboard();
            await RecordHealthAsync();
            await RecordSnapshotAsync(context.Windows.FullBuild, context.ElevatedAsAnotherUser);
            await RecordHealthLogAsync(context.IsElevated, forLanguage);
            await RefreshJournalUnlessEngineStoppedAsync();

            // Sa réponse s'enregistre par le moteur : droits refermés entre-temps, la question attend le prochain audit.
            if (CanReadJournalSilently())
            {
                await AskLaptopChoiceOnceAsync(context);
            }
        }
        catch (Maus.Core.Engine.Broker.EngineDeclinedException declined)
        {
            // Autorisation refusée : l'audit n'a pas lieu, le reste de MAUS reste utilisable.
            StatusText = declined.Message;
            ShowLastResults();
        }
        catch (Exception ex)
        {
            Maus.Core.Diagnostics.CrashLog.Write(ex, "audit");
            StatusText = T("L'audit a échoué : {0}", Maus.Core.Diagnostics.ErrorText.For(ex));
            ShowLastResults();
        }
        finally
        {
            IsRunning = false;
            StopAuditClock();
        }

        return fresh;
    }

    /// <summary>
    /// Audit en échec : chaque module a pu recevoir son nouveau résultat avant la panne. Tous reviennent sur le dernier
    /// audit complet, pour que l'affichage par module dise la même chose que le reste de la page.
    /// </summary>
    private void ShowLastResults()
    {
        var last = _results.ToDictionary(r => r.ModuleId);
        foreach (var module in Modules)
        {
            module.SetResult(last.GetValueOrDefault(module.Id));
            module.AuditState = last.ContainsKey(module.Id) ? AuditState.Done : AuditState.Pending;
        }

        OnPropertyChanged(nameof(SelectedModuleFindings));
    }

    private System.Windows.Threading.DispatcherTimer? _auditClock;
    private DateTime _auditStarted;

    /// <summary>« 12 s » pendant l'audit : l'attente paraît plus courte quand elle se voit avancer.</summary>
    public string AuditElapsedText { get; private set; } = string.Empty;

    private void StartAuditClock()
    {
        _auditStarted = DateTime.UtcNow;
        AuditElapsedText = string.Empty;
        OnPropertyChanged(nameof(AuditElapsedText));
        if (Application.Current is null)
        {
            return;
        }

        _auditClock ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _auditClock.Tick -= OnAuditClockTick;
        _auditClock.Tick += OnAuditClockTick;
        _auditClock.Start();
    }

    private void OnAuditClockTick(object? sender, EventArgs e)
    {
        AuditElapsedText = T("{0} s écoulées", (int)(DateTime.UtcNow - _auditStarted).TotalSeconds);
        OnPropertyChanged(nameof(AuditElapsedText));
    }

    private void StopAuditClock()
    {
        _auditClock?.Stop();
        AuditElapsedText = string.Empty;
        OnPropertyChanged(nameof(AuditElapsedText));
    }

    /// <summary>Les choix restent modifiables pendant un audit : ceux enregistrés entre-temps priment sur ceux lus à son début.</summary>
    private async Task<EngineSnapshot> WithCurrentChoicesAsync(EngineSnapshot context)
    {
        try
        {
            var current = await Task.Run(() => PreferencesStore.Load());
            var refreshed = context with { Preferences = current };
            _lastContext = refreshed;
            return refreshed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Maus.Core.Fixes.JournalUnsafeException or Maus.Core.Engine.Broker.EngineException)
        {
            return context;
        }
    }

    /// <summary>Relance un seul module (après un choix de l'utilisateur) et met à jour l'affichage et les corrections.</summary>
    private async Task RefreshModulesAsync(params string[] moduleIds)
    {
        // Audit sans droits : relancer un module passerait par le moteur, et mêlerait ses résultats à ceux de cet audit.
        if (_lastContext is null || IsLimitedAudit)
        {
            return;
        }

        foreach (var id in moduleIds)
        {
            if (await Engine.RunModuleAsync(id, CancellationToken.None) is not { } result)
            {
                continue;
            }

            _results = [.. _results.Where(r => r.ModuleId != id), result];
            Modules.FirstOrDefault(m => m.Id == id)?.SetResult(result);
        }

        OnPropertyChanged(nameof(SelectedModule));
        await ReplanAsync();
        RefreshDashboard();
    }

    private async Task SaveReportAsync()
    {
        if (_lastContext is null)
        {
            return;
        }

        var path = PickReportPath(T("MAUS-rapport") + $"-{DateTime.Now:yyyy-MM-dd-HHmm}.html");
        if (path is null)
        {
            return;
        }

        try
        {
            var after = CurrentReport(_lastContext);
            var extension = Path.GetExtension(path);
            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase) || extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
            {
                var privacy = PrivacyFilter.ForCurrentUser();
                var isCsv = extension.Equals(".csv", StringComparison.OrdinalIgnoreCase);

                // CSV avec marque UTF-8 : sans elle, Excel affiche mal les accents.
                await File.WriteAllTextAsync(path, isCsv ? FindingsCsv.Build(after, privacy) : after.ToJson(privacy), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: isCsv));
                StatusText = T("Constats enregistrés : {0}", path);
                return;
            }

            var input = _lastApplied is { } applied
                ? new HtmlReportInput
                {
                    Before = applied.Before,
                    After = after,
                    // Sans droits, relire le journal demanderait une autorisation pour un simple complément : la séance gardée suffit.
                    Session = applied.Session is { } session && EngineHost.IsElevated ? await Engine.LoadSessionAsync(session.Id, CancellationToken.None) ?? session : applied.Session,
                    Outcomes = applied.Verified,
                }
                : new HtmlReportInput { After = after };
            var html = HtmlReport.Build(input with { Privacy = PrivacyFilter.ForCurrentUser() });
            await File.WriteAllTextAsync(path, html, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            StatusText = T("Rapport enregistré : {0} (ouvrez-le avec votre navigateur).", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Maus.Core.Engine.Broker.EngineException)
        {
            StatusText = T("Le rapport n'a pas pu être enregistré : {0}", Maus.Core.Diagnostics.ErrorText.For(ex));
        }
    }

    /// <summary>Dernier audit, modules dans l'ordre de l'interface.</summary>
    private AuditReport CurrentReport(EngineSnapshot context) =>
        AuditReport.Create(context, _results.OrderBy(r => Modules.IndexOf(Modules.First(m => m.Id == r.ModuleId))).ToList());
}
