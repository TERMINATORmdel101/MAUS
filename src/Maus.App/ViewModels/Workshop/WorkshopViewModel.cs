using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using Maus.Core;
using Maus.Core.Preferences;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>
/// Atelier matériel : « Mon PC », « En direct », « Processus », « Tests ». Les mesures ne tournent que lorsque
/// l'atelier est affiché (ou qu'un test est en cours), pour ne rien consommer le reste du temps.
/// </summary>
public sealed class WorkshopViewModel : ObservableObject
{
    public const int SectionPc = 0;
    public const int SectionLive = 1;
    public const int SectionProcesses = 2;
    public const int SectionTests = 3;

    private readonly Func<string, string, bool> _confirm;
    private readonly Func<AuditContext?> _context;
    private readonly IPreferencesStore _preferences;
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _processTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ProcessCatalog _catalog = ProcessCatalog.Default;
    private ISensorSource? _sensors;
    private ProcessMonitor? _processes;
    private bool _isActive;
    private int _section;
    private bool _inventoryLoaded;
    private bool _sampling;
    private bool _processSampling;
    private string _inventoryStatus = T("Ouvrez « Mon PC » pour lire la fiche d'identité de votre matériel.");
    private ProcessRowViewModel? _selectedProcess;
    private string _filter = string.Empty;
    private IReadOnlyList<ProcessSample> _lastProcesses = [];
    private TestOption<SearchEngine> _searchEngine;
    private bool _isDiagnosing;
    private string _diagnosisStatus = string.Empty;
    private Action? _stopTest;
    private bool _isTesting;
    private double _cpuProgress;
    private string _cpuStatus = string.Empty;
    private double _ramProgress;
    private string _ramStatus = string.Empty;

    public WorkshopViewModel(Func<string, string, bool> confirm, Func<AuditContext?> context, IPreferencesStore preferences)
    {
        _confirm = confirm;
        _context = context;
        _preferences = preferences;
        _liveTimer.Tick += async (_, _) => await SampleLiveAsync();
        _processTimer.Tick += async (_, _) => await SampleProcessesAsync();
        SearchEngines =
        [
            new("DuckDuckGo", SearchEngine.DuckDuckGo),
            new("Qwant", SearchEngine.Qwant),
            new("Ecosia", SearchEngine.Ecosia),
            new("Google", SearchEngine.Google),
            new("Bing", SearchEngine.Bing),
        ];
        _searchEngine = SearchEngines.FirstOrDefault(e => e.Value == _preferences.Load().SearchEngine) ?? SearchEngines[0];
        CpuDurations =
        [
            new(T("1 minute (rapide)"), TimeSpan.FromMinutes(1)),
            new(T("5 minutes (recommandé)"), TimeSpan.FromMinutes(5)),
            new(T("15 minutes (approfondi)"), TimeSpan.FromMinutes(15)),
        ];
        _cpuDuration = CpuDurations[1];
        RamSizes =
        [
            new(T("Automatique : la moitié de la mémoire libre"), 0L),
            new(T("1 Go"), 1L << 30),
            new(T("4 Go"), 4L << 30),
            new(T("8 Go"), 8L << 30),
        ];
        _ramSize = RamSizes[0];

        SearchProcessCommand = new AsyncCommand(() => Run(() => { if (SelectedProcess is { } p) { OpenSearch(WebSearch.ForProcess(p.Name)); } }));
        ShowProcessCommand = new AsyncCommand(() => Run(() => { if (SelectedProcess?.Sample.Path is { } path) { ShellLauncher.ShowInFolder(path); } }));
        TerminateCommand = new AsyncCommand(TerminateAsync);
        DiagnoseCommand = new AsyncCommand(DiagnoseAsync);
        StartCpuTestCommand = new AsyncCommand(RunCpuTestAsync);
        StartRamTestCommand = new AsyncCommand(RunRamTestAsync);
        StopTestCommand = new AsyncCommand(() => Run(() => _stopTest?.Invoke()));
    }

    public LiveViewModel Live { get; } = new();

    public ObservableCollection<ComponentCardViewModel> Components { get; } = [];

    public ProcessListState ProcessList { get; } = new();

    public string InventoryStatus
    {
        get => _inventoryStatus;
        private set => SetProperty(ref _inventoryStatus, value);
    }

