using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M01Audit;

/// <summary>Catégorie « Defender » : protection en temps réel, falsifications, exclusions, et contexte du PC géré.</summary>
public sealed partial class RiskyChangesAuditModule
{
    private const string DefenderCategory = "Defender";
    internal const string DefenderStatusQuery = "SELECT RealTimeProtectionEnabled, IsTamperProtected, AntivirusEnabled, AMServiceEnabled FROM MSFT_MpComputerStatus";
    internal const string DefenderPreferenceQuery = "SELECT ExclusionPath, ExclusionExtension, ExclusionProcess FROM MSFT_MpPreference";
    private const string RealtimePolicyKey = @"SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection";

    /// <summary>
    /// Page « Sécurité Windows » des Paramètres (Microsoft Learn, « Launch Windows Settings ») : elle liste les zones de protection
    /// (virus et menaces, pare-feu, sécurité des appareils…). Aucune adresse « ms-settings: » n'ouvre directement une de ces zones.
    /// </summary>
    internal const string WindowsSecurityPage = "ms-settings:windowsdefender";

    private static Check ManagedCheck => new("M01.managed-pc", T("PC géré par une organisation"), "Contexte", Severity.Info);

    private static Check RealtimeCheck => new("M01.defender-realtime", T("Protection en temps réel de l'antivirus"), DefenderCategory, Severity.Critical, Fixable: true, SettingsPage: WindowsSecurityPage);

    private static Check TamperCheck => new("M01.defender-tamper", T("Protection contre les falsifications"), DefenderCategory, Severity.High, SettingsPage: WindowsSecurityPage);

    private static Check ExclusionsCheck => new("M01.defender-exclusions", T("Exclusions de l'antivirus"), DefenderCategory, Severity.Critical, Fixable: true, SettingsPage: WindowsSecurityPage);

    private static Finding ManagedFinding() => ManagedCheck.Neutral(
        T("joint à un domaine ou inscrit dans une gestion à distance (MDM)"),
        null,
        T("Ce PC est administré par une organisation (entreprise, école). Certains réglages signalés ici peuvent avoir été voulus par elle : " +
        "les constats restent affichés, mais MAUS ne proposera aucune correction sur ce PC."),
        T("En cas de doute, adressez-vous au service informatique qui gère ce PC."));

    private static Finding DetectRealtimeProtection(IRegistryReader registry, CimQueryResult defender, IReadOnlyList<SecurityProduct>? antivirus)
    {
        var explanation = T("La protection en temps réel analyse chaque fichier au moment où il est ouvert ou téléchargé. " +
            "Sans elle, un logiciel malveillant peut s'exécuter sans être arrêté.");
        var expected = T("active");
        var policy = registry.GetDword(Hklm, RealtimePolicyKey, "DisableRealtimeMonitoring") == 1;
        var realtime = defender.First?.GetBool("RealTimeProtectionEnabled");
        var thirdParty = SecurityProduct.ActiveThirdParty(antivirus);

        if (thirdParty is not null && realtime != true)
        {
            return RealtimeCheck.Neutral(
                T("assurée par {0}", thirdParty.Name),
                T("un antivirus actif"),
                T("Un autre antivirus ({0}) protège ce PC : Microsoft Defender se met alors en retrait. C'est le fonctionnement normal.", thirdParty.Name),
                policy ? T("La stratégie DisableRealtimeMonitoring est aussi présente : pensez à la supprimer si vous désinstallez un jour cet antivirus.") : null);
        }

        if (policy)
        {
            return RealtimeCheck.Deviation(
                T("coupée par une stratégie (DisableRealtimeMonitoring = 1)"),
                expected,
                T("Microsoft Defender est coupé par une stratégie. Votre PC n'a plus de protection en temps réel. ") + explanation,
                T("Supprimer la valeur DisableRealtimeMonitoring, puis réactiver la protection dans Sécurité Windows > Protection contre les virus et menaces."));
        }

        switch (realtime)
        {
            case true:
                return RealtimeCheck.Compliant(T("active"), expected, explanation);
            case false:
                return RealtimeCheck.Deviation(
                    T("désactivée"),
                    expected,
                    explanation,
                    T("Réactiver la protection en temps réel dans Sécurité Windows > Protection contre les virus et menaces > Gérer les paramètres."));
        }

        // Defender illisible : le Centre de sécurité dit encore si un antivirus, quel qu'il soit, fonctionne.
        if (antivirus is { Count: > 0 } && antivirus.All(product => !product.IsActive))
        {
            return RealtimeCheck.Deviation(
                T("aucun antivirus actif"),
                T("un antivirus actif"),
                T("Le Centre de sécurité Windows ne signale aucun antivirus en fonctionnement. ") + explanation,
                T("Ouvrir Sécurité Windows et réactiver Microsoft Defender, ou réinstaller votre antivirus."));
        }

        return defender.AccessDenied ? RealtimeCheck.AdminRequired() : RealtimeCheck.Unknown(T("État de Microsoft Defender illisible sur ce PC."));
    }

