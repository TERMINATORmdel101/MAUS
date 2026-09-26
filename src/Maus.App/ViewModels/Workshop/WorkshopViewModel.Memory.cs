using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using Maus.Core.Platform;
using Maus.Core.Workshop;
using Maus.Core.Workshop.Memory;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>Une ligne de timing : nom, valeur (cycles et parfois nanosecondes), explication en infobulle.</summary>
public sealed class TimingRowViewModel(MemoryTiming timing)
{
    public string Key { get; } = timing.Key;

    public string Value { get; } = timing.Nanoseconds is { } ns
        ? T("{0} ({1:0.#} ns)", timing.Clocks, ns)
        : timing.Clocks?.ToString(CultureInfo.InvariantCulture) ?? "—";

    public string? Hint { get; } = MemoryDetails.Describe(timing.Key);
}

/// <summary>Un bloc de timings (primaires, secondaires, tertiaires, ou un profil SPD).</summary>
public sealed class TimingBlockViewModel(string title, string? subtitle, IEnumerable<MemoryTiming> timings)
{
    public string Title { get; } = title;

    public string? Subtitle { get; } = subtitle;

    public bool HasSubtitle => Subtitle is not null;

    public IReadOnlyList<TimingRowViewModel> Rows { get; } = timings.Select(t => new TimingRowViewModel(t)).ToList();
}

/// <summary>Une barrette : identité, organisation et profils SPD.</summary>
public sealed class MemoryModuleViewModel(SpdModule module)
{
    public string Title { get; } = T("Emplacement {0} · {1} {2}", module.Slot + 1, module.ModuleManufacturer ?? T("fabricant inconnu"), module.PartNumber);

    public IReadOnlyList<string> Lines { get; } =
    [
        T("{0:0.#} Go {1} {2}, {3}", module.CapacityBytes / (1024.0 * 1024 * 1024), module.MemoryType, module.ModuleType, module.Organization),
        T("Puces : {0}{1}, {2} groupes de {3} banques, {4} lignes / {5} colonnes (bits d'adresse)",
            module.DramManufacturer ?? T("fabricant inconnu"),
            module.DramStepping is { } stepping ? T(" (révision 0x{0:X2})", stepping) : string.Empty,
            module.BankGroups, module.BanksPerGroup, module.RowBits, module.ColumnBits),
        module.MemoryType == "DDR5"
            ? T("{0} sous-canaux · {1} · capteur de température : {2} · alimentation : {3}", module.SubChannels, module.Ecc ? "ECC" : T("sans ECC"), module.HasThermalSensor ? T("oui") : T("non"), module.Pmic ?? "?")
            : T("{0} · capteur de température : {1}", module.Ecc ? "ECC" : T("sans ECC"), module.HasThermalSensor ? T("oui") : T("non")),
        T("Fabriquée : {0} · révision SPD {1} · somme de contrôle : {2}",
            module.ManufacturedYear is { } year ? T("{0}, semaine {1}", year, module.ManufacturedWeek ?? 0) : "?",
            module.SpdRevision, module.ChecksumOk ? T("correcte") : T("INCORRECTE")),
    ];

    public IReadOnlyList<TimingBlockViewModel> Profiles { get; } = module.Profiles.Select(p => new TimingBlockViewModel(
        MemoryDetails.ProfileName(p),
        T("{0} MT/s · {1}{2}{3}", p.SpeedMts, p.Summary,
            p.TrueLatencyNs is { } latency ? T(" · latence réelle {0:0.0} ns", latency) : string.Empty,
            p.Vdd is { } vdd ? T(" · VDD {0:0.00} V{1}", vdd, p.Vddq is { } vddq ? T(" / VDDQ {0:0.00} V", vddq) : string.Empty) : string.Empty),
        p.Timings)).ToList();
}

/// <summary>Atelier, onglet Mémoire : fiche complète (SPD, timings réels, FCLK/UCLK/MCLK), par le pilote PawnIO.</summary>
public sealed partial class WorkshopViewModel
{
    public const int SectionMemory = 5;

    private bool _isReadingMemory;
    private bool _memoryDriverMissing;
    private string _memoryStatus = T("« Lire la mémoire » interroge la puce SPD de chaque barrette et le contrôleur mémoire (AMD Ryzen, Intel Core). Lecture seule, quelques secondes.");
    private string _memoryClocks = string.Empty;
    private string _memorySettings = string.Empty;
    private string _memoryNotes = string.Empty;
    private string _memoryConfiguration = string.Empty;
    private IReadOnlyList<MemorySlotConfiguration> _memorySlots = [];
    private MemoryDetailReport? _memoryReport;
    private ICommand? _readMemory;
    private ICommand? _copyMemory;

