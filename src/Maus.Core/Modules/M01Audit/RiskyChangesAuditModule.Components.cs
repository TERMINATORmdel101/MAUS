using System.Globalization;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M01Audit;

/// <summary>Catégorie « Composants » : services système, applications intégrées, WebView2, fichier d'échange.</summary>
public sealed partial class RiskyChangesAuditModule
{
    private static string ComponentsCategory => T("Composants");
    private const string WebView2ClientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    private const string EdgeClientId = "{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}";

    /// <summary>Services dont l'absence n'est pas anormale (arrivés avec une mise à jour récente).</summary>
    private static readonly HashSet<string> OptionalServices = new(StringComparer.OrdinalIgnoreCase) { "UCPD" };

    private static readonly string[] CoreServices = ["CryptSvc", "TrustedInstaller", "Audiosrv", "WSearch", "WinDefend", "mpssvc", "UCPD"];

    private static readonly (string Name, string Label)[] RequiredApps =
    [
        ("Microsoft.WindowsStore", "Microsoft Store"),
        ("Microsoft.SecHealthUI", T("Sécurité Windows")),
        ("Microsoft.DesktopAppInstaller", T("Programme d'installation d'application (winget)")),
    ];

    private static Check CoreServicesCheck => new("M01.core-services", T("Services système essentiels"), ComponentsCategory, Severity.High, Fixable: true);

    private static Check AppsCheck => new("M01.system-apps", T("Microsoft Store et Sécurité Windows"), ComponentsCategory, Severity.Medium);

    private static readonly Check WebViewCheck = new("M01.webview2", "Composant WebView2 et Microsoft Edge", ComponentsCategory, Severity.High);

    private static Check PageFileCheck => new("M01.pagefile", T("Fichier d'échange"), ComponentsCategory, Severity.Medium, Fixable: true);

    private static Finding DetectCoreServices(IRegistryReader registry, IReadOnlyList<SecurityProduct>? antivirus)
    {
        var thirdParty = SecurityProduct.ActiveThirdParty(antivirus) is not null;
        var services = CoreServices
            .Select(name => ServiceStart.Read(registry, name))
            .Where(s => s.Exists || !OptionalServices.Contains(s.Name))
            .ToList();

        return EvaluateServices(
            CoreServicesCheck,
            services,
            T("Ces services font fonctionner des briques de base de Windows : vérification des signatures (CryptSvc), installation des mises à jour (TrustedInstaller), " +
            "son (Audiosrv), recherche (WSearch), antivirus (WinDefend), pare-feu (mpssvc), protection des applications par défaut (UCPD). " +
            "Désactivés, ils provoquent des pannes difficiles à relier à leur cause."),
            T("Rétablir le type de démarrage d'origine de ces services (automatique ou manuel selon le service)."),
            service => service.Name switch
            {
                // Couper l'indexation est un choix répandu ; la recherche reste possible, en plus lent.
                "WSearch" or "UCPD" => Severity.Medium,
                "WinDefend" or "mpssvc" when thirdParty => Severity.Medium,
                _ => Severity.High,
            });
    }

