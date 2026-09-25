using System.Globalization;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Modules.M13Security;

/// <summary>
/// Module 13 — Performance contre sécurité. Décrit l'intégrité de la mémoire (HVCI) et la VBS, ce qui en dépend
/// et ce qui en réduit le coût. Signale en rouge les atténuations CPU coupées et la liste de blocage des pilotes désactivée.
/// Aucun constat ne propose de couper une protection : l'option experte reste désactivée par défaut.
/// </summary>
public sealed class SecurityTradeoffModule : IAuditModule
{
    internal const string DeviceGuardQuery =
        "SELECT AvailableSecurityProperties, SecurityServicesConfigured, SecurityServicesRunning, VirtualizationBasedSecurityStatus FROM Win32_DeviceGuard";

    internal const string OptionalFeaturesQuery =
        "SELECT Name, InstallState FROM Win32_OptionalFeature WHERE Name = 'Microsoft-Hyper-V-All' OR Name = 'VirtualMachinePlatform' OR Name = 'Containers-DisposableClientVM' OR Name = 'Recall'";

    internal const string HypervisorQuery = "SELECT HypervisorPresent FROM Win32_ComputerSystem";

    internal const string DeviceGuardKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard";
    internal const string MemoryIntegrityKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";
    internal const string DeviceGuardPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\DeviceGuard";
    internal const string MemoryManagementKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    internal const string CodeIntegrityConfigKey = @"SYSTEM\CurrentControlSet\Control\CI\Config";
    internal const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    private const string IsolationCategory = "Isolation du noyau";
    private const string DependenciesCategory = "Dépendances";

    /// <summary>Message de la fiche technique, affiché avec l'état de l'intégrité de la mémoire.</summary>
    private const string MemoryIntegrityMessage =
        "L'intégrité de la mémoire empêche un pilote malveillant de prendre le contrôle de Windows. La couper peut faire gagner quelques images par seconde, surtout sur un processeur ancien, mais rend le PC plus vulnérable. Valorant et FACEIT peuvent refuser de se lancer.";

    /// <summary>Anti-triche noyau : nom de service ou de pilote, nom affiché.</summary>
    private static readonly (string Service, string Name)[] AntiCheats =
    [
        ("vgc", "Riot Vanguard"),
        ("vgk", "Riot Vanguard"),
        ("FACEIT", "FACEIT"),
    ];

    /// <summary>Fonctions facultatives qui gardent l'hyperviseur chargé.</summary>
    private static readonly (string Feature, string Name)[] HypervisorFeatures =
    [
        ("Microsoft-Hyper-V-All", "Hyper-V"),
        ("VirtualMachinePlatform", "Plateforme de machine virtuelle (WSL2)"),
        ("Containers-DisposableClientVM", "Bac à sable Windows"),
    ];

    public string Id => "M13";

    public string Title => "Performance contre sécurité";

    public int Order => 130;