    public ObservableCollection<TimingBlockViewModel> LiveTimingBlocks { get; } = [];

    public ObservableCollection<MemoryModuleViewModel> MemoryModules { get; } = [];

    public bool IsReadingMemory
    {
        get => _isReadingMemory;
        private set
        {
            if (SetProperty(ref _isReadingMemory, value))
            {
                OnPropertyChanged(nameof(IsNotReadingMemory));
            }
        }
    }

    public bool IsNotReadingMemory => !IsReadingMemory;

    public string MemoryStatus
    {
        get => _memoryStatus;
        private set => SetProperty(ref _memoryStatus, value);
    }

    public string MemoryClocks
    {
        get => _memoryClocks;
        private set
        {
            if (SetProperty(ref _memoryClocks, value))
            {
                OnPropertyChanged(nameof(HasMemoryClocks));
            }
        }
    }

    public bool HasMemoryClocks => MemoryClocks.Length > 0;

    public string MemorySettings
    {
        get => _memorySettings;
        private set => SetProperty(ref _memorySettings, value);
    }

    public string MemoryNotes
    {
        get => _memoryNotes;
        private set
        {
            if (SetProperty(ref _memoryNotes, value))
            {
                OnPropertyChanged(nameof(HasMemoryNotes));
            }
        }
    }

    public bool HasMemoryNotes => MemoryNotes.Length > 0;

    /// <summary>Vitesse et tension réellement appliquées, lues sans pilote : seule valeur réelle disponible sur Intel.</summary>
    public string MemoryConfiguration
    {
        get => _memoryConfiguration;
        private set
        {
            if (SetProperty(ref _memoryConfiguration, value))
            {
                OnPropertyChanged(nameof(HasMemoryConfiguration));
            }
        }
    }

    public bool HasMemoryConfiguration => MemoryConfiguration.Length > 0;

    public bool HasMemoryReport => _memoryReport is not null;

    /// <summary>PawnIO absent, constaté à la lecture : le bouton d'installation s'affiche dans l'onglet Mémoire.</summary>
    public bool IsMemoryDriverMissing
    {
        get => _memoryDriverMissing;
        private set => SetProperty(ref _memoryDriverMissing, value);
    }

    public ICommand ReadMemoryCommand => _readMemory ??= new AsyncCommand(ReadMemoryAsync);

    public ICommand CopyMemoryCommand => _copyMemory ??= new AsyncCommand(() => Run(() =>
    {
        if (_memoryReport is { } report)
        {
            try
            {
                Clipboard.SetText(MemoryConfiguration + Environment.NewLine + Environment.NewLine + MemoryDetails.ToText(report));
                MemoryStatus = T("Fiche copiée : collez-la avec Ctrl+V.");
            }
            catch (ExternalException)
            {
                MemoryStatus = T("Le presse-papiers est occupé : réessayez.");
            }
        }
    }));

    private async Task ReadMemoryAsync()
    {
        if (IsReadingMemory)
        {
            return;
        }

        // Sans pilote et sans droits particuliers : vitesse et tension réellement appliquées, pour tous les processeurs.
        _memorySlots = await Task.Run(() => WindowsMemoryConfiguration.Read(new WmiCimReader()));
        MemoryConfiguration = WindowsMemoryConfiguration.Describe(_memorySlots);

        var registry = new WindowsRegistryReader();
        var installed = PawnIo.State(registry).Installed;
        IsMemoryDriverMissing = !installed;
        if (!installed)
        {
            MemoryStatus = T("Pour lire les puces des barrettes et les timings réels, il faut le pilote libre PawnIO : bouton « Installer PawnIO » ci-dessus, puis relancez MAUS en administrateur. La vitesse et la tension appliquées, lues sans pilote, sont déjà affichées ci-dessous.");
            return;
        }

        if (!ProcessElevation.IsElevated())
        {
            MemoryStatus = PawnIoMemoryDetails.DriverRequired;
            return;
        }

        IsReadingMemory = true;
        MemoryStatus = T("Lecture en cours (jusqu'à une vingtaine de secondes avec 4 barrettes)…");
        try
        {
            var cpu = CpuIdParser.Read(new X86CpuIdSource());
            var report = await Task.Run(() => PawnIoMemoryDetails.Read(cpu, null));
            Show(report);
            MemoryStatus = T("Lu le {0:g}. Survolez un timing pour savoir ce qu'il mesure. MAUS ne modifie aucun réglage mémoire : cela se fait dans le BIOS.", DateTime.Now);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MemoryStatus = T("La lecture a échoué : {0}", ex.Message);
        }
        finally
        {
            IsReadingMemory = false;
        }
    }

