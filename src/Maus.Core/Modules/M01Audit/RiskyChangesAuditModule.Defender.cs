using Maus.Core.Platform;

namespace Maus.Core.Modules.M01Audit;

/// <summary>Catégorie « Defender » : protection en temps réel, falsifications, exclusions, et contexte du PC géré.</summary>
public sealed partial class RiskyChangesAuditModule
{
    private const string DefenderCategory = "Defender";
    internal const string DefenderStatusQuery = "SELECT RealTimeProtectionEnabled, IsTamperProtected, AntivirusEnabled, AMServiceEnabled FROM MSFT_MpComputerStatus";
    internal const string DefenderPreferenceQuery = "SELECT ExclusionPath, ExclusionExtension, ExclusionProcess FROM MSFT_MpPreference";
    private const string RealtimePolicyKey = @"SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection";

    private static readonly Check ManagedCheck = new("M01.managed-pc", "PC géré par une organisation", "Contexte", Severity.Info);

    private static readonly Check RealtimeCheck = new("M01.defender-realtime", "Protection en temps réel de l'antivirus", DefenderCategory, Severity.Critical, Fixable: true);

    private static readonly Check TamperCheck = new("M01.defender-tamper", "Protection contre les falsifications", DefenderCategory, Severity.High);

    private static readonly Check ExclusionsCheck = new("M01.defender-exclusions", "Exclusions de l'antivirus", DefenderCategory, Severity.Critical, Fixable: true);

    private static Finding ManagedFinding() => ManagedCheck.Neutral(
        "joint à un domaine ou inscrit dans une gestion à distance (MDM)",
        null,
        "Ce PC est administré par une organisation (entreprise, école). Certains réglages signalés ici peuvent avoir été voulus par elle : " +
        "les constats restent affichés, mais MAUS ne proposera aucune correction sur ce PC.",
        "En cas de doute, adressez-vous au service informatique qui gère ce PC.");

    private static Finding DetectRealtimeProtection(IRegistryReader registry, CimQueryResult defender, IReadOnlyList<SecurityProduct>? antivirus)
    {
        const string explanation =
            "La protection en temps réel analyse chaque fichier au moment où il est ouvert ou téléchargé. " +
            "Sans elle, un logiciel malveillant peut s'exécuter sans être arrêté.";
        const string expected = "active";
        var policy = registry.GetDword(Hklm, RealtimePolicyKey, "DisableRealtimeMonitoring") == 1;
        var realtime = defender.First?.GetBool("RealTimeProtectionEnabled");
        var thirdParty = SecurityProduct.ActiveThirdParty(antivirus);

        if (thirdParty is not null && realtime != true)
        {
            return RealtimeCheck.Neutral(
                $"assurée par {thirdParty.Name}",
                "un antivirus actif",
                $"Un autre antivirus ({thirdParty.Name}) protège ce PC : Microsoft Defender se met alors en retrait. C'est le fonctionnement normal.",
                policy ? "La stratégie DisableRealtimeMonitoring est aussi présente : pensez à la supprimer si vous désinstallez un jour cet antivirus." : null);
        }

        if (policy)
        {
            return RealtimeCheck.Deviation(
                "coupée par une stratégie (DisableRealtimeMonitoring = 1)",
                expected,
                "Microsoft Defender est coupé par une stratégie. Votre PC n'a plus de protection en temps réel. " + explanation,
                "Supprimer la valeur DisableRealtimeMonitoring, puis réactiver la protection dans Sécurité Windows > Protection contre les virus et menaces.");
        }

        switch (realtime)
        {
            case true:
                return RealtimeCheck.Compliant("active", expected, explanation);
            case false:
                return RealtimeCheck.Deviation(
                    "désactivée",
                    expected,
                    explanation,
                    "Réactiver la protection en temps réel dans Sécurité Windows > Protection contre les virus et menaces > Gérer les paramètres.");
        }

        // Defender illisible : le Centre de sécurité dit encore si un antivirus, quel qu'il soit, fonctionne.
        if (antivirus is { Count: > 0 } && antivirus.All(product => !product.IsActive))
        {
            return RealtimeCheck.Deviation(
                "aucun antivirus actif",
                "un antivirus actif",
                "Le Centre de sécurité Windows ne signale aucun antivirus en fonctionnement. " + explanation,
                "Ouvrir Sécurité Windows et réactiver Microsoft Defender, ou réinstaller votre antivirus.");
        }

        return defender.AccessDenied ? RealtimeCheck.AdminRequired() : RealtimeCheck.Unknown("État de Microsoft Defender illisible sur ce PC.");
    }