    public Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken)
    {
        var registry = context.Registry;
        var (deviceGuard, deviceGuardDenied) = ReadDeviceGuard(context.Cim);
        var configuration = ReadConfiguration(registry);
        var antiCheats = DetectAntiCheats(registry);
        var (features, featuresDenied) = ReadOptionalFeatures(context.Cim);

        IReadOnlyList<Finding> findings =
        [
            EvaluateCpuMitigations(registry),
            EvaluateDriverBlocklist(registry),
            EvaluateMemoryIntegrity(deviceGuard, deviceGuardDenied, configuration, antiCheats),
            DescribeAntiCheats(antiCheats),
            DescribeModeBasedExecution(deviceGuard, deviceGuardDenied),
            DescribeVbs(deviceGuard, deviceGuardDenied),
            DescribeUefiLock(configuration),
            DescribeVbsDependencies(deviceGuard, features, deviceGuardDenied || featuresDenied),
            DescribeHypervisorFeatures(features, featuresDenied, ReadHypervisorPresent(context.Cim)),
        ];
        return Task.FromResult(findings);
    }

    private static Finding EvaluateCpuMitigations(IRegistryReader registry)
    {
        const string id = "M13.cpu-mitigations";
        const string title = "Atténuations Spectre et Meltdown actives";
        const string category = "Atténuations du processeur";
        long? overrideValue;
        long? mask;
        try
        {
            overrideValue = CpuMitigationOverrides.ToNumber(registry.GetValue(RegistryHive.LocalMachine, MemoryManagementKey, "FeatureSettingsOverride"));
            mask = CpuMitigationOverrides.ToNumber(registry.GetValue(RegistryHive.LocalMachine, MemoryManagementKey, "FeatureSettingsOverrideMask"));
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(id, title, category);
        }

        var state = CpuMitigationOverrides.Evaluate(overrideValue, mask);
        var values = $"FeatureSettingsOverride = {Format(overrideValue)}, FeatureSettingsOverrideMask = {Format(mask)}";
        var severity = state switch
        {
            MitigationState.Disabled => Severity.High,
            MitigationState.DisabledWithoutMask => Severity.Medium,
            _ => Severity.High,
        };
        var deviating = state is MitigationState.Disabled or MitigationState.DisabledWithoutMask;
        return new Finding
        {
            Id = id,
            Title = title,
            Category = category,
            Status = deviating ? FindingStatusExtensions.ForDeviation(severity) : FindingStatus.Ok,
            Severity = severity,
            Current = state switch
            {
                MitigationState.Disabled => $"désactivées : {CpuMitigationOverrides.DescribeDisabled(overrideValue!.Value)} ({values})",
                MitigationState.DisabledWithoutMask => $"demande de désactivation incomplète, probablement sans effet ({values})",
                MitigationState.Strengthened => $"actives et renforcées ({values})",
                _ => overrideValue is null ? "actives (réglage Windows par défaut)" : $"actives ({values})",
            },
            Expected = "actives (valeurs absentes, ou sans bit de désactivation)",
            Explanation = "Ces protections du processeur empêchent un programme de lire la mémoire du noyau ou d'autres programmes (failles Spectre et Meltdown). Des scripts d'optimisation les coupent pour gagner quelques pour cent : le PC devient alors vulnérable. Windows les active par défaut et MAUS ne propose jamais de les couper.",
            Advice = deviating
                ? $@"Supprimer les valeurs FeatureSettingsOverride et FeatureSettingsOverrideMask sous HKLM\{MemoryManagementKey}, puis redémarrer. Elles viennent le plus souvent d'un script d'optimisation (voir aussi le Module 1)."
                : null,
            Fixable = deviating,
        };
    }

    private static Finding EvaluateDriverBlocklist(IRegistryReader registry)
    {
        const string id = "M13.driver-blocklist";
        const string title = "Liste de blocage des pilotes vulnérables active";
        const string category = "Pilotes";
        long? value;
        try
        {
            value = CpuMitigationOverrides.ToNumber(registry.GetValue(RegistryHive.LocalMachine, CodeIntegrityConfigKey, "VulnerableDriverBlocklistEnable"));
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(id, title, category);
        }

        var disabled = value == 0;
        return new Finding
        {
            Id = id,
            Title = title,
            Category = category,
            Status = disabled ? FindingStatusExtensions.ForDeviation(Severity.High) : FindingStatus.Ok,
            Severity = Severity.High,
            Current = value switch
            {
                null => "active (réglage Windows par défaut)",
                0 => "désactivée (VulnerableDriverBlocklistEnable = 0)",
                _ => $"active (VulnerableDriverBlocklistEnable = {Format(value)})",
            },
            Expected = "active (1 ou valeur absente)",
            Explanation = "Windows refuse de charger les pilotes connus pour leurs failles, souvent détournés par des logiciels malveillants pour prendre le contrôle du noyau. Cette liste est active par défaut depuis Windows 11 22H2, et MAUS ne la désactive jamais.",
            Advice = disabled
                ? "Réactiver la liste dans Sécurité Windows > Sécurité des appareils > Isolation du noyau > Liste de blocage des pilotes vulnérables Microsoft, puis redémarrer."
                : null,
            Fixable = disabled,
        };
    }

    private static Finding EvaluateMemoryIntegrity(DeviceGuardState? deviceGuard, bool denied, VbsConfiguration? configuration, List<string>? antiCheats)
    {
        const string id = "M13.memory-integrity";
        const string title = "Intégrité de la mémoire (HVCI)";
        if (deviceGuard is null)
        {
            return denied
                ? Finding.AdminRequired(id, title, IsolationCategory)
                : Finding.Unknown(id, title, "L'état de la sécurité basée sur la virtualisation (Win32_DeviceGuard) est illisible sur ce PC.", IsolationCategory);
        }

        var hasAntiCheat = antiCheats is { Count: > 0 };
        var policyNote = configuration?.IsImposedByPolicy == true ? " Ce réglage est imposé par une stratégie (organisation ou script)." : string.Empty;
        if (deviceGuard.IsMemoryIntegrityRunning)
        {
            var advice = "Recommandé : la laisser active. Option experte, jamais appliquée par défaut : la couper peut faire gagner environ 2 à 6 % en jeu sur un processeur récent, au prix d'un PC plus vulnérable.";
            if (hasAntiCheat)
            {
                advice += $" {Installed(antiCheats!)} : sans elle, vos jeux protégés risquent de refuser de se lancer.";
            }

            if (configuration?.IsUefiLocked == true)
            {
                advice += " Le verrou UEFI empêche de toute façon de la couper depuis Windows.";
            }

            return new Finding
            {
                Id = id,
                Title = title,
                Category = IsolationCategory,
                Status = FindingStatus.Ok,
                Current = "en cours d'exécution",
                Expected = "en cours d'exécution (recommandé)",
                Explanation = MemoryIntegrityMessage + policyNote,
                Advice = advice,
            };
        }

        if (deviceGuard.IsMemoryIntegrityConfigured || configuration?.IsMemoryIntegrityRequested == true)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = IsolationCategory,
                Status = FindingStatusExtensions.ForDeviation(Severity.Medium),
                Severity = Severity.Medium,
                Current = "activée dans les réglages, mais pas en cours d'exécution",
                Expected = "en cours d'exécution",
                Explanation = MemoryIntegrityMessage + " Elle est demandée mais ne tourne pas : redémarrage en attente, virtualisation coupée dans le BIOS (souvent après une mise à jour du BIOS, voir Module 8) ou pilote incompatible." + policyNote,
                Advice = "Redémarrer le PC. Si rien ne change, vérifier que la virtualisation (Intel VT-x ou AMD SVM) est activée dans le BIOS, puis consulter Sécurité Windows > Sécurité des appareils > Isolation du noyau.",
            };
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = IsolationCategory,
            Status = FindingStatus.Info,
            Current = "désactivée",
            Explanation = MemoryIntegrityMessage + policyNote,
            Advice = "Pour plus de sécurité, vous pouvez l'activer dans Sécurité Windows > Sécurité des appareils > Isolation du noyau, après avoir vérifié qu'aucun pilote incompatible n'est signalé."
                + (hasAntiCheat ? $" {Installed(antiCheats!)} : si un jeu protégé refuse de se lancer, activez-la." : string.Empty),
        };
    }

    private static Finding DescribeAntiCheats(List<string>? antiCheats)
    {
        const string id = "M13.anti-cheat";
        const string title = "Anti-triche noyau (Riot Vanguard, FACEIT)";
        const string category = "Jeux";
        if (antiCheats is null)
        {
            return Finding.Unknown(id, title, "Liste des services Windows illisible.", category);
        }

        var detected = antiCheats.Count > 0;
        return new Finding
        {
            Id = id,
            Title = title,
            Category = category,
            Status = FindingStatus.Info,
            Current = detected ? string.Join(", ", antiCheats) : "aucun détecté",
            Explanation = "Riot Vanguard (Valorant) et FACEIT surveillent le noyau de Windows. Ils peuvent refuser de fonctionner si l'intégrité de la mémoire ou la VBS est coupée.",
            Advice = detected
                ? "Gardez l'intégrité de la mémoire active : la couper risque d'empêcher vos jeux de se lancer. MAUS ne bloque rien, le choix vous revient."
                : null,
        };
    }

    private static Finding DescribeModeBasedExecution(DeviceGuardState? deviceGuard, bool denied)
    {
        const string id = "M13.mbec";
        const string title = "Accélération matérielle de l'intégrité de la mémoire (MBEC/GMET)";
        const string category = "Processeur";
        if (deviceGuard is null)
        {
            return denied
                ? Finding.AdminRequired(id, title, category)
                : Finding.Unknown(id, title, "Capacités de sécurité du processeur illisibles.", category);
        }

        // 0 seul ou liste vide : Windows ne communique aucune capacité, ce qui ne prouve pas l'absence de MBEC/GMET.
        if (!deviceGuard.AvailableProperties.Any(p => p != 0))
        {
            return Finding.Unknown(id, title, "Windows ne communique pas les capacités de sécurité du processeur (virtualisation peut-être désactivée dans le BIOS).", category);
        }

        if (deviceGuard.HasModeBasedExecutionControl)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = category,
                Status = FindingStatus.Ok,
                Current = "présente",
                Expected = "présente",
                Explanation = "Les processeurs Intel de 7e génération (Kaby Lake) et plus récents, et AMD Zen 2 et plus récents, accélèrent l'intégrité de la mémoire (MBEC ou GMET) : son coût en performances reste faible.",
            };
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = category,
            Status = FindingStatus.Info,
            Current = "absente (émulation logicielle)",
            Expected = "présente",
            Explanation = "Ce processeur n'a pas MBEC ni GMET : Windows émule cette fonction (Restricted User Mode) et l'intégrité de la mémoire y coûte davantage, surtout sur Ryzen 1000 et 2000 ou sur Intel antérieur à la 7e génération.",
            Advice = deviceGuard.IsMemoryIntegrityRunning
                ? "L'intégrité de la mémoire est active : MAUS la laisse ainsi par défaut. Si vous privilégiez les performances en jeu, l'option experte permettra de la couper, en connaissance de cause."
                : null,
        };
    }

    private static Finding DescribeVbs(DeviceGuardState? deviceGuard, bool denied)
    {
        const string id = "M13.vbs";
        const string title = "Sécurité basée sur la virtualisation (VBS)";
        if (deviceGuard?.VbsStatus is null)
        {
            return denied
                ? Finding.AdminRequired(id, title, IsolationCategory)
                : Finding.Unknown(id, title, "L'état de la VBS (Win32_DeviceGuard) est illisible sur ce PC.", IsolationCategory);
        }

        var services = deviceGuard.ServicesRunning.Select(DeviceGuardState.ServiceLabel).OfType<string>().ToList();
        var current = deviceGuard.VbsStatus switch
        {
            2 => services.Count > 0 ? $"en cours d'exécution ({string.Join(", ", services)})" : "en cours d'exécution",
            1 => "activée, mais pas en cours d'exécution",
            _ => "non activée",
        };
        return new Finding
        {
            Id = id,
            Title = title,
            Category = IsolationCategory,
            Status = deviceGuard.IsVbsRunning ? FindingStatus.Ok : FindingStatus.Info,
            Current = current,
            Explanation = "La VBS isole une partie de Windows dans un espace protégé par l'hyperviseur. L'intégrité de la mémoire, Credential Guard, la connexion Windows Hello renforcée et Recall en dépendent. La couper entièrement est rarement utile et réduit fortement la sécurité.",
        };
    }

    private static Finding DescribeUefiLock(VbsConfiguration? configuration)
    {
        const string id = "M13.uefi-lock";
        const string title = "Verrou UEFI de l'isolation du noyau";
        if (configuration is null)
        {
            return Finding.AdminRequired(id, title, IsolationCategory);
        }

        var locked = configuration.IsUefiLocked;
        return new Finding
        {
            Id = id,
            Title = title,
            Category = IsolationCategory,
            Status = FindingStatus.Info,
            Current = locked ? "présent" : "absent",
            Explanation = locked
                ? "Le verrou UEFI empêche de désactiver l'intégrité de la mémoire ou la VBS depuis Windows : aucun logiciel ne peut les couper, il faut une procédure spéciale au démarrage."
                : "Aucun verrou UEFI : l'intégrité de la mémoire et la VBS se règlent depuis Sécurité Windows.",
        };
    }

    private static Finding DescribeVbsDependencies(DeviceGuardState? deviceGuard, IReadOnlyDictionary<string, bool>? features, bool denied)
    {
        const string id = "M13.vbs-dependencies";
        const string title = "Fonctions qui exigent la VBS";
        if (deviceGuard is null && features is null)
        {
            return denied
                ? Finding.AdminRequired(id, title, DependenciesCategory)
                : Finding.Unknown(id, title, "État de Credential Guard et des fonctions facultatives illisible.", DependenciesCategory);
        }

        var found = new List<string>();
        if (deviceGuard?.IsCredentialGuardRunning == true)
        {
            found.Add("Credential Guard (en cours)");
        }

        if (features?.GetValueOrDefault("Recall") == true)
        {
            found.Add("Recall (installé)");
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = DependenciesCategory,
            Status = FindingStatus.Info,
            Current = found.Count > 0 ? string.Join(", ", found) : "aucune détectée",
            Explanation = "Credential Guard, Recall et la connexion Windows Hello renforcée ne fonctionnent qu'avec la VBS : la couper les désactiverait.",
        };
    }

    private static Finding DescribeHypervisorFeatures(IReadOnlyDictionary<string, bool>? features, bool denied, bool? hypervisorPresent)
    {
        const string id = "M13.hypervisor-features";
        const string title = "Fonctions qui gardent l'hyperviseur chargé";
        if (features is null)
        {
            return denied
                ? Finding.AdminRequired(id, title, DependenciesCategory)
                : Finding.Unknown(id, title, "Liste des fonctionnalités facultatives de Windows illisible.", DependenciesCategory);
        }

        var enabled = HypervisorFeatures.Where(f => features.GetValueOrDefault(f.Feature)).Select(f => f.Name).ToList();
        var current = enabled.Count > 0 ? string.Join(", ", enabled) : "aucune";
        if (hypervisorPresent is not null)
        {
            current += hypervisorPresent.Value ? " ; hyperviseur Windows chargé" : " ; hyperviseur Windows non chargé";
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = DependenciesCategory,
            Status = FindingStatus.Info,
            Current = current,
            Explanation = "Hyper-V, la Plateforme de machine virtuelle (WSL2, sous-système Android) et le Bac à sable Windows chargent l'hyperviseur en permanence : couper l'intégrité de la mémoire rapporterait alors encore moins.",
        };
    }

    private static (DeviceGuardState? State, bool Denied) ReadDeviceGuard(ICimReader cim)
    {
        try
        {
            var rows = cim.Query(DeviceGuardQuery, CimScopes.DeviceGuard);
            return (rows.Count > 0 ? DeviceGuardState.FromRow(rows[0]) : null, false);
        }
        catch (MausAccessDeniedException)
        {
            return (null, true);
        }
        catch (DataSourceUnavailableException)
        {
            return (null, false);
        }
    }

    private static VbsConfiguration? ReadConfiguration(IRegistryReader registry)
    {
        try
        {
            return new VbsConfiguration(
                registry.GetDword(RegistryHive.LocalMachine, MemoryIntegrityKey, "Enabled"),
                registry.GetDword(RegistryHive.LocalMachine, MemoryIntegrityKey, "Locked"),
                registry.GetDword(RegistryHive.LocalMachine, DeviceGuardKey, "EnableVirtualizationBasedSecurity"),
                registry.GetDword(RegistryHive.LocalMachine, DeviceGuardKey, "Locked"),
                registry.GetDword(RegistryHive.LocalMachine, DeviceGuardPolicyKey, "EnableVirtualizationBasedSecurity"),
                registry.GetDword(RegistryHive.LocalMachine, DeviceGuardPolicyKey, "HypervisorEnforcedCodeIntegrity"));
        }
        catch (MausAccessDeniedException)
        {
            return null;
        }
    }

    /// <summary>Anti-triche installés (clé de service ou de pilote présente), ou <c>null</c> si la liste des services est illisible.</summary>
    private static List<string>? DetectAntiCheats(IRegistryReader registry)
    {
        try
        {
            return AntiCheats
                .Where(a => registry.KeyExists(RegistryHive.LocalMachine, $@"{ServicesKey}\{a.Service}"))
                .Select(a => a.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (MausAccessDeniedException)
        {
            return null;
        }
    }

    /// <summary>Fonctionnalités facultatives par nom : <c>InstallState</c> = 1 signifie activée.</summary>
    private static (Dictionary<string, bool>? Features, bool Denied) ReadOptionalFeatures(ICimReader cim)
    {
        try
        {
            var features = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in cim.Query(OptionalFeaturesQuery))
            {
                if (row.GetString("Name") is { } name)
                {
                    features[name] = row.GetInt64("InstallState") == 1;
                }
            }

            return (features, false);
        }
        catch (MausAccessDeniedException)
        {
            return (null, true);
        }
        catch (DataSourceUnavailableException)
        {
            return (null, false);
        }
    }

    private static bool? ReadHypervisorPresent(ICimReader cim)
    {
        try
        {
            var rows = cim.Query(HypervisorQuery);
            return rows.Count > 0 ? rows[0].GetBool("HypervisorPresent") : null;
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or DataSourceUnavailableException)
        {
            return null;
        }
    }

    /// <summary>« Riot Vanguard est installé » ou « Riot Vanguard et FACEIT sont installés ».</summary>
    private static string Installed(List<string> names) =>
        names.Count > 1 ? $"{string.Join(" et ", names)} sont installés" : $"{names[0]} est installé";

    private static string Format(long? value) =>
        value is null ? "absente" : value.Value.ToString(CultureInfo.InvariantCulture);
}