    private void Show(MemoryDetailReport report)
    {
        _memoryReport = report;
        OnPropertyChanged(nameof(HasMemoryReport));
        LiveTimingBlocks.Clear();
        MemoryModules.Clear();
        MemoryClocks = report.Clocks is { } clocks
            ? T("FCLK {0} · UCLK {1} · MCLK {2}{3}", Mhz(clocks.FclkMhz), Mhz(clocks.UclkMhz), Mhz(clocks.MclkMhz), clocks.UclkMode is { } mode ? T(" (UCLK:MCLK {0})", mode) : string.Empty)
              + Environment.NewLine
              + T("Tensions : SoC {0} · VDDP {1} · VDDG IOD {2} · VDDG CCD {3}", Volts(clocks.SocVolts), Volts(clocks.VddpVolts), Volts(clocks.VddgIodVolts), Volts(clocks.VddgCcdVolts))
            : report.Live is { } onlyLive
                ? T("MCLK {0:0} MHz (d'après le coefficient du contrôleur, BCLK de 100 MHz supposée)", onlyLive.MclkMhz)
                : report.Intel is { } intelClocks
                    ? MemoryDetails.IntelClocks(intelClocks)
                    : string.Empty;

        if (report.Intel is { } intel)
        {
            MemorySettings = MemoryDetails.IntelSettings(intel)
                + (intel.ChannelsDiffer ? Environment.NewLine + T("Attention : les canaux n'ont pas tous les mêmes timings ; ceux du premier canal sont affichés.") : string.Empty);
            foreach (var group in Enum.GetValues<TimingGroup>())
            {
                var timings = intel.Timings.Where(t => t.Group == group).ToList();
                if (timings.Count > 0)
                {
                    LiveTimingBlocks.Add(new TimingBlockViewModel(MemoryDetails.GroupName(group), null, timings));
                }
            }
        }
        else if (report.Live is { } live)
        {
            var s = live.Settings;
            MemorySettings = T("{0}-{1} · canaux {2} · GDM {3} · commande {4} · Power Down {5} · BGS {6} · BGS Alt {7} · rafraîchissement {8}",
                live.Ddr5 ? "DDR5" : "DDR4", live.SpeedMts, string.Join(", ", live.Channels.Select(c => (char)('A' + c))),
                OnOff(s.GearDownMode), s.Command2T ? "2T" : "1T", OnOff(s.PowerDown), OnOff(s.BankGroupSwap), OnOff(s.BankGroupSwapAlt), s.RefreshMode)
                + (live.ChannelsDiffer ? Environment.NewLine + T("Attention : les canaux n'ont pas tous les mêmes timings ; ceux du premier canal sont affichés.") : string.Empty);
            foreach (var group in Enum.GetValues<TimingGroup>())
            {
                LiveTimingBlocks.Add(new TimingBlockViewModel(MemoryDetails.GroupName(group), null, live.Timings.Where(t => t.Group == group)));
            }
        }
        else
        {
            MemorySettings = string.Empty;
        }

        foreach (var module in report.Modules)
        {
            MemoryModules.Add(new MemoryModuleViewModel(module));
        }

        if (WindowsMemoryConfiguration.CompareWithSpd(_memorySlots, report.Modules) is { } comparison)
        {
            MemoryConfiguration = WindowsMemoryConfiguration.Describe(_memorySlots) + Environment.NewLine + Environment.NewLine + comparison;
        }

        MemoryNotes = string.Join(Environment.NewLine, report.Notes.Select(n => "• " + n));
    }

    private static string OnOff(bool value) => value ? T("activé") : T("désactivé");

    private static string Mhz(double? value) => value is { } v ? v.ToString("0", CultureInfo.InvariantCulture) + " MHz" : "?";

    private static string Volts(double? value) => value is { } v ? v.ToString("0.000", CultureInfo.InvariantCulture) + " V" : "?";
}
