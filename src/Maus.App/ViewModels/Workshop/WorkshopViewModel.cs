using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using Maus.Core.Diagnostics;
using Maus.Core.Platform;
using Maus.Core.Preferences;
using Maus.Core.Workshop;
using Maus.Core.Workshop.Memory;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>
/// Atelier matériel, en trois entrées (0.8.6) : « Mon PC » (fiche, stockage, mémoire, pilotes), « En direct » (capteurs,
/// processus), « Tests ». Les mesures ne tournent que lorsque l'atelier est affiché (ou qu'un test est en cours), pour ne
/// rien consommer le reste du temps.
/// </summary>
public sealed partial class WorkshopViewModel : ObservableObject, IDisposable
{
    public const int SectionPc = 0;
    public const int SectionLive = 1;
    public const int SectionProcesses = 2;
    public const int SectionTests = 3;
    public const int SectionStorage = 4;

    public const int GroupPc = 0;
    public const int GroupLive = 1;
    public const int GroupTests = 2;

    private readonly Func<string, string, bool> _confirm;
    private readonly IPreferencesStore _preferences;
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>Arrêt automatique des tests, d'après des mesures fraîches (jamais une alarme figée).</summary>
    private readonly ThermalWatchdog _watchdog = new();
    private readonly DispatcherTimer _processTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ProcessCatalog _catalog = ProcessCatalog.Default;
    private ISensorSource? _sensors;
    private ProcessMonitor? _processes;

    private bool _isActive;
    private bool _monitorOpen;
    private bool _stopped;
    private int _section;

    /// <summary>Dernière section ouverte dans chaque entrée : y revenir ne ramène pas à la première.</summary>
    private readonly int[] _lastInGroup = [SectionPc, SectionLive, SectionTests];
    private bool _inventoryLoaded;
    private MachineDetails? _details;
    private bool _sampling;
    private bool _processSampling;
    private string _inventoryStatus = T("Ouvrez « Mon PC » pour lire la fiche d'identité de votre matériel.");
    private ProcessRowViewModel? _selectedProcess;
    private string _filter = string.Empty;
    private IReadOnlyList<ProcessSample> _lastProcesses = [];
    private TestOption<SearchEngine> _searchEngine;
    private bool _isDiagnosing;
    private string _diagnosisStatus = string.Empty;
    private string _lastRestartText = string.Empty;
    private Action? _stopTest;
    private bool _isTesting;
    private double _cpuProgress;
    private string _cpuStatus = string.Empty;
    private double _ramProgress;
    private string _ramStatus = string.Empty;
    private readonly IGpuMemoryProvider _gpuProvider = new D3D11GpuMemoryProvider();
    private readonly ICoreTopology _topology = new WindowsCoreTopology();
    private readonly FileCoreTestCheckpoint _checkpoint = FileCoreTestCheckpoint.CreateDefault();
    private double _coreProgress;
    private string _coreStatus = string.Empty;
    private string? _crashNotice;
    private bool _gpuAdaptersLoaded;
    private TestOption<GpuAdapterInfo>? _vramAdapter;
    private double _vramProgress;
    private string _vramStatus = string.Empty;

    public WorkshopViewModel(Func<string, string, bool> confirm, IPreferencesStore preferences)
    {
        _confirm = confirm;
        _preferences = preferences;
        _liveTimer.Interval = Appearance.AppearanceManager.Current.RefreshInterval;
        Appearance.AppearanceManager.Changed += OnAppearanceChanged;
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
        var saved = _preferences.Load();
        _searchEngine = SearchEngines.FirstOrDefault(e => e.Value == saved.SearchEngine) ?? SearchEngines[0];
        CpuDurations =
        [
            new(T("1 minute (rapide)"), TimeSpan.FromMinutes(1)),
            new(T("5 minutes (recommandé)"), TimeSpan.FromMinutes(5)),
            new(T("15 minutes (approfondi)"), TimeSpan.FromMinutes(15)),
        ];
        _cpuDuration = CpuDurations[1];
        RamSizes =
        [
            new(T("Maximum : toute la mémoire libre (recommandé)"), 0L),
            new(T("La moitié de la mémoire libre (plus rapide)"), -1L),
            new(T("4 Go"), 4L << 30),
            new(T("8 Go"), 8L << 30),
            new(T("16 Go"), 16L << 30),
        ];
        _ramSize = RamSizes[0];
        VramSizes =
        [
            new(T("Maximum : toute la mémoire libre de la carte (recommandé)"), 0L),
            new(T("60 % de la mémoire de la carte (plus rapide)"), -1L),
            new(T("1 Go"), 1L << 30),
            new(T("2 Go"), 2L << 30),
            new(T("4 Go"), 4L << 30),
        ];
        _vramSize = VramSizes[0];
        CoreDurations =
        [
            new(T("5 minutes (aperçu)"), TimeSpan.FromMinutes(5)),
            new(T("20 minutes (recommandé)"), TimeSpan.FromMinutes(20)),
            new(T("1 heure (approfondi)"), TimeSpan.FromHours(1)),
            new(T("4 heures (idéal pour une nuit)"), TimeSpan.FromHours(4)),
        ];
        _coreDuration = CoreDurations[1];
        _crashNotice = DescribeCrash(_checkpoint.Load(), DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64));
        PropertyChanged += OnTestValueChanged;
        EngineHost.ElevationChanged += OnElevationChanged;

