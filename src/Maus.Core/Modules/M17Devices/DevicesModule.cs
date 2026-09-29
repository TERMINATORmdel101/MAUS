using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M17Devices;

/// <summary>
/// Module 17 — Périphériques et pilotes : les appareils que le Gestionnaire de périphériques marque d'un point d'exclamation,
/// avec leur code d'erreur expliqué et la marche à suivre. Lecture seule ; MAUS n'installe aucun pilote.
/// </summary>
public sealed class DevicesModule : IAuditModule
{
    internal const string ProblemQuery =
        "SELECT Name, PNPDeviceID, PNPClass, ConfigManagerErrorCode, Present FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0";

    /// <summary>Codes sans intérêt : appareil débranché, retiré en toute sécurité, arrêt de Windows en cours.</summary>
    private static readonly HashSet<long> Ignored = [45, 46, 47];

    /// <summary>Codes qui ne sont pas des pannes : redémarrage attendu, appareil en attente ou en cours de réinitialisation, débogueur.</summary>
    private static readonly HashSet<long> Transient = [14, 51, 53, 54];

    private static string Category => T("Périphériques");

    /// <summary>Page « Mises à jour facultatives » de Windows Update, où Windows propose des pilotes.</summary>
    private const string OptionalUpdatesPage = "ms-settings:windowsupdate-optionalupdates";

    public string Id => "M17";

    public string Title => T("Périphériques et pilotes");

    public int Order => 170;

    public Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<CimRow> rows;
        try
        {
            rows = context.Cim.Query(ProblemQuery);
        }
        catch (MausAccessDeniedException)
        {
            return Result(Finding.AdminRequired("M17.devices", T("Périphériques en erreur"), Category));
        }
        catch (DataSourceUnavailableException ex)
        {
            return Result(Finding.Unknown("M17.devices", T("Périphériques en erreur"), T("La liste des périphériques n'a pas pu être lue : {0}", ex.Message), Category));
        }

        var devices = rows
            .Select(r => new Device(r.GetString("Name")?.Trim() ?? T("périphérique sans nom"), r.GetString("PNPDeviceID") ?? string.Empty, r.GetString("PNPClass") ?? string.Empty, r.GetInt64("ConfigManagerErrorCode") ?? 0, r.GetBool("Present") ?? true))
            .Where(d => d.Present && d.Code != 0 && !Ignored.Contains(d.Code))
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var failing = devices.Where(d => d.Code != 22 && !Transient.Contains(d.Code)).ToList();
        var disabled = devices.Where(d => d.Code == 22).ToList();
        var waiting = devices.Where(d => Transient.Contains(d.Code)).ToList();

        var findings = new List<Finding> { Summary(failing, disabled, waiting) };
        findings.AddRange(failing.Select(Describe));
        if (UnsignedDrivers(context.Cim) is { } unsigned)
        {
            findings.Add(unsigned);
        }

        if (disabled.Count > 0)
        {
            findings.Add(new Finding
            {
                Id = "M17.disabled",
                Title = T("Périphériques désactivés"),
                Category = Category,
                Status = FindingStatus.Info,
                Current = string.Join(", ", disabled.Select(d => d.Name)),
                Explanation = T("Ces appareils ont été désactivés dans le Gestionnaire de périphériques, par vous, par un outil ou par le fabricant. Ce n'est pas une panne ; certains outils d'« optimisation » désactivent ainsi des appareils utiles (Bluetooth, webcam, lecteur de cartes)."),
                Advice = T("Si l'un d'eux vous manque : Gestionnaire de périphériques (clic droit sur Démarrer), clic droit sur l'appareil, « Activer le périphérique »."),
            });
        }