    private static Finding DetectSystemApps(AuditContext context)
    {
        var explanation = T("Le Microsoft Store installe et met à jour de nombreuses applications ; l'application Sécurité Windows est l'écran de réglage de l'antivirus " +
            "et du pare-feu ; le Programme d'installation d'application fournit la commande winget. Certains scripts les retirent alors que Windows en a besoin.");
        var expected = T("présentes");
        var packages = context.Packages.GetUserPackages();
        if (packages.Count == 0)
        {
            return AppsCheck.Unknown(T("Inventaire des applications indisponible."));
        }

        var missing = RequiredApps
            .Where(app => !packages.Any(p => p.Name.Equals(app.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(app => app.Label)
            .ToList();
        return missing.Count == 0
            ? AppsCheck.Compliant(expected, expected, explanation)
            : AppsCheck.Deviation(
                T("absente(s) : {0}", Join(missing)),
                expected,
                explanation,
                T("Réinstaller les applications manquantes depuis le Microsoft Store ; si le Store lui-même manque, la commande « wsreset -i » peut le réinstaller."));
    }

    private static Finding DetectWebView(IRegistryReader registry)
    {
        var explanation = T("WebView2 affiche des pages web à l'intérieur des applications (Widgets, Teams, Outlook, Office, de nombreux jeux et lanceurs). " +
            "Retiré avec Microsoft Edge par certains scripts, il laisse ces applications vides ou en panne.");
        var expected = T("WebView2 présent");
        var webView = ReadEdgeClientVersion(registry, WebView2ClientId);
        var edge = ReadEdgeClientVersion(registry, EdgeClientId);
        if (webView is null)
        {
            return WebViewCheck.Deviation(
                edge is null ? T("WebView2 et Edge absents") : T("WebView2 absent"),
                expected,
                explanation,
                T("Réinstaller le runtime WebView2 « Evergreen » depuis le site officiel de Microsoft."));
        }

        return edge is null
            ? WebViewCheck.Neutral(
                T("WebView2 {0} ; Edge absent", webView),
                expected,
                T("Microsoft Edge a été désinstallé, mais WebView2 est présent : les applications qui en dépendent fonctionnent normalement."))
            : WebViewCheck.Compliant($"WebView2 {webView} ; Edge {edge}", expected, explanation);
    }

    /// <summary>Version déclarée à EdgeUpdate (installation pour tous, 32 ou 64 bits, ou pour l'utilisateur) ; « 0.0.0.0 » signifie désinstallé.</summary>
    private static string? ReadEdgeClientVersion(IRegistryReader registry, string clientId)
    {
        var locations = new[]
        {
            (Hklm, @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + clientId),
            (Hklm, @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + clientId),
            (Hkcu, @"Software\Microsoft\EdgeUpdate\Clients\" + clientId),
        };
        foreach (var (hive, path) in locations)
        {
            var version = registry.GetString(hive, path, "pv");
            if (IsSet(version) && version != "0.0.0.0")
            {
                return version;
            }
        }

        return null;
    }

    private static Finding DetectPageFile(ICimReader cim)
    {
        var explanation = T("Le fichier d'échange sert de réserve quand la mémoire vive est pleine. Sans lui, les applications gourmandes (jeux, navigateurs) " +
            "peuvent se fermer brutalement, et Windows ne peut pas enregistrer de rapport après un écran bleu.");
        var expected = T("géré automatiquement par Windows");
        var advice = T("Rétablir « Gestion automatique du fichier d'échange pour les lecteurs » (Paramètres système avancés > Performances > Mémoire virtuelle), puis redémarrer.");
        var automatic = FirstRow(cim.Query("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem"))?.GetBool("AutomaticManagedPagefile");
        if (automatic == true)
        {
            return PageFileCheck.Compliant(expected, expected, explanation);
        }

        var settings = cim.Query("SELECT Name, InitialSize, MaximumSize FROM Win32_PageFileSetting");
        if (settings.Count == 0)
        {
            return automatic is null
                ? PageFileCheck.Unknown(T("Réglage du fichier d'échange illisible."))
                : PageFileCheck.Deviation(T("aucun fichier d'échange"), expected, explanation, advice);
        }

        var files = settings.Select(DescribePageFile).ToList();

        // Une taille maximale nulle signifie « taille gérée par le système » pour ce fichier.
        var fixedMaximum = settings.Select(s => s.GetInt64("MaximumSize") ?? 0).ToList();
        if (fixedMaximum.All(size => size == 0))
        {
            return PageFileCheck.Compliant(Join(files), expected, explanation);
        }

        if (fixedMaximum.All(size => size > 0) && fixedMaximum.Sum() < 1024)
        {
            return PageFileCheck.Deviation(T("très petit : {0}", Join(files)), expected, explanation, advice);
        }

        return PageFileCheck.Deviation(
            T("réglé à la main : {0}", Join(files)),
            expected,
            T("Une taille choisie à la main fonctionne, mais Windows ajuste mieux la taille seul (pics de mémoire des jeux, rapports après un écran bleu). ") + explanation,
            advice,
            Severity.Low);
    }

    private static string DescribePageFile(CimRow setting)
    {
        var name = setting.GetString("Name") ?? "?";
        var initial = setting.GetInt64("InitialSize") ?? 0;
        var maximum = setting.GetInt64("MaximumSize") ?? 0;
        return maximum == 0
            ? T("{0} (taille gérée par le système)", name)
            : T("{0} ({1} à {2} Mo)", name, initial.ToString(CultureInfo.InvariantCulture), maximum.ToString(CultureInfo.InvariantCulture));
    }
}
