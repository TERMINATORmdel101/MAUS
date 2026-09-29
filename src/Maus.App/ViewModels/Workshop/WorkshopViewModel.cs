using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using Maus.Core;
using Maus.Core.Platform;
using Maus.Core.Preferences;
using Maus.Core.Workshop;
using Maus.Core.Workshop.Memory;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>
/// Atelier matériel : « Mon PC », « En direct », « Processus », « Tests ». Les mesures ne tournent que lorsque
/// l'atelier est affiché (ou qu'un test est en cours), pour ne rien consommer le reste du temps.
/// </summary>
public sealed partial class WorkshopViewModel : ObservableObject
{
    public const int SectionPc = 0;
    public const int SectionLive = 1;
    public const int SectionProcesses = 2;
    public const int SectionTests = 3;
    public const int SectionStorage = 4;

    private readonly Func<string, string, bool> _confirm;
    private readonly Func<AuditContext?> _context;
    private readonly IPreferencesStore _preferences;
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _processTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ProcessCatalog _catalog = ProcessCatalog.Default;
    private ISensorSource? _sensors;
    private ProcessMonitor? _processes;
    private bool _isActive;
    private bool _monitorOpen;
    private int _section;
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

    public WorkshopViewModel(Func<string, string, bool> confirm, Func<AuditContext?> context, IPreferencesStore preferences)
    {
        _confirm = confirm;
        _context = context;
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
        VramSizes =
        [
            new(T("Automatique : 60 % de la mémoire de la carte"), 0L),
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
        _crashNotice = DescribeCrash(_checkpoint.Load());

        SearchProcessCommand = new AsyncCommand(() => Run(() => { if (SelectedProcess is { } p) { OpenSearch(WebSearch.ForProcess(p.Name)); } }));
        ShowProcessCommand = new AsyncCommand(() => Run(() => { if (SelectedProcess?.Sample.Path is { } path) { ShellLauncher.ShowInFolder(path); } }));
        TerminateCommand = new AsyncCommand(TerminateAsync);
        DiagnoseCommand = new AsyncCommand(DiagnoseAsync);
        StartCpuTestCommand = new AsyncCommand(RunCpuTestAsync);
        StartRamTestCommand = new AsyncCommand(RunRamTestAsync);
        StartVramTestCommand = new AsyncCommand(RunVramTestAsync);
        StartCoreTestCommand = new AsyncCommand(RunCoreTestAsync);
        ScanCommand = new AsyncCommand(ScanAsync);
        UpCommand = new AsyncCommand(() => Run(GoUp));
        OpenCurrentFolderCommand = new AsyncCommand(() => Run(() => { if (_current is { } node) { ShellLauncher.OpenFolder(node.Path); } }));
        StorageSettingsCommand = new AsyncCommand(() => Run(() => ShellLauncher.OpenSettings("ms-settings:storagesense")));
        StopScanCommand = new AsyncCommand(() => Run(() => _stopScan?.Invoke()));
        StartDiskTestCommand = new AsyncCommand(RunDiskTestAsync);
        DiskSizes =
        [
            new(T("1 Go (rapide)"), 1L << 30),
            new(T("4 Go (plus fiable)"), 4L << 30),
        ];
        _diskSize = DiskSizes[0];
        DismissCrashCommand = new AsyncCommand(() => Run(() =>
        {
            _checkpoint.Clear();
            CrashNotice = null;
        }));
        StopTestCommand = new AsyncCommand(() => Run(() => _stopTest?.Invoke()));
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
            return Task.CompletedTask;
        }

        var text = T("Fiche du PC relevée par MAUS {0} le {1}", Maus.Core.AppVersion.Display, DateTime.Now.ToString("g", Culture)) +
            Environment.NewLine + Environment.NewLine + ComponentCardViewModel.ToText(Components);
        try
        {
            System.Windows.Clipboard.SetText(Maus.Core.Reporting.PrivacyFilter.ForCurrentUser().Mask(text));
            InventoryStatus = T("Fiche copiée (nom d'utilisateur, nom du PC et e-mails masqués) : collez-la avec Ctrl+V.");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            InventoryStatus = T("Le presse-papiers est occupé : réessayez.");
        }

        return Task.CompletedTask;
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
                        _preferences.Update(p => p with { SearchEngine = value.Value });
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
    /// La température du processeur n'est lue qu'avec PawnIO : sans elle, l'arrêt automatique ne se fait pas sur la chaleur du
    /// processeur. Dernière mesure si les capteurs ont déjà tourné, sinon présence du pilote. Vide si la température est lue.
    /// </summary>
    private string TemperatureCaveat()
    {
        var unreadable = Live.Samples.LastOrDefault() is { } sample ? sample.CpuTemperatureC is null : !IsPawnIoInstalled;
        return unreadable
            ? Environment.NewLine + Environment.NewLine + T("Sans le pilote PawnIO, MAUS ne lit pas la température du processeur : il ne peut pas arrêter le test sur ce critère. Le processeur se protège lui-même en ralentissant, mais installez PawnIO (onglet En direct) pour suivre sa température.")
            : string.Empty;
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

    private bool Busy => IsTesting || _preparing;

    private void SetPreparing(bool value)
    {
        _preparing = value;
        OnPropertyChanged(nameof(IsIdle));
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

    /// <summary>Arrête les mesures et un test en cours (fermeture de la fenêtre, changement de langue).</summary>
    public void Stop()
    {
        _isActive = false;
        _monitorOpen = false;
        if (_stopTest is not null)
        {
            // MAUS fermé pendant un test : arrêt voulu, pas un gel. La trace est effacée tout de suite, car la fermeture
            // n'attend pas la fin du test et laisserait croire au prochain lancement que le PC a planté.
            _checkpoint.Clear();
        }

        _stopTest?.Invoke();
        _stopNet?.Invoke();
        _stopScan?.Invoke();
        _liveTimer.Stop();
        _processTimer.Stop();
        Appearance.AppearanceManager.Changed -= OnAppearanceChanged;

        // Referme le pilote (LibreHardwareMonitor) une fois la dernière mesure terminée.
        var sensors = _sensors;
        _sensors = null;
        if (sensors is not null)
        {
            _ = Task.Run(async () =>
            {
                while (_sampling)
                {
                    await Task.Delay(50);
                }

                sensors.Dispose();
            });
        }
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
                _ = RefreshActivityAsync();
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

    private async Task RefreshActivityAsync()
    {
        if (IsActive && Section == SectionPc && !_inventoryLoaded)
        {
            await LoadInventoryAsync();
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
            var context = _context() ?? await Task.Run(AuditContext.CreateDefault);
            var inventory = await Task.Run(() => HardwareInventoryReader.Read(context, new X86CpuIdSource(), new WindowsNvmlSource()));
            _details = await Task.Run(() => ReadDetails(context));
            var limits = SafetyLimits.Load();
            ShowInventory(inventory, limits, null);
            InventoryStatus = T("Lu directement dans le matériel et le BIOS, sans pilote. « Rechercher la fiche » ouvre votre navigateur ; MAUS n'envoie rien de lui-même.");

            // Profils XMP / EXPO des barrettes : puce SPD, lisible seulement avec PawnIO et en administrateur (lecture seule).
            if (PawnIo.State(new WindowsRegistryReader()).Installed && ProcessElevation.IsElevated())
            {
                InventoryStatus = T("Lecture des puces SPD des barrettes (profils XMP / EXPO)…");
                var spd = await Task.Run(ReadSpdModules);
                ShowInventory(inventory, limits, spd);
                InventoryStatus = spd is null
                    ? T("Profils XMP / EXPO illisibles : la carte mère bloque peut-être l'accès au bus SMBus. Le reste de la fiche est lu sans pilote.")
                    : T("Lu dans le matériel et le BIOS ; profils XMP / EXPO lus dans la puce SPD de chaque barrette par PawnIO, sans rien modifier.");
            }
        }
        catch (Exception ex)
        {
            _inventoryLoaded = false;
            InventoryStatus = T("La lecture du matériel a échoué : {0}", ex.Message);
        }
        finally
        {
            IsReadingInventory = false;
        }
    }

    private void ShowInventory(HardwareInventory inventory, SafetyLimits limits, IReadOnlyList<SpdModule>? spd)
    {
        Components.Clear();
        foreach (var card in ComponentCardViewModel.From(inventory, limits, reference => OpenSearch(WebSearch.ForComponent(reference)), spd, _details))
        {
            Components.Add(card);
        }
    }

    /// <summary>
    /// Windows, écrans, réseau, son et emplacements mémoire ; <c>null</c> en cas d'échec imprévu : les cartes des composants
    /// s'affichent quand même, seules ces cartes-là manquent.
    /// </summary>
    private static MachineDetails? ReadDetails(AuditContext context)
    {
        try
        {
            return MachineDetailsReader.Read(context);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Maus.Core.Diagnostics.Breadcrumbs.Add("Mon PC : informations supplémentaires illisibles (" + ex.GetType().Name + ")");
            return null;
        }
    }

    /// <summary>Puces SPD décodées, ou <c>null</c> si aucune n'a pu être lue.</summary>
    private static List<SpdModule>? ReadSpdModules()
    {
        try
        {
            var modules = new PawnIoSpdSource().ReadAll().Select(SpdDecoder.Decode).OfType<SpdModule>().ToList();
            return modules.Count > 0 ? modules : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
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
            Live.Add(snapshot);
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
            // Une mesure impossible ne doit jamais faire tomber la fenêtre : on s'arrête et on le dit.
            _liveTimer.Stop();
            Live.Alarms.Clear();
            Live.Alarms.Add(T("Les mesures en direct se sont arrêtées : {0}", ex.Message));
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
        catch (Exception ex)
        {
            _processTimer.Stop();
            DiagnosisStatus = T("La liste des processus n'a pas pu être lue : {0}", ex.Message);
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
        catch (Exception ex)
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
        catch (Exception ex)
        {
            DiagnosisStatus = T("Le diagnostic n'a pas pu aboutir : {0}", ex.Message);
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

        var duration = CpuDuration.Value;
        var mode = CpuMode.Value;
        var heavyWarning = HeavyWarning(CpuMode) + TemperatureCaveat();
        if (!_confirm(T("Lancer le test du processeur ?"), T("Tous les cœurs vont travailler à 100 % pendant {0} : le PC chauffera et ses ventilateurs accéléreront. Le test s'arrête tout seul si une température dangereuse est atteinte, et à tout moment avec « Arrêter le test ».", CpuDuration.Label) + heavyWarning + Environment.NewLine + Environment.NewLine + Maus.Core.Legal.Disclaimer.TestReminder + Environment.NewLine + Environment.NewLine + T("Continuer ?")))
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
            var result = await CpuTest.RunAsync(new CpuTestOptions(duration, Environment.ProcessorCount, mode), progress, () => Live.DangerAlarm, cancellation.Token);
            CpuProgress = 100;
            var history = BenchmarkHistory.CreateDefault();
            var previous = history.Load();
            // Un score par type de charge : ils ne se comparent pas entre eux.
            var entry = new BenchmarkEntry(ScoreTrends.CpuKind(mode), result.Score, result.Stable, DateTimeOffset.Now);
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
                      + " " + T("Instructions : {0}", CpuStress.Instructions(result.Mode)) + "."
                    : T("INSTABLE : {0} erreur(s) de calcul. Revenez aux réglages d'origine du BIOS (surcadençage, tension) et vérifiez le refroidissement.", result.Errors);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CpuStatus = T("Le test n'a pas pu se dérouler : {0}", ex.Message);
        }
        finally
        {
            await StopTestAsync();
        }
    }

    private async Task RunRamTestAsync()
    {
        if (Busy)
        {
            return;
        }

        var available = Live.Samples.LastOrDefault() is { MemoryTotalBytes: { } total, MemoryUsedBytes: { } used } ? total - used : 4L << 30;
        var bytes = RamSize.Value == 0 ? MemoryTest.SuggestedBytes(available) : Math.Min(RamSize.Value, Math.Max(256L << 20, available - (512L << 20)));
        if (!_confirm(T("Lancer le test de la mémoire vive ?"), T("MAUS va écrire puis relire des motifs sur {0:0.0} Go de mémoire. Fermez vos jeux et programmes lourds pendant le test ; il s'arrête à tout moment avec « Arrêter le test ».", bytes / 1073741824.0) + Environment.NewLine + Environment.NewLine + Maus.Core.Legal.Disclaimer.TestReminder + Environment.NewLine + Environment.NewLine + T("Continuer ?")))
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
            var result = await MemoryTest.RunAsync(new MemoryTestOptions(bytes), progress, () => Live.DangerAlarm, cancellation.Token);
            RamProgress = 100;
            if (!result.Aborted)
            {
                BenchmarkHistory.CreateDefault().Add(new BenchmarkEntry("ram", result.CopyGigabytesPerSecond ?? 0, result.Stable, DateTimeOffset.Now,
                    result.LatencyNanoseconds?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)));
            }

            RamStatus = result.Aborted
                ? result.AbortReason is { } reason ? T("Test interrompu : {0}", reason) : T("Test interrompu.")
                : result.Stable
                    ? T("Aucune erreur sur {0:0.0} Go. Débit de copie : {1:0.0} Go/s · latence : {2:0.0} ns. (Un test sous Windows ne couvre pas la mémoire déjà utilisée : une erreur est un signal fort, l'absence d'erreur n'est pas une preuve absolue.)", result.TestedBytes / 1073741824.0, result.CopyGigabytesPerSecond, result.LatencyNanoseconds)
                    : T("ERREURS : {0} valeur(s) relue(s) différente(s). Revenez au profil mémoire d'origine dans le BIOS (voir Module 10), puis refaites le test ; si les erreurs restent, une barrette est probablement défaillante.", result.Errors);
        }
        catch (OutOfMemoryException)
        {
            RamStatus = T("Windows n'a pas pu réserver cette quantité de mémoire : fermez des applications ou choisissez une quantité plus petite.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RamStatus = T("Le test n'a pas pu se dérouler : {0}", ex.Message);
        }
        finally
        {
            await StopTestAsync();
        }
    }

    private static string? DescribeCrash(CoreTestCheckpoint? trace) => trace switch
    {
        null => null,
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
        SetPreparing(true);
        try
        {
            await CoreTestAsync();
        }
        finally
        {
            SetPreparing(false);
        }
    }

    private async Task CoreTestAsync()
    {
        IReadOnlyList<CpuCore> cores;
        try
        {
            cores = await Task.Run(_topology.Cores);
        }
        catch (Exception ex)
        {
            CoreStatus = T("Le test n'a pas pu se dérouler : {0}", ex.Message);
            return;
        }

        if (cores.Count == 0)
        {
            CoreStatus = T("Windows n'a pas décrit les cœurs du processeur : le test cœur par cœur est impossible sur ce PC.");
            return;
        }

        var plan = CoreCycleTest.Plan(CoreDuration.Value, cores.Count) with { Load = CoreMode.Value };
        if (!_confirm(T("Lancer le test cœur par cœur ?"), T("MAUS va faire travailler les {0} cœurs un par un, à leur fréquence maximale, avec des à-coups et des pauses, pendant {1}. Si le PC gèle ou redémarre, c'est que le réglage du cœur testé est trop bas : MAUS vous dira lequel au prochain lancement. Enregistrez votre travail avant de commencer.", cores.Count, CoreDuration.Label) + HeavyWarning(CoreMode) + TemperatureCaveat() + Environment.NewLine + Environment.NewLine + Maus.Core.Legal.Disclaimer.TestReminder + Environment.NewLine + Environment.NewLine + T("Continuer ?")))
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        await StartTestAsync(cancellation);
        CoreChips.Clear();
        var hybrid = cores.Select(c => c.EfficiencyClass).Distinct().Count() > 1;
        var topClass = cores.Max(c => c.EfficiencyClass);
        string Name(int index)
        {
            var core = cores.First(c => c.Index == index);
            return hybrid ? T("Cœur {0} ({1})", index, core.EfficiencyClass == topClass ? "P" : "E") : T("Cœur {0}", index);
        }

        try
        {
            CoreStatus = T("Test en cours…");
            var progress = new Progress<CoreCycleProgress>(p =>
            {
                CoreProgress = p.Percent;
                CoreStatus = T("{0} ({1}/{2}) · {3} · {4:0} % · {5} cœur(s) en erreur", Name(p.Core), p.Position + 1, p.CoreCount, p.Phase, p.Percent, p.FailedCores);
            });
            var logs = new Maus.Core.Platform.WindowsEventLogReader();
            var result = await CoreCycleTest.RunAsync(_topology, _checkpoint, plan, progress, () => Live.DangerAlarm, since => WheaEvents.CountSince(logs, since), cancellation.Token);
            CoreProgress = 100;
            foreach (var verdict in result.Cores)
            {
                CoreChips.Add(new CoreChipViewModel(
                    verdict.Froze ? T("{0} · figé", Name(verdict.Core))
                        : verdict.Errors > 0 ? T("{0} · {1} erreur(s)", Name(verdict.Core), verdict.Errors)
                        : verdict.Rounds > 0 ? T("{0} · stable", Name(verdict.Core)) : Name(verdict.Core),
                    verdict.Froze || verdict.Errors > 0 ? Controls.Palette.Red : verdict.Rounds > 0 ? Controls.Palette.Green : Controls.Palette.Grey));
            }

            var failing = result.Cores.Where(c => !c.Stable).Select(c => Name(c.Core)).ToList();
            var whea = result.WheaEvents is > 0 ? " " + T("{0} erreur(s) matérielle(s) WHEA pendant le test : même sans plantage, le processeur est à la limite.", result.WheaEvents) : string.Empty;
            var pinning = result.Cores.Any(c => !c.Pinned) ? " " + T("Windows a refusé de fixer le test sur certains cœurs : le résultat par cœur est moins fiable.") : string.Empty;
            CoreStatus = result.Aborted
                ? T("Test interrompu : {0}", result.AbortReason) + whea
                : failing.Count > 0
                    ? T("Cœur(s) instable(s) : {0}. Remontez le Curve Optimizer de ce(s) cœur(s) de 2 ou 3 points (par exemple de −15 à −12) ou réduisez l'undervolt, puis refaites le test. Si rien ne change, revenez aux réglages d'origine du BIOS.", string.Join(", ", failing)) + whea + pinning
                    : result.WheaEvents is > 0
                        ? T("Aucune erreur de calcul, mais le processeur a signalé des erreurs matérielles.") + whea + " " + T("Remontez légèrement le Curve Optimizer ou l'undervolt, puis refaites le test.")
                        : T("Tous les cœurs ont tenu : {0} cœur(s), aucune erreur de calcul, aucun gel, aucune erreur matérielle WHEA.", result.Cores.Count) + pinning + " "
                            + T("Pour valider un réglage, laissez tourner au moins 20 minutes (idéalement une nuit), puis utilisez le PC normalement quelques jours : certains gels n'arrivent qu'au repos.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CoreStatus = T("Le test n'a pas pu se dérouler : {0}", ex.Message);
        }
        finally
        {
            await StopTestAsync();
        }
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
            VramStatus = T("Direct3D ne répond pas sur ce PC : {0}", ex.Message);
        }
    }

    private async Task RunVramTestAsync()
    {
        if (Busy || VramAdapter is not { Value: var adapter })
        {
            return;
        }

        var ceiling = Math.Max(256L << 20, adapter.DedicatedBytes - (512L << 20));
        var bytes = VramSize.Value == 0 ? VramTest.SuggestedBytes(adapter.DedicatedBytes) : Math.Min(VramSize.Value, ceiling);
        if (!_confirm(T("Lancer le test de la mémoire vidéo ?"), T("MAUS va écrire puis relire des motifs sur {0:0.0} Go de la mémoire de « {1} ». Fermez vos jeux et applications 3D pendant le test ; il s'arrête à tout moment avec « Arrêter le test ».", bytes / 1073741824.0, adapter.Name) + Environment.NewLine + Environment.NewLine + Maus.Core.Legal.Disclaimer.TestReminder + Environment.NewLine + Environment.NewLine + T("Continuer ?")))
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        await StartTestAsync(cancellation, graphics: true);
        try
        {
            VramStatus = T("Test en cours…");
            var progress = new Progress<VramTestProgress>(p =>
            {
                VramProgress = p.Percent;
                VramStatus = T("{0} · {1:0} % · {2} erreur(s)", p.Step, p.Percent, p.Errors);
            });
            var result = await VramTest.RunAsync(_gpuProvider, adapter, new VramTestOptions(bytes), progress, () => Live.DangerAlarm, cancellation.Token);
            VramProgress = 100;
            if (!result.Aborted)
            {
                BenchmarkHistory.CreateDefault().Add(new BenchmarkEntry("vram", result.ReadbackGigabytesPerSecond ?? 0, result.Stable, DateTimeOffset.Now, adapter.Name));
            }

            VramStatus = result.Aborted
                ? T("Test interrompu : {0}", result.AbortReason)
                : result.Stable
                    ? T("Aucune erreur sur {0:0.0} Go de mémoire vidéo. Débit de relecture vers le processeur : {1:0.0} Go/s.", result.TestedBytes / 1073741824.0, result.ReadbackGigabytesPerSecond)
                        + (result.CardWasFull ? " " + T("La carte n'avait plus de place libre : seule la mémoire disponible a été testée.") : string.Empty)
                        + " " + T("(La mémoire déjà utilisée par l'affichage n'est pas couverte : une erreur est un signal fort, l'absence d'erreur n'est pas une preuve absolue.)")
                    : T("ERREURS : {0} valeur(s) relue(s) différente(s) dans la mémoire vidéo. Revenez aux fréquences d'origine de la carte (outil de surcadençage, voir Module 15), vérifiez sa température, puis refaites le test ; si les erreurs restent, la carte est probablement défaillante.", result.Errors);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            VramStatus = T("Direct3D ne répond pas sur ce PC : {0}", ex.Message);
        }
        finally
        {
            await StopTestAsync();
        }
    }

    private async Task StartTestAsync(CancellationTokenSource cancellation, bool graphics = false)
    {
        _stopTest = cancellation.Cancel;
        // Pas de mise en veille programmée pendant un test (appelé et levé sur le fil de l'interface).
        KeepAwake.Begin();
        _graphicsTest = graphics;
        OnPropertyChanged(nameof(TestLoad));
        OnPropertyChanged(nameof(TestTemperature));
        IsTesting = true;
        await RefreshActivityAsync();
    }

    private async Task StopTestAsync()
    {
        IsTesting = false;
        _stopTest = null;
        KeepAwake.End();
        LoadScoreHistory();
        await RefreshActivityAsync();
    }

    private void OpenSearch(string query) => ShellLauncher.OpenUrl(WebSearch.Build(SearchEngineChoice.Value, query));

    private static Task Run(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
