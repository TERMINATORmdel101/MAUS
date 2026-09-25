using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M01Audit;

/// <summary>Catégorie « Démarrage » : DEP, signature des pilotes, réglages d'horloge, démarrage sécurisé.</summary>
public sealed partial class RiskyChangesAuditModule
{
    private static string BootCategory => T("Démarrage");
    private const string ControlKey = @"SYSTEM\CurrentControlSet\Control";
    private const string SecureBootStateKey = @"SYSTEM\CurrentControlSet\Control\SecureBoot\State";

    private static Check DepCheck => new("M01.dep", T("Prévention de l'exécution des données (DEP)"), BootCategory, Severity.Critical, Fixable: true);

    private static Check DriverSigningCheck => new("M01.driver-signing", T("Vérification de la signature des pilotes"), BootCategory, Severity.Critical, Fixable: true);

    private static Check TimerCheck => new("M01.boot-timer", T("Réglages d'horloge forcés au démarrage"), BootCategory, Severity.Medium, Fixable: true);

    private static Check SecureBootCheck => new("M01.secure-boot", T("Démarrage sécurisé (Secure Boot)"), BootCategory, Severity.Medium);

    /// <summary>Éléments BCD posés par les « optimiseurs » de latence.</summary>
    private static readonly (string Element, string Label)[] TimerTweaks =
    [
        ("useplatformclock", T("horloge HPET forcée (useplatformclock)")),
        ("useplatformtick", T("tick de plateforme forcé (useplatformtick)")),
        ("disabledynamictick", T("tick dynamique désactivé (disabledynamictick)")),
    ];

    private static Finding DetectDep(ICimReader cim, IReadOnlyDictionary<string, string>? bcd)
    {
        var explanation = T("La DEP empêche d'exécuter du code placé dans des zones de mémoire réservées aux données : c'est une barrière de base contre l'exploitation des failles. " +
            "La couper n'apporte aucun gain de performance.");
        var expected = T("OptIn (défaut) ou plus strict");
        var policy = FirstRow(cim.Query("SELECT DataExecutionPrevention_SupportPolicy FROM Win32_OperatingSystem"))?.GetInt64("DataExecutionPrevention_SupportPolicy");
        string? nx = null;
        _ = bcd?.TryGetValue("nx", out nx);
        var alwaysOff = policy == 0 || string.Equals(nx, "AlwaysOff", StringComparison.OrdinalIgnoreCase);

        if (alwaysOff)
        {
            return DepCheck.Deviation(
                T("toujours désactivée (AlwaysOff)"),
                expected,
                explanation,
                T("Rétablir la valeur OptIn dans la configuration de démarrage (bcdedit /set {current} nx OptIn), puis redémarrer."));
        }

        return policy is null && nx is null
            ? DepCheck.Unknown(T("Réglage de la DEP illisible."))
            : DepCheck.Compliant(DepLabel(policy, nx), expected, explanation);
    }

    private static string DepLabel(long? policy, string? nx) => policy switch
    {
        1 => T("toujours activée (AlwaysOn)"),
        2 => T("activée pour Windows et les programmes compatibles (OptIn)"),
        3 => T("activée pour tous les programmes sauf exceptions (OptOut)"),
        _ => nx ?? T("inconnue"),
    };

    private static Finding DetectDriverSigning(IRegistryReader registry, IReadOnlyDictionary<string, string>? bcd)
    {
        var explanation = T("Windows n'accepte normalement que des pilotes signés. Le mode test ou la désactivation des contrôles d'intégrité laissent charger n'importe quel pilote, " +
            "y compris un pilote malveillant qui aurait tous les droits sur le PC. Plusieurs anti-triche refusent aussi de lancer les jeux dans ce mode.");
        var expected = T("signature exigée");

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
            issues.Add(T("mode test (testsigning)"));
        }

        if (tokens.Contains("DISABLE_INTEGRITY_CHECKS") || (bcd is not null && BcdEditParser.IsYes(bcd, "nointegritychecks")))
        {
            issues.Add(T("contrôles d'intégrité désactivés (nointegritychecks)"));
        }

        return issues.Count == 0
            ? DriverSigningCheck.Compliant(expected, expected, explanation)
            : DriverSigningCheck.Deviation(
                Join(issues),
                expected,
                explanation,
                T("Supprimer testsigning et nointegritychecks de la configuration de démarrage (en suspendant BitLocker au préalable, voir Module 8), puis redémarrer."));
    }

    private static Finding DetectTimerTweaks(IReadOnlyDictionary<string, string>? bcd, bool elevated)
    {
        var explanation = T("Des « optimiseurs » forcent l'horloge matérielle HPET ou désactivent le « tick » dynamique pour gagner en réactivité. " +
            "Sur un PC récent, ces réglages augmentent plutôt la latence et la consommation, et font baisser les performances dans les jeux.");
        var expected = T("valeurs par défaut (non définies)");
        if (bcd is null)
        {
            return elevated
                ? TimerCheck.Unknown(T("Configuration de démarrage illisible (bcdedit)."))
                : TimerCheck.AdminRequired();
        }

        var tweaks = TimerTweaks.Where(t => BcdEditParser.IsYes(bcd, t.Element)).Select(t => t.Label).ToList();
        return tweaks.Count == 0
            ? TimerCheck.Compliant(expected, expected, explanation)
            : TimerCheck.Deviation(
                Join(tweaks),
                expected,
                explanation,
                T("Supprimer ces valeurs de la configuration de démarrage (bcdedit /deletevalue), puis redémarrer."));
    }

    private static Finding DetectSecureBoot(IRegistryReader registry)
    {
        var explanation = T("Le démarrage sécurisé vérifie qu'un chargeur signé se lance à l'allumage : il bloque les « bootkits », des logiciels malveillants qui s'installent avant Windows. " +
            "Windows 11 et plusieurs jeux protégés par un anti-triche l'exigent.");
        return registry.GetDword(Hklm, SecureBootStateKey, "UEFISecureBootEnabled") switch
        {
            1 => SecureBootCheck.Compliant(T("activé"), T("activé"), explanation),
            0 => SecureBootCheck.Deviation(
                T("désactivé"),
                T("activé"),
                explanation,
                T("Activer « Secure Boot » dans les réglages du BIOS/UEFI de la carte mère (voir Module 13) ; MAUS ne peut pas le faire depuis Windows.")),
            _ => SecureBootCheck.Unknown(T("État du démarrage sécurisé illisible (PC démarré en mode BIOS hérité ?).")),
        };
    }
}
