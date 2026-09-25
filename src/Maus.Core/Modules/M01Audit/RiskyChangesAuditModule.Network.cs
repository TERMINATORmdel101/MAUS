using Maus.Core.Platform;

namespace Maus.Core.Modules.M01Audit;

/// <summary>Catégorie « Réseau » : fichier hosts et proxy.</summary>
public sealed partial class RiskyChangesAuditModule
{
    private const string NetworkCategory = "Réseau";
    private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private static readonly Check HostsCheck = new("M01.hosts", "Fichier hosts bloquant Microsoft", NetworkCategory, Severity.High, Fixable: true);

    private static readonly Check ProxyCheck = new("M01.proxy", "Proxy imposé", NetworkCategory, Severity.High, Fixable: true);

    private Finding DetectHosts(AuditContext context)
    {
        const string explanation =
            "Le fichier hosts force l'adresse de certains sites. Des lignes qui détournent des domaines de Microsoft bloquent Windows Update, " +
            "l'activation, le Microsoft Store ou le test de connexion à Internet.";
        const string expected = "aucune ligne visant Microsoft";
        var path = Path.Combine(_windowsDirectory, @"System32\drivers\etc\hosts");
        if (!context.Files.FileExists(path))
        {
            return HostsCheck.Compliant("fichier absent (Windows fonctionne sans)", expected, explanation);
        }

        var entries = HostsFileParser.FindMicrosoftEntries(context.Files.ReadAllText(path));
        if (entries.Count == 0)
        {
            return HostsCheck.Compliant(expected, expected, explanation);
        }

        var hosts = entries.Select(e => e.HostName).Distinct(StringComparer.OrdinalIgnoreCase);
        return HostsCheck.Deviation(
            $"{entries.Count} ligne(s) visant Microsoft : {Join(hosts)}",
            expected,
            explanation,
            "Retirer seulement les lignes qui visent des domaines de Microsoft (MAUS en gardera une copie .bak) ; les autres lignes restent intactes.");
    }

    private static Finding DetectProxy(IRegistryReader registry, string? winHttpOutput, bool managed)
    {
        const string explanation =
            "Un proxy fait passer votre navigation par un autre ordinateur, qui peut la lire ou la modifier. " +
            "Il est parfois installé par un logiciel publicitaire ou malveillant, parfois par un outil de filtrage, un VPN ou une entreprise.";
        const string expected = "accès direct";
        var enabled = registry.GetDword(Hkcu, InternetSettingsKey, "ProxyEnable") == 1;
        var server = registry.GetString(Hkcu, InternetSettingsKey, "ProxyServer");
        var script = registry.GetString(Hkcu, InternetSettingsKey, "AutoConfigURL");
        var system = WinHttpProxyParser.Parse(winHttpOutput);

        var found = new List<string>();
        if (enabled && IsSet(server))
        {
            found.Add($"proxy de l'utilisateur : {server}");
        }

        if (IsSet(script))
        {
            found.Add($"script de configuration : {script}");
        }

        if (system.Kind == WinHttpProxyKind.Proxy)
        {
            found.Add($"proxy système (WinHTTP) : {system.Server}");
        }

        if (found.Count > 0)
        {
            return managed
                ? ProxyCheck.Neutral(Join(found), expected, "Sur un PC géré, un proxy est souvent imposé par l'organisation pour filtrer ou sécuriser l'accès à Internet.")
                : ProxyCheck.Deviation(
                    Join(found),
                    expected,
                    explanation,
                    "Si vous n'avez pas installé vous-même ce proxy (VPN, contrôle parental, outil de développement…), " +
                    "le désactiver dans Paramètres > Réseau et Internet > Proxy ; MAUS demandera confirmation avant de toucher au proxy système.");
        }

        return system.Kind == WinHttpProxyKind.Unknown
            ? ProxyCheck.Unknown("Aucun proxy pour l'utilisateur, mais la réponse de « netsh winhttp show proxy » (proxy système) est illisible.")
            : ProxyCheck.Compliant(expected, expected, explanation);
    }
}