    /// <summary>L'atelier est affiché (onglet de la fenêtre principale).</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value))
            {
                _ = RefreshActivityAsync();
            }
        }
    }

    /// <summary>Sous-partie affichée : Mon PC, En direct, Processus ou Tests.</summary>
    public int Section
    {
        get => _section;
        set
        {
            if (SetProperty(ref _section, value))
            {
                _ = RefreshActivityAsync();
            }
        }
    }

    public ProcessRowViewModel? SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            if (SetProperty(ref _selectedProcess, value))
            {
                OnPropertyChanged(nameof(HasSelectedProcess));
                OnPropertyChanged(nameof(NoSelectedProcess));
            }
        }
    }

    public bool HasSelectedProcess => SelectedProcess is not null;

    public bool NoSelectedProcess => SelectedProcess is null;

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
            {
                ShowProcesses();
            }
        }
    }

    public IReadOnlyList<TestOption<SearchEngine>> SearchEngines { get; }

    public TestOption<SearchEngine> SearchEngineChoice
    {
        get => _searchEngine;
        set
        {
            if (value is not null && SetProperty(ref _searchEngine, value))
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        _preferences.Save(_preferences.Load() with { SearchEngine = value.Value });
                    }
                    catch (Exception ex) when (ex is Maus.Core.Fixes.JournalUnsafeException or System.IO.IOException or UnauthorizedAccessException)
                    {
                        // Le choix vaut pour cette ouverture de MAUS.
                    }
                });
            }
        }
    }

    public bool IsDiagnosing
    {
        get => _isDiagnosing;
        private set => SetProperty(ref _isDiagnosing, value);
    }

    public string DiagnosisStatus
    {
        get => _diagnosisStatus;
        private set => SetProperty(ref _diagnosisStatus, value);
    }

    public IReadOnlyList<TestOption<TimeSpan>> CpuDurations { get; }

    private TestOption<TimeSpan> _cpuDuration;

    public TestOption<TimeSpan> CpuDuration
    {
        get => _cpuDuration;
        set => SetProperty(ref _cpuDuration, value);
    }

    public IReadOnlyList<TestOption<long>> RamSizes { get; }

    private TestOption<long> _ramSize;

    public TestOption<long> RamSize
    {
        get => _ramSize;
        set => SetProperty(ref _ramSize, value);
    }

    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            if (SetProperty(ref _isTesting, value))
            {
                OnPropertyChanged(nameof(IsIdle));
            }
        }
    }

    public bool IsIdle => !IsTesting;

    public double CpuProgress
    {
        get => _cpuProgress;
        private set => SetProperty(ref _cpuProgress, value);
    }

    public string CpuStatus
    {
        get => _cpuStatus;
        private set => SetProperty(ref _cpuStatus, value);
    }

    public double RamProgress
    {
        get => _ramProgress;
        private set => SetProperty(ref _ramProgress, value);
    }

    public string RamStatus
    {
        get => _ramStatus;
        private set => SetProperty(ref _ramStatus, value);
    }

    public ICommand SearchProcessCommand { get; }

    public ICommand ShowProcessCommand { get; }

    public ICommand TerminateCommand { get; }

    public ICommand DiagnoseCommand { get; }

    public ICommand StartCpuTestCommand { get; }

    public ICommand StartRamTestCommand { get; }

    public ICommand StopTestCommand { get; }

    /// <summary>Arrête les mesures et un test en cours (fermeture de la fenêtre, changement de langue).</summary>
    public void Stop()
    {
        _isActive = false;
        _stopTest?.Invoke();
        _liveTimer.Stop();
        _processTimer.Stop();
    }

    /// <summary>Ouvre l'atelier sur une sous-partie (depuis les actions rapides de l'accueil).</summary>
    public void Open(int section) => Section = section;

    private async Task RefreshActivityAsync()
    {
        if (IsActive && Section == SectionPc && !_inventoryLoaded)
        {
            await LoadInventoryAsync();
        }

        var live = (IsActive && Section == SectionLive) || IsTesting || IsDiagnosing;
        if (live && !_liveTimer.IsEnabled)
        {
            _liveTimer.Start();
            await SampleLiveAsync();
        }
        else if (!live)
        {
            _liveTimer.Stop();
        }

        var processes = (IsActive && Section == SectionProcesses) || IsDiagnosing;
        if (processes && !_processTimer.IsEnabled)
        {
            _processTimer.Start();
            await SampleProcessesAsync();
        }
        else if (!processes)
        {
            _processTimer.Stop();
        }
    }

    private async Task LoadInventoryAsync()
    {
        _inventoryLoaded = true;
        InventoryStatus = T("Lecture du matériel…");
        try
        {
            var context = _context() ?? await Task.Run(AuditContext.CreateDefault);
            var inventory = await Task.Run(() => HardwareInventoryReader.Read(context, new X86CpuIdSource(), new WindowsNvmlSource()));
            Components.Clear();
            foreach (var card in ComponentCardViewModel.From(inventory, SafetyLimits.Load(), reference => OpenSearch(WebSearch.ForComponent(reference))))
            {
                Components.Add(card);
            }

            InventoryStatus = T("Lu directement dans le matériel et le BIOS, sans pilote. « Rechercher la fiche » ouvre votre navigateur ; MAUS n'envoie rien de lui-même.");
        }
        catch (Exception ex)
        {
            _inventoryLoaded = false;
            InventoryStatus = T("La lecture du matériel a échoué : {0}", ex.Message);
        }
    }

    private async Task SampleLiveAsync()
    {
        if (_sampling)
        {
            return;
        }

        _sampling = true;
        try
        {
            _sensors ??= await Task.Run(() => (ISensorSource)new WindowsSensorSource());
            var snapshot = await Task.Run(_sensors.Sample);
            Live.Add(snapshot);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            _liveTimer.Stop();
        }
        finally
        {
            _sampling = false;
        }
    }

    private async Task SampleProcessesAsync()
    {
        if (_processSampling)
        {
            return;
        }

        _processSampling = true;
        try
        {
            _processes ??= new ProcessMonitor(new WindowsProcessSource(), Environment.ProcessorCount);
            var monitor = _processes;
            _lastProcesses = await Task.Run(monitor.Sample);
            ShowProcesses();
        }
        finally
        {
            _processSampling = false;
        }
    }

    private void ShowProcesses()
    {
        var selected = SelectedProcess?.Pid;
        var folders = SystemFolders.Current;
        var rows = _lastProcesses
            .Where(p => Filter.Length == 0 || p.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.PrivateBytes)
            .Take(200)
            .Select(p => new ProcessRowViewModel(p, _catalog, folders))
            .ToList();
        ProcessList.Rows.Clear();
        foreach (var row in rows)
        {
            ProcessList.Rows.Add(row);
        }

        SelectedProcess = rows.FirstOrDefault(r => r.Pid == selected);
    }

    private async Task TerminateAsync()
    {
        if (SelectedProcess is not { } process || !process.CanTerminate)
        {
            return;
        }

        if (!_confirm(T("Arrêter ce processus ?"), T("« {0} » va être arrêté immédiatement : son travail non enregistré sera perdu.", process.Name) + Environment.NewLine + Environment.NewLine + T("Continuer ?")))
        {
            return;
        }

        try
        {
            await Task.Run(() => new WindowsProcessTerminator().Terminate(process.Sample));
            DiagnosisStatus = T("« {0} » a été arrêté.", process.Name);
        }
        catch (InvalidOperationException ex)
        {
            DiagnosisStatus = ex.Message;
        }

        await SampleProcessesAsync();
    }

    private async Task DiagnoseAsync()
    {
        if (IsDiagnosing)
        {
            return;
        }

        IsDiagnosing = true;
        ProcessList.Causes.Clear();
        await RefreshActivityAsync();
        try
        {
            for (var second = 60; second > 0; second--)
            {
                DiagnosisStatus = T("Mesure en cours : encore {0} s. Utilisez votre PC normalement pendant ce temps.", second);
                await Task.Delay(1000);
            }

            var causes = SlownessDiagnosis.Analyze(Live.Samples, _lastProcesses, _catalog);
            foreach (var cause in causes)
            {
                ProcessList.Causes.Add(new CauseViewModel(cause.Title, cause.Explanation, cause.ModuleId is null ? string.Empty : T("Voir le module {0}", cause.ModuleId)));
            }

            DiagnosisStatus = T("Diagnostic terminé : voici les causes principales, de la plus importante à la moins importante.");
        }
        finally
        {
            IsDiagnosing = false;
            await RefreshActivityAsync();
        }
    }

    private async Task RunCpuTestAsync()
    {
        if (IsTesting)
        {
            return;
        }

        var duration = CpuDuration.Value;
        if (!_confirm(T("Lancer le test du processeur ?"), T("Tous les cœurs vont travailler à 100 % pendant {0} : le PC chauffera et ses ventilateurs accéléreront. Le test s'arrête tout seul si une température dangereuse est atteinte, et à tout moment avec « Arrêter le test ».", CpuDuration.Label) + Environment.NewLine + Environment.NewLine + T("Continuer ?")))
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        await StartTestAsync(cancellation);
        try
        {
            CpuStatus = T("Test en cours…");
            var progress = new Progress<CpuTestProgress>(p =>
            {
                CpuProgress = Math.Min(100, 100 * p.Elapsed.TotalSeconds / duration.TotalSeconds);
                CpuStatus = T("{0:0} s · {1} tours vérifiés · {2} erreur(s)", p.Elapsed.TotalSeconds, p.Rounds, p.Errors);
            });
            var result = await CpuTest.RunAsync(new CpuTestOptions(duration, Environment.ProcessorCount), progress, () => Live.DangerAlarm, cancellation.Token);
            CpuProgress = 100;
            var history = BenchmarkHistory.CreateDefault();
            var previous = history.Load();
            var entry = new BenchmarkEntry("cpu-multi", result.Score, result.Stable, DateTimeOffset.Now);
            if (!result.Aborted)
            {
                history.Add(entry);
            }

            var comparison = BenchmarkHistory.CompareToPrevious(previous, entry) is { } delta
                ? " " + T("({0:+0.0;-0.0;0} % par rapport à vos passages précédents)", delta)
                : string.Empty;
            CpuStatus = result.Aborted
                ? T("Test interrompu : {0}", result.AbortReason)
                : result.Stable
                    ? T("Stable : aucune erreur de calcul en {0:0} s. Score : {1:0.0} tours par seconde.", result.Duration.TotalSeconds, result.Score) + comparison
                    : T("INSTABLE : {0} erreur(s) de calcul. Revenez aux réglages d'origine du BIOS (surcadençage, tension) et vérifiez le refroidissement.", result.Errors);
        }
        finally
        {
            await StopTestAsync();
        }
    }

    private async Task RunRamTestAsync()
    {
        if (IsTesting)
        {
            return;
        }

        var available = Live.Samples.LastOrDefault() is { MemoryTotalBytes: { } total, MemoryUsedBytes: { } used } ? total - used : 4L << 30;
        var bytes = RamSize.Value == 0 ? MemoryTest.SuggestedBytes(available) : Math.Min(RamSize.Value, Math.Max(256L << 20, available - (512L << 20)));
        if (!_confirm(T("Lancer le test de la mémoire vive ?"), T("MAUS va écrire puis relire des motifs sur {0:0.0} Go de mémoire. Fermez vos jeux et programmes lourds pendant le test ; il s'arrête à tout moment avec « Arrêter le test ».", bytes / 1073741824.0) + Environment.NewLine + Environment.NewLine + T("Continuer ?")))
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        await StartTestAsync(cancellation);
        try
        {
            RamStatus = T("Test en cours…");
            var progress = new Progress<MemoryTestProgress>(p =>
            {
                RamProgress = p.Percent;
                RamStatus = T("{0} · {1:0} % · {2} erreur(s)", p.Step, p.Percent, p.Errors);
            });
            var result = await MemoryTest.RunAsync(new MemoryTestOptions(bytes), progress, cancellation.Token);
            RamProgress = 100;
            if (!result.Aborted)
            {
                BenchmarkHistory.CreateDefault().Add(new BenchmarkEntry("ram", result.CopyGigabytesPerSecond ?? 0, result.Stable, DateTimeOffset.Now,
                    result.LatencyNanoseconds?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)));
            }

            RamStatus = result.Aborted
                ? T("Test interrompu.")
                : result.Stable
                    ? T("Aucune erreur sur {0:0.0} Go. Débit de copie : {1:0.0} Go/s · latence : {2:0.0} ns. (Un test sous Windows ne couvre pas la mémoire déjà utilisée : une erreur est un signal fort, l'absence d'erreur n'est pas une preuve absolue.)", result.TestedBytes / 1073741824.0, result.CopyGigabytesPerSecond, result.LatencyNanoseconds)
                    : T("ERREURS : {0} valeur(s) relue(s) différente(s). Revenez au profil mémoire d'origine dans le BIOS (voir Module 10), puis refaites le test ; si les erreurs restent, une barrette est probablement défaillante.", result.Errors);
        }
        finally
        {
            await StopTestAsync();
        }
    }

    private async Task StartTestAsync(CancellationTokenSource cancellation)
    {
        _stopTest = cancellation.Cancel;
        IsTesting = true;
        await RefreshActivityAsync();
    }

    private async Task StopTestAsync()
    {
        IsTesting = false;
        _stopTest = null;
        await RefreshActivityAsync();
    }

    private void OpenSearch(string query) => ShellLauncher.OpenUrl(WebSearch.Build(SearchEngineChoice.Value, query));

    private static Task Run(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