    private static Finding DetectTamperProtection(CimQueryResult defender, IReadOnlyList<SecurityProduct>? antivirus)
    {
        var explanation = T("Cette protection empêche les programmes, y compris les scripts lancés en administrateur, de désactiver Microsoft Defender dans votre dos. " +
            "Seul un réglage manuel dans Sécurité Windows peut la couper.");
        var tamper = defender.First?.GetBool("IsTamperProtected");
        if (tamper == true)
        {
            return TamperCheck.Compliant(T("active"), T("active"), explanation);
        }

        var thirdParty = SecurityProduct.ActiveThirdParty(antivirus);
        if (thirdParty is not null)
        {
            return TamperCheck.Neutral(
                T("sans objet ({0} protège ce PC)", thirdParty.Name),
                T("active"),
                T("Microsoft Defender est en retrait derrière un autre antivirus : sa protection contre les falsifications n'est alors pas utilisée."));
        }

        if (tamper == false)
        {
            return TamperCheck.Deviation(
                T("désactivée"),
                T("active"),
                explanation,
                T("Ouvrir Sécurité Windows > Protection contre les virus et menaces > Gérer les paramètres, puis activer « Protection contre les falsifications ». " +
                "Ce réglage ne peut pas être changé par un programme : MAUS vous guidera pas à pas."));
        }

        return defender.AccessDenied ? TamperCheck.AdminRequired() : TamperCheck.Unknown(T("État de Microsoft Defender illisible sur ce PC."));
    }

    private static Finding DetectExclusions(AuditContext context)
    {
        var explanation = T("Une exclusion demande à l'antivirus de ne pas analyser un dossier, un type de fichier ou un programme. " +
            "Exclure un disque entier, un dossier système, les fichiers .exe ou PowerShell ouvre une porte que les logiciels malveillants savent utiliser.");
        var expected = T("aucune exclusion large");
        var preferences = CimQueryResult.Run(context.Cim, DefenderPreferenceQuery, CimScopes.Defender);
        if (preferences.AccessDenied)
        {
            return ExclusionsCheck.AdminRequired();
        }

        if (preferences.First is not { } row)
        {
            return ExclusionsCheck.Unknown(T("Réglages de Microsoft Defender illisibles sur ce PC."));
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
                T("{0} exclusion(s) trop large(s) : {1}", broad.Count, Join(broad.Select(e => e.ToString()))),
                expected,
                explanation,
                T("Retirer ces exclusions dans Sécurité Windows > Protection contre les virus et menaces > Gérer les paramètres > Exclusions. " +
                "Si un jeu ou un logiciel en avait besoin, exclure seulement son dossier précis."));
        }

        if (exclusions.Count > 0)
        {
            return ExclusionsCheck.Neutral(
                T("{0} exclusion(s) ciblée(s) : {1}", exclusions.Count, Join(exclusions.Select(e => e.ToString()))),
                expected,
                T("Ces exclusions visent des emplacements précis ; elles sont en général voulues (dossier d'un jeu, d'un outil de développement). " +
                "Vérifiez simplement que vous les reconnaissez."));
        }

        return ExclusionsCheck.Compliant(T("aucune"), expected, explanation);
    }
}
