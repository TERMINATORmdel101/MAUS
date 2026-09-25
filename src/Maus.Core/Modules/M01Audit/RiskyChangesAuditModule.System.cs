using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M01Audit;

/// <summary>Catégorie « Système » : image modifiée, activation, stratégies résiduelles.</summary>
public sealed partial class RiskyChangesAuditModule
{
    private static string SystemCategory => T("Système");
    private const string PoliciesRoot = @"SOFTWARE\Policies\Microsoft";
    private const int MaxPolicyKeys = 3000;

    /// <summary>Identifiant d'application de Windows dans <c>SoftwareLicensingProduct</c>.</summary>
    internal const string ActivationQuery =
        "SELECT Name, ProductKeyChannel, KeyManagementServiceMachine, DiscoveredKeyManagementServiceMachineName, LicenseStatus " +
        "FROM SoftwareLicensingProduct WHERE ApplicationID = '55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL";

    /// <summary>
    /// Sous-clés de <c>HKLM\SOFTWARE\Policies\Microsoft</c> qui ne sont pas des « restes » : présentes sur une installation propre,
    /// déjà couvertes par un autre contrôle de ce module, ou réglages de confort et de confidentialité proposés au choix par d'autres modules de MAUS.
    /// </summary>
    private static readonly string[] AcceptedPolicyPrefixes =
    [
        // Présentes sur une installation propre de Windows 11.
        "Cryptography", "PeerDist", "Peernet", "SystemCertificates", "TPM",
        @"Windows\Appx", @"Windows\CurrentVersion\Internet Settings", @"Windows\EnhancedStorageDevices", @"Windows\IPSec",
        @"Windows\Network Connections", @"Windows\NetworkConnectivityStatusIndicator", @"Windows\safer", @"Windows\SettingSync",
        @"Windows\WcmSvc", @"Windows\WorkplaceJoin", @"Windows NT\Printers", @"Windows NT\Terminal Services", @"Windows NT\Windows File Protection",
        @"Windows NT\Rpc",

        // Couvertes par un contrôle dédié de ce module.
        "Windows Defender", @"Windows\WindowsUpdate", "WindowsFirewall", @"Windows\System\EnableSmartScreen", @"Windows\System\ShellSmartScreenLevel",

        // Choix de confort ou de confidentialité laissés à l'utilisateur (Widgets, Copilot, Recall, OneDrive, télémétrie, Edge…).
        "Dsh", "Edge", "MicrosoftEdge", @"Windows\WindowsCopilot", @"Windows\WindowsAI", @"Windows\CloudContent", @"Windows\Windows Feeds",
        @"Windows\OneDrive", @"Windows\DataCollection", @"Windows\GameDVR", @"Windows\AdvertisingInfo", @"Windows\Windows Search",
    ];

    private static Check ModifiedImageCheck => new("M01.modified-image", T("Image de Windows modifiée"), SystemCategory, Severity.High);

    private static readonly Check ActivationCheck = new("M01.activation", "Activation de Windows", SystemCategory, Severity.Medium);

    private static Check ResidualPoliciesCheck => new("M01.residual-policies", T("Stratégies locales résiduelles"), SystemCategory, Severity.Low, Fixable: true);

    private Finding DetectModifiedImage(AuditContext context, bool? winRe)
    {
        var explanation = T("Des versions « allégées » de Windows (Tiny11, AtlasOS, ReviOS…) retirent des composants de sécurité et de mise à jour. " +
            "MAUS cherche un faisceau d'indices : services supprimés, dossiers propres à ces versions, environnement de récupération absent.");
        var expected = T("image officielle de Microsoft");
        var clues = new List<string>();
        var strong = false;
        foreach (var (service, label) in new[]
        {
            ("WinDefend", T("service de l'antivirus (WinDefend) supprimé")),
            ("wuauserv", T("service Windows Update supprimé")),
            ("WaaSMedicSvc", T("service de réparation de Windows Update supprimé")),
        })
        {
            if (!context.Registry.KeyExists(Hklm, ServicesKey + service))
            {
                clues.Add(label);
            }
        }

        if (context.Files.DirectoryExists(Path.Combine(_windowsDirectory, "AtlasModules")))
        {
            clues.Add("dossier AtlasModules (AtlasOS)");
            strong = true;
        }

        if (winRe == false)
        {
            clues.Add(T("environnement de récupération (WinRE) désactivé"));
        }

        if (clues.Count == 0)
        {
            return ModifiedImageCheck.Compliant(
                winRe is null ? T("aucun indice (WinRE non vérifié sans droits administrateur)") : T("aucun indice"),
                expected,
                explanation);
        }

        if (strong || clues.Count >= 2)
        {
            return ModifiedImageCheck.Deviation(
                Join(clues),
                expected,
                T("Ce Windows provient d'une image modifiée. Des composants de sécurité et de mise à jour manquent. " +
                "Seule une réinstallation depuis l'outil officiel Microsoft les rétablit. ") + explanation,
                T("Sauvegarder vos données, puis réinstaller Windows avec l'outil de création de support officiel de Microsoft."));
        }

        var onlyWinRe = winRe == false;
        return ModifiedImageCheck.Deviation(
            T("indice isolé : {0}", clues[0]),
            expected,
            T("Un seul indice ne suffit pas à conclure : il peut venir d'une manipulation ponctuelle. ") + explanation,
            onlyWinRe
                ? T("Réactiver l'environnement de récupération (commande « reagentc /enable » en administrateur) ; s'il est introuvable, voir Module 2.")
                : T("Vérifier l'origine de cette installation ; en cas de doute, réinstaller Windows avec l'outil officiel de Microsoft."),
            Severity.Medium);
    }

