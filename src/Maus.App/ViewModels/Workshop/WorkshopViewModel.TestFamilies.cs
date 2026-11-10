using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Maus.App.Appearance;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>Résultat du dernier test terminé (coche de la page des tests).</summary>
public enum TestOutcome
{
    None,
    Good,
    Problem,
    Stopped,
}

/// <summary>
/// Atelier, onglet Tests : une tuile par famille, un test processeur à trois niveaux, un bilan qui enchaîne les tests et un panneau
/// unique du test en cours (demande du porteur du 1/11/2026 : rassembler et simplifier). Le bilan demande une seule confirmation,
/// qui annonce tout ce qu'il va lancer.
/// </summary>
public sealed partial class WorkshopViewModel
{
    private const int FamilyCpu = 0;
    private const int FamilyRam = 1;
    private const int FamilyGpu = 2;
    private const int FamilyDisk = 3;
    private const int FamilyNet = 4;
    private const int FamilyDevices = 5;

    private int _testFamily;
    private bool _seriesConfirmed;
    private bool _seriesStopped;
    private double _activeProgress;
    private string _activeStatus = string.Empty;
    private string _outcomeText = string.Empty;
    private TestOutcome _outcome;
    private ICommand? _startCpuLevel;
    private ICommand? _startFullCheck;

    public bool ShowCpuTests
    {
        get => _testFamily == FamilyCpu;
        set => ShowFamily(value, FamilyCpu);
    }

    public bool ShowRamTests
    {
        get => _testFamily == FamilyRam;
        set => ShowFamily(value, FamilyRam);
    }

    public bool ShowGpuTests
    {
        get => _testFamily == FamilyGpu;
        set => ShowFamily(value, FamilyGpu);
    }

    public bool ShowDiskTests
    {
        get => _testFamily == FamilyDisk;
        set => ShowFamily(value, FamilyDisk);
    }

    public bool ShowNetTests
    {
        get => _testFamily == FamilyNet;
        set => ShowFamily(value, FamilyNet);
    }

    public bool ShowDeviceTests
    {
        get => _testFamily == FamilyDevices;
        set => ShowFamily(value, FamilyDevices);
    }

    public bool CpuQuick
    {
        get => CpuDuration == CpuDurations[0];
        set => ChooseCpuLevel(value, 0);
    }

    public bool CpuNormal
    {
        get => CpuDuration == CpuDurations[1];
        set => ChooseCpuLevel(value, 1);
    }

    public bool CpuLong
    {
        get => CpuDuration == CpuDurations[2];
        set => ChooseCpuLevel(value, 2);
    }

    public string CpuLevelDetail => CpuLong
        ? T("15 minutes sur tous les cœurs, puis 5 minutes de test cœur par cœur.")
        : CpuQuick
            ? T("1 minute sur tous les cœurs, pour un premier avis.")
            : T("5 minutes sur tous les cœurs : le bon choix dans la plupart des cas.");

