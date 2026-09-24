using System.Collections.ObjectModel;
using System.Windows.Input;
using Maus.Core;
using Maus.Core.Engine;
using Maus.Core.Reporting;

namespace Maus.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly AuditEngine _engine = AuditEngine.CreateWithBuiltInModules();
    private ModuleViewModel? _selectedModule;
    private bool _isRunning;
    private string _statusText = "Prêt. Lancez l'audit : MAUS lit votre configuration sans rien modifier.";
    private string _systemSummary = string.Empty;
    private int _completed;

    public MainViewModel()
    {
        Modules = new ObservableCollection<ModuleViewModel>(_engine.Modules.Select(m => new ModuleViewModel(m)));
        SelectedModule = Modules.FirstOrDefault();
        RunAuditCommand = new AsyncCommand(RunAuditAsync);
    }

    public ObservableCollection<ModuleViewModel> Modules { get; }

    public ICommand RunAuditCommand { get; }

    public int ModuleCount => Modules.Count;

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

    public string About { get; } = "Conçu et codé avec Claude, une IA d'Anthropic, sous la direction de son auteur · Logiciel libre sous licence GPL-3.0 · " +
                           $"version {typeof(AuditEngine).Assembly.GetName().Version?.ToString(3)}";

    private async Task RunAuditAsync()
    {
        IsRunning = true;
        Completed = 0;
        StatusText = "Lecture de la configuration…";
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
                StatusText = $"Audit en cours… {Completed}/{ModuleCount}";
            });

            var results = await _engine.RunAsync(context, progress);
            var findings = results.SelectMany(r => r.Findings).ToList();
            StatusText = $"Audit terminé : {findings.Count(f => f.Status == FindingStatus.Problem)} problème(s), " +
                         $"{findings.Count(f => f.Status == FindingStatus.Warning)} à surveiller, " +
                         $"{findings.Count(f => f.Status == FindingStatus.Improvable)} optimisation(s) possible(s). Rien n'a été modifié.";
            OnPropertyChanged(nameof(SelectedModule));
        }
        catch (Exception ex)
        {
            StatusText = $"L'audit a échoué : {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }
}