    private static Finding DetectActivation(AuditContext context)
    {
        var explanation = T("MAUS lit le canal de licence de Windows à titre d'information. L'outil ne modifie jamais l'activation.");
        var expected = T("activé (licence Retail, OEM ou numérique)");
        var rows = context.Cim.Query(ActivationQuery);
        var row = rows.FirstOrDefault(r => r.GetInt64("LicenseStatus") == 1) ?? FirstRow(rows);
        if (row is null)
        {
            return ActivationCheck.Unknown(T("Informations de licence indisponibles."));
        }

        var channel = row.GetString("ProductKeyChannel") ?? T("inconnu");
        var server = new[] { row.GetString("KeyManagementServiceMachine"), row.GetString("DiscoveredKeyManagementServiceMachineName") }.FirstOrDefault(IsSet);
        var licensed = row.GetInt64("LicenseStatus") == 1;
        var volumeKey = channel.Equals("Volume:GVLK", StringComparison.OrdinalIgnoreCase) || IsSet(server);

        if (volumeKey && !context.Windows.IsEnterpriseOrEducation && !context.Hardware.IsManaged)
        {
            return ActivationCheck.Neutral(
                IsSet(server) ? T("canal {0}, serveur {1}", channel, server) : T("canal {0}", channel),
                expected,
                T("L'activation de Windows semble passer par un serveur d'activation (KMS) non officiel, ce qui est inhabituel sur une édition Famille ou Pro " +
                "hors entreprise. ") + explanation);
        }

        return licensed
            ? ActivationCheck.Compliant(T("activé (canal {0})", channel), expected, explanation)
            : ActivationCheck.Neutral(
                T("non activé (canal {0})", channel),
                expected,
                T("Windows n'est pas activé : quelques options de personnalisation sont bloquées, mais les mises à jour de sécurité continuent. ") + explanation);
    }

    private static Finding DetectResidualPolicies(IRegistryReader registry, bool managed)
    {
        var explanation = T("Des stratégies posées par un script ou un ancien outil restent actives sous HKLM\\SOFTWARE\\Policies. Elles verrouillent des réglages " +
            "(Windows affiche alors « Certains paramètres sont gérés par votre organisation ») et peuvent gêner des fonctions de Windows.");
        var expected = T("aucune sur un PC personnel");
        var residual = new List<string>();
        var visited = 0;
        var pending = new Stack<string>();
        pending.Push(string.Empty);
        while (pending.Count > 0 && visited < MaxPolicyKeys)
        {
            var relative = pending.Pop();
            visited++;
            var path = relative.Length == 0 ? PoliciesRoot : $@"{PoliciesRoot}\{relative}";
            try
            {
                foreach (var name in registry.GetValueNames(Hklm, path).Where(n => n.Length > 0))
                {
                    var entry = relative.Length == 0 ? name : $@"{relative}\{name}";
                    if (!IsAcceptedPolicy(entry))
                    {
                        residual.Add(entry);
                    }
                }

                foreach (var child in registry.GetSubKeyNames(Hklm, path))
                {
                    var childRelative = relative.Length == 0 ? child : $@"{relative}\{child}";
                    if (!IsAcceptedPolicy(childRelative))
                    {
                        pending.Push(childRelative);
                    }
                }
            }
            catch (MausAccessDeniedException)
            {
                // Clé protégée : ignorée, le contrôle reste indicatif.
            }
        }

        if (residual.Count == 0)
        {
            return ResidualPoliciesCheck.Compliant(T("aucune en dehors des réglages connus"), expected, explanation);
        }

        residual.Sort(StringComparer.OrdinalIgnoreCase);
        var current = T("{0} valeur(s) : {1}", residual.Count, Join(residual));
        return managed
            ? ResidualPoliciesCheck.Neutral(current, expected, T("Sur un PC géré, ces stratégies sont normalement posées par l'organisation."))
            : ResidualPoliciesCheck.Deviation(
                current,
                expected,
                explanation,
                T("Passer en revue ces valeurs : celles que vous n'avez pas choisies pourront être supprimées une à une."));
    }

    internal static bool IsAcceptedPolicy(string relativePath) =>
        AcceptedPolicyPrefixes.Any(prefix =>
            relativePath.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
            relativePath.StartsWith(prefix + @"\", StringComparison.OrdinalIgnoreCase));
}