    /// <summary>Le test cœur par cœur s'affiche déplié sur un processeur AMD (Curve Optimizer) et en mode technicien.</summary>
    public bool FineTuningOpen { get; } =
        AppearanceManager.Current.TechnicianMode
        || (Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")?.Contains("AuthenticAMD", StringComparison.Ordinal) ?? false);

    public ICommand StartCpuLevelCommand => _startCpuLevel ??= new AsyncCommand(RunCpuLevelAsync);

    public ICommand StartFullCheckCommand => _startFullCheck ??= new AsyncCommand(RunFullCheckAsync);

    /// <summary>Une ligne par test du bilan complet.</summary>
    public ObservableCollection<string> FullCheckLines { get; } = [];

    /// <summary>Avancement du test en cours, quel qu'il soit.</summary>
    public double ActiveProgress
    {
        get => _activeProgress;
        private set => SetProperty(ref _activeProgress, value);
    }

    /// <summary>Dernier message du test en cours, quel qu'il soit.</summary>
    public string ActiveStatus
    {
        get => _activeStatus;
        private set => SetProperty(ref _activeStatus, value);
    }

    public bool HasOutcome => _outcome != TestOutcome.None && !IsTesting;

    public bool OutcomeGood => _outcome == TestOutcome.Good;

    public string OutcomeText
    {
        get => _outcomeText;
        private set => SetProperty(ref _outcomeText, value);
    }

    private void ShowFamily(bool chosen, int family)
    {
        if (!chosen || family == _testFamily)
        {
            return;
        }

        _testFamily = family;
        if (family == FamilyDisk)
        {
            LoadRemovableDrives();
        }

        OnPropertyChanged(nameof(ShowCpuTests));
        OnPropertyChanged(nameof(ShowRamTests));
        OnPropertyChanged(nameof(ShowGpuTests));
        OnPropertyChanged(nameof(ShowDiskTests));
        OnPropertyChanged(nameof(ShowNetTests));
        OnPropertyChanged(nameof(ShowDeviceTests));
        OnPropertyChanged(nameof(ShowBatteryTest));
    }

    private void ChooseCpuLevel(bool chosen, int level)
    {
        if (!chosen)
        {
            return;
        }

        CpuDuration = CpuDurations[level];
        OnPropertyChanged(nameof(CpuQuick));
        OnPropertyChanged(nameof(CpuNormal));
        OnPropertyChanged(nameof(CpuLong));
        OnPropertyChanged(nameof(CpuLevelDetail));
    }

    /// <summary>Droits accordés ou repris : ce qui a été lu avant se relit, sans attendre le prochain passage sur la page.</summary>
    private void OnElevationChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _inventoryLoaded = false;
            _driversLoaded = false;
            Maus.Core.Diagnostics.BackgroundTasks.Forget(ReloadAfterElevationAsync(), "atelier après les droits administrateur", _ => { });
        });

    private async Task ReloadAfterElevationAsync()
    {
        await RefreshActivityAsync();
        if (_liveTimer.IsEnabled)
        {
            await SampleLiveAsync();
        }
    }

    /// <summary>Confirmation d'un test : le bilan complet l'a déjà demandée pour toute sa série.</summary>
    private bool Ask(string title, string text) => _seriesConfirmed || _confirm(title, text);

    private void Report(TestOutcome outcome)
    {
        _outcome = outcome;
        OnPropertyChanged(nameof(HasOutcome));
        OnPropertyChanged(nameof(OutcomeGood));
    }

    /// <summary>Le panneau du test en cours et la coche suivent le test qui avance, quel qu'il soit.</summary>
    private void OnTestValueChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CpuProgress):
                ActiveProgress = CpuProgress;
                break;
            case nameof(RamProgress):
                ActiveProgress = RamProgress;
                break;
            case nameof(VramProgress):
                ActiveProgress = VramProgress;
                break;
            case nameof(CoreProgress):
                ActiveProgress = CoreProgress;
                break;
            case nameof(DiskProgress):
                ActiveProgress = DiskProgress;
                break;
            case nameof(CpuStatus):
                ActiveStatus = CpuStatus;
                break;
            case nameof(RamStatus):
                ActiveStatus = RamStatus;
                break;
            case nameof(VramStatus):
                ActiveStatus = VramStatus;
                break;
            case nameof(CoreStatus):
                ActiveStatus = CoreStatus;
                break;
            case nameof(DiskStatus):
                ActiveStatus = DiskStatus;
                break;
            case nameof(IsTesting):
                OutcomeText = IsTesting ? string.Empty : ActiveStatus;
                OnPropertyChanged(nameof(HasOutcome));
                break;
        }
    }

    /// <summary>Le niveau long ajoute le test cœur par cœur, si le processeur a tenu.</summary>
    private async Task RunCpuLevelAsync()
    {
        var chain = CpuLong;
        await RunCpuTestAsync();
        if (chain && _outcome == TestOutcome.Good)
        {
            CoreDuration = CoreDurations[0];
            await RunCoreTestAsync();
        }
    }

    private async Task RunFullCheckAsync()
    {
        if (Busy)
        {
            return;
        }

        await SampleBeforeTestAsync();
        if (!_confirm(T("Lancer le bilan complet du PC ?"), Maus.Core.Reporting.ConfirmationText.Build(
            T("MAUS va enchaîner quatre tests : le processeur (1 minute, tous les cœurs à 100 %), la mémoire vive, la mémoire vidéo et la vitesse du disque (1 Go écrit puis supprimé)."),
            T("Le PC chauffera et ses ventilateurs accéléreront. Le bilan s'arrête tout seul si une température devient trop haute, et le bouton d'arrêt reste à portée de main.") + HeavyWarning(CpuMode) + TemperatureCaveat())))
        {
            return;
        }

        var (cpu, ram, vram, disk) = (CpuDuration, RamSize, VramSize, DiskSize);
        var problem = false;
        _seriesConfirmed = true;
        _seriesStopped = false;
        FullCheckLines.Clear();
        var started = DateTimeOffset.Now;
        try
        {
            CpuDuration = CpuDurations[0];
            RamSize = RamSizes[1];
            VramSize = VramSizes[1];
            DiskSize = DiskSizes[0];
            (string Name, Func<Task> Run, Func<string> Status)[] steps =
            [
                (T("Processeur"), RunCpuTestAsync, () => CpuStatus),
                (T("Mémoire vive"), RunRamTestAsync, () => RamStatus),
                (T("Mémoire vidéo"), RunVramTestAsync, () => VramStatus),
                (T("Disque"), RunDiskTestAsync, () => DiskStatus),
            ];
            foreach (var (name, run, status) in steps)
            {
                await run();
                FullCheckLines.Add(T("{0} : {1}", name, status()));
                problem |= _outcome == TestOutcome.Problem;
                if (_seriesStopped || _outcome == TestOutcome.Stopped)
                {
                    return;
                }
            }

            var hardwareErrors = await Task.Run(() => Maus.Core.Workshop.WheaEvents.CountSince(new Maus.Core.Platform.WindowsEventLogReader(), started));
            FullCheckLines.Add(hardwareErrors switch
            {
                null => T("Erreurs matérielles (WHEA) pendant le bilan : journal illisible."),
                0 => T("Erreurs matérielles (WHEA) pendant le bilan : aucune."),
                _ => T("Erreurs matérielles (WHEA) pendant le bilan : {0}. Le PC n'est pas stable dans ces conditions.", hardwareErrors),
            });
            problem |= hardwareErrors > 0;
            Report(problem ? TestOutcome.Problem : TestOutcome.Good);
            OutcomeText = problem ? T("Bilan terminé : au moins un test a trouvé un problème.") : T("Bilan terminé : aucun problème trouvé.");
        }
        finally
        {
            _seriesConfirmed = false;
            (CpuDuration, RamSize, VramSize, DiskSize) = (cpu, ram, vram, disk);
        }
    }
}
