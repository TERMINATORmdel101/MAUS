using System.Globalization;
using Maus.Core.Platform;
using Microsoft.Win32;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M13Security;

/// <summary>
/// Module 13 — Performance contre sécurité. Décrit l'intégrité de la mémoire (HVCI) et la VBS, ce qui en dépend
/// et ce qui en réduit le coût. Signale en rouge les atténuations CPU coupées et la liste de blocage des pilotes désactivée.
/// Aucun constat ne propose de couper une protection : l'option experte reste désactivée par défaut.
/// </summary>
public sealed class SecurityTradeoffModule : Fixes.IFixableModule
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

    private static string IsolationCategory => T("Isolation du noyau");
    private static string DependenciesCategory => T("Dépendances");

    /// <summary>Message de la fiche technique, affiché avec l'état de l'intégrité de la mémoire.</summary>
    private static string MemoryIntegrityMessage => T("L'intégrité de la mémoire empêche un pilote malveillant de prendre le contrôle de Windows. La couper peut faire gagner quelques images par seconde, surtout sur un processeur ancien, mais rend le PC plus vulnérable. Valorant et FACEIT peuvent refuser de se lancer.");

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
        ("VirtualMachinePlatform", T("Plateforme de machine virtuelle (WSL2)")),
        ("Containers-DisposableClientVM", T("Bac à sable Windows")),
    ];

    public string Id => "M13";

    public string Title => T("Performance contre sécurité");

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

    /// <summary>
    /// Corrections : uniquement le retour des protections (atténuations CPU, liste de blocage des pilotes).
    /// MAUS ne propose jamais de couper une atténuation ; l'intégrité de la mémoire reste guidée dans Sécurité Windows.
    /// </summary>
    public IReadOnlyList<Fixes.PlannedChange> Plan(AuditContext context, IReadOnlyList<Finding> findings)
    {
        var changes = new List<Fixes.PlannedChange>();
        foreach (var finding in findings.Where(f => f.Fixable && f.Status is FindingStatus.Problem or FindingStatus.Warning))
        {
            switch (finding.Id)
            {
                case "M13.cpu-mitigations":
                    changes.Add(new Fixes.PlannedChange
                    {
                        Id = finding.Id,
                        ModuleId = Id,
                        Title = T("Réactiver les atténuations Spectre et Meltdown"),
                        Description = T("Supprime FeatureSettingsOverride et FeatureSettingsOverrideMask : Windows reprend ses protections par défaut."),
                        Category = finding.Category,
                        Gain = T("Le PC redevient protégé contre la lecture de la mémoire du noyau par un programme malveillant."),
                        Risk = T("Quelques pour cent de performance en moins sur certains processeurs anciens."),
                        Effect = Fixes.ChangeEffect.Restart,
                        Writes =
                        [
                            new Fixes.SettingWrite(Fixes.SettingKey.Registry("HKLM", MemoryManagementKey, "FeatureSettingsOverride"), null),
                            new Fixes.SettingWrite(Fixes.SettingKey.Registry("HKLM", MemoryManagementKey, "FeatureSettingsOverrideMask"), null),
                        ],
                    });
                    break;
                case "M13.driver-blocklist":
                    changes.Add(new Fixes.PlannedChange
                    {
                        Id = finding.Id,
                        ModuleId = Id,
                        Title = T("Réactiver la liste de blocage des pilotes vulnérables"),
                        Description = T("Windows refuse de nouveau de charger les pilotes connus pour leurs failles."),
                        Category = finding.Category,
                        Risk = T("Un ancien outil qui dépend d'un pilote vulnérable (certains utilitaires de ventilation ou d'overclocking) peut ne plus démarrer."),
                        Effect = Fixes.ChangeEffect.Restart,
                        Writes = [new Fixes.SettingWrite(Fixes.SettingKey.Registry("HKLM", CodeIntegrityConfigKey, "VulnerableDriverBlocklistEnable"), Fixes.SettingValue.Dword(1))],
                    });
                    break;
            }
        }

        return changes;
    }

    private static Finding EvaluateCpuMitigations(IRegistryReader registry)
    {
        const string id = "M13.cpu-mitigations";
        var title = T("Atténuations Spectre et Meltdown actives");
        var category = T("Atténuations du processeur");
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
                MitigationState.Disabled => T("désactivées : {0} ({1})", CpuMitigationOverrides.DescribeDisabled(overrideValue!.Value), values),
                MitigationState.DisabledWithoutMask => T("demande de désactivation incomplète, probablement sans effet ({0})", values),
                MitigationState.Strengthened => T("actives et renforcées ({0})", values),
                _ => overrideValue is null ? T("actives (réglage Windows par défaut)") : $"actives ({values})",
            },
            Expected = T("actives (valeurs absentes, ou sans bit de désactivation)"),
            Explanation = T("Ces protections du processeur empêchent un programme de lire la mémoire du noyau ou d'autres programmes (failles Spectre et Meltdown). Des scripts d'optimisation les coupent pour gagner quelques pour cent : le PC devient alors vulnérable. Windows les active par défaut et MAUS ne propose jamais de les couper."),
            Advice = deviating
                ? $@"Supprimer les valeurs FeatureSettingsOverride et FeatureSettingsOverrideMask sous HKLM\{MemoryManagementKey}, puis redémarrer. Elles viennent le plus souvent d'un script d'optimisation (voir aussi le Module 1)."
                : null,
            Fixable = deviating,
        };
    }

    private static Finding EvaluateDriverBlocklist(IRegistryReader registry)
    {
        const string id = "M13.driver-blocklist";
        var title = T("Liste de blocage des pilotes vulnérables active");
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
                null => T("active (réglage Windows par défaut)"),
                0 => T("désactivée (VulnerableDriverBlocklistEnable = 0)"),
                _ => $"active (VulnerableDriverBlocklistEnable = {Format(value)})",
            },
            Expected = T("active (1 ou valeur absente)"),
            Explanation = T("Windows refuse de charger les pilotes connus pour leurs failles, souvent détournés par des logiciels malveillants pour prendre le contrôle du noyau. Cette liste est active par défaut depuis Windows 11 22H2, et MAUS ne la désactive jamais."),
            Advice = disabled
                ? T("Réactiver la liste dans Sécurité Windows > Sécurité des appareils > Isolation du noyau > Liste de blocage des pilotes vulnérables Microsoft, puis redémarrer.")
                : null,
            Fixable = disabled,
        };
    }

    private static Finding EvaluateMemoryIntegrity(DeviceGuardState? deviceGuard, bool denied, VbsConfiguration? configuration, List<string>? antiCheats)
    {
        const string id = "M13.memory-integrity";
        var title = T("Intégrité de la mémoire (HVCI)");
        if (deviceGuard is null)
        {
            return denied
                ? Finding.AdminRequired(id, title, IsolationCategory)
                : Finding.Unknown(id, title, T("L'état de la sécurité basée sur la virtualisation (Win32_DeviceGuard) est illisible sur ce PC."), IsolationCategory);
        }

        var hasAntiCheat = antiCheats is { Count: > 0 };
        var policyNote = configuration?.IsImposedByPolicy == true ? T(" Ce réglage est imposé par une stratégie (organisation ou script).") : string.Empty;
        if (deviceGuard.IsMemoryIntegrityRunning)
        {
            var advice = T("Recommandé : la laisser active. Option experte, jamais appliquée par défaut : la couper peut faire gagner environ 2 à 6 % en jeu sur un processeur récent, au prix d'un PC plus vulnérable.");
            if (hasAntiCheat)
            {
                advice += T(" {0} : sans elle, vos jeux protégés risquent de refuser de se lancer.", Installed(antiCheats!));
            }

            if (configuration?.IsUefiLocked == true)
            {
                advice += T(" Le verrou UEFI empêche de toute façon de la couper depuis Windows.");
            }

            return new Finding
            {
                Id = id,
                Title = title,
                Category = IsolationCategory,
                Status = FindingStatus.Ok,
                Current = T("en cours d'exécution"),
                Expected = T("en cours d'exécution (recommandé)"),
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
                Current = T("activée dans les réglages, mais pas en cours d'exécution"),
                Expected = T("en cours d'exécution"),
                Explanation = MemoryIntegrityMessage + T(" Elle est demandée mais ne tourne pas : redémarrage en attente, virtualisation coupée dans le BIOS (souvent après une mise à jour du BIOS, voir Module 8) ou pilote incompatible.") + policyNote,
                Advice = T("Redémarrer le PC. Si rien ne change, vérifier que la virtualisation (Intel VT-x ou AMD SVM) est activée dans le BIOS, puis consulter Sécurité Windows > Sécurité des appareils > Isolation du noyau."),
            };
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = IsolationCategory,
            Status = FindingStatus.Info,
            Current = T("désactivée"),
            Explanation = MemoryIntegrityMessage + policyNote,
            Advice = T("Pour plus de sécurité, vous pouvez l'activer dans Sécurité Windows > Sécurité des appareils > Isolation du noyau, après avoir vérifié qu'aucun pilote incompatible n'est signalé.")
                + (hasAntiCheat ? T(" {0} : si un jeu protégé refuse de se lancer, activez-la.", Installed(antiCheats!)) : string.Empty),
        };
    }

    private static Finding DescribeAntiCheats(List<string>? antiCheats)
    {
        const string id = "M13.anti-cheat";
        var title = T("Anti-triche noyau (Riot Vanguard, FACEIT)");
        const string category = "Jeux";
        if (antiCheats is null)
        {
            return Finding.Unknown(id, title, T("Liste des services Windows illisible."), category);
        }

        var detected = antiCheats.Count > 0;
        return new Finding
        {
            Id = id,
            Title = title,
            Category = category,
            Status = FindingStatus.Info,
            Current = detected ? string.Join(", ", antiCheats) : T("aucun détecté"),
            Explanation = T("Riot Vanguard (Valorant) et FACEIT surveillent le noyau de Windows. Ils peuvent refuser de fonctionner si l'intégrité de la mémoire ou la VBS est coupée."),
            Advice = detected
                ? T("Gardez l'intégrité de la mémoire active : la couper risque d'empêcher vos jeux de se lancer. MAUS ne bloque rien, le choix vous revient.")
                : null,
        };
    }

    private static Finding DescribeModeBasedExecution(DeviceGuardState? deviceGuard, bool denied)
    {
        const string id = "M13.mbec";
        var title = T("Accélération matérielle de l'intégrité de la mémoire (MBEC/GMET)");
        const string category = "Processeur";
        if (deviceGuard is null)
        {
            return denied
                ? Finding.AdminRequired(id, title, category)
                : Finding.Unknown(id, title, T("Capacités de sécurité du processeur illisibles."), category);
        }

        // 0 seul ou liste vide : Windows ne communique aucune capacité, ce qui ne prouve pas l'absence de MBEC/GMET.
        if (!deviceGuard.AvailableProperties.Any(p => p != 0))
        {
            return Finding.Unknown(id, title, T("Windows ne communique pas les capacités de sécurité du processeur (virtualisation peut-être désactivée dans le BIOS)."), category);
        }

        if (deviceGuard.HasModeBasedExecutionControl)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = category,
                Status = FindingStatus.Ok,
                Current = T("présente"),
                Expected = T("présente"),
                Explanation = T("Les processeurs Intel de 7e génération (Kaby Lake) et plus récents, et AMD Zen 2 et plus récents, accélèrent l'intégrité de la mémoire (MBEC ou GMET) : son coût en performances reste faible."),
            };
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = category,
            Status = FindingStatus.Info,
            Current = T("absente (émulation logicielle)"),
            Expected = T("présente"),
            Explanation = T("Ce processeur n'a pas MBEC ni GMET : Windows émule cette fonction (Restricted User Mode) et l'intégrité de la mémoire y coûte davantage, surtout sur Ryzen 1000 et 2000 ou sur Intel antérieur à la 7e génération."),
            Advice = deviceGuard.IsMemoryIntegrityRunning
                ? T("L'intégrité de la mémoire est active : MAUS la laisse ainsi par défaut. Si vous privilégiez les performances en jeu, l'option experte permettra de la couper, en connaissance de cause.")
                : null,
        };
    }

    private static Finding DescribeVbs(DeviceGuardState? deviceGuard, bool denied)
    {
        const string id = "M13.vbs";
        var title = T("Sécurité basée sur la virtualisation (VBS)");
        if (deviceGuard?.VbsStatus is null)
        {
            return denied
                ? Finding.AdminRequired(id, title, IsolationCategory)
                : Finding.Unknown(id, title, T("L'état de la VBS (Win32_DeviceGuard) est illisible sur ce PC."), IsolationCategory);
        }

        var services = deviceGuard.ServicesRunning.Select(DeviceGuardState.ServiceLabel).OfType<string>().ToList();
        var current = deviceGuard.VbsStatus switch
        {
            2 => services.Count > 0 ? T("en cours d'exécution ({0})", string.Join(", ", services)) : T("en cours d'exécution"),
            1 => T("activée, mais pas en cours d'exécution"),
            _ => T("non activée"),
        };
        return new Finding
        {
            Id = id,
            Title = title,
            Category = IsolationCategory,
            Status = deviceGuard.IsVbsRunning ? FindingStatus.Ok : FindingStatus.Info,
            Current = current,
            Explanation = T("La VBS isole une partie de Windows dans un espace protégé par l'hyperviseur. L'intégrité de la mémoire, Credential Guard, la connexion Windows Hello renforcée et Recall en dépendent. La couper entièrement est rarement utile et réduit fortement la sécurité."),
        };
    }

    private static Finding DescribeUefiLock(VbsConfiguration? configuration)
    {
        const string id = "M13.uefi-lock";
        var title = T("Verrou UEFI de l'isolation du noyau");
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
            Current = locked ? T("présent") : T("absent"),
            Explanation = locked
                ? T("Le verrou UEFI empêche de désactiver l'intégrité de la mémoire ou la VBS depuis Windows : aucun logiciel ne peut les couper, il faut une procédure spéciale au démarrage.")
                : T("Aucun verrou UEFI : l'intégrité de la mémoire et la VBS se règlent depuis Sécurité Windows."),
        };
    }

    private static Finding DescribeVbsDependencies(DeviceGuardState? deviceGuard, IReadOnlyDictionary<string, bool>? features, bool denied)
    {
        const string id = "M13.vbs-dependencies";
        var title = T("Fonctions qui exigent la VBS");
        if (deviceGuard is null && features is null)
        {
            return denied
                ? Finding.AdminRequired(id, title, DependenciesCategory)
                : Finding.Unknown(id, title, T("État de Credential Guard et des fonctions facultatives illisible."), DependenciesCategory);
        }

        var found = new List<string>();
        if (deviceGuard?.IsCredentialGuardRunning == true)
        {
            found.Add(T("Credential Guard (en cours)"));
        }

        if (features?.GetValueOrDefault("Recall") == true)
        {
            found.Add(T("Recall (installé)"));
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = DependenciesCategory,
            Status = FindingStatus.Info,
            Current = found.Count > 0 ? string.Join(", ", found) : T("aucune détectée"),
            Explanation = T("Credential Guard, Recall et la connexion Windows Hello renforcée ne fonctionnent qu'avec la VBS : la couper les désactiverait."),
        };
    }

    private static Finding DescribeHypervisorFeatures(IReadOnlyDictionary<string, bool>? features, bool denied, bool? hypervisorPresent)
    {
        const string id = "M13.hypervisor-features";
        var title = T("Fonctions qui gardent l'hyperviseur chargé");
        if (features is null)
        {
            return denied
                ? Finding.AdminRequired(id, title, DependenciesCategory)
                : Finding.Unknown(id, title, T("Liste des fonctionnalités facultatives de Windows illisible."), DependenciesCategory);
        }

        var enabled = HypervisorFeatures.Where(f => features.GetValueOrDefault(f.Feature)).Select(f => f.Name).ToList();
        var current = enabled.Count > 0 ? string.Join(", ", enabled) : T("aucune");
        if (hypervisorPresent is not null)
        {
            current += hypervisorPresent.Value ? T(" ; hyperviseur Windows chargé") : T(" ; hyperviseur Windows non chargé");
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = DependenciesCategory,
            Status = FindingStatus.Info,
            Current = current,
            Explanation = T("Hyper-V, la Plateforme de machine virtuelle (WSL2, sous-système Android) et le Bac à sable Windows chargent l'hyperviseur en permanence : couper l'intégrité de la mémoire rapporterait alors encore moins."),
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
        names.Count > 1 ? T("{0} sont installés", string.Join(" et ", names)) : T("{0} est installé", names[0]);

    private static string Format(long? value) =>
        value is null ? T("absente") : value.Value.ToString(CultureInfo.InvariantCulture);
}
