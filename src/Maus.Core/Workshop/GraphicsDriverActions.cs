using System.Globalization;
using System.Text.RegularExpressions;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Actions manuelles sur le pilote d'une carte graphique, lancées seulement par l'utilisateur, après confirmation.</summary>
public enum GraphicsDriverAction
{
    /// <summary>Redémarrer le pilote (<c>pnputil /restart-device</c>) : écran noir quelques secondes.</summary>
    Restart,

    /// <summary>Retirer la carte puis la faire redétecter (<c>/remove-device</c> puis <c>/scan-devices</c>).</summary>
    Reinstall,

    /// <summary>Sauvegarder le paquet du pilote (<c>/export-driver</c>) dans le dossier des sauvegardes de MAUS.</summary>
    Backup,

    /// <summary>Sauvegarder puis supprimer le pilote (<c>/export-driver</c> puis <c>/delete-driver /uninstall</c>).</summary>
    Remove,
}

/// <summary>
/// Commandes <c>pnputil</c> (outil de Microsoft livré avec Windows) pour la carte graphique, lancées directement, sans cmd.exe, et
/// leur résultat affiché par MAUS. Syntaxe et versions : Microsoft Learn, « PnPUtil Command Syntax » (<c>/restart-device</c>,
/// <c>/remove-device</c> et <c>/scan-devices</c> depuis Windows 10 2004 ; <c>/export-driver</c>, <c>/delete-driver</c> et
/// <c>/add-driver</c> depuis Windows 10 1607). MAUS ne vise que Windows 11, où toutes sont disponibles.
/// Rien n'est fait en arrière-plan ni automatiquement.
/// </summary>
public static partial class GraphicsDriverActions
{
    /// <summary>Dossier des sauvegardes de pilotes (protégé comme le journal, sous <c>%ProgramData%\MAUS</c>).</summary>
    public static string BackupRoot => Path.Combine(Maus.Core.Platform.SystemPaths.MausData, "pilotes");

    public static string PnpUtilPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");

    /// <summary>
    /// Identifiant d'instance acceptable : lettres, chiffres et <c>\ &amp; _ . - # { }</c>
    /// seulement (forme des identifiants PCI et USB). Tout autre caractère (guillemet, %, !, espace…) est refusé.
    /// </summary>
    public static bool IsSafeInstanceId(string? instanceId) =>
        instanceId is { Length: > 0 and < 400 } && SafeInstanceId().IsMatch(instanceId);

    /// <summary>Seuls les paquets ajoutés au magasin (<c>oem#.inf</c>) se sauvegardent et se suppriment ; jamais un pilote de Windows.</summary>
    public static bool CanBackupOrRemove(DriverEntry driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        return driver.IsThirdPartyPackage;
    }

    /// <summary>Sous-dossier daté pour une sauvegarde, par exemple <c>20260929-101500-oem12</c>.</summary>
    public static string BackupFolder(string infName, DateTimeOffset now) =>
        Path.Combine(BackupRoot, now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Path.GetFileNameWithoutExtension(infName).ToLowerInvariant());

    /// <summary>
    /// Action possible pour ce pilote : non pour un identifiant douteux, un pilote de Windows à sauvegarder ou supprimer, une carte
    /// qui n'est pas une carte graphique, ou un adaptateur virtuel à retirer.
    /// </summary>
    public static bool CanRun(GraphicsDriverAction action, DriverEntry gpu, string backupFolder)
    {
        ArgumentNullException.ThrowIfNull(backupFolder);
        ArgumentNullException.ThrowIfNull(gpu);

        return Enum.IsDefined(action) && gpu.Group == DriverGroup.Graphics && IsSafeInstanceId(gpu.InstanceId) && SafePath(backupFolder)
            && (action is not (GraphicsDriverAction.Backup or GraphicsDriverAction.Remove) || CanBackupOrRemove(gpu))
            && (action != GraphicsDriverAction.Reinstall || CanReinstall(gpu));
    }

    /// <param name="pnputil">Lance pnputil avec ces arguments ; rend son code de sortie.</param>
    /// <param name="line">Reçoit le texte à afficher.</param>
    /// <returns>Le résultat à afficher.</returns>
    public static async Task<string> RunAsync(GraphicsDriverAction action, DriverEntry gpu, string backupFolder, Func<IReadOnlyList<string>, Task<int>> pnputil, Action<string> line)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(pnputil);
        ArgumentNullException.ThrowIfNull(line);