        return Result([.. findings]);
    }

    /// <summary>
    /// Pilotes sans signature numérique reconnue (<c>Win32_PnPSignedDriver.IsSigned</c>). La signature permet à Windows de
    /// vérifier l'intégrité du paquet et l'identité de son éditeur (Microsoft Learn, « Driver Signing ») ; son absence n'est pas
    /// une panne, d'où une simple information. <c>null</c> si la liste des pilotes est illisible.
    /// </summary>
    private static Finding? UnsignedDrivers(ICimReader cim)
    {
        var drivers = Workshop.DriverInventoryReader.Read(cim);
        if (drivers.Count == 0)
        {
            return null;
        }

        var unsigned = drivers.Where(d => d.IsSigned == false).ToList();
        return new Finding
        {
            Id = "M17.unsigned-drivers",
            Title = T("Pilotes sans signature numérique"),
            Category = Category,
            Status = unsigned.Count == 0 ? FindingStatus.Ok : FindingStatus.Info,
            Current = unsigned.Count == 0
                ? T("aucun, sur {0} pilotes", drivers.Count)
                : T("{0} sur {1} : {2}", unsigned.Count, drivers.Count, string.Join(", ", unsigned.Take(8).Select(d => d.Device))),
            Expected = T("aucun"),
            Explanation = T("La signature numérique d'un pilote permet à Windows de vérifier qui l'a publié et qu'il n'a pas été modifié. "
                + "Un pilote sans signature n'est pas forcément en panne, mais son origine n'est pas garantie."),
            Advice = unsigned.Count == 0
                ? null
                : T("Vérifiez qu'ils viennent bien du fabricant de l'appareil (Atelier > Pilotes, bouton « Voir les pilotes non signés ») ; sinon, réinstallez le pilote depuis le site du fabricant."),
        };
    }

    private static Task<IReadOnlyList<Finding>> Result(params Finding[] findings) => Task.FromResult<IReadOnlyList<Finding>>(findings);

    private static Finding Summary(List<Device> failing, List<Device> disabled, List<Device> waiting) => new()
    {
        Id = "M17.devices",
        Title = T("Périphériques en erreur"),
        Category = Category,
        Status = failing.Count == 0 ? FindingStatus.Ok : FindingStatusExtensions.ForDeviation(Severity.Medium),
        Severity = Severity.Medium,
        Current = failing.Count == 0
            ? T("aucun périphérique en erreur")
            : T("{0} périphérique(s) en erreur", failing.Count),
        Expected = T("aucun périphérique en erreur"),
        Explanation = T("Un périphérique en erreur (point d'exclamation dans le Gestionnaire de périphériques) ne fonctionne pas, ou mal : son, Wi-Fi, Bluetooth, webcam, ports USB, lecteur de cartes… Chaque appareil est détaillé ci-dessous avec son code d'erreur expliqué.")
            + (waiting.Count > 0 ? " " + T("En attente d'un redémarrage ou d'une réinitialisation : {0}.", string.Join(", ", waiting.Select(d => d.Name))) : string.Empty)
            + (disabled.Count > 0 ? " " + T("{0} appareil(s) désactivé(s), listé(s) à part.", disabled.Count) : string.Empty),
        Advice = failing.Count == 0 ? null : T("Préférez Windows Update (Paramètres > Windows Update > Options avancées > Mises à jour facultatives > Pilotes) ou le site du fabricant du PC. Évitez les logiciels « de mise à jour de pilotes » : ils installent parfois des pilotes inadaptés."),
        SettingsPage = failing.Count == 0 ? null : OptionalUpdatesPage,
    };

    private static Finding Describe(Device device)
    {
        var (meaning, advice, settingsPage) = Explain(device.Code);
        var critical = device.Class is "Display" or "SCSIAdapter" or "HDC" && device.Code is 10 or 28 or 31 or 39 or 43;
        var severity = critical ? Severity.High : Severity.Medium;
        return new Finding
        {
            Id = "M17.device." + Slug(device.PnpId.Length > 0 ? device.PnpId : device.Name),
            Title = T("Périphérique en erreur : {0}", device.Name),
            Category = Category,
            Status = FindingStatusExtensions.ForDeviation(severity),
            Severity = severity,
            Current = T("code {0} : {1}", device.Code, meaning),
            Expected = T("fonctionne normalement"),
            Explanation = T("Windows signale ce code d'erreur pour l'appareil « {0} » ({1}).", device.Name, ClassLabel(device.Class))
                + (critical ? " " + T("C'est un composant essentiel : sans lui, l'affichage ou les disques peuvent fonctionner en mode dégradé.") : string.Empty),
            Advice = advice,
            SettingsPage = settingsPage,
        };
    }

    /// <summary>
    /// Sens du code d'erreur, marche à suivre (codes documentés par Microsoft pour le Gestionnaire de périphériques) et page
    /// des Paramètres quand la marche à suivre y renvoie.
    /// </summary>
    internal static (string Meaning, string Advice, string? SettingsPage) Explain(long code) => code switch
    {
        1 or 19 or 40 => (T("configuration du pilote incomplète ou abîmée"),
            T("Désinstallez le périphérique (clic droit > Désinstaller l'appareil) puis redémarrez : Windows le réinstalle tout seul. Sinon, installez le pilote du fabricant."), null),
        3 => (T("pilote endommagé, ou mémoire insuffisante"), T("Fermez des applications, redémarrez, puis réinstallez le pilote si l'erreur revient."), null),
        10 => (T("le périphérique ne peut pas démarrer"),
            T("Débranchez et rebranchez l'appareil (autre port USB si possible), redémarrez, puis mettez à jour ou réinstallez son pilote."), null),
        12 or 16 or 29 or 33 or 34 or 35 or 36 => (T("ressources matérielles introuvables ou désactivées dans le BIOS"),
            T("Vérifiez que l'appareil est activé dans le BIOS (Module 8) et mettez le BIOS à jour ; retirez les cartes d'extension inutilisées."), null),
        18 or 39 or 41 => (T("pilote absent, endommagé ou incompatible"),
            T("Réinstallez le pilote : désinstallez l'appareil (en cochant la suppression du pilote si proposée), redémarrez, puis installez le pilote du fabricant."), null),
        21 => (T("Windows est en train de supprimer ce périphérique"), T("Attendez quelques secondes puis redémarrez si l'erreur reste affichée."), null),
        24 or 42 => (T("périphérique absent, en double ou mal installé"), T("Débranchez-le, redémarrez, puis rebranchez-le. S'il est intégré au PC, installez le pilote de puce (chipset) du fabricant."), null),
        28 => (T("aucun pilote installé"),
            T("Cherchez le pilote dans Windows Update (Options avancées > Mises à jour facultatives > Pilotes), puis sur le site du fabricant du PC ou de la carte mère."), OptionalUpdatesPage),
        31 or 37 or 38 => (T("Windows ne peut pas charger le pilote"), T("Redémarrez le PC. Si l'erreur reste, réinstallez le pilote du fabricant."), null),
        32 => (T("pilote ou service désactivé"), T("Un outil a probablement désactivé le service de ce pilote. Réinstallez le pilote, ou restaurez le point de restauration d'avant le réglage."), null),
        43 => (T("Windows a arrêté ce périphérique car il a signalé des problèmes"),
            T("Débranchez-le et rebranchez-le, mettez son pilote à jour. Pour une carte graphique : pilote propre (Module 9), vérifiez son alimentation et sa température ; si l'erreur persiste, la carte peut être défaillante."), null),
        48 => (T("pilote bloqué car il pose des problèmes connus"), T("Installez une version récente du pilote depuis le site du fabricant : Windows bloque les anciennes versions connues pour poser problème."), null),
        49 => (T("registre système trop volumineux"), T("Désinstallez les périphériques qui ne sont plus utilisés (menu Affichage > Afficher les périphériques cachés), puis redémarrez."), null),
        52 => (T("signature numérique du pilote invérifiable"),
            T("Installez un pilote signé depuis le site du fabricant ; un pilote non signé peut aussi être bloqué par Secure Boot ou l'intégrité de la mémoire (Module 13)."), null),
        _ => (T("erreur signalée par Windows"), T("Ouvrez le Gestionnaire de périphériques, double-cliquez sur l'appareil pour lire l'erreur, puis mettez à jour ou réinstallez son pilote."), null),
    };

    private static string ClassLabel(string pnpClass) => pnpClass switch
    {
        "Display" => T("carte graphique"),
        "Net" => T("réseau"),
        "Bluetooth" => "Bluetooth",
        "MEDIA" or "AudioEndpoint" => T("son"),
        "USB" or "USBDevice" => "USB",
        "Camera" or "Image" => T("caméra"),
        "HIDClass" or "Keyboard" or "Mouse" => T("clavier, souris ou manette"),
        "SCSIAdapter" or "HDC" or "DiskDrive" => T("stockage"),
        "Printer" or "PrintQueue" => T("imprimante"),
        "Biometric" => T("biométrie"),
        "System" => T("système"),
        "" => T("type inconnu"),
        _ => pnpClass,
    };

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars).Trim('-');
        return slug.Length > 60 ? slug[..60] : slug.Length == 0 ? "x" : slug;
    }

    private sealed record Device(string Name, string PnpId, string Class, long Code, bool Present);
}
