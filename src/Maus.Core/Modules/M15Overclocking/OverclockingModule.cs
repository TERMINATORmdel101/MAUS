using Maus.Core.Hardware;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Modules.M15Overclocking;

/// <summary>
/// Module 15 — Overclocking et undervolting. N'overclocke rien : détecte le processeur, la carte graphique et le type de PC,
/// recommande l'outil officiel adapté avec son adresse, propose une recherche de tutoriel pour le modèle exact et
/// déconseille tout réglage sur les Core de bureau de 13e et 14e génération.
/// </summary>
public sealed class OverclockingModule : IAuditModule
{
    internal const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    internal const string Wow64UninstallPath = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    internal const string AfterburnerUrl = "https://www.msi.com/Landing/afterburner/graphics-cards";
    internal const string RyzenMasterUrl = "https://www.amd.com/en/products/software/ryzen-master.html";
    internal const string XtuUrl = "https://www.intel.com/content/www/us/en/download/17881/intel-extreme-tuning-utility-intel-xtu.html";
    internal const string NvidiaAppUrl = "https://www.nvidia.com/en-us/software/nvidia-app/";
    internal const string AmdDriversUrl = "https://www.amd.com/en/support/download/drivers.html";
    internal const string IntelArcUrl = "https://www.intel.com/content/www/us/en/download/785597/intel-arc-iris-xe-graphics-windows.html";

    private const string StartCategory = "Avant de commencer";
    private const string CpuCategory = "Processeur";
    private const string GpuCategory = "Carte graphique";
    private const string ToolsCategory = "Outils";

    private const string UndervoltFirst =
        "Commencez par l'undervolting (baisser légèrement la tension) : moins de chaleur et de bruit, souvent sans perte de performance. "
        + "L'overclocking ne vient qu'ensuite, par petits pas.";

    private const string FakeSitesWarning =
        "Attention : plus de 50 faux sites MSI Afterburner ont diffusé un voleur de mots de passe et un mineur de cryptomonnaie "
        + "en 2022 (BleepingComputer, 23/11/2022).";

    private static readonly (RegistryHive Hive, string Path)[] UninstallSources =
    [
        (RegistryHive.LocalMachine, UninstallPath),
        (RegistryHive.LocalMachine, Wow64UninstallPath),
        (RegistryHive.CurrentUser, UninstallPath),
    ];

    public string Id => "M15";

    public string Title => "Overclocking et undervolting";

    public int Order => 150;

