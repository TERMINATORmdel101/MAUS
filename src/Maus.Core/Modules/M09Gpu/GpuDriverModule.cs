using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Maus.Core.Hardware;
using Maus.Core.Platform;
using Microsoft.Win32;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M09Gpu;

/// <summary>
/// Module 9 — Pilotes carte graphique. Compare le pilote de chaque GPU à la dernière version officielle connue
/// et contrôle trois réglages bridants : HAGS, Resizable BAR et largeur du lien PCIe. Ne télécharge rien.
/// </summary>
public sealed partial class GpuDriverModule : Fixes.IFixableModule
{
    private const string DriverCategory = "Pilote";
    private static string SettingsCategory => T("Réglages de la carte graphique");
    private const string UpdateCategory = "Windows Update";
    private const string GraphicsDriversKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string WindowsUpdatePolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string DriverSearchingKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching";
    private const string VideoControllerQuery = "SELECT Name, PNPDeviceID, DriverVersion, DriverDate, InfFilename FROM Win32_VideoController";

    internal static readonly string[] NvidiaSmiQueryArguments = ["--query-gpu=pci.bus_id,driver_version,name", "--format=csv,noheader"];
    internal static readonly string[] NvidiaSmiMemoryArguments = ["-q", "-d", "MEMORY"];

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    private readonly IGpuSchedulingReader _scheduling;
    private readonly GpuDriverCatalog? _catalog;

    public GpuDriverModule()
        : this(new D3dkmtGpuSchedulingReader())
    {
    }

    internal GpuDriverModule(IGpuSchedulingReader scheduling, GpuDriverCatalog? catalog = null)
    {
        _scheduling = scheduling;
        _catalog = catalog;
    }

    public string Id => "M09";

    public string Title => T("Pilotes carte graphique");

    public int Order => 90;

