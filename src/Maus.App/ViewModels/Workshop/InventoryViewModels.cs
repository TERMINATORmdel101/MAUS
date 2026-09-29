using System.Globalization;
using System.Windows.Input;
using Maus.Core.Workshop;
using Maus.Core.Workshop.Memory;
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

    /// <param name="spd">Puces SPD lues par PawnIO, ou <c>null</c> si elles ne sont pas lisibles (pilote absent, pas d'administrateur).</param>
    /// <param name="details">Windows, écrans, réseau, son et emplacements mémoire, ou <c>null</c> s'ils n'ont pas été lus.</param>
    public static IReadOnlyList<ComponentCardViewModel> From(HardwareInventory inventory, SafetyLimits limits, Action<string> search, IReadOnlyList<SpdModule>? spd = null, MachineDetails? details = null, DateTime? now = null)
    {
        var cards = new List<ComponentCardViewModel>();
        if (details is not null)
        {
            cards.Add(SystemCard(details.System, now ?? DateTime.Now));
        }

        var cpu = inventory.Cpu;
        var cpuLimit = limits.ForCpu(cpu.Name);
        cards.Add(new ComponentCardViewModel("P", T("Processeur"), cpu.Name,
        [
            new(T("Cœurs / threads"), $"{cpu.Cores} / {cpu.Threads}"),
            new(T("Fréquence de base"), cpu.BaseClockMhz > 0 ? $"{cpu.BaseClockMhz} MHz" : "—"),
            new(T("Cache L2 / L3"), $"{Kb(cpu.L2CacheKb)} / {Kb(cpu.L3CacheKb)}"),
            new(T("Signature"), cpu.Signature ?? "—"),
            new(T("Microcode"), details?.Microcode ?? "—"),
            new(T("Instructions"), cpu.InstructionSets.Count == 0 ? "—" : string.Join(", ", cpu.InstructionSets)),
            new(T("Architecture hybride"), cpu.Hybrid ? T("oui") : T("non")),
            new(T("Température maximale prévue"), cpuLimit is null ? T("non répertoriée") : $"{cpuLimit.MaxC} °C"),
        ], cpu.Name, search));

        cards.Add(new ComponentCardViewModel("C", T("Carte mère et BIOS"), $"{inventory.Board.Manufacturer} {inventory.Board.Product}".Trim(),
        [
            new(T("Fabricant du BIOS"), inventory.Board.BiosVendor ?? "—"),
            new(T("Version du BIOS"), inventory.Board.BiosVersion ?? "—"),
            new(T("Date du BIOS"), inventory.Board.BiosDate?.ToString("d", Culture) ?? "—"),
            .. SlotLines(details?.Slots, inventory.Memory.Count),
        ], inventory.Board.Product is { } board ? $"{inventory.Board.Manufacturer} {board}" : null, search));

        for (var index = 0; index < inventory.Memory.Count; index++)
        {
            var module = inventory.Memory[index];
            var limit = limits.ForMemory(module.Generation);
            var gauge = module.ConfiguredMillivolts is { } mv && limit is not null
                ? limit.DangerAboveMv is { } danger
                    ? new GaugeInfo(T("Tension déclarée par le BIOS"), limit.NominalMv - 200, danger + 150, limit.ElevatedAboveMv, danger, mv,
                        T("{0} · élevée au-delà de {1} · maximum absolu {2}", V(mv), V(limit.ElevatedAboveMv), V(danger)))
                    : new GaugeInfo(T("Tension déclarée par le BIOS"), limit.NominalMv - 200, limit.ElevatedAboveMv + 150, limit.ElevatedAboveMv, limit.ElevatedAboveMv + 150, mv,
                        T("{0} · élevée au-delà de {1}", V(mv), V(limit.ElevatedAboveMv)))
                : null;
            cards.Add(new ComponentCardViewModel("R", T("Mémoire vive · {0}", module.Slot),
                $"{Gb(module.CapacityBytes)} DDR{module.Generation?.ToString(CultureInfo.InvariantCulture) ?? "?"} · {module.Manufacturer ?? T("fabricant inconnu")}",
            [
                new(T("Référence"), module.PartNumber ?? "—"),
                .. ProfileLines(spd, module.PartNumber, index),
                new(T("Vitesse nominale / appliquée"), $"{module.RatedSpeedMts?.ToString(CultureInfo.InvariantCulture) ?? "—"} / {module.ConfiguredSpeedMts?.ToString(CultureInfo.InvariantCulture) ?? "—"} MT/s"),
                new(T("Tension déclarée (min / max)"), module.MinMillivolts is null ? "—" : $"{V(module.MinMillivolts.Value)} / {V(module.MaxMillivolts ?? 0)}"),
            ], module.PartNumber, search, gauge));
        }

        foreach (var gpu in inventory.Gpus)
        {
            var nvidia = gpu.Nvidia;
            var lines = new List<InfoLine>
            {
                new(T("Mémoire vidéo"), gpu.MemoryBytes is { } vram ? Gb(vram) : "—"),
                new(T("Pilote"), gpu.Info.DriverVersion ?? "—"),
                new(T("Date du pilote"), gpu.Info.DriverDate?.ToString("d", Culture) ?? "—"),
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

        foreach (var display in details?.Displays ?? [])
        {
            cards.Add(DisplayCard(display, search));
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

        if (details is { Network.Count: > 0 })
        {
            var connected = details.Network.FirstOrDefault(a => a.Connected);
            cards.Add(new ComponentCardViewModel("N", T("Réseau"), connected?.Description ?? T("Aucune carte réseau connectée"),
                details.Network.Select(a => new InfoLine(a.Description, NetworkState(a))).ToList(), null, search));
        }

        if (details is { Audio.Count: > 0 })
        {
            cards.Add(new ComponentCardViewModel("S", T("Son"), T("{0} périphériques audio", details.Audio.Count),
                details.Audio.Select(d => new InfoLine(d.Name, d.Manufacturer ?? "—")).ToList(), null, search));
        }

        return cards;
    }

    /// <summary>Carte « Windows et ce PC » : modèle, version, installation, démarrage, UEFI, Secure Boot et TPM.</summary>
    private static ComponentCardViewModel SystemCard(SystemIdentity system, DateTime now)
    {
        var windows = system.Windows;
        var model = string.Join(' ', new[] { system.Manufacturer, system.Model }.Where(s => s is not null));
        return new ComponentCardViewModel("W", T("Windows et ce PC"), $"{windows.ProductName} {windows.DisplayVersion}".Trim(),
        [
            new(T("Modèle"), model.Length > 0 ? model : T("non renseigné par le fabricant")),
            new(T("Type"), system.FormFactor switch
            {
                Maus.Core.Hardware.FormFactor.Laptop => T("PC portable"),
                Maus.Core.Hardware.FormFactor.Desktop => T("PC fixe"),
                _ => "—",
            }),
            new(T("Version de Windows"), T("{0} · build {1} · édition {2}", windows.DisplayVersion, windows.FullBuild, windows.EditionId)),
            new(T("Architecture"), system.Architecture ?? "—"),

            // InstallDate peut changer lors d'une mise à niveau majeure de Windows (souvent constaté, non documenté par Microsoft) :
            // d'où le libellé prudent « installé ou mis à niveau ».
            new(T("Installé ou mis à niveau le"), system.InstalledOn?.ToString("d", Culture) ?? "—"),
            new(T("Dernier démarrage complet"), system.BootedAt is { } boot ? T("{0} (il y a {1})", boot.ToString("g", Culture), Duration(now - boot)) : "—"),

            // Démarrage rapide : l'arrêt met en veille prolongée la session du noyau, le compteur continue (Microsoft Learn,
            // « Distinguishing Fast Startup from Wake-from-Hibernation »).
            new(T("À savoir"), T("un arrêt avec le démarrage rapide de Windows n'est pas un redémarrage complet")),
            new(T("Mode de démarrage"), system.Firmware switch
            {
                FirmwareKind.Uefi => "UEFI",
                FirmwareKind.LegacyBios => T("BIOS hérité (CSM)"),
                _ => "—",
            }),
            new("Secure Boot", system.SecureBoot switch
            {
                true => T("activé"),
                false => T("désactivé"),
                null => T("non publié par Windows"),
            }),
            new(T("Puce TPM"), Tpm(system)),
        ], null, _ => { });
    }

    private static string Tpm(SystemIdentity system)
    {
        if (system.Tpm is not { } tpm)
        {
            return system.TpmNeedsAdministrator ? T("lisible avec MAUS en administrateur") : T("aucune détectée");
        }

        var parts = new List<string> { tpm.SpecVersion is { } spec ? T("version {0}", spec) : T("version inconnue") };
        if (tpm.Manufacturer is { } maker)
        {
            parts.Add(maker);
        }

        parts.Add(tpm.Enabled == false ? T("désactivée") : tpm.Activated == false ? T("non activée") : T("active"));
        return string.Join(" · ", parts);
    }

    private static IEnumerable<InfoLine> SlotLines(MemorySlots? slots, int used)
    {
        if (slots?.Total is { } total)
        {
            yield return new(T("Emplacements mémoire"), T("{0} utilisés sur {1}", used, total));
        }

        if (slots?.MaxCapacityBytes is { } max)
        {
            yield return new(T("Mémoire maximale (déclarée par le BIOS)"), Gb(max));
        }
    }

    private static ComponentCardViewModel DisplayCard(DisplayIdentity display, Action<string> search)
    {
        var size = $"{display.Width} × {display.Height}";
        // Mode « préféré » affiché seulement s'il dépasse le mode actuel : plus petit, il est incohérent (pilote) ou virtuel (DSR).
        if (display.NativeWidth is { } nativeWidth && display.NativeHeight is { } nativeHeight && (long)nativeWidth * nativeHeight > (long)display.Width * display.Height)
        {
            size = T("{0} (native : {1} × {2})", size, nativeWidth, nativeHeight);
        }

        var lines = new List<InfoLine>
        {
            new(T("Définition"), size),
            new(T("Fréquence"), display.RefreshHz is { } hz ? hz.ToString("0.##", Culture) + " Hz" : "—"),
            new(T("Connecteur"), display.Connector),
            new(T("Carte graphique"), display.Adapter ?? "—"),
            new("HDR", display.HdrActive == true ? T("activé") : display.HdrSupported == true ? T("pris en charge, désactivé") : display.HdrSupported == false ? T("non pris en charge") : "—"),
        };
        if (display.BitsPerColor is { } bits)
        {
            lines.Add(new(T("Couleurs"), T("{0} bits par couleur", bits)));
        }

        return new ComponentCardViewModel("E", T("Écran"), display.Name, lines, display.Name, search);
    }

    private static string NetworkState(NetworkAdapterIdentity adapter)
    {
        var kind = adapter.Kind switch
        {
            NetworkKind.Ethernet => T("câble (Ethernet)"),
            NetworkKind.WiFi => "Wi-Fi",
            _ => T("autre"),
        };
        if (!adapter.Connected)
        {
            return T("{0} · non connectée", kind);
        }

        return adapter.LinkSpeedBitsPerSecond is { } bits
            ? T("{0} · connectée · {1}", kind, bits >= 1_000_000_000 ? (bits / 1e9).ToString("0.#", Culture) + " Gbit/s" : (bits / 1e6).ToString("0.#", Culture) + " Mbit/s")
            : T("{0} · connectée", kind);
    }

    /// <summary>Durée lisible : « 3 j 4 h », « 2 h 15 min » ou « 12 min ».</summary>
    private static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            return "—";
        }

        return span.TotalDays >= 1 ? T("{0} j {1} h", (int)span.TotalDays, span.Hours)
            : span.TotalHours >= 1 ? T("{0} h {1} min", (int)span.TotalHours, span.Minutes)
            : T("{0} min", span.Minutes);
    }

    /// <summary>La fiche entière en texte, pour la coller dans un message d'aide ou un signalement.</summary>
    public static string ToText(IEnumerable<ComponentCardViewModel> cards)
    {
        var text = new System.Text.StringBuilder();
        foreach (var card in cards)
        {
            text.Append(CultureInfo.InvariantCulture, $"{card.Title} : {card.Subtitle}").AppendLine();
            foreach (var line in card.Lines)
            {
                text.Append(CultureInfo.InvariantCulture, $"  - {line.Label} : {line.Value}").AppendLine();
            }

            if (card.Gauge is { } gauge)
            {
                text.Append(CultureInfo.InvariantCulture, $"  - {gauge.Caption} : {gauge.Legend}").AppendLine();
            }

            text.AppendLine();
        }

        return text.ToString().TrimEnd();
    }

    private static string Kb(int? kb) => kb is null or 0 ? "—" : kb >= 1024 ? T("{0:0.#} Mo", kb / 1024.0) : T("{0} Ko", kb);

    private static string Gb(long bytes) => (bytes / 1073741824.0).ToString("0.# ", Culture) + T("Go");

    private static string V(int millivolts) => (millivolts / 1000.0).ToString("0.00 V", Culture);

    /// <summary>Profils XMP / EXPO de la barrette (vitesse, timings principaux, tension), lus dans sa puce SPD.</summary>
    private static IEnumerable<InfoLine> ProfileLines(IReadOnlyList<SpdModule>? spd, string? partNumber, int index)
    {
        if (spd is null)
        {
            return [new(T("Profils XMP / EXPO"), T("lisibles avec le pilote PawnIO et MAUS en administrateur"))];
        }

        if (MemoryDetails.MatchModule(spd, partNumber, index) is not { } module)
        {
            return [new(T("Profils XMP / EXPO"), T("puce SPD non lue pour cette barrette"))];
        }

        var profiles = module.Profiles.Where(p => p.Kind != ProfileKind.Jedec).ToList();
        if (profiles.Count == 0)
        {
            var jedec = module.Profiles.Where(p => p.Kind == ProfileKind.Jedec).MaxBy(p => p.SpeedMts);
            return [new(T("Profils XMP / EXPO"), jedec is null ? T("aucun") : T("aucun · standard JEDEC {0}", MemoryDetails.ProfileSummary(jedec)))];
        }

        return profiles.Select(p => new InfoLine(MemoryDetails.ProfileName(p), MemoryDetails.ProfileSummary(p)));
    }
}
