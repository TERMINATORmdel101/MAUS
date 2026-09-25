using Maus.Core.Platform;

namespace Maus.Core.Modules.M01Audit;

/// <summary>Catégorie « Démarrage » : DEP, signature des pilotes, réglages d'horloge, démarrage sécurisé.</summary>
public sealed partial class RiskyChangesAuditModule
{
    private const string BootCategory = "Démarrage";
    private const string ControlKey = @"SYSTEM\CurrentControlSet\Control";
    private const string SecureBootStateKey = @"SYSTEM\CurrentControlSet\Control\SecureBoot\State";

    private static readonly Check DepCheck = new("M01.dep", "Prévention de l'exécution des données (DEP)", BootCategory, Severity.Critical, Fixable: true);

    private static readonly Check DriverSigningCheck = new("M01.driver-signing", "Vérification de la signature des pilotes", BootCategory, Severity.Critical, Fixable: true);

    private static readonly Check TimerCheck = new("M01.boot-timer", "Réglages d'horloge forcés au démarrage", BootCategory, Severity.Medium, Fixable: true);

    private static readonly Check SecureBootCheck = new("M01.secure-boot", "Démarrage sécurisé (Secure Boot)", BootCategory, Severity.Medium);

    /// <summary>Éléments BCD posés par les « optimiseurs » de latence.</summary>
    private static readonly (string Element, string Label)[] TimerTweaks =
    [
        ("useplatformclock", "horloge HPET forcée (useplatformclock)"),
        ("useplatformtick", "tick de plateforme forcé (useplatformtick)"),
        ("disabledynamictick", "tick dynamique désactivé (disabledynamictick)"),
    ];

    private static Finding DetectDep(ICimReader cim, IReadOnlyDictionary<string, string>? bcd)
    {
        const string explanation =
            "La DEP empêche d'exécuter du code placé dans des zones de mémoire réservées aux données : c'est une barrière de base contre l'exploitation des failles. " +
            "La couper n'apporte aucun gain de performance.";
        const string expected = "OptIn (défaut) ou plus strict";
        var policy = FirstRow(cim.Query("SELECT DataExecutionPrevention_SupportPolicy FROM Win32_OperatingSystem"))?.GetInt64("DataExecutionPrevention_SupportPolicy");
        string? nx = null;
        _ = bcd?.TryGetValue("nx", out nx);
        var alwaysOff = policy == 0 || string.Equals(nx, "AlwaysOff", StringComparison.OrdinalIgnoreCase);

        if (alwaysOff)
        {
            return DepCheck.Deviation(
                "toujours désactivée (AlwaysOff)",
                expected,
                explanation,
                "Rétablir la valeur OptIn dans la configuration de démarrage (bcdedit /set {current} nx OptIn), puis redémarrer.");
        }

        return policy is null && nx is null
            ? DepCheck.Unknown("Réglage de la DEP illisible.")
            : DepCheck.Compliant(DepLabel(policy, nx), expected, explanation);
    }

    private static string DepLabel(long? policy, string? nx) => policy switch
    {
        1 => "toujours activée (AlwaysOn)",
        2 => "activée pour Windows et les programmes compatibles (OptIn)",
        3 => "activée pour tous les programmes sauf exceptions (OptOut)",
        _ => nx ?? "inconnue",
    };

    private static Finding DetectDriverSigning(IRegistryReader registry, IReadOnlyDictionary<string, string>? bcd)
    {
        const string explanation =
            "Windows n'accepte normalement que des pilotes signés. Le mode test ou la désactivation des contrôles d'intégrité laissent charger n'importe quel pilote, " +
            "y compris un pilote malveillant qui aurait tous les droits sur le PC. Plusieurs anti-triche refusent aussi de lancer les jeux dans ce mode.";
        const string expected = "signature exigée";

        // SystemStartOptions (lisible sans droits) reflète le démarrage en cours ; bcdedit (administrateur) le prochain.
        var options = registry.GetString(Hklm, ControlKey, "SystemStartOptions");
        var tokens = new HashSet<string>(
            (options ?? string.Empty).Split([' ', '/'], StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);
        if (bcd is null && options is null)
        {
            return DriverSigningCheck.AdminRequired();
        }

        var issues = new List<string>();
        if (tokens.Contains("TESTSIGNING") || (bcd is not null && BcdEditParser.IsYes(bcd, "testsigning")))
        {
            issues.Add("mode test (testsigning)");
        }

        if (tokens.Contains("DISABLE_INTEGRITY_CHECKS") || (bcd is not null && BcdEditParser.IsYes(bcd, "nointegritychecks")))
        {
            issues.Add("contrôles d'intégrité désactivés (nointegritychecks)");
        }

        return issues.Count == 0
            ? DriverSigningCheck.Compliant(expected, expected, explanation)
            : DriverSigningCheck.Deviation(
                Join(issues),
                expected,
                explanation,
                "Supprimer testsigning et nointegritychecks de la configuration de démarrage (en suspendant BitLocker au préalable, voir Module 8), puis redémarrer.");
    }

    private static Finding DetectTimerTweaks(IReadOnlyDictionary<string, string>? bcd, bool elevated)
    {
        const string explanation =
            "Des « optimiseurs » forcent l'horloge matérielle HPET ou désactivent le « tick » dynamique pour gagner en réactivité. " +
            "Sur un PC récent, ces réglages augmentent plutôt la latence et la consommation, et font baisser les performances dans les jeux.";
        const string expected = "valeurs par défaut (non définies)";
        if (bcd is null)
        {
            return elevated
                ? TimerCheck.Unknown("Configuration de démarrage illisible (bcdedit).")
                : TimerCheck.AdminRequired();
        }

        var tweaks = TimerTweaks.Where(t => BcdEditParser.IsYes(bcd, t.Element)).Select(t => t.Label).ToList();
        return tweaks.Count == 0
            ? TimerCheck.Compliant(expected, expected, explanation)
            : TimerCheck.Deviation(
                Join(tweaks),
                expected,
                explanation,
                "Supprimer ces valeurs de la configuration de démarrage (bcdedit /deletevalue), puis redémarrer.");
    }

    private static Finding DetectSecureBoot(IRegistryReader registry)
    {
        const string explanation =
            "Le démarrage sécurisé vérifie qu'un chargeur signé se lance à l'allumage : il bloque les « bootkits », des logiciels malveillants qui s'installent avant Windows. " +
            "Windows 11 et plusieurs jeux protégés par un anti-triche l'exigent.";
        return registry.GetDword(Hklm, SecureBootStateKey, "UEFISecureBootEnabled") switch
        {
            1 => SecureBootCheck.Compliant("activé", "activé", explanation),
            0 => SecureBootCheck.Deviation(
                "désactivé",
                "activé",
                explanation,
                "Activer « Secure Boot » dans les réglages du BIOS/UEFI de la carte mère (voir Module 13) ; MAUS ne peut pas le faire depuis Windows."),
            _ => SecureBootCheck.Unknown("État du démarrage sécurisé illisible (PC démarré en mode BIOS hérité ?)."),
        };
    }
}
