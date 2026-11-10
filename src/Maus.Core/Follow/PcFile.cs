using System.Globalization;
using System.Text;
using Maus.Core.Modules.M11Health;
using Maus.Core.Platform;
using Maus.Core.Reporting;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Follow;

/// <summary>Ce que MAUS a lu pour le dossier du PC. Rien d'autre ne peut y entrer : ces fiches n'ont ni numéro de série ni adresse MAC.</summary>
public sealed record PcFileInput
{
    public required DateTimeOffset At { get; init; }

    public required HardwareInventory Inventory { get; init; }

    /// <summary>Modèle du PC et écrans ; <c>null</c> s'ils n'ont pas été lus.</summary>
    public MachineDetails? Details { get; init; }

    public WindowsInfo? Windows { get; init; }

    /// <summary>La lecture s'est faite avec les droits d'administrateur.</summary>
    public bool ReadWithRights { get; init; }

    /// <summary>Windows annonce une batterie (état d'alimentation, lisible sans droits).</summary>
    public bool BatteryDetected { get; init; }

    /// <summary>Audits gardés sur ce PC, et celui de cette ouverture s'il y en a un (ajouté en dernier).</summary>
    public IReadOnlyList<AuditSnapshot> Audits { get; init; } = [];
}

/// <summary>Le dossier prêt à être relu, puis imprimé ou enregistré. Tout son texte est déjà masqué.</summary>
public sealed record PcFileDocument(string Title, string Notice, IReadOnlyList<string> Header, IReadOnlyList<HtmlPageSection> Sections, string Footer, PrivacyFilter Privacy)
{
    public string ToHtml() => HtmlReport.BuildPage(new HtmlPageInput(Title, Notice, Header, Sections, Footer, Privacy));

    public string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine(Title).AppendLine(Notice);
        foreach (var line in Header)
        {
            text.AppendLine(line);
        }

        foreach (var section in Sections)
        {
            text.AppendLine().AppendLine(section.Title);
            if (section.Note is not null)
            {
                text.AppendLine(section.Note);
            }

            foreach (var line in section.Lines)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- {T("{0} : {1}", line.Label, line.Value)}");
            }
        }

        return text.AppendLine().AppendLine(Footer).ToString();
    }

    /// <summary>Ce que le masquage a réellement retiré (la catégorie, jamais la valeur).</summary>
    public string MaskingNote => Privacy.Removed.Count == 0
        ? T("Masquage : aucun nom d'utilisateur, nom de PC, numéro de série, adresse MAC ni e-mail n'a été trouvé dans ce relevé.")
        : T("Masquage : MAUS a retiré de ce relevé : {0}.", string.Join(", ", Privacy.Removed));
}

/// <summary>
/// Le dossier du PC : une page pour une revente, une réparation ou une garantie. Un acheteur peut la lire : seules y figurent des
/// valeurs lues par MAUS, sans jugement ajouté ; ce qui n'a pas été lu reste « non lu ». Liste fermée de champs, puis masquage.
/// </summary>
public static class PcFile
{
    /// <summary>Préfixe des constats de santé par disque (M11).</summary>
    private const string DiskFindingPrefix = "M11.disk-";

    public static PcFileDocument Build(PcFileInput input, PrivacyFilter privacy)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(privacy);

