using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Maus.App.Views;
using Maus.Core;
using Maus.Core.Engine;
using Maus.Core.Fixes;
using Maus.Core.Preferences;
using Maus.Core.Reporting;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Fenêtre principale : audit (ce fichier), corrections, historique et choix de l'utilisateur (fichiers partiels).</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly AuditEngine _engine = AuditEngine.CreateWithBuiltInModules();
    private List<ModuleResult> _results = [];
    private ModuleViewModel? _selectedModule;
    private bool _isRunning;
    private string _statusText = T("Prêt. Lancez l'audit : MAUS lit votre configuration sans rien modifier.");
    private string _systemSummary = string.Empty;
    private int _completed;
    private AuditContext? _lastContext;
    private bool _laptopQuestionAsked;

    public MainViewModel()
    {
        Modules = new ObservableCollection<ModuleViewModel>(_engine.Modules.Select(m => new ModuleViewModel(m, OnAcknowledgeAsync)));
        SelectedModule = Modules.FirstOrDefault();
        RunAuditCommand = new AsyncCommand(RunAuditAsync);
        ApplyCommand = new AsyncCommand(ApplyAsync);
        RefreshJournalCommand = new AsyncCommand(RefreshJournalAsync);
        SaveReportCommand = new AsyncCommand(SaveReportAsync);
        RestartExplorerCommand = new AsyncCommand(RestartExplorerAsync);
        Profiles = FixProfile.All.Select(p => new ProfileViewModel(p, () => ApplyProfile(p))).ToList();
        _gameBarChoice = GameBarOptions[0];
        _laptopChoice = LaptopOptions[0];
    }

    /// <summary>Demande de confirmation (titre, message) ; remplaçable pour les tests.</summary>
    public Func<string, string, bool> Confirm { get; init; } = (title, message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>Préférences enregistrées (profil Game Bar, alimentation, constats « voulus ») ; remplaçable pour les tests.</summary>
    public IPreferencesStore PreferencesStore { get; init; } = FilePreferencesStore.CreateDefault();

    /// <summary>Choix de l'emplacement du rapport HTML (nom proposé) ; <c>null</c> si l'utilisateur renonce.</summary>
    public Func<string, string?> PickReportPath { get; init; } = suggested =>
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = suggested,
            DefaultExt = ".html",
            Filter = T("Page web") + " (*.html)|*.html",
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

    public ICommand SaveReportCommand { get; }

    public int ModuleCount => Modules.Count;

    public bool HasAudit => _lastContext is not null;

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

    public string About { get; } = T("Conçu et codé avec Claude, une IA d'Anthropic, sous la direction de son auteur · Logiciel libre sous licence GPL-3.0 · version {0}",
        typeof(AuditEngine).Assembly.GetName().Version?.ToString(3));

    private async Task RunAuditAsync()
    {
        IsRunning = true;
        Completed = 0;
        StatusText = T("Lecture de la configuration…");
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
                StatusText = T("Audit en cours… {0}/{1}", Completed, ModuleCount);
            });

            _results = [.. await _engine.RunAsync(context, progress)];
            var findings = _results.SelectMany(r => r.Findings).ToList();
            StatusText = T("Audit terminé : {0} problème(s), {1} à surveiller, {2} optimisation(s) possible(s).",
                findings.Count(f => f.Status == FindingStatus.Problem),
                findings.Count(f => f.Status == FindingStatus.Warning),
                findings.Count(f => f.Status == FindingStatus.Improvable));
            OnPropertyChanged(nameof(SelectedModule));

            _lastContext = context;
            await RebindAcknowledgementsAsync(findings);
            OnPropertyChanged(nameof(HasAudit));
            LoadChoices(context);
            var fixContext = await Task.Run(() => FixContext.CreateDefault(context));
            BlockingReason = new FixEngine(fixContext).GetBlockingReason()
                ?? (fixContext.ElevatedAsAnotherUser
                    ? T("MAUS a été lancé avec un autre compte administrateur : les réglages de votre profil (effets visuels, confidentialité, Game Bar) seront ignorés. Relancez MAUS depuis votre propre session.")
                    : null);
            Replan();
            RefreshDashboard();
            await RefreshJournalAsync();
            await AskLaptopChoiceOnceAsync(context);
        }
        catch (Exception ex)
        {
            StatusText = T("L'audit a échoué : {0}", ex.Message);
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>Relance un seul module (après un choix de l'utilisateur) et met à jour l'affichage et les corrections.</summary>
    private async Task RefreshModulesAsync(params string[] moduleIds)
    {
        if (_lastContext is null)
        {
            return;
        }

        foreach (var module in _engine.Modules.Where(m => moduleIds.Contains(m.Id)))
        {
            var result = await AuditEngine.RunModuleAsync(module, _lastContext, CancellationToken.None);
            _results = [.. _results.Where(r => r.ModuleId != module.Id), result];
            Modules.FirstOrDefault(m => m.Id == module.Id)?.SetResult(result);
        }

        OnPropertyChanged(nameof(SelectedModule));
        Replan();
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
            var after = AuditReport.Create(_lastContext, _results.OrderBy(r => Modules.IndexOf(Modules.First(m => m.Id == r.ModuleId))).ToList());
            var input = _lastApplied is { } applied
                ? new HtmlReportInput
                {
                    Before = applied.Before,
                    After = after,
                    Session = applied.SessionId is null ? null : await Task.Run(() => FileJournalStore.CreateDefault().Load(applied.SessionId)),
                    Outcomes = applied.Verified,
                }
                : new HtmlReportInput { After = after };
            var html = HtmlReport.Build(input);
            await File.WriteAllTextAsync(path, html, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            StatusText = T("Rapport enregistré : {0} (ouvrez-le avec votre navigateur).", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = T("Le rapport n'a pas pu être enregistré : {0}", ex.Message);
        }
    }
}