    private static Finding DetectTamperProtection(CimQueryResult defender, IReadOnlyList<SecurityProduct>? antivirus)
    {
        const string explanation =
            "Cette protection empêche les programmes, y compris les scripts lancés en administrateur, de désactiver Microsoft Defender dans votre dos. " +
            "Seul un réglage manuel dans Sécurité Windows peut la couper.";
        var tamper = defender.First?.GetBool("IsTamperProtected");
        if (tamper == true)
        {
            return TamperCheck.Compliant("active", "active", explanation);
        }

        var thirdParty = SecurityProduct.ActiveThirdParty(antivirus);
        if (thirdParty is not null)
        {
            return TamperCheck.Neutral(
                $"sans objet ({thirdParty.Name} protège ce PC)",
                "active",
                "Microsoft Defender est en retrait derrière un autre antivirus : sa protection contre les falsifications n'est alors pas utilisée.");
        }

        if (tamper == false)
        {
            return TamperCheck.Deviation(
                "désactivée",
                "active",
                explanation,
                "Ouvrir Sécurité Windows > Protection contre les virus et menaces > Gérer les paramètres, puis activer « Protection contre les falsifications ». " +
                "Ce réglage ne peut pas être changé par un programme : MAUS vous guidera pas à pas.");
        }

        return defender.AccessDenied ? TamperCheck.AdminRequired() : TamperCheck.Unknown("État de Microsoft Defender illisible sur ce PC.");
    }

    private static Finding DetectExclusions(AuditContext context)
    {
        const string explanation =
            "Une exclusion demande à l'antivirus de ne pas analyser un dossier, un type de fichier ou un programme. " +
            "Exclure un disque entier, un dossier système, les fichiers .exe ou PowerShell ouvre une porte que les logiciels malveillants savent utiliser.";
        const string expected = "aucune exclusion large";
        var preferences = CimQueryResult.Run(context.Cim, DefenderPreferenceQuery, CimScopes.Defender);
        if (preferences.AccessDenied)
        {
            return ExclusionsCheck.AdminRequired();
        }

        if (preferences.First is not { } row)
        {
            return ExclusionsCheck.Unknown("Réglages de Microsoft Defender illisibles sur ce PC.");
        }

        var exclusions = DefenderExclusion.ReadAll(row);
        if (exclusions is null)
        {
            return ExclusionsCheck.AdminRequired();
        }

        var broad = exclusions.Where(DefenderExclusionClassifier.IsBroad).ToList();
        if (broad.Count > 0)
        {
            return ExclusionsCheck.Deviation(
                $"{broad.Count} exclusion(s) trop large(s) : {Join(broad.Select(e => e.ToString()))}",
                expected,
                explanation,
                "Retirer ces exclusions dans Sécurité Windows > Protection contre les virus et menaces > Gérer les paramètres > Exclusions. " +
                "Si un jeu ou un logiciel en avait besoin, exclure seulement son dossier précis.");
        }

        if (exclusions.Count > 0)
        {
            return ExclusionsCheck.Neutral(
                $"{exclusions.Count} exclusion(s) ciblée(s) : {Join(exclusions.Select(e => e.ToString()))}",
                expected,
                "Ces exclusions visent des emplacements précis ; elles sont en général voulues (dossier d'un jeu, d'un outil de développement). " +
                "Vérifiez simplement que vous les reconnaissez.");
        }

        return ExclusionsCheck.Compliant("aucune", expected, explanation);
    }
}