        List<HtmlPageSection> sections =
        [
            System(input),
            Hardware(input),
            .. input.Inventory.Disks.Select(disk => Disk(disk, input)),
            DiskHealth(input),
            Battery(input),
            Score(input),
        ];
        return new(
            privacy.Mask(T("Dossier du PC")),
            privacy.Mask(T("Relevé par MAUS, non certifié")),
            [
                privacy.Mask(T("Relevé le {0} avec MAUS {1}, sur ce PC, par son propriétaire.", Date(input.At, "f"), AppVersion.Display)),
                privacy.Mask(T("Ce fichier peut être modifié à la main : il ne prouve rien, et MAUS ne garantit pas ce PC.")),
            ],
            [.. sections.Select(section => new HtmlPageSection(
                privacy.Mask(section.Title),
                [.. section.Lines.Select(line => new HtmlPageLine(privacy.Mask(line.Label), privacy.Mask(line.Value)))],
                section.Note is { } note ? privacy.Mask(note) : null))],
            privacy.Mask(T("Ce relevé ne contient ni nom d'utilisateur, ni nom de PC, ni numéro de série, ni adresse MAC, ni e-mail. MAUS n'a rien envoyé.")),
            privacy);
    }

    private static HtmlPageSection System(PcFileInput input)
    {
        var system = input.Details?.System;
        var model = string.Join(' ', new[] { system?.Manufacturer, system?.Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new(T("Ce PC et Windows"),
        [
            new(T("Modèle"), model.Length > 0 ? model : T("non lu")),
            new(T("Type"), system is null ? T("non lu") : Labels.Of(system.FormFactor)),
            new(T("Version de Windows"), input.Windows is { Build: > 0 } windows
                ? T("{0} {1} · build {2} · édition {3}", windows.ProductName, windows.DisplayVersion, windows.FullBuild, windows.EditionId)
                : T("non lu")),
        ]);
    }

    private static HtmlPageSection Hardware(PcFileInput input)
    {
        var inventory = input.Inventory;
        var cpu = inventory.Cpu;
        var board = string.Join(' ', new[] { inventory.Board.Manufacturer, inventory.Board.Product }.Where(s => !string.IsNullOrWhiteSpace(s)));
        List<HtmlPageLine> lines =
        [
            new(T("Processeur"), cpu.Cores > 0 ? T("{0} ({1} cœurs, {2} threads)", cpu.Name, cpu.Cores, cpu.Threads) : cpu.Name),
            new(T("Carte mère"), board.Length > 0 ? board : T("non lu")),
            new(T("Version du BIOS"), inventory.Board.BiosVersion is { } bios
                ? inventory.Board.BiosDate is { } date ? T("{0} du {1}", bios, date.ToString("d", Culture)) : bios
                : T("non lu")),
        ];
        lines.AddRange(inventory.Memory.Count == 0
            ? [new(T("Mémoire vive"), T("non lu"))]
            : inventory.Memory.Select(module => new HtmlPageLine(T("Mémoire vive · {0}", module.Slot), Module(module))));
        lines.AddRange(inventory.Gpus.Count == 0
            ? [new(T("Carte graphique"), T("non lu"))]
            : inventory.Gpus.Select(gpu => new HtmlPageLine(T("Carte graphique"), gpu.MemoryBytes is { } vram ? $"{gpu.Info.Name} · {HealthParsers.FormatGigabytes(vram)}" : gpu.Info.Name)));
        lines.AddRange((input.Details?.Displays ?? []).Select(display => new HtmlPageLine(T("Écran"),
            display.NativeWidth is { } width && display.NativeHeight is { } height ? $"{display.Name} · {width} × {height}" : display.Name)));
        return new(T("Matériel"), lines);
    }

    private static string Module(MemoryModuleInfo module)
    {
        List<string> parts = [HealthParsers.FormatGigabytes(module.CapacityBytes)];
        if (module.Generation is { } generation)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"DDR{generation}"));
        }

        if (module.Manufacturer is { } maker)
        {
            parts.Add(maker);
        }

        if (module.PartNumber is { } part)
        {
            parts.Add(part);
        }

        if (module.ConfiguredSpeedMts is { } speed)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{speed} MT/s"));
        }

        return string.Join(" · ", parts);
    }

    private static HtmlPageSection Disk(DiskIdentity disk, PcFileInput input)
    {
        var kind = string.Join(" · ", new[] { disk.Media, disk.Bus }.Where(s => s is not null));
        return new(T("Disque : {0}", disk.Model),
        [
            new(T("Type"), kind.Length > 0 ? kind : T("non lu")),
            new(T("Capacité"), disk.SizeBytes is { } size ? HealthParsers.FormatGigabytes(size) : T("non lu")),

            // Comme l'audit (M11) : un disque dur à plateaux répond souvent 0 %, ce qui ne dit rien de son usure.
            new(T("Usure"), disk.Media == "HDD" ? T("sans objet pour un disque dur") : disk.WearPercent is { } wear ? $"{wear} %" : NotRead(input)),
            new(T("Température au moment du relevé"), disk.TemperatureC is { } temperature ? $"{temperature} °C" : NotRead(input)),
            new(T("Heures de fonctionnement"), disk.PowerOnHours?.ToString("N0", Culture) ?? NotRead(input)),
            new(T("Micrologiciel"), disk.Firmware ?? T("non lu")),
        ], Rights(input));
    }

    /// <summary>Le verdict de Windows sur chaque disque, tel que le dernier audit l'a lu ; sinon « non lu », jamais « sain ».</summary>
    private static HtmlPageSection DiskHealth(PcFileInput input)
    {
        var audit = input.Audits.OrderBy(a => a.At).LastOrDefault(a => a.Findings.Any(IsDiskFinding));
        if (audit is null)
        {
            return new(T("Santé des disques selon Windows"), [new(T("État de santé"), T("non lu : aucun audit gardé sur ce PC"))]);
        }

        // Valeur lue si cet audit est celui de cette ouverture (les fichiers gardés n'ont que l'état), sinon l'état en mot.
        return new(T("Santé des disques selon Windows"),
            [.. audit.Findings.Where(IsDiskFinding).Select(finding => new HtmlPageLine(finding.Title,
                finding.Current ?? (finding.Status == FindingStatus.Unknown ? T("non lu") : Labels.Of(finding.Status))))],
            audit.HasScore
                ? T("D'après l'audit du {0}.", Date(audit.At, "d"))
                : T("D'après l'audit du {0}, fait sans droits administrateur.", Date(audit.At, "d")));
    }

    private static bool IsDiskFinding(SnapshotFinding finding) => finding.Id.StartsWith(DiskFindingPrefix, StringComparison.Ordinal);

    private static HtmlPageSection Battery(PcFileInput input)
    {
        if (input.Inventory.Batteries.Count > 0)
        {
            return new(T("Batterie"),
                [.. input.Inventory.Batteries.SelectMany(battery => BatteryLines(battery, input))],
                Rights(input));
        }

        return input.BatteryDetected
            ? new(T("Batterie"), [new(T("Capacité restante"), NotRead(input))], T("Windows annonce une batterie, mais sa capacité n'a pas pu être lue."))
            : new(T("Batterie"), [new(T("Batterie"), T("aucune annoncée par Windows"))]);
    }

    private static IEnumerable<HtmlPageLine> BatteryLines(BatteryIdentity battery, PcFileInput input)
    {
        var name = $"{battery.Manufacturer} {battery.Name}".Trim();
        return
        [
            new(T("Batterie"), name.Length > 0 ? name : T("non lu")),
            new(T("Capacité restante"), battery.HealthPercent is { } health ? T("{0} % de la capacité d'origine", health) : NotRead(input)),
            new(T("Capacité d'origine"), battery.DesignCapacityMwh is { } design ? $"{design.ToString("N0", Culture)} mWh" : NotRead(input)),
            new(T("Capacité actuelle"), battery.FullChargeCapacityMwh is { } full ? $"{full.ToString("N0", Culture)} mWh" : NotRead(input)),
            new(T("Cycles de charge"), battery.CycleCount?.ToString("N0", Culture) ?? NotRead(input)),
        ];
    }

    private static HtmlPageSection Score(PcFileInput input)
    {
        var audit = input.Audits.OrderBy(a => a.At).LastOrDefault(a => a.Score is >= 0 and <= 100);
        return audit is null
            ? new(T("Score de santé"), [new(T("Dernier score de santé MAUS"), T("aucun score gardé sur ce PC"))])
            : new(T("Score de santé"),
                [new(T("Dernier score de santé MAUS"), audit.IsPartial
                    ? T("{0}/100, audit du {1} (score partiel : des contrôles n'ont pas été lus)", audit.Score, Date(audit.At, "d"))
                    : T("{0}/100, audit du {1}", audit.Score, Date(audit.At, "d")))],
                T("Repère calculé par MAUS, pas une mesure officielle."));
    }

    private static string NotRead(PcFileInput input) => input.ReadWithRights ? T("non lu") : T("non lu sans droits administrateur");

    private static string Rights(PcFileInput input) => input.ReadWithRights
        ? T("Valeurs telles que Windows les donne, lues à l'instant avec les droits administrateur.")
        : T("Valeurs telles que Windows les donne, lues à l'instant sans droits administrateur.");

    private static string Date(DateTimeOffset at, string format) => at.ToLocalTime().ToString(format, Culture);
}
