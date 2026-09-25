using System.Globalization;
using System.Windows.Input;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

public sealed record InfoLine(string Label, string Value);

/// <summary>Jauge de sécurité d'une fiche (valeur actuelle et zones normale, élevée, dangereuse).</summary>
public sealed record GaugeInfo(string Caption, double Minimum, double Maximum, double Elevated, double Danger, double Value, string Legend);

/// <summary>Fiche d'un composant dans « Mon PC ».</summary>
public sealed class ComponentCardViewModel
{
    public ComponentCardViewModel(string letter, string title, string subtitle, IReadOnlyList<InfoLine> lines, string? searchReference, Action<string> search, GaugeInfo? gauge = null)
    {
        Letter = letter;
        Title = title;
        Subtitle = subtitle;
        Lines = lines;
        Gauge = gauge;
        HasSearch = searchReference is not null;
        SearchCommand = new AsyncCommand(() =>
        {
            if (searchReference is not null)
            {
                search(searchReference);
            }

            return Task.CompletedTask;
        });
    }

    public string Letter { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public IReadOnlyList<InfoLine> Lines { get; }

    public GaugeInfo? Gauge { get; }

    public bool HasGauge => Gauge is not null;

    public bool HasSearch { get; }

    public ICommand SearchCommand { get; }

    public static IReadOnlyList<ComponentCardViewModel> From(HardwareInventory inventory, SafetyLimits limits, Action<string> search)
    {
        var cards = new List<ComponentCardViewModel>();
        var cpu = inventory.Cpu;
        var cpuLimit = limits.ForCpu(cpu.Name);
        cards.Add(new ComponentCardViewModel("P", T("Processeur"), cpu.Name,
        [
            new(T("Cœurs / threads"), $"{cpu.Cores} / {cpu.Threads}"),
            new(T("Fréquence de base"), cpu.BaseClockMhz > 0 ? $"{cpu.BaseClockMhz} MHz" : "—"),
            new(T("Cache L2 / L3"), $"{Kb(cpu.L2CacheKb)} / {Kb(cpu.L3CacheKb)}"),
            new(T("Signature"), cpu.Signature ?? "—"),
            new(T("Instructions"), cpu.InstructionSets.Count == 0 ? "—" : string.Join(", ", cpu.InstructionSets)),
            new(T("Architecture hybride"), cpu.Hybrid ? T("oui") : T("non")),
            new(T("Température maximale prévue"), cpuLimit is null ? T("non répertoriée") : $"{cpuLimit.MaxC} °C"),
        ], cpu.Name, search));

        cards.Add(new ComponentCardViewModel("C", T("Carte mère et BIOS"), $"{inventory.Board.Manufacturer} {inventory.Board.Product}".Trim(),
        [
            new(T("Fabricant du BIOS"), inventory.Board.BiosVendor ?? "—"),
            new(T("Version du BIOS"), inventory.Board.BiosVersion ?? "—"),
            new(T("Date du BIOS"), inventory.Board.BiosDate?.ToString("d", Culture) ?? "—"),
        ], inventory.Board.Product is { } board ? $"{inventory.Board.Manufacturer} {board}" : null, search));

        foreach (var module in inventory.Memory)
        {
            var limit = limits.ForMemory(module.Generation);
            var gauge = module.ConfiguredMillivolts is { } mv && limit is not null
                ? new GaugeInfo(T("Tension"), limit.NominalMv - 200, limit.DangerAboveMv + 150, limit.ElevatedAboveMv, limit.DangerAboveMv, mv,
                    T("{0} · élevée au-delà de {1} · dangereuse au-delà de {2}", V(mv), V(limit.ElevatedAboveMv), V(limit.DangerAboveMv)))
                : null;
            cards.Add(new ComponentCardViewModel("R", T("Mémoire vive · {0}", module.Slot),
                $"{Gb(module.CapacityBytes)} DDR{module.Generation?.ToString(CultureInfo.InvariantCulture) ?? "?"} · {module.Manufacturer ?? T("fabricant inconnu")}",
            [
                new(T("Référence"), module.PartNumber ?? "—"),
                new(T("Vitesse nominale / appliquée"), $"{module.RatedSpeedMts?.ToString(CultureInfo.InvariantCulture) ?? "—"} / {module.ConfiguredSpeedMts?.ToString(CultureInfo.InvariantCulture) ?? "—"} MT/s"),
                new(T("Tension (min / max)"), module.MinMillivolts is null ? "—" : $"{V(module.MinMillivolts.Value)} / {V(module.MaxMillivolts ?? 0)}"),
            ], module.PartNumber, search, gauge));
        }

        foreach (var gpu in inventory.Gpus)
        {
            var nvidia = gpu.Nvidia;
            var lines = new List<InfoLine>
            {
                new(T("Mémoire vidéo"), gpu.MemoryBytes is { } vram ? Gb(vram) : "—"),
                new(T("Pilote"), gpu.Info.DriverVersion ?? "—"),
            };
            GaugeInfo? gauge = null;
            if (nvidia is not null)
            {
                lines.Add(new(T("Fréquence max (GPU / mémoire)"), $"{nvidia.MaxGraphicsClockMhz?.ToString(CultureInfo.InvariantCulture) ?? "—"} / {nvidia.MaxMemoryClockMhz?.ToString(CultureInfo.InvariantCulture) ?? "—"} MHz"));
                lines.Add(new(T("Limite de puissance (appliquée / défaut)"), $"{nvidia.PowerLimitWatts:0} / {nvidia.DefaultPowerLimitWatts:0} W"));
                lines.Add(new(T("Lien PCIe"), $"Gen {nvidia.PcieGeneration}/{nvidia.PcieMaxGeneration} · x{nvidia.PcieWidth}/{nvidia.PcieMaxWidth}"));
                lines.Add(new(T("BIOS de la carte"), nvidia.VbiosVersion ?? "—"));
                if (nvidia.TemperatureC is { } t && nvidia.SlowdownTemperatureC is { } slow)
                {
                    var shutdown = nvidia.ShutdownTemperatureC ?? slow + 5;
                    gauge = new GaugeInfo(T("Température"), 20, shutdown + 5, slow - 10, slow, t,
                        T("{0} °C · ralentit à {1} °C · s'éteint à {2} °C", t, slow, shutdown));
                }
            }

            cards.Add(new ComponentCardViewModel("G", T("Carte graphique"), gpu.Info.Name, lines, gpu.Info.Name, search, gauge));
        }

        foreach (var disk in inventory.Disks)
        {
            cards.Add(new ComponentCardViewModel("D", T("Disque"), disk.Model,
            [
                new(T("Type"), $"{disk.Media ?? "?"} · {disk.Bus ?? "?"}"),
                new(T("Capacité"), disk.SizeBytes is { } size ? Gb(size) : "—"),
                new(T("Température"), disk.TemperatureC is { } c ? $"{c} °C" : "—"),
                new(T("Usure"), disk.WearPercent is { } w ? $"{w} %" : "—"),
                new(T("Heures de fonctionnement"), disk.PowerOnHours?.ToString("N0", Culture) ?? "—"),
                new(T("Micrologiciel"), disk.Firmware ?? "—"),
            ], disk.Model, search));
        }

        foreach (var battery in inventory.Batteries)
        {
            var gauge = battery.HealthPercent is { } health
                ? new GaugeInfo(T("Capacité restante"), 0, 100, 60, 80, health, T("{0} % de la capacité d'origine", health))
                : null;
            cards.Add(new ComponentCardViewModel("B", T("Batterie"), $"{battery.Manufacturer} {battery.Name}".Trim(),
            [
                new(T("Capacité d'origine / actuelle"), $"{battery.DesignCapacityMwh?.ToString("N0", Culture) ?? "—"} / {battery.FullChargeCapacityMwh?.ToString("N0", Culture) ?? "—"} mWh"),
                new(T("Tension"), battery.VoltageMv is { } mv ? V(mv) : "—"),
            ], battery.Name, search, gauge));
        }

        return cards;
    }

    private static string Kb(int? kb) => kb is null or 0 ? "—" : kb >= 1024 ? T("{0:0.#} Mo", kb / 1024.0) : T("{0} Ko", kb);

    private static string Gb(long bytes) => (bytes / 1073741824.0).ToString("0.# ", Culture) + T("Go");

    private static string V(int millivolts) => (millivolts / 1000.0).ToString("0.00 V", Culture);
}
