using Maus.Core.Platform;

namespace Maus.Core.Modules.M01Audit;

/// <summary>Catégorie « Défenses » : invite de l'UAC et pare-feu (SmartScreen et EnableLUA sont dans le catalogue JSON).</summary>
public sealed partial class RiskyChangesAuditModule
{
    private const string DefensesCategory = "Défenses";
    private const string UacKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string FirewallPolicyKey = @"SOFTWARE\Policies\Microsoft\WindowsFirewall\";
    internal const string FirewallProfileQuery = "SELECT Name, Enabled FROM MSFT_NetFirewallProfile";

    private static readonly Check UacPromptCheck = new("M01.uac-prompt", "Demande de confirmation de l'UAC", DefensesCategory, Severity.High, Fixable: true);

    private static readonly Check FirewallCheck = new("M01.firewall", "Pare-feu Windows", DefensesCategory, Severity.Critical, Fixable: true);

    /// <summary>Clés de stratégie par profil ; « StandardProfile » est l'ancien nom du profil privé.</summary>
    private static readonly (string Key, string Label)[] FirewallPolicyProfiles =
    [
        ("DomainProfile", "domaine"),
        ("PrivateProfile", "privé"),
        ("StandardProfile", "privé"),
        ("PublicProfile", "public"),
    ];

    private static Finding DetectUacPrompt(IRegistryReader registry)
    {
        const string explanation =
            "Quand un programme demande les droits administrateur, l'UAC affiche une fenêtre de confirmation sur un bureau sécurisé (écran assombri), " +
            "qu'aucun autre programme ne peut valider à votre place.";
        const string expected = "confirmation sur bureau sécurisé (5 et 1)";
        const string advice = "Remettre ConsentPromptBehaviorAdmin à 5 et PromptOnSecureDesktop à 1 (curseur de l'UAC sur le niveau par défaut).";
        var consent = registry.GetDword(Hklm, UacKey, "ConsentPromptBehaviorAdmin");
        var secureDesktop = registry.GetDword(Hklm, UacKey, "PromptOnSecureDesktop");
        var current = $"{ConsentLabel(consent)} ; bureau sécurisé : {(secureDesktop == 0 ? "non" : "oui")}";

        if (consent == 0)
        {
            return UacPromptCheck.Deviation(
                current,
                expected,
                "Les programmes obtiennent les droits administrateur sans rien vous demander : un logiciel malveillant peut modifier Windows en silence. " + explanation,
                advice);
        }

        if (secureDesktop == 0)
        {
            return UacPromptCheck.Deviation(
                current,
                expected,
                "La fenêtre de confirmation s'affiche sans assombrir l'écran : un programme malveillant déjà présent pourrait la valider à votre place. " + explanation,
                advice,
                Severity.Medium);
        }

        return UacPromptCheck.Compliant(current, expected, explanation);
    }

    private static string ConsentLabel(int? consent) => consent switch
    {
        0 => "élévation sans aucune demande",
        1 => "mot de passe demandé (bureau sécurisé)",
        2 => "confirmation à chaque changement",
        3 => "mot de passe demandé",
        4 => "confirmation demandée",
        null or 5 => "confirmation pour les programmes (défaut)",
        _ => "réglage inconnu",
    };

    private static Finding DetectFirewall(AuditContext context, IReadOnlyList<SecurityProduct>? firewalls)
    {
        const string explanation =
            "Le pare-feu filtre les connexions entrantes : il empêche un autre appareil du réseau (Wi-Fi public, box, PC infecté) " +
            "de joindre directement les services de votre PC.";
        const string expected = "activé sur les 3 profils";

        var off = new SortedSet<string>(StringComparer.Ordinal);
        var byPolicy = false;
        foreach (var (key, label) in FirewallPolicyProfiles)
        {
            if (context.Registry.GetDword(Hklm, FirewallPolicyKey + key, "EnableFirewall") == 0)
            {
                off.Add(label);
                byPolicy = true;
            }
        }

        var profiles = CimQueryResult.Run(context.Cim, FirewallProfileQuery, CimScopes.StandardCimv2);
        foreach (var row in profiles.Rows ?? [])
        {
            // Enabled vaut 0 (non), 1 (oui) ou 2 (non configuré, donc actif par défaut).
            if (row.GetBool("Enabled") == false)
            {
                off.Add(FirewallProfileLabel(row.GetString("Name")));
            }
        }

        if (off.Count == 0)
        {
            if (profiles.Rows is not { Count: > 0 })
            {
                return profiles.AccessDenied ? FirewallCheck.AdminRequired() : FirewallCheck.Unknown("État du pare-feu illisible sur ce PC.");
            }

            return FirewallCheck.Compliant("activé sur les 3 profils", expected, explanation);
        }

        var current = $"coupé sur le(s) profil(s) : {string.Join(", ", off)}{(byPolicy ? " (par une stratégie)" : string.Empty)}";
        if (SecurityProduct.ActiveThirdParty(firewalls) is { } thirdParty)
        {
            return FirewallCheck.Neutral(
                $"{current} ; {thirdParty.Name} actif",
                expected,
                $"Un autre pare-feu ({thirdParty.Name}) protège ce PC : le pare-feu Windows peut alors être coupé. C'est un fonctionnement normal.");
        }

        const string advice =
            "Supprimer les stratégies EnableFirewall éventuelles, puis réactiver le pare-feu pour tous les profils dans Sécurité Windows > Pare-feu et protection du réseau.";

        // Le profil « domaine » ne sert que sur un réseau d'entreprise : coupé seul sur un PC personnel, le risque est limité.
        var onlyDomain = off.Count == 1 && off.Contains("domaine");
        return onlyDomain && !context.Hardware.IsManaged
            ? FirewallCheck.Deviation(current, expected, "Le profil « domaine » ne sert que sur un réseau d'entreprise, mais sa désactivation trahit souvent un script de « nettoyage ». " + explanation, advice, Severity.Medium)
            : FirewallCheck.Deviation(current, expected, explanation, advice);
    }

    private static string FirewallProfileLabel(string? name) => name?.Trim().ToUpperInvariant() switch
    {
        "DOMAIN" => "domaine",
        "PRIVATE" => "privé",
        "PUBLIC" => "public",
        _ => name ?? "inconnu",
    };
}