    public async Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken)
    {
        var catalog = _catalog ?? GpuDriverCatalog.Default;
        var gpus = ReadGpus(context);
        var findings = new List<Finding>();

        if (gpus.Count == 0)
        {
            findings.Add(Finding.Unknown(
                "M09.gpu",
                T("Carte graphique détectée"),
                T("Aucune carte graphique PCI n'a été trouvée par WMI : impossible de contrôler son pilote."),
                DriverCategory));
        }
        else
        {
            findings.Add(DetectBasicDriver(gpus, catalog));

            var withDriver = gpus.Where(g => !g.IsBasicDriver).ToList();
            var smi = withDriver.Any(g => g.Vendor == HardwareVendor.Nvidia)
                ? await ReadNvidiaSmiAsync(context.Commands, cancellationToken).ConfigureAwait(false)
                : NvidiaSmiData.Empty;

            findings.AddRange(withDriver.Select(gpu => DetectDriverVersion(context, catalog, gpu, gpus, smi)));
            findings.AddRange(withDriver.Where(g => !g.IsIntegrated).Select(DetectPcieLink));
            findings.Add(DetectHags(context.Registry, catalog, withDriver));
            findings.AddRange(withDriver.Where(g => !g.IsIntegrated).Select(gpu => DetectResizableBar(context, catalog, gpu, gpus, smi)));
        }

        findings.Add(DetectWindowsUpdateDrivers(context));
        findings.Add(DetectDeviceInstallationSettings(context));
        return findings;
    }

    private static List<GpuDevice> ReadGpus(AuditContext context)
    {
        var rows = TryQuery(context.Cim, VideoControllerQuery)
            .Where(r => r.GetString("PNPDeviceID")?.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        var infos = context.Hardware.Gpus.Count > 0
            ? context.Hardware.Gpus
            : rows.Select(r =>
            {
                var name = r.GetString("Name")?.Trim() ?? "GPU inconnu";
                var pnp = r.GetString("PNPDeviceID") ?? string.Empty;
                var vendor = GpuClassifier.VendorOf(pnp, name);
                return new GpuInfo(name, vendor, r.GetString("DriverVersion"), r.GetDateTime("DriverDate"), pnp, GpuClassifier.IsLikelyIntegrated(vendor, name));
            }).ToList();

        var slugs = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<GpuDevice>();
        foreach (var info in infos)
        {
            var baseSlug = info.Vendor == HardwareVendor.Other ? "gpu" : info.Vendor.ToString().ToLowerInvariant();
            var index = slugs[baseSlug] = slugs.GetValueOrDefault(baseSlug) + 1;
            var row = rows.FirstOrDefault(r => string.Equals(r.GetString("PNPDeviceID"), info.PnpDeviceId, StringComparison.OrdinalIgnoreCase));
            result.Add(new GpuDevice(
                info,
                index == 1 ? baseSlug : $"{baseSlug}-{index}",
                row?.GetString("InfFilename"),
                ReadDeviceProperties(context.Cim, info.PnpDeviceId)));
        }

        return result;
    }

    // ----- Pilote de base Microsoft -----

    private static Finding DetectBasicDriver(IReadOnlyList<GpuDevice> gpus, GpuDriverCatalog catalog)
    {
        const string id = "M09.basic-display-driver";
        var title = T("Pilote du fabricant installé");
        var basic = gpus.FirstOrDefault(g => g.IsBasicDriver);
        if (basic is null)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = DriverCategory,
                Status = FindingStatus.Ok,
                Severity = Severity.High,
                Current = string.Join(", ", gpus.Select(g => g.Name)),
                Expected = T("pilote du fabricant (NVIDIA, AMD ou Intel)"),
                Explanation = T("Chaque carte graphique utilise le pilote de son fabricant, indispensable à l'accélération 3D, aux jeux et aux hautes fréquences d'écran."),
            };
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = DriverCategory,
            Status = FindingStatusExtensions.ForDeviation(Severity.High),
            Severity = Severity.High,
            Current = T("carte graphique de base Microsoft (carte {0} sans son pilote)", VendorLabel(basic.Vendor)),
            Expected = T("pilote du fabricant (NVIDIA, AMD ou Intel)"),
            Explanation = T("La carte graphique fonctionne avec le pilote de secours de Windows : pas d'accélération 3D, jeux inutilisables, " +
                          "fréquence et résolution de l'écran souvent limitées."),
            Advice = T("Installez le pilote officiel depuis {0} ou l'application officielle du fabricant. " +
                     "Si le pilote refuse de s'installer ou si l'écran reste noir, DDU (téléchargé uniquement sur wagnardsoft.com), " +
                     "lancé en mode sans échec et réseau coupé, permet de repartir de zéro.", catalog.DownloadUrlFor(basic.Vendor)),
        };
    }

    // ----- Version du pilote -----

    private static Finding DetectDriverVersion(AuditContext context, GpuDriverCatalog catalog, GpuDevice gpu, IReadOnlyList<GpuDevice> all, NvidiaSmiData smi)
    {
        var id = $"M09.driver.{gpu.Slug}";
        var title = T("Pilote à jour : {0}", gpu.Name);
        var branch = catalog.FindBranch(gpu.Vendor, gpu.Name);
        var versions = ResolveVersions(context.Registry, gpu, all, smi, branch);
        var date = gpu.Info.DriverDate;

        if (versions.InstalledLabel is null && date is null)
        {
            return Finding.Unknown(id, title, T("Version et date du pilote illisibles dans WMI."), DriverCategory);
        }

        var ageDays = date is null ? (int?)null : (int)(context.Now.Date - date.Value.Date).TotalDays;
        var gapDays = date is null || branch is null ? (int?)null : branch.ReleasedOn.DayNumber - DateOnly.FromDateTime(date.Value).DayNumber;
        var comparison = versions.Installed is not null && versions.Latest is not null ? versions.Installed.CompareTo(versions.Latest) : (int?)null;
        var old = ageDays > catalog.MaxDriverAgeDays || gapDays > catalog.SignificantGapDays;

        var status = comparison switch
        {
            >= 0 => FindingStatus.Ok,
            < 0 when old => FindingStatusExtensions.ForDeviation(Severity.Low),
            < 0 => FindingStatus.Info,
            null when ageDays > catalog.MaxDriverAgeDays => FindingStatusExtensions.ForDeviation(Severity.Low),
            null => FindingStatus.Ok,
        };

        var current = versions.InstalledLabel ?? T("version illisible");
        if (versions.WindowsVersion is { } windowsVersion && !string.Equals(windowsVersion, versions.InstalledLabel, StringComparison.Ordinal))
        {
            current += $" (version Windows {windowsVersion})";
        }

        if (date is not null)
        {
            current += $", du {FormatDate(date.Value)}";
        }

        var downloadUrl = branch?.DownloadUrl ?? catalog.DownloadUrlFor(gpu.Vendor);
        var expected = branch is null
            ? T("pilote de moins de {0} mois", catalog.MaxDriverAgeDays / 30)
            : T("{0} ou plus récent ({1}, publié le {2})", versions.LatestLabel, branch.Label, FormatDate(branch.ReleasedOn));

        var explanation = branch is null
            ? T("MAUS ne connaît pas la dernière version pour cette carte : il juge seulement l'âge du pilote (plus de {0} mois = ancien).", catalog.MaxDriverAgeDays / 30)
            : T("MAUS compare le pilote installé à la dernière version officielle connue (catalogue vérifié le {0}).", FormatDate(catalog.CheckedOn));
        explanation += T(" Un pilote plus récent n'améliore les performances que dans certains jeux récents, mais il corrige aussi des bugs et des failles.");
        if (branch?.Note is { } note)
        {
            explanation += " " + note;
        }

        string? advice = null;
        if (status != FindingStatus.Ok)
        {
            advice = T("Téléchargez-le uniquement sur le site officiel ({0}) ou via l'application officielle du fabricant. " +
                     "N'utilisez DDU qu'en cas de problème : écran noir, plantages, passage de NVIDIA à AMD. " +
                     "Pour revenir en arrière : Gestionnaire de périphériques, onglet Pilote, « Restaurer le pilote ».", downloadUrl);
            if (context.Hardware.IsLaptop)
            {
                advice += T(" Sur un portable, passez d'abord par l'outil ou le site du fabricant du PC s'il impose ses propres pilotes.");
            }
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = DriverCategory,
            Status = status,
            Severity = Severity.Low,
            Current = current,
            Expected = expected,
            Explanation = explanation,
            Advice = advice,
        };
    }

    private static DriverVersions ResolveVersions(IRegistryReader registry, GpuDevice gpu, IReadOnlyList<GpuDevice> all, NvidiaSmiData smi, GpuDriverBranch? branch)
    {
        var windowsVersion = gpu.Info.DriverVersion?.Trim();
        switch (gpu.Vendor)
        {
            case HardwareVendor.Nvidia:
            {
                var marketing = MatchNvidia(gpu, all, smi.Gpus, g => g.BusNumber)?.DriverVersion ?? GpuParsers.NvidiaMarketingVersion(windowsVersion);
                var latest = branch?.Scheme == DriverVersionScheme.NvidiaMarketing ? branch.LatestVersion : null;
                return new DriverVersions(marketing ?? windowsVersion, GpuParsers.ParseVersion(marketing), latest, GpuParsers.ParseVersion(latest), windowsVersion);
            }

            case HardwareVendor.Amd:
            {
                var software = ReadDriverKeyString(registry, gpu, "RadeonSoftwareVersion");
                if (software is not null && branch?.Scheme == DriverVersionScheme.AmdSoftware)
                {
                    return new DriverVersions(software, GpuParsers.ParseVersion(software), branch.LatestVersion, GpuParsers.ParseVersion(branch.LatestVersion), windowsVersion);
                }

                var latestDriver = branch?.LatestDriverVersion;
                return new DriverVersions(software ?? windowsVersion, latestDriver is null ? null : GpuParsers.ParseVersion(windowsVersion), latestDriver ?? branch?.LatestVersion, GpuParsers.ParseVersion(latestDriver), windowsVersion);
            }

            default:
            {
                var latest = branch?.LatestDriverVersion ?? branch?.LatestVersion;
                return new DriverVersions(windowsVersion, GpuParsers.ParseVersion(windowsVersion), latest, GpuParsers.ParseVersion(latest), windowsVersion);
            }
        }
    }

    // ----- Lien PCIe -----

    private static Finding DetectPcieLink(GpuDevice gpu)
    {
        var id = $"M09.pcie-link.{gpu.Slug}";
        var title = T("Largeur du lien PCIe : {0}", gpu.Name);
        var width = gpu.GetProperty("DEVPKEY_PciDevice_CurrentLinkWidth");
        var maxWidth = gpu.GetProperty("DEVPKEY_PciDevice_MaxLinkWidth");
        if (width is null or 0 || maxWidth is null or 0)
        {
            return Finding.Unknown(id, title, T("Largeur du lien PCIe illisible pour cette carte."), SettingsCategory);
        }

        var speed = GpuParsers.PcieGeneration(gpu.GetProperty("DEVPKEY_PciDevice_CurrentLinkSpeed"));
        var maxSpeed = GpuParsers.PcieGeneration(gpu.GetProperty("DEVPKEY_PciDevice_MaxLinkSpeed"));
        var narrow = width < maxWidth;

        // Sur les Radeon récentes, la valeur lue peut être celle du commutateur PCIe interne à la carte (à confirmer).
        var status = !narrow
            ? FindingStatus.Ok
            : gpu.Vendor == HardwareVendor.Amd ? FindingStatus.Info : FindingStatusExtensions.ForDeviation(Severity.Medium);

        return new Finding
        {
            Id = id,
            Title = title,
            Category = SettingsCategory,
            Status = status,
            Severity = Severity.Medium,
            Current = T("x{0}, {1} au moment de l'audit", width, speed),
            Expected = T("x{0} (vitesse maximale de la carte : {1})", maxWidth, maxSpeed),
            Explanation = T("La carte graphique échange avec le processeur par des lignes PCIe. Une largeur inférieure au maximum de la carte " +
                          "(x8 au lieu de x16, par exemple) trahit une carte mal enfoncée, un mauvais slot ou des lignes partagées avec un SSD M.2. " +
                          "La vitesse (Gen) baisse au repos pour économiser l'énergie : seule la largeur est jugée ici."),
            Advice = !narrow
                ? null
                : gpu.Vendor == HardwareVendor.Amd
                    ? T("Sur les Radeon, cette valeur peut venir du commutateur interne de la carte : confirmez avec GPU-Z (onglet Bus Interface) avant de démonter quoi que ce soit.")
                    : T("PC éteint et débranché, vérifiez que la carte est bien enfoncée dans le slot PCIe principal (le plus proche du processeur). " +
                      "Consultez le manuel de la carte mère : un SSD M.2 ou une seconde carte peut partager ces lignes. Certaines cartes sont nativement x8."),
        };
    }

    // ----- HAGS -----

    private Finding DetectHags(IRegistryReader registry, GpuDriverCatalog catalog, IReadOnlyList<GpuDevice> gpus)
    {
        const string id = "M09.hags";
        var title = T("Planification GPU accélérée par le matériel (HAGS)");
        var target = gpus
            .OrderByDescending(g => catalog.NeedsHagsForFrameGeneration(g.Name))
            .ThenBy(g => g.IsIntegrated)
            .FirstOrDefault();
        if (target is null)
        {
            return Finding.Unknown(id, title, T("Aucune carte graphique n'a de pilote du fabricant : HAGS ne s'applique pas."), SettingsCategory);
        }

        int? mode;
        try
        {
            mode = registry.GetDword(RegistryHive.LocalMachine, GraphicsDriversKey, "HwSchMode");
        }
        catch (MausAccessDeniedException)
        {
            mode = null;
        }

        var ids = PciIds(target.Info.PnpDeviceId);
        var kernel = _scheduling.Read()?.FirstOrDefault(a => ids is not null && a.VendorId == ids.Value.Vendor && a.DeviceId == ids.Value.Device);
        var needsFrameGeneration = catalog.NeedsHagsForFrameGeneration(target.Name);
        var explanation = T("HAGS laisse la carte graphique gérer elle-même sa file de travail. Elle est indispensable à DLSS Frame Generation " +
                                   "(RTX 40 et suivantes) et active par défaut sous Windows 11 sur les cartes compatibles. Ailleurs, le gain attendu est faible.");

        if (kernel is { Supported: false })
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = SettingsCategory,
                Status = FindingStatus.Info,
                Current = T("non prise en charge par {0} ou son pilote", target.Name),
                Explanation = explanation,
            };
        }

        bool? enabled = kernel is not null ? kernel.Enabled : mode switch
        {
            2 => true,
            1 => false,
            _ => null,
        };
        if (enabled is null)
        {
            return Finding.Unknown(id, title, T("État réel de HAGS illisible et valeur HwSchMode absente : Windows et le pilote appliquent leur choix par défaut."), SettingsCategory);
        }

        if (kernel is { Enabled: false } && mode == 2)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = SettingsCategory,
                Status = FindingStatus.Info,
                Current = T("activation demandée (HwSchMode = 2), effective au prochain redémarrage"),
                Expected = needsFrameGeneration ? T("activée (requise pour DLSS Frame Generation)") : T("au choix : gain faible sur cette carte"),
                Explanation = explanation,
                Advice = T("Redémarrez le PC pour que HAGS s'active."),
            };
        }

        var source = kernel is not null ? string.Empty : $" (valeur HwSchMode = {mode})";
        var status = enabled.Value
            ? FindingStatus.Ok
            : needsFrameGeneration ? FindingStatusExtensions.ForDeviation(Severity.Low) : FindingStatus.Info;

        return new Finding
        {
            Id = id,
            Title = title,
            Category = SettingsCategory,
            Status = status,
            Severity = Severity.Low,
            Current = (enabled.Value ? T("activée") : T("désactivée")) + source,
            Expected = needsFrameGeneration ? T("activée (requise pour DLSS Frame Generation)") : T("au choix : gain faible sur cette carte"),
            Explanation = explanation,
            Advice = enabled.Value
                ? null
                : T("Paramètres > Système > Écran > Graphiques > « Modifier les paramètres graphiques par défaut », puis redémarrez le PC."),
            Fixable = !enabled.Value && needsFrameGeneration,
        };
    }

    // ----- Resizable BAR -----

    private static Finding DetectResizableBar(AuditContext context, GpuDriverCatalog catalog, GpuDevice gpu, IReadOnlyList<GpuDevice> all, NvidiaSmiData smi)
    {
        var id = $"M09.resizable-bar.{gpu.Slug}";
        var title = $"Resizable BAR : {gpu.Name}";
        var capable = catalog.IsResizableBarCapable(gpu.Vendor, gpu.Name);
        var explanation = T("Resizable BAR (Smart Access Memory chez AMD) permet au processeur d'accéder à toute la mémoire de la carte graphique d'un coup. " +
                                   "NVIDIA annonce quelques pour cent de gain, jusqu'à 12 % dans certains jeux ; Intel le requiert pour des performances optimales sur Arc.");

        long? barMiB = null;
        if (gpu.Vendor == HardwareVendor.Nvidia)
        {
            barMiB = MatchNvidia(gpu, all, smi.Bar1, b => b.BusNumber)?.TotalMiB;
        }

        barMiB ??= ReadLargestMemoryRangeMiB(context.Cim, gpu.Info.PnpDeviceId);

        if (!capable)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = SettingsCategory,
                Status = FindingStatus.Info,
                Current = barMiB is > 256 ? T("actif (fenêtre de {0} Mio)", barMiB) : T("non pris en charge par cette carte"),
                Explanation = explanation,
            };
        }

        if (barMiB is null)
        {
            return Finding.Unknown(id, title, T("Taille de la fenêtre mémoire de la carte illisible."), SettingsCategory);
        }

        var active = barMiB > 256;
        var vram = ReadVramMiB(context.Registry, gpu);
        return new Finding
        {
            Id = id,
            Title = title,
            Category = SettingsCategory,
            Status = active ? FindingStatus.Ok : FindingStatusExtensions.ForDeviation(Severity.Low),
            Severity = Severity.Low,
            Current = active ? T("actif (fenêtre de {0} Mio)", barMiB) : T("inactif (fenêtre de {0} Mio)", barMiB),
            Expected = vram is null ? T("actif") : T("actif (fenêtre proche de la mémoire de la carte, {0} Mio)", vram),
            Explanation = explanation,
            Advice = active
                ? null
                : T("Il s'active dans le BIOS : « Above 4G Decoding » et « Re-Size BAR Support » activés, CSM désactivé (démarrage UEFI). " +
                  "Ces réglages sont guidés, jamais appliqués par MAUS : voir le Module 8."),
        };
    }

    // ----- Windows Update -----

    private static Finding DetectWindowsUpdateDrivers(AuditContext context)
    {
        const string id = "M09.windows-update-drivers";
        var title = T("Pilotes installés par Windows Update");
        int? value;
        try
        {
            value = context.Registry.GetDword(RegistryHive.LocalMachine, WindowsUpdatePolicyKey, "ExcludeWUDriversInQualityUpdate");
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(id, title, UpdateCategory);
        }

        var home = context.Windows.IsHomeEdition;
        var excluded = value == 1 && !home;
        return new Finding
        {
            Id = id,
            Title = title,
            Category = UpdateCategory,
            Status = FindingStatus.Info,
            Current = excluded
                ? T("exclus (stratégie ExcludeWUDriversInQualityUpdate)")
                : value == 1 ? T("stratégie présente mais sans effet sur l'édition Famille") : T("autorisés (réglage par défaut)"),
            Expected = T("au choix de l'utilisateur (autorisés par défaut)"),
            Explanation = T("Windows Update peut remplacer un pilote graphique installé à la main par une autre version. " +
                          "La stratégie ExcludeWUDriversInQualityUpdate (Pro, Entreprise et Éducation) exclut tous les pilotes de Windows Update : " +
                          "elle bloque donc aussi ceux de vos autres périphériques."),
            Advice = excluded
                ? T("Mettez vous-même à jour les pilotes de vos périphériques (carte graphique, Wi-Fi, audio, chipset) : voir aussi les Modules 3 et 8.")
                : home
                    ? T("Sur l'édition Famille, seul le réglage « Paramètres d'installation de périphérique » limite les pilotes automatiques.")
                    : T("Si Windows Update remplace votre pilote graphique, MAUS pourra bloquer ces pilotes à votre demande (blocage valable pour tous les périphériques)."),
            Fixable = !home,
        };
    }

    private static Finding DetectDeviceInstallationSettings(AuditContext context)
    {
        const string id = "M09.device-installation-settings";
        var title = T("Téléchargement automatique des pilotes de périphériques");
        int? value;
        try
        {
            value = context.Registry.GetDword(RegistryHive.LocalMachine, DriverSearchingKey, "SearchOrderConfig");
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(id, title, UpdateCategory);
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = UpdateCategory,
            Status = FindingStatus.Info,
            Current = value == 0 ? T("désactivé (SearchOrderConfig = 0)") : T("activé (réglage par défaut)"),
            Expected = T("au choix de l'utilisateur"),
            Explanation = T("Le réglage « Paramètres d'installation de périphérique » (Panneau de configuration > Système) laisse Windows télécharger " +
                          "les applications et pilotes des fabricants. Son effet sur les pilotes graphiques n'est que partiel."),
            Fixable = context.Windows.IsHomeEdition,
        };
    }

    // ----- Lectures -----

    private static async Task<NvidiaSmiData> ReadNvidiaSmiAsync(ICommandRunner commands, CancellationToken cancellationToken)
    {
        var query = await RunAsync(commands, NvidiaSmiQueryArguments, cancellationToken).ConfigureAwait(false);
        var memory = await RunAsync(commands, NvidiaSmiMemoryArguments, cancellationToken).ConfigureAwait(false);
        return new NvidiaSmiData(
            query is null ? [] : GpuParsers.ParseNvidiaSmiQuery(query),
            memory is null ? [] : GpuParsers.ParseNvidiaBar1(memory));
    }

    private static async Task<string?> RunAsync(ICommandRunner commands, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await commands.RunAsync("nvidia-smi", arguments, CommandTimeout, cancellationToken).ConfigureAwait(false);
            return result is { TimedOut: false, ExitCode: 0 } ? result.StandardOutput : null;
        }
        catch (Exception ex) when (ex is DataSourceUnavailableException or InvalidOperationException or MausAccessDeniedException)
        {
            return null;
        }
    }

    /// <summary>Correspondance entre une carte NVIDIA et une ligne de nvidia-smi : par numéro de bus, sinon s'il n'y en a qu'une de chaque.</summary>
    private static T? MatchNvidia<T>(GpuDevice gpu, IReadOnlyList<GpuDevice> all, IReadOnlyList<T> entries, Func<T, int?> busOf)
        where T : class
    {
        if (gpu.BusNumber is { } bus && entries.FirstOrDefault(e => busOf(e) == bus) is { } byBus)
        {
            return byBus;
        }

        return entries.Count == 1 && all.Count(g => g.Vendor == HardwareVendor.Nvidia) == 1 ? entries[0] : null;
    }

    internal static string DeviceQuery(string pnpDeviceId) =>
        $"SELECT * FROM Win32_PnPEntity WHERE PNPDeviceID = '{GpuParsers.EscapeWql(pnpDeviceId)}'";

    internal static string MemoryRangesQuery(string pnpDeviceId) =>
        "ASSOCIATORS OF {Win32_PnPEntity.DeviceID=\"" + pnpDeviceId.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) +
        "\"} WHERE ResultClass = Win32_DeviceMemoryAddress";

    private static Dictionary<string, CimRow> ReadDeviceProperties(ICimReader cim, string pnpDeviceId)
    {
        var properties = new Dictionary<string, CimRow>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(pnpDeviceId))
        {
            return properties;
        }

        try
        {
            // Sans liste de clés, la méthode renvoie toutes les propriétés : une clé inconnue ferait échouer tout l'appel.
            var output = cim.InvokeMethod(DeviceQuery(pnpDeviceId), "GetDeviceProperties");
            foreach (var row in output?.GetRows("deviceProperties") ?? [])
            {
                if (row.GetString("KeyName") is { } key)
                {
                    properties.TryAdd(key, row);
                }
            }
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            // Propriétés indisponibles : les contrôles concernés passent en « indéterminé ».
        }

        return properties;
    }

    private static long? ReadLargestMemoryRangeMiB(ICimReader cim, string pnpDeviceId)
    {
        var sizes = TryQuery(cim, MemoryRangesQuery(pnpDeviceId))
            .Select(r => (Start: r.GetInt64("StartingAddress"), End: r.GetInt64("EndingAddress")))
            .Where(r => r.Start is not null && r.End > r.Start)
            .Select(r => (r.End!.Value - r.Start!.Value + 1) / (1024 * 1024))
            .ToList();
        return sizes.Count == 0 ? null : sizes.Max();
    }

    private static long? ReadVramMiB(IRegistryReader registry, GpuDevice gpu)
    {
        if (gpu.DriverKey is not { } key)
        {
            return null;
        }

        try
        {
            return registry.GetValue(RegistryHive.LocalMachine, key, "HardwareInformation.qwMemorySize") switch
            {
                long bytes when bytes > 0 => bytes / (1024 * 1024),
                int bytes when bytes > 0 => bytes / (1024 * 1024),
                _ => null,
            };
        }
        catch (MausAccessDeniedException)
        {
            return null;
        }
    }

    private static string? ReadDriverKeyString(IRegistryReader registry, GpuDevice gpu, string name)
    {
        if (gpu.DriverKey is not { } key)
        {
            return null;
        }

        try
        {
            return registry.GetString(RegistryHive.LocalMachine, key, name)?.Trim() is { Length: > 0 } value ? value : null;
        }
        catch (MausAccessDeniedException)
        {
            return null;
        }
    }

    private static IReadOnlyList<CimRow> TryQuery(ICimReader cim, string wql)
    {
        try
        {
            return cim.Query(wql);
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            return [];
        }
    }

    private static bool IsReadFailure(Exception ex) =>
        ex is MausAccessDeniedException or DataSourceUnavailableException or ManagementException or COMException or UnauthorizedAccessException;

    private static (int Vendor, int Device)? PciIds(string pnpDeviceId)
    {
        var match = PciIdPattern().Match(pnpDeviceId);
        return match.Success
            ? (int.Parse(match.Groups["ven"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture), int.Parse(match.Groups["dev"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            : null;
    }

    private static string VendorLabel(HardwareVendor vendor) => vendor switch
    {
        HardwareVendor.Nvidia => "NVIDIA",
        HardwareVendor.Amd => "AMD",
        HardwareVendor.Intel => "Intel",
        _ => "graphique",
    };

    private static string FormatDate(DateTime date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string FormatDate(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"VEN_(?<ven>[0-9A-F]{4})&DEV_(?<dev>[0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex PciIdPattern();

    private sealed record DriverVersions(string? InstalledLabel, Version? Installed, string? LatestLabel, Version? Latest, string? WindowsVersion);

    private sealed record NvidiaSmiData(IReadOnlyList<NvidiaSmiGpu> Gpus, IReadOnlyList<NvidiaBar1> Bar1)
    {
        public static NvidiaSmiData Empty { get; } = new([], []);
    }

    /// <summary>Carte graphique PCI enrichie des détails WMI et des propriétés de périphérique.</summary>
    private sealed record GpuDevice(GpuInfo Info, string Slug, string? InfFilename, IReadOnlyDictionary<string, CimRow> Properties)
    {
        public string Name => Info.Name;

        public HardwareVendor Vendor => Info.Vendor;

        public bool IsIntegrated => Info.IsIntegrated;

        /// <summary>Carte graphique de base Microsoft : pilote de secours <c>display.inf</c>, nom traduit selon la langue.</summary>
        public bool IsBasicDriver =>
            string.Equals(InfFilename, "display.inf", StringComparison.OrdinalIgnoreCase) ||
            Name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
            Name.Contains("de base Microsoft", StringComparison.OrdinalIgnoreCase);

        public int? BusNumber => GetProperty("DEVPKEY_Device_BusNumber") is { } bus ? (int)bus : null;

        /// <summary>Clé du pilote sous <c>Control\Class</c>, d'après <c>DEVPKEY_Device_Driver</c>.</summary>
        public string? DriverKey =>
            Properties.TryGetValue("DEVPKEY_Device_Driver", out var row) && row.GetString("Data") is { Length: > 0 } driver
                ? $@"SYSTEM\CurrentControlSet\Control\Class\{driver}"
                : null;

        public long? GetProperty(string key) => Properties.TryGetValue(key, out var row) ? row.GetInt64("Data") : null;
    }
}
