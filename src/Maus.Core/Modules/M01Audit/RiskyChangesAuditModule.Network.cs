using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M01Audit;

/// <summary>Catégorie « Réseau » : fichier hosts et proxy.</summary>
public sealed partial class RiskyChangesAuditModule
{
    private static string NetworkCategory => T("Réseau");
    private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private static Check HostsCheck => new("M01.hosts", T("Fichier hosts bloquant Microsoft"), NetworkCategory, Severity.High, Fixable: true);

    private static Check ProxyCheck => new("M01.proxy", T("Proxy imposé"), NetworkCategory, Severity.High, Fixable: true);

    private Finding DetectHosts(AuditContext context)
    {
        var explanation = T("Le fichier hosts force l'adresse de certains sites. Des lignes qui détournent des domaines de Microsoft bloquent Windows Update, " +
            "l'activation, le Microsoft Store ou le test de connexion à Internet.");
        var expected = T("aucune ligne visant Microsoft");
        var path = Path.Combine(_windowsDirectory, @"System32\drivers\etc\hosts");
        if (!context.Files.FileExists(path))
        {
            return HostsCheck.Compliant(T("fichier absent (Windows fonctionne sans)"), expected, explanation);
        }

        var entries = HostsFileParser.FindMicrosoftEntries(context.Files.ReadAllText(path));
        if (entries.Count == 0)
        {
            return HostsCheck.Compliant(expected, expected, explanation);
        }

        var hosts = entries.Select(e => e.HostName).Distinct(StringComparer.OrdinalIgnoreCase);
        return HostsCheck.Deviation(
            T("{0} ligne(s) visant Microsoft : {1}", entries.Count, Join(hosts)),
            expected,
            explanation,
            T("Retirer seulement les lignes qui visent des domaines de Microsoft (MAUS en gardera une copie .bak) ; les autres lignes restent intactes."));
    }

    private static Finding DetectProxy(IRegistryReader registry, string? winHttpOutput, bool managed)
    {
        var explanation = T("Un proxy fait passer votre navigation par un autre ordinateur, qui peut la lire ou la modifier. " +
            "Il est parfois installé par un logiciel publicitaire ou malveillant, parfois par un outil de filtrage, un VPN ou une entreprise.");
        var expected = T("accès direct");
        var enabled = registry.GetDword(Hkcu, InternetSettingsKey, "ProxyEnable") == 1;
        var server = registry.GetString(Hkcu, InternetSettingsKey, "ProxyServer");
        var script = registry.GetString(Hkcu, InternetSettingsKey, "AutoConfigURL");
        var system = WinHttpProxyParser.Parse(winHttpOutput);

        var found = new List<string>();
        if (enabled && IsSet(server))
        {
            found.Add(T("proxy de l'utilisateur : {0}", server));
        }

        if (IsSet(script))
        {
            found.Add(T("script de configuration : {0}", script));
        }

        if (system.Kind == WinHttpProxyKind.Proxy)
        {
            found.Add(T("proxy système (WinHTTP) : {0}", system.Server));
        }

        if (found.Count > 0)
        {
            return managed
                ? ProxyCheck.Neutral(Join(found), expected, T("Sur un PC géré, un proxy est souvent imposé par l'organisation pour filtrer ou sécuriser l'accès à Internet."))
                : ProxyCheck.Deviation(
                    Join(found),
                    expected,
                    explanation,
                    T("Si vous n'avez pas installé vous-même ce proxy (VPN, contrôle parental, outil de développement…), " +
                    "le désactiver dans Paramètres > Réseau et Internet > Proxy ; MAUS demandera confirmation avant de toucher au proxy système."));
        }

        return system.Kind == WinHttpProxyKind.Unknown
            ? ProxyCheck.Unknown(T("Aucun proxy pour l'utilisateur, mais la réponse de « netsh winhttp show proxy » (proxy système) est illisible."))
            : ProxyCheck.Compliant(expected, expected, explanation);
    }
}