    public Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken)
    {
        var tools = ReadInstalledTools(context.Registry, out var toolsReadable);
        cancellationToken.ThrowIfCancellationRequested();

        var findings = new List<Finding> { BeforeYouStart(), DetectCpu(context.Hardware, tools) };
        findings.AddRange(DetectGpus(context.Hardware, tools));
        findings.Add(DetectTuningTools(tools, toolsReadable));
        findings.Add(DetectStabilityTools(tools, toolsReadable));

        // Les constats les plus graves d'abord ; l'ordre de lecture est conservé à gravité égale.
        return Task.FromResult<IReadOnlyList<Finding>>(findings.OrderByDescending(f => f.Status.Rank()).ToList());
    }

    /// <summary>Programmes installés (HKLM 64 et 32 bits, HKCU) reconnus comme outils de réglage ou de test.</summary>
    private static List<InstalledTool> ReadInstalledTools(IRegistryReader registry, out bool readable)
    {
        var found = new Dictionary<string, InstalledTool>(StringComparer.Ordinal);
        var deniedSources = 0;
        foreach (var (hive, path) in UninstallSources)
        {
            IReadOnlyList<string> keys;
            try
            {
                keys = registry.GetSubKeyNames(hive, path);
            }
            catch (MausAccessDeniedException)
            {
                deniedSources++;
                continue;
            }

            foreach (var key in keys)
            {
                var keyPath = $@"{path}\{key}";
                try
                {
                    var tool = OverclockingParsers.MatchTool(registry.GetString(hive, keyPath, "DisplayName"));
                    if (tool is not null && !found.ContainsKey(tool.Key))
                    {
                        found[tool.Key] = new InstalledTool(
                            tool,
                            registry.GetString(hive, keyPath, "DisplayVersion"),
                            registry.GetString(hive, keyPath, "Publisher"));
                    }
                }
                catch (MausAccessDeniedException)
                {
                    // Une clé illisible n'empêche pas de lire les autres.
                }
            }
        }

        readable = deniedSources < UninstallSources.Length;
        return OverclockingParsers.KnownTools.Where(t => found.ContainsKey(t.Key)).Select(t => found[t.Key]).ToList();
    }

    private static Finding BeforeYouStart() => new()
    {
        Id = "M15.before-you-start",
        Title = "Overclocking et undervolting : ce que MAUS fait et ne fait pas",
        Category = StartCategory,
        Status = FindingStatus.Info,
        Current = "aucun réglage appliqué par MAUS",
        Explanation = "L'overclocking et l'undervolting peuvent faire gagner quelques pour cent, mais aussi provoquer plantages, corruption de "
            + "données ou usure prématurée. Suivez une vidéo pour votre modèle exact, avancez par petits pas et testez la stabilité à chaque "
            + "étape. Téléchargez les outils uniquement sur le site officiel. Ces réglages se font hors de l'utilitaire, sous votre contrôle : "
            + "il ne peut pas en garantir le résultat. MAUS n'overclocke rien : il indique l'outil officiel adapté à votre matériel.",
        Advice = "Avant de commencer : 1) BIOS à jour (Module 8) ; 2) pilote graphique à jour (Module 9) ; 3) mesure de performance de "
            + "référence (Module 11, le test intégré arrive en V0.3) ; 4) aucune erreur matérielle WHEA dans le journal (Module 2).",
    };

    private static Finding DetectCpu(HardwareProfile hardware, List<InstalledTool> tools)
    {
        const string id = "M15.cpu";
        var cpu = hardware.Cpu;
        if (string.IsNullOrWhiteSpace(cpu.Name) || cpu == CpuInfo.Unknown)
        {
            return Finding.Unknown(id, "Processeur : outil de réglage recommandé", "Le processeur n'a pas pu être identifié.", CpuCategory);
        }

        var model = OverclockingParsers.CpuModel(cpu.Name);
        if (cpu.Vendor == HardwareVendor.Intel && cpu.IsIntelRaptorLakeDesktop)
        {
            return RaptorLakeWarning(model, Find(tools, "xtu"));
        }

        if (hardware.IsLaptop)
        {
            return LaptopCpu(hardware, model);
        }

        return cpu.Vendor switch
        {
            HardwareVendor.Intel when OverclockingParsers.IsUnlockedIntel(model) => IntelUnlocked(model, Find(tools, "xtu")),
            HardwareVendor.Intel => IntelLocked(model),
            HardwareVendor.Amd => AmdRyzen(model, cpu.Name, Find(tools, "ryzen-master")),
            _ => new Finding
            {
                Id = id,
                Title = $"Processeur {model} : pas d'outil de réglage connu",
                Category = CpuCategory,
                Status = FindingStatus.Info,
                Current = model,
                Explanation = "MAUS ne connaît pas d'outil officiel de réglage pour ce processeur. Laissez-le sur ses réglages d'origine.",
            },
        };
    }

    private static Finding RaptorLakeWarning(string model, InstalledTool? xtu)
    {
        var advice = "Gardez le profil « Intel Default Settings » du BIOS et vérifiez que le BIOS apporte le microcode 0x12F ou plus récent "
            + "(voir Module 8).";
        if (xtu is not null)
        {
            advice += $" {xtu.Label} est installé : ne l'utilisez pas pour augmenter fréquences ou tensions.";
        }

        return new Finding
        {
            Id = "M15.cpu",
            Title = $"Processeur {model} : pas d'overclocking",
            Category = CpuCategory,
            Status = FindingStatus.Warning,
            Severity = Severity.Medium,
            Current = $"{model} (Core de bureau de 13e ou 14e génération)",
            Expected = "réglages Intel par défaut, microcode 0x12F ou plus récent",
            Explanation = "Les Core i5, i7 et i9 de bureau de 13e et 14e génération peuvent s'abîmer à cause de tensions trop élevées, avec des "
                + "plantages de plus en plus fréquents. Intel a corrigé ce défaut par des mises à jour du microcode (0x12F et suivantes) livrées "
                + "avec le BIOS. Sur ces processeurs : ni overclocking, ni undervolting agressif.",
            Advice = advice,
        };
    }

    private static Finding LaptopCpu(HardwareProfile hardware, string model)
    {
        var pcModel = OverclockingParsers.PcModel(hardware.Manufacturer, hardware.Model);
        var utility = OverclockingParsers.LaptopUtility(hardware.Manufacturer);
        var tool = utility is { } u
            ? $"Utilitaire du constructeur : {u.Utility}, à télécharger uniquement sur {u.Site}."
            : "Utilisez l'utilitaire fourni par le constructeur de votre portable, téléchargé sur son site officiel.";
        return new Finding
        {
            Id = "M15.cpu",
            Title = $"Processeur {model} (portable) : utilitaire du constructeur",
            Category = CpuCategory,
            Status = FindingStatus.Info,
            Current = $"{model}, PC portable",
            Explanation = "Sur un portable, le processeur est généralement verrouillé et la marge thermique est faible : l'overclocking est à "
                + "éviter. Les modes de performance et de ventilation de l'utilitaire du constructeur sont la voie sûre ; l'undervolting du "
                + "processeur est souvent bloqué par le BIOS.",
            Advice = $"{tool} Tutoriel pour votre modèle : {OverclockingParsers.TutorialUrl($"{(pcModel.Length > 0 ? pcModel : model)} undervolt")}",
        };
    }

    private static Finding IntelUnlocked(string model, InstalledTool? xtu) => new()
    {
        Id = "M15.cpu",
        Title = $"Processeur {model} : Intel Extreme Tuning Utility (XTU)",
        Category = CpuCategory,
        Status = FindingStatus.Info,
        Current = xtu is null ? $"{model}, coefficient débloqué" : $"{model}, coefficient débloqué · {xtu.Label} installé",
        Explanation = "Votre processeur Intel se règle avec Intel XTU, l'outil officiel d'Intel, ou directement dans le BIOS. "
            + UndervoltFirst + " Une carte mère à chipset Z est nécessaire, et certains BIOS verrouillent le réglage de tension.",
        Advice = $"Téléchargez XTU uniquement sur intel.com : {XtuUrl} (Core de 14e génération et plus anciens : version 7.14, proposée "
            + $"sur la même page). Tutoriel pour votre modèle : {OverclockingParsers.TutorialUrl($"undervolt {model} XTU")}",
    };

    private static Finding IntelLocked(string model) => new()
    {
        Id = "M15.cpu",
        Title = $"Processeur {model} : coefficient verrouillé",
        Category = CpuCategory,
        Status = FindingStatus.Info,
        Current = $"{model}, coefficient verrouillé",
        Explanation = "Ce processeur Intel (sans suffixe K) ne peut pas être overclocké : Intel bloque son coefficient. Seules les limites de "
            + "puissance se règlent dans le BIOS, pour un gain faible. La performance se gagne plutôt ailleurs : carte graphique, mémoire, "
            + "applications au démarrage (Module 12).",
        Advice = "Rien à régler côté processeur : gardez les réglages d'origine et un BIOS à jour (Module 8).",
    };

    private static Finding AmdRyzen(string model, string name, InstalledTool? ryzenMaster)
    {
        var generationNote = OverclockingParsers.X3DGenerationOf(name) switch
        {
            X3DGeneration.Ryzen5000 => " Ryzen 5000X3D : coefficient et tension verrouillés par AMD ; seul Curve Optimizer est proposé, "
                + "et seulement par les BIOS récents.",
            X3DGeneration.Ryzen7000 => " Ryzen 7000X3D : coefficient verrouillé ; PBO et Curve Optimizer (valeurs négatives) sont les seuls "
                + "réglages prévus.",
            X3DGeneration.Ryzen9000 => " Ryzen 9000X3D : AMD autorise l'overclocking complet, mais Curve Optimizer reste le meilleur point de départ.",
            X3DGeneration.Other => " Processeur X3D : les réglages possibles dépendent de la génération ; vérifiez-les avant de commencer.",
            _ => string.Empty,
        };
        var current = ryzenMaster is null ? model : $"{model} · {ryzenMaster.Label} installé";
        return new Finding
        {
            Id = "M15.cpu",
            Title = $"Processeur {model} : AMD Ryzen Master, PBO et Curve Optimizer",
            Category = CpuCategory,
            Status = FindingStatus.Info,
            Current = current,
            Explanation = "Les processeurs Ryzen se règlent avec AMD Ryzen Master, l'outil officiel d'AMD, ou dans le BIOS avec Precision Boost "
                + "Overdrive (PBO) et Curve Optimizer. " + UndervoltFirst + " Avec Curve Optimizer, l'undervolting se fait par valeurs "
                + "négatives, cœur par cœur ou pour tous les cœurs. Ces réglages sortent des spécifications d'AMD." + generationNote,
            Advice = $"Téléchargez Ryzen Master uniquement sur amd.com : {RyzenMasterUrl}. "
                + $"Tutoriel pour votre modèle : {OverclockingParsers.TutorialUrl($"Curve Optimizer {model}")}",
        };
    }

    private static List<Finding> DetectGpus(HardwareProfile hardware, List<InstalledTool> tools)
    {
        var dedicated = hardware.Gpus.Where(g => !g.IsIntegrated).ToList();
        if (dedicated.Count == 0)
        {
            return
            [
                hardware.Gpus.Count == 0
                    ? Finding.Unknown("M15.gpu-0", "Carte graphique : outil de réglage recommandé", "La carte graphique n'a pas pu être identifiée.", GpuCategory)
                    : new Finding
                    {
                        Id = "M15.gpu-0",
                        Title = "Graphiques intégrés au processeur : rien à régler",
                        Category = GpuCategory,
                        Status = FindingStatus.Info,
                        Current = string.Join(", ", hardware.Gpus.Select(g => OverclockingParsers.GpuModel(g.Name))),
                        Explanation = "Ce PC n'a pas de carte graphique dédiée. Les graphiques intégrés au processeur se règlent peu, pour un gain "
                            + "très faible : mieux vaut les laisser d'origine.",
                    },
            ];
        }

        return dedicated.Select((gpu, index) => DescribeGpu(gpu, $"M15.gpu-{index}", hardware.IsLaptop, tools)).ToList();
    }

    private static Finding DescribeGpu(GpuInfo gpu, string id, bool laptop, List<InstalledTool> tools)
    {
        var model = OverclockingParsers.GpuModel(gpu.Name);
        var laptopNote = laptop
            ? " Sur un portable, l'undervolting de la carte graphique reste possible, mais la marge thermique est faible et le constructeur "
                + "limite souvent la puissance."
            : string.Empty;

        return gpu.Vendor switch
        {
            HardwareVendor.Nvidia => Nvidia(id, model, laptopNote, tools),
            HardwareVendor.Amd => new Finding
            {
                Id = id,
                Title = $"Carte graphique {model} : AMD Software Adrenalin",
                Category = GpuCategory,
                Status = FindingStatus.Info,
                Current = WithInstalled(model, Find(tools, "adrenalin")),
                Explanation = "Les cartes Radeon se règlent dans AMD Software: Adrenalin Edition, installé avec le pilote : Performance > Réglage "
                    + "(undervolting, limite de puissance, ventilation). Le bouton Réinitialiser remet tout d'origine. " + UndervoltFirst + laptopNote,
                Advice = $"Rien à télécharger si le pilote AMD est installé ; sinon, uniquement sur amd.com : {AmdDriversUrl}. "
                    + $"Tutoriel pour votre modèle : {OverclockingParsers.TutorialUrl($"undervolt {model} Adrenalin")}",
            },
            HardwareVendor.Intel => new Finding
            {
                Id = id,
                Title = $"Carte graphique {model} : Intel Graphics Software",
                Category = GpuCategory,
                Status = FindingStatus.Info,
                Current = WithInstalled(model, Find(tools, "intel-graphics")),
                Explanation = "Les cartes Intel Arc se règlent dans Intel Graphics Software, installé avec le pilote (réglages de performance : "
                    + "tension, limite de puissance). Les gains sont modestes. " + UndervoltFirst + laptopNote,
                Advice = $"Pilote et logiciel uniquement sur intel.com : {IntelArcUrl}. "
                    + $"Tutoriel pour votre modèle : {OverclockingParsers.TutorialUrl($"undervolt {model}")}",
            },
            _ => new Finding
            {
                Id = id,
                Title = $"Carte graphique {model} : pas d'outil de réglage connu",
                Category = GpuCategory,
                Status = FindingStatus.Info,
                Current = model,
                Explanation = "MAUS ne connaît pas d'outil officiel de réglage pour cette carte graphique. Laissez-la sur ses réglages d'origine.",
            },
        };
    }

    private static Finding Nvidia(string id, string model, string laptopNote, List<InstalledTool> tools)
    {
        var afterburner = Find(tools, "afterburner");
        var nvidiaApp = Find(tools, "nvidia-app");
        var installed = new[] { afterburner, Find(tools, "rtss"), nvidiaApp }.OfType<InstalledTool>().Select(t => t.Label).ToList();
        var advice = afterburner is null
            ? $"Téléchargez MSI Afterburner uniquement sur {AfterburnerUrl} ; NVIDIA App : {NvidiaAppUrl}."
            : $"{afterburner.Label} est installé : vérifiez qu'il vient bien de {AfterburnerUrl}.";
        return new Finding
        {
            Id = id,
            Title = $"Carte graphique {model} : MSI Afterburner ou NVIDIA App",
            Category = GpuCategory,
            Status = FindingStatus.Info,
            Current = installed.Count == 0 ? model : $"{model} · installé : {string.Join(", ", installed)}",
            Explanation = "Les cartes GeForce se règlent avec MSI Afterburner (et RivaTuner pour l'affichage en jeu), ou avec le réglage "
                + "automatique de la NVIDIA App (onglet Performance), qui teste lui-même une légère hausse de fréquence. " + UndervoltFirst
                + " Avec Afterburner, l'undervolting se fait sur la courbe tension-fréquence." + laptopNote + " " + FakeSitesWarning,
            Advice = $"{advice} Tutoriel pour votre modèle : {OverclockingParsers.TutorialUrl($"undervolt {model} Afterburner")}",
        };
    }

    private static Finding DetectTuningTools(List<InstalledTool> tools, bool readable)
    {
        const string id = "M15.tuning-tools";
        const string title = "Outils de réglage officiels installés";
        if (!readable)
        {
            return Finding.AdminRequired(id, title, ToolsCategory);
        }

        var tuning = tools.Where(t => !t.Tool.IsStabilityTool).ToList();
        return new Finding
        {
            Id = id,
            Title = title,
            Category = ToolsCategory,
            Status = FindingStatus.Info,
            Current = tuning.Count == 0 ? "aucun" : string.Join(", ", tuning.Select(Describe)),
            Explanation = "Recherche dans la liste des programmes installés : AMD Ryzen Master, MSI Afterburner et RivaTuner, Intel XTU, "
                + "NVIDIA App, AMD Software et Intel Graphics Software. Un outil installé ne ralentit rien tant qu'il ne se lance pas au "
                + "démarrage (voir Module 12).",
            Advice = tuning.Count == 0
                ? null
                : "Vérifiez que chaque outil vient du site officiel de son éditeur ; en cas de doute, désinstallez-le et retéléchargez-le depuis ce site.",
        };
    }

    private static Finding DetectStabilityTools(List<InstalledTool> tools, bool readable)
    {
        const string id = "M15.stability-tools";
        const string title = "Outils de test de stabilité";
        if (!readable)
        {
            return Finding.AdminRequired(id, title, ToolsCategory);
        }

        var stability = tools.Where(t => t.Tool.IsStabilityTool).ToList();
        return new Finding
        {
            Id = id,
            Title = title,
            Category = ToolsCategory,
            Status = FindingStatus.Info,
            Current = stability.Count == 0 ? "aucun installé" : "installés : " + string.Join(", ", stability.Select(t => t.Label)),
            Explanation = "Après chaque réglage, testez la stabilité : HWiNFO pour surveiller températures et tensions, OCCT, Cinebench 2024, "
                + "3DMark et y-cruncher pour la charge ; TestMem5 et MemTest86 pour la mémoire. Un réglage qui plante, même rarement, doit "
                + "être annulé.",
            Advice = "Téléchargez-les uniquement sur le site de leur éditeur : hwinfo.com, ocbase.com (OCCT), maxon.net (Cinebench), "
                + "3dmark.com, numberworld.org (y-cruncher), memtest86.com.",
        };
    }

    private static InstalledTool? Find(List<InstalledTool> tools, string key) => tools.FirstOrDefault(t => t.Tool.Key == key);

    private static string WithInstalled(string model, InstalledTool? tool) => tool is null ? model : $"{model} · {tool.Label} installé";

    private static string Describe(InstalledTool tool) =>
        string.IsNullOrWhiteSpace(tool.Publisher) ? tool.Label : $"{tool.Label} ({tool.Publisher.Trim()})";
}