        SearchProcessCommand = new AsyncCommand(() => { if (SelectedProcess is { } p) { OpenSearch(WebSearch.ForProcess(p.Name)); } });
        ShowProcessCommand = new AsyncCommand(() => { if (SelectedProcess?.Sample.Path is { } path) { ShellLauncher.ShowInFolder(path); } });
        TerminateCommand = new AsyncCommand(TerminateAsync);
        DiagnoseCommand = new AsyncCommand(DiagnoseAsync);
        StartCpuTestCommand = new AsyncCommand(RunCpuTestAsync);
        StartRamTestCommand = new AsyncCommand(RunRamTestAsync);
        StartVramTestCommand = new AsyncCommand(RunVramTestAsync);
        StartCoreTestCommand = new AsyncCommand(RunCoreTestAsync);
        ScanCommand = new AsyncCommand(ScanAsync);
        UpCommand = new AsyncCommand(GoUp);
        OpenCurrentFolderCommand = new AsyncCommand(() => { if (_current is { } node) { ShellLauncher.OpenFolder(node.Path); } });
        StorageSettingsCommand = new AsyncCommand(() => ShellLauncher.OpenSettings("ms-settings:storagesense"));
        StopScanCommand = new AsyncCommand(() => _stopScan?.Invoke());
        StartDiskTestCommand = new AsyncCommand(RunDiskTestAsync);
        DiskSizes =
        [
            new(T("1 Go (rapide)"), 1L << 30),
            new(T("4 Go (plus fiable)"), 4L << 30),
        ];
        _diskSize = DiskSizes[0];
        DismissCrashCommand = new AsyncCommand(() =>
        {
            // Pendant un test, la trace est celle du test en cours : elle sert encore.
            if (!Busy)
            {
                _checkpoint.Clear();
            }

            CrashNotice = null;
        });
        StopTestCommand = new AsyncCommand(() =>
        {
            _seriesStopped = true;
            _stopTest?.Invoke();
        });
    }

    public LiveViewModel Live { get; } = new();

    public ObservableCollection<ComponentCardViewModel> Components { get; } = [];

    /// <summary>
    /// Copie toute la fiche « Mon PC » en texte (pour un forum ou un signalement), après avoir masqué le nom
    /// d'utilisateur, le nom du PC et les adresses e-mail. MAUS n'envoie rien : l'utilisateur colle où il veut.
    /// </summary>
    public ICommand CopyInventoryCommand => _copyInventory ??= new AsyncCommand(() =>
    {
        if (Components.Count == 0)
        {
            return;
        }

        var text = T("Fiche du PC relevée par MAUS {0} le {1}", Maus.Core.AppVersion.Display, DateTime.Now.ToString("g", Culture)) +
            Environment.NewLine + Environment.NewLine + ComponentCardViewModel.ToText(Components);
        InventoryStatus = ClipboardText.TryCopy(text)
            ? T("Fiche copiée (nom d'utilisateur, nom du PC et e-mails masqués) : collez-la avec Ctrl+V.")
            : ClipboardText.Busy;
    });

    private ICommand? _copyInventory;

    public ProcessListState ProcessList { get; } = new();

    public string InventoryStatus
    {
        get => _inventoryStatus;
        private set => SetProperty(ref _inventoryStatus, value);
    }

    /// <summary>La fiche « Mon PC » est en cours de lecture (barre de progression animée).</summary>
    public bool IsReadingInventory
    {
        get => _isReadingInventory;
        private set => SetProperty(ref _isReadingInventory, value);
    }

    private bool _isReadingInventory;

    /// <summary>L'atelier est affiché (onglet de la fenêtre principale).</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value))
            {
                RefreshActivity();
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
                _lastInGroup[Group] = value;
                OnPropertyChanged(nameof(Group));
                OnPropertyChanged(nameof(ShowPcSections));
                OnPropertyChanged(nameof(ShowLiveSections));
                RefreshActivity();
            }
        }
    }

    /// <summary>
    /// Entrée affichée. Les sections gardent leur numéro : la dernière ouverte reste retenue dans les choix, et les liens de
    /// l'accueil continuent de mener à la bonne.
    /// </summary>
    public int Group
    {
        get => GroupOf(_section);
        set
        {
            if (value is >= GroupPc and <= GroupTests && value != Group)
            {
                Section = _lastInGroup[value];
            }
        }
    }

    public bool ShowPcSections => Group == GroupPc;

    public bool ShowLiveSections => Group == GroupLive;

    private static int GroupOf(int section) => section switch
    {
        SectionLive or SectionProcesses => GroupLive,
        SectionTests => GroupTests,
        _ => GroupPc,
    };

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
                Task.Run(() =>
                {
                    try
                    {
                        _preferences.Update(p => p with { SearchEngine = value.Value });
                    }
                    catch (Exception ex) when (ex is Maus.Core.Fixes.JournalUnsafeException or System.IO.IOException or UnauthorizedAccessException)
                    {
                        // Le choix vaut pour cette ouverture de MAUS.
                    }
                }).Forget("processus : choix du moteur de recherche");
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

    /// <summary>Le dernier vrai redémarrage, à côté du diagnostic et hors des causes : aucun lien avec la lenteur n'est prouvé.</summary>
    public string LastRestartText
    {
        get => _lastRestartText;
        private set
        {
            if (SetProperty(ref _lastRestartText, value))
            {
                OnPropertyChanged(nameof(HasLastRestart));
            }
        }
    }

    public bool HasLastRestart => LastRestartText.Length > 0;

    public IReadOnlyList<TestOption<TimeSpan>> CpuDurations { get; }

    private TestOption<TimeSpan> _cpuDuration;

    public TestOption<TimeSpan> CpuDuration
    {
        get => _cpuDuration;
        set => SetProperty(ref _cpuDuration, value);
    }

    /// <summary>Types de charge proposés : AVX et « très lourd » seulement si le processeur a l'AVX.</summary>
    public IReadOnlyList<TestOption<CpuStressMode>> CpuModes { get; } = BuildCpuModes();

    private TestOption<CpuStressMode>? _cpuMode;

    public TestOption<CpuStressMode> CpuMode
    {
        get => _cpuMode ?? CpuModes[0];
        set
        {
            if (SetProperty(ref _cpuMode, value))
            {
                OnPropertyChanged(nameof(CpuModeDetail));
            }
        }
    }

    /// <summary>Instructions réellement utilisées par la charge choisie, sur ce processeur.</summary>
    public string CpuModeDetail => T("Instructions : {0}", CpuStress.Instructions(CpuMode.Value));

    private static List<TestOption<CpuStressMode>> BuildCpuModes() =>
        CpuStress.Available().Select(mode => new TestOption<CpuStressMode>(ModeLabel(mode), mode)).ToList();

    private static string ModeLabel(CpuStressMode mode) => mode switch
    {
        CpuStressMode.Scalar => T("Entiers : calculs sans vecteurs"),
        CpuStressMode.Sse => T("SSE2 : vecteurs 128 bits"),
        CpuStressMode.Avx => T("AVX : vecteurs 256 bits"),
        CpuStressMode.Fma => T("AVX2 + FMA : vecteurs 256 bits, charge lourde"),
        CpuStressMode.Avx512 => T("AVX-512 : vecteurs 512 bits, charge la plus lourde"),
        CpuStressMode.Memory => T("Caches et mémoire : grands tableaux"),
        _ => T("Automatique : mélange de calculs (recommandé)"),
    };

    /// <summary>
    /// Avertissement des charges vectorielles larges (AVX, FMA, AVX-512), qui font consommer et chauffer davantage ;
    /// et, sans PawnIO, rappel que la température du processeur n'est pas lue. Vide pour les autres charges.
    /// </summary>
    private static string HeavyWarning(TestOption<CpuStressMode> mode) =>
        mode.Value is not (CpuStressMode.Avx or CpuStressMode.Fma or CpuStressMode.Avx512)
            ? string.Empty
            : Environment.NewLine + Environment.NewLine + T("Charge « {0} » : les calculs vectoriels font consommer et chauffer le processeur davantage que le mélange automatique. Gardez un œil sur la température affichée pendant le test.", mode.Label);

    /// <summary>
    /// L'arrêt automatique sur la chaleur du processeur demande sa température (pilote PawnIO) et sa limite : sinon, la
    /// confirmation le dit, avec la raison. Vide si la température et la limite sont lues.
    /// </summary>
    private string TemperatureCaveat() =>
        ThermalWatchdog.Caveat(CpuTemperatureState()) is { } caveat ? Environment.NewLine + Environment.NewLine + caveat : string.Empty;

    /// <summary>
    /// État de l'arrêt automatique sur la chaleur du processeur : d'après la dernière mesure si les capteurs ont déjà tourné,
    /// sinon d'après la présence du pilote (lue dans le registre : <see cref="IsPawnIoInstalled"/> n'est connu qu'une fois les
    /// capteurs ouverts).
    /// </summary>
    private CpuTemperatureWatch CpuTemperatureState()
    {
        var installed = _sensors is not null ? IsPawnIoInstalled : PawnIo.State(new WindowsRegistryReader()).Installed;
        return ThermalWatchdog.Assess(Live.Samples.LastOrDefault(), Live.CpuMaxC, installed, EngineHasRights);
    }

    /// <summary>Mesures montrées pendant un test : processeur (tests processeur, mémoire, cœur par cœur) ou carte graphique (mémoire vidéo).</summary>
    public MetricViewModel TestLoad => _graphicsTest ? Live.Gpu : Live.Cpu;

    public MetricViewModel TestTemperature => _graphicsTest ? Live.GpuTemperature : Live.CpuTemperature;

    private bool _graphicsTest;

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

    /// <summary>Aucun test en cours ni en préparation : les boutons « Démarrer » sont actifs.</summary>
    public bool IsIdle => !IsTesting && !_preparing;

    /// <summary>Un test prépare son lancement (lecture des cœurs, des programmes actifs, confirmation) : aucun autre ne part.</summary>
    private bool _preparing;

    private bool Busy => IsTesting || _preparing || IsBenchmarkRunning;

    private void SetPreparing(bool value)
    {
        _preparing = value;
        OnPropertyChanged(nameof(IsIdle));
    }

    /// <summary>Une seule erreur suffit à dire qu'un PC n'est pas stable : un arrêt du test ne l'efface pas.</summary>
    private static string ErrorsBeforeStop(long errors) =>
        errors > 0 ? " " + T("{0} erreur(s) trouvée(s) avant l'arrêt : ce PC n'est pas stable dans ces conditions.", errors) : string.Empty;

    /// <summary>
    /// Mesure fraîche avant la confirmation d'un test : les mesures ne tournent pas sur cet onglet, et ce que la confirmation
    /// annonce (mémoire libre, arrêt sur la chaleur) en dépend. Aucun autre test ne part pendant ce temps.
    /// </summary>
    private async Task SampleBeforeTestAsync()
    {
        SetPreparing(true);
        try
        {
            await SampleLiveAsync();
        }
        finally
        {
            SetPreparing(false);
        }
    }

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

    /// <summary>Ouvre les constats d'un module (posé par la fenêtre principale).</summary>
    public Action<string>? OpenModuleFindings { get; init; }

    /// <summary>La fiche du PC vient d'être lue.</summary>
    public Func<HardwareInventory, Task>? InventoryRead { get; init; }

    public bool HasCauses => ProcessList.Causes.Count > 0;

    public ICommand StartCpuTestCommand { get; }

    public ICommand StartRamTestCommand { get; }

    public ICommand StopTestCommand { get; }

    public ICommand StartVramTestCommand { get; }

    public ICommand StartCoreTestCommand { get; }

    public ICommand DismissCrashCommand { get; }

    public IReadOnlyList<TestOption<TimeSpan>> CoreDurations { get; }

    private TestOption<TimeSpan> _coreDuration;

    public TestOption<TimeSpan> CoreDuration
    {
        get => _coreDuration;
        set => SetProperty(ref _coreDuration, value);
    }

    public double CoreProgress
    {
        get => _coreProgress;
        private set => SetProperty(ref _coreProgress, value);
    }

    public string CoreStatus
    {
        get => _coreStatus;
        private set => SetProperty(ref _coreStatus, value);
    }

    /// <summary>Bilan par cœur (pastilles vertes ou rouges).</summary>
    public ObservableCollection<CoreChipViewModel> CoreChips { get; } = [];

    /// <summary>Le dernier test cœur par cœur s'est interrompu brutalement (gel ou redémarrage) : explication, sinon <c>null</c>.</summary>
    public string? CrashNotice
    {
        get => _crashNotice;
        private set
        {
            if (SetProperty(ref _crashNotice, value))
            {
                OnPropertyChanged(nameof(HasCrashNotice));
            }
        }
    }

    public bool HasCrashNotice => CrashNotice is not null;

    /// <summary>Cartes graphiques testables (mémoire dédiée), chargées à l'ouverture de l'onglet Tests.</summary>
    public ObservableCollection<TestOption<GpuAdapterInfo>> VramAdapters { get; } = [];

    public TestOption<GpuAdapterInfo>? VramAdapter
    {
        get => _vramAdapter;
        set => SetProperty(ref _vramAdapter, value);
    }

    public IReadOnlyList<TestOption<long>> VramSizes { get; }

    private TestOption<long> _vramSize;

    public TestOption<long> VramSize
    {
        get => _vramSize;
        set => SetProperty(ref _vramSize, value);
    }

    public double VramProgress
    {
        get => _vramProgress;
        private set => SetProperty(ref _vramProgress, value);
    }

    public string VramStatus
    {
        get => _vramStatus;
        private set => SetProperty(ref _vramStatus, value);
    }

    /// <summary>Fermeture : comme <see cref="Stop"/>, qui rend aussi les ressources de Windows (pilote, requêtes PDH).</summary>
    public void Dispose() => Stop();

    /// <summary>Note la température la plus haute vue en direct, pour la courbe sur plusieurs semaines. Un échec est sans conséquence.</summary>
    private void RecordPeakTemperatures()
    {
        try
        {
            var history = BenchmarkHistory.CreateMeasures();
            var now = DateTimeOffset.Now;
            if (Live.TakePeakCpu() is { } cpu)
            {
                history.Add(MeasureHistory.Peak("cpu", T("Processeur : température maximale"), cpu, now));
            }

            if (Live.TakePeakGpu() is { } gpu)
            {
                history.Add(MeasureHistory.Peak("gpu", T("Carte graphique : température maximale"), gpu, now));
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Maus.Core.Diagnostics.Breadcrumbs.Add("températures maximales non enregistrées");
        }
    }

    /// <summary>Arrête les mesures et un test en cours (fermeture de la fenêtre, changement de langue).</summary>
    public void Stop()
    {
        _stopped = true;
        RecordPeakTemperatures();
        _isActive = false;
        _monitorOpen = false;
        if (_stopTest is not null)
        {
            // MAUS fermé pendant un test : arrêt voulu, pas un gel. La trace est effacée tout de suite, car la fermeture
            // n'attend pas la fin du test et laisserait croire au prochain lancement que le PC a planté.
            _checkpoint.Clear();
        }

        _stopTest?.Invoke();
        StopFrameCapture();
        _stopNet?.Invoke();
        _stopScan?.Invoke();
        _liveTimer.Stop();
        _processTimer.Stop();
        Appearance.AppearanceManager.Changed -= OnAppearanceChanged;
        EngineHost.ElevationChanged -= OnElevationChanged;

        // Referme le pilote (LibreHardwareMonitor) une fois la dernière mesure terminée.
        var sensors = _sensors;
        _sensors = null;
        if (sensors is not null)
        {
            Task.Run(async () =>
            {
                while (_sampling)
                {
                    await Task.Delay(50);
                }

                sensors.Dispose();
            }).Forget("atelier : fermeture des capteurs");
        }

        _processes = null;
    }

    /// <summary>Nouvel échantillon des mesures en direct (pour la fenêtre de surveillance : aucune mesure en double).</summary>
    public event EventHandler<SensorSnapshot>? Sampled;

    /// <summary>La fenêtre de surveillance est ouverte : les mesures continuent, quel que soit l'onglet affiché.</summary>
    public bool IsMonitorOpen
    {
        get => _monitorOpen;
        set
        {
            if (SetProperty(ref _monitorOpen, value))
            {
                RefreshActivity();
            }
        }
    }

    /// <summary>Intervalle des mesures en direct, choisi dans les paramètres.</summary>
    public TimeSpan RefreshInterval => _liveTimer.Interval;

    private void OnAppearanceChanged(object? sender, EventArgs e)
    {
        var interval = Appearance.AppearanceManager.Current.RefreshInterval;
        if (_liveTimer.Interval != interval)
        {
            _liveTimer.Interval = interval;
            OnPropertyChanged(nameof(RefreshInterval));
        }
    }

    /// <summary>Ouvre l'atelier sur une sous-partie (depuis les actions rapides de l'accueil).</summary>
    public void Open(int section) => Section = section;

    /// <summary>Actualise l'atelier sans attendre (onglet, sous-partie, fenêtre de surveillance, relevé).</summary>
    private void RefreshActivity() => RefreshActivityAsync().Forget("atelier : actualisation", ShowActivityError);

    /// <summary>Erreur imprévue pendant une actualisation : affichée dans la partie de l'atelier concernée.</summary>
    private void ShowActivityError(Exception ex)
    {
        var message = T("Cette partie de l'atelier n'a pas pu être actualisée : {0}", Maus.Core.Diagnostics.ErrorText.For(ex));
        switch (Section)
        {
            case SectionLive or SectionTests:
                Live.Alarms.Add(message);
                break;
            case SectionProcesses:
                DiagnosisStatus = message;
                break;
            case SectionStorage:
                ScanStatus = message;
                break;
            case SectionMemory:
                MemoryStatus = message;
                break;
            case SectionDrivers:
                DriversStatus = message;
                break;
            default:
                InventoryStatus = message;
                break;
        }
    }

    private async Task RefreshActivityAsync()
    {
        if (_stopped)
        {
            return;
        }

        if (IsActive && Section == SectionPc && !_inventoryLoaded)
        {
            await LoadInventoryAsync();
        }

        if (IsActive && Section == SectionDrivers && !_driversLoaded)
        {
            await LoadDriversAsync();
        }

        if (IsActive && Section == SectionTests && !_gpuAdaptersLoaded)
        {
            await LoadGpuAdaptersAsync();
        }

        if (IsActive && Section == SectionTests && ScoreKinds.Count == 0)
        {
            LoadScoreHistory();
        }

        if (IsActive && Section is SectionStorage or SectionTests && Drives.Count == 0)
        {
            LoadDrives();
        }

        var live = (IsActive && Section == SectionLive) || IsTesting || IsDiagnosing || IsRecording || IsMonitorOpen;
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
        Maus.Core.Diagnostics.Breadcrumbs.Add("Mon PC : lecture du matériel");
        _inventoryLoaded = true;
        InventoryStatus = T("Lecture du matériel…");
        IsReadingInventory = true;
        try
        {
            // Le moteur lit le matériel (et les puces SPD des barrettes si PawnIO est installé) : lecture seule.
            var reading = await EngineHost.Current.ReadInventoryAsync(CancellationToken.None);
            _details = reading.Details;
            var limits = SafetyLimits.Load();
            ShowInventory(reading.Inventory, limits, reading.Spd);
            InventoryStatus = !reading.SpdRead
                ? T("Lu directement dans le matériel et le BIOS, sans pilote. « Rechercher la fiche » ouvre votre navigateur ; MAUS n'envoie rien de lui-même.")
                : reading.Spd is null
                    ? T("Profils XMP / EXPO illisibles : la carte mère bloque peut-être l'accès au bus SMBus. Le reste de la fiche est lu sans pilote.")
                    : T("Lu dans le matériel et le BIOS ; profils XMP / EXPO lus dans la puce SPD de chaque barrette par PawnIO, sans rien modifier.");
            if (InventoryRead is { } inventoryRead)
            {
                await inventoryRead(reading.Inventory);
            }
        }
        catch (Exception ex)
        {
            _inventoryLoaded = false;
            Maus.Core.Diagnostics.CrashLog.Write(ex, "atelier : matériel");
            InventoryStatus = T("La lecture du matériel a échoué : {0}", Maus.Core.Diagnostics.ErrorText.For(ex));
        }
        finally
        {
            IsReadingInventory = false;
        }
    }

    private void ShowInventory(HardwareInventory inventory, SafetyLimits limits, IReadOnlyList<SpdModule>? spd)
    {
        Components.Clear();
        foreach (var card in ComponentCardViewModel.From(inventory, limits, reference => OpenSearch(WebSearch.ForComponent(reference)), spd, _details, batteryReport: OpenBatteryReportAsync, batteryHistory: ReadBatteryHistoryAsync))
        {
            Components.Add(card);
        }
    }


    private async Task SampleLiveAsync()
    {
        if (_sampling)
        {
            return;
        }

        _sampling = true;
        var started = DateTime.UtcNow;
        try
        {
            _sensors ??= await Task.Run(CreateSensors);
            var snapshot = await Task.Run(_sensors.Sample);
            // Le chien de garde d'abord : l'arrêt automatique ne dépend pas de l'affichage.
            if (_watchdog.Record(snapshot, Live.CpuMaxC, IsPawnIoInstalled, EngineHasRights) is > 0 and var failures)
            {
                Maus.Core.Diagnostics.Breadcrumbs.Add($"mesures en direct rétablies après {failures} échec(s)");
            }

            Live.Add(snapshot);
            if (IsTesting && _watchdog.Notice() is { } notice)
            {
                Live.Alarms.Add(notice);
            }

            OnRecordedSample(snapshot);
            Sampled?.Invoke(this, snapshot);
            CheckAdvancedSensors();
            if (DateTime.UtcNow - started > TimeSpan.FromMilliseconds(700))
            {
                Maus.Core.Diagnostics.Breadcrumbs.Add($"mesure en direct lente : {(DateTime.UtcNow - started).TotalSeconds:0.0} s");
            }
        }
        catch (Exception ex)
        {
            // Une mesure impossible ne doit jamais faire tomber la fenêtre. Premier échec d'une série noté (pas un par seconde).
            if (_watchdog.RecordFailure(Maus.Core.Diagnostics.ErrorText.For(ex)) == 1)
            {
                Maus.Core.Diagnostics.Breadcrumbs.Add("mesure en direct en échec (" + ex.GetType().Name + ")");
            }

            Live.Alarms.Clear();
            if (IsTesting || _monitorOpen)
            {
                // Pendant un test, les mesures continuent : l'arrêt automatique en dépend. Si elles ne reviennent pas, le chien
                // de garde arrête le test par prudence.
                Live.Alarms.Add(T("Une mesure en direct a échoué : {0}. MAUS réessaie à chaque mesure ; si les mesures ne reviennent pas d'ici {1:0} s, le test s'arrêtera par prudence.", Maus.Core.Diagnostics.ErrorText.For(ex), ThermalWatchdog.Grace.TotalSeconds));
            }
            else
            {
                _liveTimer.Stop();
                Live.Alarms.Add(T("Les mesures en direct se sont arrêtées : {0}", Maus.Core.Diagnostics.ErrorText.For(ex)));
            }
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
            // Processus lus par le moteur : tous les comptes, chemins complets.
            _processes ??= new ProcessMonitor(new Maus.Core.Engine.Broker.EngineProcessSource(EngineHost.Current), Environment.ProcessorCount);

            var monitor = _processes;
            _lastProcesses = await Task.Run(monitor.Sample);
            ShowProcesses();
        }
        catch (Exception ex)
        {
            _processTimer.Stop();
            DiagnosisStatus = T("La liste des processus n'a pas pu être lue : {0}", Maus.Core.Diagnostics.ErrorText.For(ex));
        }
        finally
        {
            _processSampling = false;
        }
    }

    private const int MaxProcessRows = 200;

    private string _processListNote = string.Empty;

    /// <summary>Dit que la liste des processus est coupée ; vide sinon.</summary>
    public string ProcessListNote
    {
        get => _processListNote;
        private set
        {
            if (SetProperty(ref _processListNote, value))
            {
                OnPropertyChanged(nameof(HasProcessListNote));
            }
        }
    }

    public bool HasProcessListNote => ProcessListNote.Length > 0;

    private void ShowProcesses()
    {
        var selected = SelectedProcess?.Pid;
        var folders = SystemFolders.Current;
        var all = _lastProcesses
            .Where(p => Filter.Length == 0 || p.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.PrivateBytes)
            .ToList();

        // La liste est coupée : un programme discret à l'emplacement inhabituel n'en sort pas, et l'écran dit qu'elle est coupée.
        var samples = all
            .OrderByDescending(p => ProcessRules.TrustOf(p, folders) == ProcessTrust.Unusual)
            .Take(MaxProcessRows)
            .ToList();
        ProcessListNote = all.Count > MaxProcessRows ? T("{0} processus sur {1} : cherchez par nom pour voir les autres.", MaxProcessRows, all.Count) : string.Empty;
        ProcessList.Show(samples, _catalog, folders);
        SelectedProcess = ProcessList.Rows.FirstOrDefault(r => r.Pid == selected);
    }

    private async Task TerminateAsync()
    {
        if (SelectedProcess is not { } process || !process.CanTerminate)
        {
            return;
        }

        if (!_confirm(T("Arrêter ce processus ?"), Maus.Core.Reporting.ConfirmationText.Build(T("« {0} » va être arrêté immédiatement : son travail non enregistré sera perdu.", process.Name), undo: T("aucun ; un programme arrêté ne se relance pas tout seul."))))
        {
            return;
        }

        try
        {
            // Le moteur relit lui-même le processus (nom, heure de lancement) avant de décider.
            DiagnosisStatus = await EngineHost.Current.TerminateProcessAsync(process.Sample.Pid, process.Sample.StartTime, CancellationToken.None)
                ?? T("« {0} » a été arrêté.", process.Name);
        }
        catch (Exception ex)
        {
            DiagnosisStatus = Maus.Core.Diagnostics.ErrorText.For(ex);
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
        LastRestartText = string.Empty;
        OnPropertyChanged(nameof(HasCauses));
        await RefreshActivityAsync();
        try
        {
            var started = DateTimeOffset.Now;
            for (var second = 60; second > 0; second--)
            {
                DiagnosisStatus = T("Mesure en cours : encore {0} s. Utilisez votre PC normalement pendant ce temps.", second);
                await Task.Delay(1000);
            }

            // Les mesures de cette minute seulement : l'historique garde aussi celles d'il y a peut-être des heures.
            var causes = SlownessDiagnosis.Analyze(Live.Samples.Where(s => s.At >= started).ToList(), _lastProcesses, _catalog, Live.CpuMaxC);
            foreach (var cause in causes)
            {
                var moduleId = cause.ModuleId;
                ProcessList.Causes.Add(new CauseViewModel(cause.Title, cause.Explanation, moduleId is null ? string.Empty : T("Voir les constats du module {0}", moduleId),
                    moduleId is null ? null : new AsyncCommand(() =>
                    {
                        OpenModuleFindings?.Invoke(moduleId);
                    })));
            }

            OnPropertyChanged(nameof(HasCauses));

            // Lecture d'appoint : son échec ne retire pas les causes déjà affichées.
            try
            {
                LastRestartText = await Task.Run(() => LastRestart.Read(new WindowsEventLogReader(), new WmiCimReader(), new WindowsRegistryReader()).Text);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LastRestartText = T("La date du dernier vrai redémarrage de Windows n'a pas pu être lue.");
            }

            DiagnosisStatus = T("Diagnostic terminé : voici les causes principales, de la plus importante à la moins importante.") + " " + SlownessDiagnosis.ThresholdNote;
        }
        catch (Exception ex)
        {
            DiagnosisStatus = T("Le diagnostic n'a pas pu aboutir : {0}", Maus.Core.Diagnostics.ErrorText.For(ex));
        }
        finally
        {
            IsDiagnosing = false;
            await RefreshActivityAsync();
        }
    }

    private async Task RunCpuTestAsync()
    {
        if (Busy)
        {
            return;
        }

        await SampleBeforeTestAsync();
        var duration = CpuDuration.Value;
        var mode = CpuMode.Value;
        var heavyWarning = HeavyWarning(CpuMode) + TemperatureCaveat();
        if (!Ask(T("Lancer le test du processeur ?"), Maus.Core.Reporting.ConfirmationText.Build(T("Tous les cœurs vont travailler à 100 % pendant {0} : le PC chauffera et ses ventilateurs accéléreront. Le test s'arrête tout seul si une température dangereuse est atteinte, et à tout moment avec « Arrêter le test ».", CpuDuration.Label) + heavyWarning, reminder: Maus.Core.Legal.Disclaimer.TestReminder)))
        {
            return;
        }

        await RunGuardedTestAsync(status => CpuStatus = status, async cancellationToken =>
        {
            CpuStatus = T("Test en cours…");
            var progress = new Progress<CpuTestProgress>(p =>
            {
                CpuProgress = Math.Min(100, 100 * p.Elapsed.TotalSeconds / duration.TotalSeconds);
                CpuStatus = T("{0:0} s · {1} tours vérifiés · {2} erreur(s)", p.Elapsed.TotalSeconds, p.Rounds, p.Errors);
            });
            var result = await CpuTest.RunAsync(new CpuTestOptions(duration, Environment.ProcessorCount, mode), progress, _watchdog.AbortReason, cancellationToken);
            CpuProgress = result.Aborted ? CpuProgress : 100;
            var history = BenchmarkHistory.CreateDefault();
            var previous = history.Load();
            // Un score par type de charge : ils ne se comparent pas entre eux.
            var entry = new BenchmarkEntry(ScoreTrends.CpuKind(mode), result.Score, result.Stable, DateTimeOffset.Now);
            if (!result.Aborted && result.Verified)
            {
                SaveScore(history, entry);
            }

            var comparison = BenchmarkHistory.CompareToPrevious(previous, entry) is { } delta
                ? " " + T("({0:+0.0;-0.0;0} % par rapport à vos mesures précédentes)", delta)
                : string.Empty;
            Report(result.Aborted ? TestOutcome.Stopped : !result.Verified ? TestOutcome.None : result.Stable ? TestOutcome.Good : TestOutcome.Problem);
            CpuStatus = result.Aborted
                ? T("Test interrompu : {0}", result.AbortReason) + ErrorsBeforeStop(result.Errors)
                : !result.Verified
                    ? T("Le test n'a rien vérifié (durée trop courte, ou arrêt dès le début) : pas de verdict.")
                : result.Stable
                    ? T("Stable : aucune erreur de calcul en {0:0} s. Score : {1:0.0} tours par seconde.", result.Duration.TotalSeconds, result.Score) + comparison
                      + " " + T("Instructions : {0}", CpuStress.Instructions(result.Mode)) + "."
                    : T("INSTABLE : {0} erreur(s) de calcul. Revenez aux réglages d'origine du BIOS (surcadençage, tension) et vérifiez le refroidissement.", result.Errors);
        }, crashLabel: "atelier : test du processeur");
    }

    private async Task RunRamTestAsync()
    {
        if (Busy)
        {
            return;
        }

        await SampleBeforeTestAsync();
        if (Live.Samples.LastOrDefault() is not { MemoryTotalBytes: { } total, MemoryUsedBytes: { } used } sample
            || DateTimeOffset.Now - sample.At > TimeSpan.FromMinutes(1))
        {
            RamStatus = T("MAUS n'a pas pu mesurer la mémoire libre : le test n'est pas lancé. Réessayez dans quelques secondes.");
            return;
        }

        var available = total - used;
        var maximum = MemoryTest.MaximumBytes(Math.Min(available, WindowsSensorSource.AvailableCommitBytes() ?? available), total);
        var bytes = RamSize.Value switch
        {
            0 => maximum,
            < 0 => Math.Min(MemoryTest.SuggestedBytes(available), maximum),
            _ => Math.Min(RamSize.Value, maximum),
        };
        if (!Ask(T("Lancer le test de la mémoire vive ?"), Maus.Core.Reporting.ConfirmationText.Build(T("MAUS va écrire puis relire des motifs sur {0:0.0} Go de mémoire. Fermez vos jeux et programmes lourds pendant le test ; il s'arrête à tout moment avec « Arrêter le test ».", bytes / 1073741824.0), reminder: Maus.Core.Legal.Disclaimer.TestReminder)))
        {
            return;
        }

        await RunGuardedTestAsync(status => RamStatus = status, async cancellationToken =>
        {
            RamStatus = T("Test en cours…");
            var progress = new Progress<MemoryTestProgress>(p =>
            {
                RamProgress = p.Percent;
                RamStatus = T("{0} · {1:0} % · {2} erreur(s)", p.Step, p.Percent, p.Errors);
            });
            var result = await MemoryTest.RunAsync(new MemoryTestOptions(bytes), progress, _watchdog.AbortReason, cancellationToken);
            RamProgress = result.Aborted ? RamProgress : 100;
            if (!result.Aborted && result.Verified)
            {
                SaveScore(BenchmarkHistory.CreateDefault(), new BenchmarkEntry("ram", result.CopyGigabytesPerSecond ?? 0, result.Stable, DateTimeOffset.Now,
                    result.LatencyNanoseconds?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)));
            }

            Report(result.Aborted ? TestOutcome.Stopped : !result.Verified ? TestOutcome.None : result.Stable ? TestOutcome.Good : TestOutcome.Problem);
            RamStatus = result.Aborted
                ? (result.AbortReason is { } reason ? T("Test interrompu : {0}", reason) : T("Test interrompu.")) + ErrorsBeforeStop(result.Errors)
                : !result.Verified
                    ? T("Le test n'a rien vérifié (durée trop courte, ou arrêt dès le début) : pas de verdict.")
                : result.Stable
                    ? T("Aucune erreur sur {0:0.0} Go. Débit de copie : {1:0.0} Go/s · latence : {2:0.0} ns. (Un test sous Windows ne couvre pas la mémoire déjà utilisée : une erreur est un signal fort, l'absence d'erreur n'est pas une preuve absolue.)", result.TestedBytes / 1073741824.0, result.CopyGigabytesPerSecond, result.LatencyNanoseconds)
                    : T("ERREURS : {0} valeur(s) relue(s) différente(s). Revenez au profil mémoire d'origine dans le BIOS (voir Module 10), puis refaites le test ; si les erreurs restent, une barrette est probablement défaillante.", result.Errors);
        }, failure: ex => ex is OutOfMemoryException
            ? T("Windows n'a pas pu réserver cette quantité de mémoire : fermez des applications ou choisissez une quantité plus petite.")
            : null);
    }

    /// <summary>
    /// Message d'un test resté inachevé. Un gel finit toujours par un redémarrage : si le PC n'a pas redémarré depuis le début
    /// du test (<paramref name="bootedAt"/>), c'est MAUS qui s'est fermé, et cela ne dit rien du réglage.
    /// </summary>
    internal static string? DescribeCrash(CoreTestCheckpoint? trace, DateTimeOffset bootedAt) => trace switch
    {
        null => null,
        _ when trace.StartedAt > bootedAt => T("Le dernier test, lancé le {0:g}, ne s'est pas terminé : MAUS s'est fermé pendant le test, sans que le PC redémarre. Cela ne dit rien de vos réglages.", trace.StartedAt.ToLocalTime()),
        { Phase: 2 } => T("Le dernier programme complet, lancé le {0:g}, s'est arrêté brutalement pendant la phase des transitoires (tous les cœurs chargés puis arrêtés en même temps) : le PC a gelé ou redémarré. Le réglage global est trop bas pour les brusques variations de charge : remontez l'ensemble du Curve Optimizer de 2 ou 3 points (ou réduisez l'undervolt), puis refaites le programme.", trace.StartedAt.ToLocalTime()),
        { Phase: 1 } => T("Le dernier programme complet, lancé le {0:g}, s'est arrêté brutalement pendant la phase cœur par cœur, sur le cœur {1} : le PC a gelé ou redémarré. Le réglage de ce cœur (Curve Optimizer ou undervolt) est probablement trop bas : remontez-le de 2 ou 3 points (par exemple de −15 à −12), puis refaites le programme.", trace.StartedAt.ToLocalTime(), trace.Core),
        _ => T("Le dernier test cœur par cœur, lancé le {0:g}, s'est arrêté brutalement pendant le cœur {1} : le PC a gelé ou redémarré. Le réglage de ce cœur (Curve Optimizer ou undervolt) est probablement trop bas : remontez-le de 2 ou 3 points (par exemple de −15 à −12), puis refaites le test.", trace.StartedAt.ToLocalTime(), trace.Core),
    };

    private async Task RunCoreTestAsync()
    {
        if (Busy)
        {
            return;
        }

        // Occupé dès le clic : la lecture des cœurs et la confirmation prennent du temps, aucun autre test ne doit partir.
        await WithPreparingAsync(CoreTestAsync);
    }

    private async Task CoreTestAsync()
    {
        CrashNotice = null;
        if (await ReadCoresAsync(status => CoreStatus = status) is not { } cores)
        {
            return;
        }

        // Mesure fraîche : ce que la confirmation dit de l'arrêt sur la chaleur en dépend.
        await SampleLiveAsync();
        var plan = CoreCycleTest.Plan(CoreDuration.Value, cores.Count) with { Load = CoreMode.Value };
        if (!_confirm(T("Lancer le test cœur par cœur ?"), Maus.Core.Reporting.ConfirmationText.Build(T("MAUS va faire travailler les {0} cœurs un par un, à leur fréquence maximale, avec des à-coups et des pauses, pendant {1}. Si le PC gèle ou redémarre, c'est que le réglage du cœur testé est trop bas : MAUS vous dira lequel au prochain lancement. Enregistrez votre travail avant de commencer.", cores.Count, CoreDuration.Label) + HeavyWarning(CoreMode) + TemperatureCaveat(), reminder: Maus.Core.Legal.Disclaimer.TestReminder)))
        {
            return;
        }

        await RunGuardedTestAsync(status => CoreStatus = status, async cancellationToken =>
        {
            CoreChips.Clear();
            var name = CoreNamer(cores);
            CoreStatus = T("Test en cours…");
            var progress = new Progress<CoreCycleProgress>(p =>
            {
                CoreProgress = p.Percent;
                CoreStatus = T("{0} ({1}/{2}) · {3} · {4:0} % · {5} cœur(s) en erreur", name(p.Core), p.Position + 1, p.CoreCount, p.Phase, p.Percent, p.FailedCores);
            });
            var logs = new Maus.Core.Platform.WindowsEventLogReader();
            var result = await CoreCycleTest.RunAsync(_topology, _checkpoint, plan, progress, _watchdog.AbortReason, since => WheaEvents.CountSince(logs, since), cancellationToken);
            CoreProgress = result.Aborted ? CoreProgress : 100;
            AddCoreChips(result.Cores, name);
            var failing = result.Cores.Where(c => !c.Stable).Select(c => name(c.Core)).ToList();
            var whea = result.WheaEvents is > 0 ? " " + T("{0} erreur(s) matérielle(s) WHEA pendant le test : même sans plantage, le processeur est à la limite.", result.WheaEvents) : string.Empty;
            var pinning = result.Cores.Any(c => !c.Pinned) ? " " + T("Windows a refusé de fixer le test sur certains cœurs : le résultat par cœur est moins fiable.") : string.Empty;
            Report(result.Aborted ? TestOutcome.Stopped : failing.Count > 0 || result.WheaEvents is > 0 ? TestOutcome.Problem : TestOutcome.Good);
            CoreStatus = result.Aborted
                ? T("Test interrompu : {0}", result.AbortReason) + whea
                : failing.Count > 0
                    ? T("Cœur(s) instable(s) : {0}. Remontez le Curve Optimizer de ce(s) cœur(s) de 2 ou 3 points (par exemple de −15 à −12) ou réduisez l'undervolt, puis refaites le test. Si rien ne change, revenez aux réglages d'origine du BIOS.", string.Join(", ", failing)) + whea + pinning
                    : result.WheaEvents is > 0
                        ? T("Aucune erreur de calcul, mais le processeur a signalé des erreurs matérielles.") + whea + " " + T("Remontez légèrement le Curve Optimizer ou l'undervolt, puis refaites le test.")
                        : result.Untested.Count > 0
                            ? T("Aucune erreur sur les cœurs testés, mais {0} cœur(s) n'ont pas été testés ({1}) : le test était trop court pour faire le tour. Allongez-le avant de conclure.", result.Untested.Count, string.Join(", ", result.Untested.Select(name))) + pinning
                            : result.WheaEvents is null
                                ? T("Aucune erreur de calcul ni gel sur {0} cœur(s).", result.Cores.Count) + " " + T("Le journal des erreurs matérielles (WHEA) n'a pas pu être lu : regardez-le dans l'Observateur d'événements avant de conclure.") + pinning
                                : T("Tous les cœurs ont tenu : {0} cœur(s), aucune erreur de calcul, aucun gel, aucune erreur matérielle WHEA.", result.Cores.Count) + pinning + " "
                                    + T("Pour valider un réglage, laissez tourner au moins 20 minutes (idéalement une nuit), puis utilisez le PC normalement quelques jours : certains gels n'arrivent qu'au repos.");
        });
    }

    private async Task LoadGpuAdaptersAsync()
    {
        _gpuAdaptersLoaded = true;
        try
        {
            var adapters = await Task.Run(_gpuProvider.Adapters);
            VramAdapters.Clear();
            foreach (var adapter in adapters.Where(a => a.DedicatedBytes >= 512L << 20).OrderByDescending(a => a.DedicatedBytes))
            {
                VramAdapters.Add(new(T("{0} · {1:0.#} Go", adapter.Name, adapter.DedicatedBytes / 1073741824.0), adapter));
            }

            VramAdapter = VramAdapters.FirstOrDefault();
            VramStatus = VramAdapters.Count == 0
                ? T("Aucune carte graphique avec de la mémoire dédiée : une puce graphique intégrée utilise la mémoire vive, déjà couverte par le test de la RAM.")
                : string.Empty;
        }
        catch (Exception ex)
        {
            VramStatus = T("Direct3D ne répond pas sur ce PC : {0}", Maus.Core.Diagnostics.ErrorText.For(ex));
        }
    }

    private async Task RunVramTestAsync()
    {
        if (Busy || VramAdapter is not { Value: var adapter })
        {
            return;
        }

        var maximum = VramTest.MaximumBytes(adapter.DedicatedBytes, _gpuProvider.AvailableBytes(adapter));
        var bytes = VramSize.Value switch
        {
            0 => maximum,
            < 0 => Math.Min(VramTest.SuggestedBytes(adapter.DedicatedBytes), maximum),
            _ => Math.Min(VramSize.Value, maximum),
        };
        if (!Ask(T("Lancer le test de la mémoire vidéo ?"), Maus.Core.Reporting.ConfirmationText.Build(T("MAUS va écrire puis relire des motifs sur {0:0.0} Go de la mémoire de « {1} ». Fermez vos jeux et applications 3D pendant le test ; il s'arrête à tout moment avec « Arrêter le test ».", bytes / 1073741824.0, adapter.Name), reminder: Maus.Core.Legal.Disclaimer.TestReminder)))
        {
            return;
        }

        await RunGuardedTestAsync(status => VramStatus = status, async cancellationToken =>
        {
            VramStatus = T("Test en cours…");
            var progress = new Progress<VramTestProgress>(p =>
            {
                VramProgress = p.Percent;
                VramStatus = T("{0} · {1:0} % · {2} erreur(s)", p.Step, p.Percent, p.Errors);
            });
            var result = await VramTest.RunAsync(_gpuProvider, adapter, new VramTestOptions(bytes), progress, _watchdog.AbortReason, cancellationToken);
            VramProgress = result.Aborted ? VramProgress : 100;
            if (!result.Aborted && result.Verified)
            {
                SaveScore(BenchmarkHistory.CreateDefault(), new BenchmarkEntry(ScoreTrends.VramKind(adapter.Name), result.ReadbackGigabytesPerSecond ?? 0, result.Stable, DateTimeOffset.Now, adapter.Name));
            }

            Report(result.Aborted ? TestOutcome.Stopped : !result.Verified ? TestOutcome.None : result.Stable ? TestOutcome.Good : TestOutcome.Problem);
            VramStatus = result.Aborted
                ? T("Test interrompu : {0}", result.AbortReason) + ErrorsBeforeStop(result.Errors)
                : !result.Verified
                    ? T("Le test n'a rien vérifié (durée trop courte, ou arrêt dès le début) : pas de verdict.")
                : result.Stable
                    ? T("Aucune erreur sur {0:0.0} Go de mémoire vidéo. Débit de relecture vers le processeur : {1:0.0} Go/s.", result.TestedBytes / 1073741824.0, result.ReadbackGigabytesPerSecond)
                        + (result.CardWasFull ? " " + T("La carte n'avait plus de place libre : seule la mémoire disponible a été testée.") : string.Empty)
                        + " " + T("(La mémoire déjà utilisée par l'affichage n'est pas couverte : une erreur est un signal fort, l'absence d'erreur n'est pas une preuve absolue.)")
                    : T("ERREURS : {0} valeur(s) relue(s) différente(s) dans la mémoire vidéo. Revenez aux fréquences d'origine de la carte (outil de surcadençage, voir Module 15), vérifiez sa température, puis refaites le test ; si les erreurs restent, la carte est probablement défaillante.", result.Errors);
        }, failure: ex => T("Direct3D ne répond pas sur ce PC : {0}", Maus.Core.Diagnostics.ErrorText.For(ex)), graphics: true);
    }

    /// <summary>
    /// Déroulé commun d'un test, après sa confirmation : démarrage (chien de garde compris), erreur affichée à la place du verdict,
    /// et fin du test quoi qu'il arrive. <paramref name="failure"/> donne un message propre à ce test, <c>null</c> pour le message commun.
    /// </summary>
    private async Task RunGuardedTestAsync(Action<string> status, Func<CancellationToken, Task> body, string? crashLabel = null, Func<Exception, string?>? failure = null, bool graphics = false)
    {
        using var cancellation = new CancellationTokenSource();
        try
        {
            await StartTestAsync(cancellation, graphics);
            await body(cancellation.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (crashLabel is not null)
            {
                Maus.Core.Diagnostics.CrashLog.Write(ex, crashLabel);
            }

            status(failure?.Invoke(ex) ?? T("Le test n'a pas pu se dérouler : {0}", Maus.Core.Diagnostics.ErrorText.For(ex)));
        }
        finally
        {
            await StopTestAsync();
        }
    }

    /// <summary>Occupé pendant toute la préparation d'un test : aucun autre ne doit partir.</summary>
    private async Task WithPreparingAsync(Func<Task> prepare)
    {
        SetPreparing(true);
        try
        {
            await prepare();
        }
        finally
        {
            SetPreparing(false);
        }
    }

    /// <summary>Cœurs du processeur, ou <c>null</c> si Windows ne les décrit pas : <paramref name="status"/> dit alors pourquoi.</summary>
    private async Task<IReadOnlyList<CpuCore>?> ReadCoresAsync(Action<string> status)
    {
        try
        {
            var cores = await Task.Run(_topology.Cores);
            if (cores.Count > 0)
            {
                return cores;
            }

            status(T("Windows n'a pas décrit les cœurs du processeur : le test cœur par cœur est impossible sur ce PC."));
        }
        catch (Exception ex)
        {
            status(T("Le test n'a pas pu se dérouler : {0}", Maus.Core.Diagnostics.ErrorText.For(ex)));
        }

        return null;
    }

    /// <summary>Une pastille par cœur : figé, en erreur, stable ou non testé.</summary>
    private void AddCoreChips(IEnumerable<CoreVerdict> verdicts, Func<int, string> name)
    {
        foreach (var verdict in verdicts)
        {
            CoreChips.Add(new CoreChipViewModel(
                verdict.Froze ? T("{0} · figé", name(verdict.Core))
                    : verdict.Errors > 0 ? T("{0} · {1} erreur(s)", name(verdict.Core), verdict.Errors)
                    : verdict.Rounds > 0 ? T("{0} · stable", name(verdict.Core)) : name(verdict.Core),
                verdict.Froze || verdict.Errors > 0 ? Controls.Palette.Red : verdict.Rounds > 0 ? Controls.Palette.Green : Controls.Palette.Grey));
        }
    }

    private async Task StartTestAsync(CancellationTokenSource cancellation, bool graphics = false)
    {
        _stopTest = cancellation.Cancel;
        Report(TestOutcome.None);
        // Pas de mise en veille programmée pendant un test (appelé et levé sur le fil de l'interface).
        KeepAwake.Begin();
        _graphicsTest = graphics;
        OnPropertyChanged(nameof(TestLoad));
        OnPropertyChanged(nameof(TestTemperature));
        // Avant la première mesure du test, qui compte déjà ; même état de la température qu'à la confirmation.
        _watchdog.Begin(graphics ? ThermalTarget.Gpu : ThermalTarget.Cpu, CpuTemperatureState());
        IsTesting = true;
        await RefreshActivityAsync();
    }

    private async Task StopTestAsync()
    {
        _watchdog.End();
        IsTesting = false;
        _stopTest = null;
        KeepAwake.End();
        LoadScoreHistory();
        await RefreshActivityAsync();
    }

    /// <summary>
    /// Enregistre le score d'un test. Un historique inaccessible (disque plein, fichier verrouillé) ne doit jamais remplacer le
    /// verdict du test qui vient de finir par « le test n'a pas pu se dérouler ».
    /// </summary>
    private static void SaveScore(BenchmarkHistory history, BenchmarkEntry entry)
    {
        try
        {
            history.Add(entry);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Maus.Core.Diagnostics.Breadcrumbs.Add("Historique des scores non enregistré (" + ex.GetType().Name + ")");
        }
    }

    private void OpenSearch(string query) => ShellLauncher.OpenUrl(WebSearch.Build(SearchEngineChoice.Value, query));

    /// <summary>Capacité passée de la batterie (powercfg, en XML), lue ici avec les droits de l'interface : le moteur n'y prend aucune part.</summary>
    private static Task<Maus.Core.Workshop.BatteryHistoryResult> ReadBatteryHistoryAsync() =>
        Task.Run(() => Maus.Core.Workshop.BatteryReport.ReadHistoryAsync(new Maus.Core.Platform.ReadOnlyCommandRunner(), CancellationToken.None));

    /// <summary>Rapport de batterie de Windows (powercfg), fait ici avec les droits de l'interface, puis ouvert dans le navigateur.</summary>
    private async Task OpenBatteryReportAsync()
    {
        var report = await Task.Run(() => Maus.Core.Workshop.BatteryReport.CreateAsync(new Maus.Core.Platform.ReadOnlyCommandRunner(), CancellationToken.None));
        if (report.File is null)
        {
            Views.QuestionWindow.Inform("MAUS", report.Refusal ?? T("Windows n'a pas pu faire le rapport de batterie (aucune batterie détectée, ou rapport refusé)."), Views.QuestionTone.Information);
            return;
        }

        ShellLauncher.OpenFile(report.File);
    }
}