        line(Intro(action, gpu));
        var inf = gpu.InfName?.ToLowerInvariant() ?? string.Empty;
        switch (action)
        {
            case GraphicsDriverAction.Restart:
                return Outcome(await pnputil(["/restart-device", gpu.InstanceId]).ConfigureAwait(false), T("Pilote redémarré."), T("pnputil demande un redémarrage du PC pour terminer."), null,
                    T("Le redémarrage du pilote a échoué : lisez le message de pnputil ci-dessus."));
            case GraphicsDriverAction.Reinstall:
                await pnputil(["/remove-device", gpu.InstanceId]).ConfigureAwait(false);
                await pnputil(["/scan-devices"]).ConfigureAwait(false);
                return Done(action, backupFolder);
            case GraphicsDriverAction.Backup:
                return await pnputil(["/export-driver", inf, backupFolder]).ConfigureAwait(false) == 0
                    ? Done(action, backupFolder)
                    : T("Sauvegarde impossible : lisez le message ci-dessus.");
            case GraphicsDriverAction.Remove:
                // Suppression seulement si la sauvegarde a réussi.
                if (await pnputil(["/export-driver", inf, backupFolder]).ConfigureAwait(false) != 0)
                {
                    return T("Sauvegarde impossible : lisez le message ci-dessus. Le pilote n'a pas été supprimé.");
                }

                return Outcome(await pnputil(["/delete-driver", inf, "/uninstall"]).ConfigureAwait(false), T("Pilote sauvegardé puis supprimé."), T("Pilote sauvegardé puis supprimé : redémarrez le PC pour terminer."), null,
                        T("La sauvegarde a réussi mais la suppression a échoué : lisez le message de pnputil ci-dessus. Le pilote est toujours installé."))
                    + Environment.NewLine + Done(action, backupFolder);
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    /// <summary>Le dossier d'une sauvegarde est acceptable.</summary>
    public static bool CanRestore(string backupFolder)
    {
        ArgumentNullException.ThrowIfNull(backupFolder);
        return SafePath(backupFolder);
    }

    /// <summary>Réinstallation d'une sauvegarde : tous les .inf du dossier, installés sur les cartes correspondantes.</summary>
    public static async Task<string> RestoreAsync(string backupFolder, Func<IReadOnlyList<string>, Task<int>> pnputil, Action<string> line)
    {
        ArgumentNullException.ThrowIfNull(pnputil);
        ArgumentNullException.ThrowIfNull(line);

        line(T("Réinstallation du pilote sauvegardé par MAUS (pnputil /add-driver). L'écran peut devenir noir quelques secondes."));
        return Outcome(await pnputil(["/add-driver", Path.Combine(backupFolder, "*.inf"), "/subdirs", "/install"]).ConfigureAwait(false), T("Pilote réinstallé."), T("Pilote réinstallé : redémarrez le PC pour terminer."),
            T("Rien n'a été remplacé : la carte utilise déjà un pilote plus récent ou équivalent, ou aucune carte ne correspond à cette sauvegarde. pnputil n'installe jamais un pilote plus ancien par-dessus un plus récent."),
            T("La réinstallation a échoué : lisez le message de pnputil ci-dessus."));
    }

    private static string Intro(GraphicsDriverAction action, DriverEntry gpu) => action switch
    {
        GraphicsDriverAction.Restart => T("Redémarrage du pilote de « {0} » (pnputil /restart-device). L'écran peut devenir noir quelques secondes.", gpu.Device),
        GraphicsDriverAction.Reinstall => T("Retrait puis redétection de « {0} » (pnputil /remove-device puis /scan-devices). L'écran peut devenir noir quelques secondes.", gpu.Device),
        GraphicsDriverAction.Backup => T("Sauvegarde du pilote de « {0} » (pnputil /export-driver {1}).", gpu.Device, gpu.InfName ?? "?"),
        GraphicsDriverAction.Remove => T("Sauvegarde puis suppression du pilote de « {0} » (pnputil /export-driver puis /delete-driver {1} /uninstall). L'écran peut devenir noir quelques secondes.", gpu.Device, gpu.InfName ?? "?"),
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static string Done(GraphicsDriverAction action, string backupFolder) => action switch
    {
        GraphicsDriverAction.Restart or GraphicsDriverAction.Reinstall => T("Terminé. Si Windows demande un redémarrage, redémarrez le PC. Vous pouvez fermer cette fenêtre."),
        GraphicsDriverAction.Backup => T("Sauvegarde dans {0}. Vous pouvez fermer cette fenêtre.", backupFolder),
        GraphicsDriverAction.Remove => T("Après une suppression réussie, Windows affiche l'image avec son pilote graphique de base, et Windows Update peut réinstaller un pilote de lui-même. La sauvegarde ({0}) se réinstalle par Atelier > Pilotes > « Réinstaller une sauvegarde », tant qu'aucun pilote plus récent n'a été installé : Windows refuse de remplacer un pilote récent par un plus ancien.", backupFolder),
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>
    /// Retirer puis redétecter n'a de sens que pour une carte du bus PCI : un adaptateur virtuel (ROOT, SWD : Parsec,
    /// écran virtuel…) retiré par <c>/remove-device</c> n'est pas recréé par <c>/scan-devices</c>.
    /// </summary>
    public static bool CanReinstall(DriverEntry driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        return driver.InstanceId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Message selon le code de sortie de pnputil. Codes documentés (Microsoft Learn, « PnPUtil Return Values ») : 0 réussi, 3010
    /// réussi avec redémarrage nécessaire, 259 aucun périphérique mis à jour (pilote plus récent ou équivalent déjà en place, ou
    /// aucun ne correspond) ; tout le reste, y compris un code négatif, est un échec.
    /// </summary>
    internal static string Outcome(int code, string success, string restart, string? unchanged, string failure) => code switch
    {
        0 => success,
        3010 => restart,
        259 when unchanged is not null => unchanged,
        _ => failure,
    };

    /// <summary>Chemin sans caractère douteux (guillemet, %, !, &amp;, |, &lt;, &gt;, ^) : MAUS construit ce chemin, il n'en a jamais besoin.</summary>
    private static bool SafePath(string path) =>
        path.Length is > 0 and < 250 && path.IndexOfAny(['"', '%', '!', '&', '|', '<', '>', '^', '\r', '\n']) < 0;

    [GeneratedRegex(@"\A[A-Za-z0-9_\\&.\-#{}]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeInstanceId();
}
