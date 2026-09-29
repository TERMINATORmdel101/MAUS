using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Maus.Core.Modules.M02Repair;
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
/// Commandes <c>pnputil</c> (outil de Microsoft livré avec Windows) pour la carte graphique, affichées dans une fenêtre de
/// commande visible. Syntaxe et versions : Microsoft Learn, « PnPUtil Command Syntax » (<c>/restart-device</c>,
/// <c>/remove-device</c> et <c>/scan-devices</c> depuis Windows 10 2004 ; <c>/export-driver</c>, <c>/delete-driver</c> et
/// <c>/add-driver</c> depuis Windows 10 1607). MAUS ne vise que Windows 11, où toutes sont disponibles.
/// Rien n'est fait en arrière-plan ni automatiquement.
/// </summary>
public static partial class GraphicsDriverActions
{
    /// <summary>Dossier des sauvegardes de pilotes (protégé comme le journal, sous <c>%ProgramData%\MAUS</c>).</summary>
    public static string BackupRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MAUS", "pilotes");

    public static string PnpUtilPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");

    /// <summary>
    /// Identifiant d'instance acceptable dans une ligne de commande : lettres, chiffres et <c>\ &amp; _ . - # { }</c>
    /// seulement (forme des identifiants PCI et USB). Tout autre caractère (guillemet, %, !, espace…) est refusé.
    /// </summary>
    public static bool IsSafeInstanceId(string? instanceId) =>
        instanceId is { Length: > 0 and < 400 } && SafeInstanceId().IsMatch(instanceId);

    /// <summary>Seuls les paquets ajoutés au magasin (<c>oem#.inf</c>) se sauvegardent et se suppriment ; jamais un pilote de Windows.</summary>
    public static bool CanBackupOrRemove(DriverEntry driver) => driver.IsThirdPartyPackage;

    /// <summary>Sous-dossier daté pour une sauvegarde, par exemple <c>20260929-101500-oem12</c>.</summary>
    public static string BackupFolder(string infName, DateTimeOffset now) =>
        Path.Combine(BackupRoot, now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Path.GetFileNameWithoutExtension(infName).ToLowerInvariant());

    /// <summary>
    /// Arguments de <c>cmd.exe</c> (fenêtre gardée ouverte pour lire le résultat), ou <c>null</c> si l'action n'est pas
    /// possible pour ce pilote (identifiant douteux, pilote de Windows à sauvegarder ou supprimer, carte qui n'est pas une carte graphique).
    /// </summary>
    public static string? ConsoleArguments(GraphicsDriverAction action, DriverEntry gpu, string backupFolder, string? pnputil = null)
    {
        if (gpu.Group != DriverGroup.Graphics || !IsSafeInstanceId(gpu.InstanceId) || !SafePath(backupFolder))
        {
            return null;
        }

        if (action is GraphicsDriverAction.Backup or GraphicsDriverAction.Remove && !CanBackupOrRemove(gpu))
        {
            return null;
        }

        if (action == GraphicsDriverAction.Reinstall && !CanReinstall(gpu))
        {
            return null;
        }

        var tool = Quote(pnputil ?? PnpUtilPath);
        var device = Quote(gpu.InstanceId);
        var inf = gpu.InfName?.ToLowerInvariant();
        var folder = Quote(backupFolder);
        var line = new StringBuilder("/s /k \"title MAUS & echo ").Append(RepairConsole.Escape(Intro(action, gpu))).Append(" & echo.");
        switch (action)
        {
            case GraphicsDriverAction.Restart:
                line.Append(" & ").Append(tool).Append(" /restart-device ").Append(device)
                    .Append(" & ").Append(Outcome(T("Pilote redémarré."), T("pnputil demande un redémarrage du PC pour terminer."), null, T("Le redémarrage du pilote a échoué : lisez le message de pnputil ci-dessus.")));
                break;
            case GraphicsDriverAction.Reinstall:
                line.Append(" & ").Append(tool).Append(" /remove-device ").Append(device)
                    .Append(" & ").Append(tool).Append(" /scan-devices");
                break;
            case GraphicsDriverAction.Backup:
                line.Append(" & (if not exist ").Append(folder).Append(" mkdir ").Append(folder).Append(')')
                    .Append(" & ").Append(tool).Append(" /export-driver ").Append(inf).Append(' ').Append(folder);
                break;
            case GraphicsDriverAction.Remove:
                // Suppression seulement si la sauvegarde a réussi (&&) ; le résultat de la suppression est lu (0, 3010 ou échec) ;
                // le groupe finit toujours par un echo réussi, donc || ne répond qu'à l'échec de la sauvegarde.
                line.Append(" & (if not exist ").Append(folder).Append(" mkdir ").Append(folder).Append(')')
                    .Append(" & (").Append(tool).Append(" /export-driver ").Append(inf).Append(' ').Append(folder)
                    .Append(" && (").Append(tool).Append(" /delete-driver ").Append(inf).Append(" /uninstall & ")
                    .Append(Outcome(T("Pilote sauvegardé puis supprimé."), T("Pilote sauvegardé puis supprimé : redémarrez le PC pour terminer."), null,
                        T("La sauvegarde a réussi mais la suppression a échoué : lisez le message de pnputil ci-dessus. Le pilote est toujours installé.")))
                    .Append(") || echo ").Append(RepairConsole.Escape(T("Sauvegarde impossible : lisez le message ci-dessus. Le pilote n'a pas été supprimé."))).Append(')');
                break;
        }

        line.Append(" & echo. & echo ").Append(RepairConsole.Escape(Done(action, backupFolder))).Append('"');
        return line.ToString();
    }

    /// <summary>Réinstallation d'une sauvegarde : tous les .inf du dossier, installés sur les cartes correspondantes.</summary>
    public static string? RestoreArguments(string backupFolder, string? pnputil = null)
    {
        if (!SafePath(backupFolder))
        {
            return null;
        }

        var intro = T("Réinstallation du pilote sauvegardé par MAUS (pnputil /add-driver). L'écran peut devenir noir quelques secondes.");
        return new StringBuilder("/s /k \"title MAUS & echo ").Append(RepairConsole.Escape(intro)).Append(" & echo. & ")
            .Append(Quote(pnputil ?? PnpUtilPath)).Append(" /add-driver ").Append(Quote(Path.Combine(backupFolder, "*.inf"))).Append(" /subdirs /install")
            .Append(" & ").Append(Outcome(T("Pilote réinstallé."), T("Pilote réinstallé : redémarrez le PC pour terminer."),
                T("Rien n'a été remplacé : la carte utilise déjà un pilote plus récent ou équivalent, ou aucune carte ne correspond à cette sauvegarde. pnputil n'installe jamais un pilote plus ancien par-dessus un plus récent."),
                T("La réinstallation a échoué : lisez le message de pnputil ci-dessus.")))
            .Append(" & echo. & echo ").Append(RepairConsole.Escape(T("Vous pouvez fermer cette fenêtre."))).Append('"')
            .ToString();
    }

    private static string Intro(GraphicsDriverAction action, DriverEntry gpu) => action switch
    {
        GraphicsDriverAction.Restart => T("Redémarrage du pilote de « {0} » (pnputil /restart-device). L'écran peut devenir noir quelques secondes.", gpu.Device),
        GraphicsDriverAction.Reinstall => T("Retrait puis redétection de « {0} » (pnputil /remove-device puis /scan-devices). L'écran peut devenir noir quelques secondes.", gpu.Device),
        GraphicsDriverAction.Backup => T("Sauvegarde du pilote de « {0} » (pnputil /export-driver {1}).", gpu.Device, gpu.InfName ?? "?"),
        _ => T("Sauvegarde puis suppression du pilote de « {0} » (pnputil /export-driver puis /delete-driver {1} /uninstall). L'écran peut devenir noir quelques secondes.", gpu.Device, gpu.InfName ?? "?"),
    };

    private static string Done(GraphicsDriverAction action, string backupFolder) => action switch
    {
        GraphicsDriverAction.Restart or GraphicsDriverAction.Reinstall => T("Terminé. Si pnputil demande un redémarrage, redémarrez le PC. Vous pouvez fermer cette fenêtre."),
        GraphicsDriverAction.Backup => T("Sauvegarde dans {0}. Vous pouvez fermer cette fenêtre.", backupFolder),
        _ => T("Après une suppression réussie, Windows affiche avec son pilote de base, et Windows Update peut réinstaller un pilote de lui-même. La sauvegarde ({0}) se réinstalle par Atelier > Pilotes > « Réinstaller une sauvegarde », tant qu'aucun pilote plus récent n'a été installé : pnputil ne remplace pas un pilote plus récent.", backupFolder),
    };

    /// <summary>
    /// Retirer puis redétecter n'a de sens que pour une carte du bus PCI : un adaptateur virtuel (ROOT, SWD : Parsec,
    /// écran virtuel…) retiré par <c>/remove-device</c> n'est pas recréé par <c>/scan-devices</c>.
    /// </summary>
    public static bool CanReinstall(DriverEntry driver) => driver.InstanceId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Message selon le code de sortie de pnputil, lu au moment de l'exécution (<c>if errorlevel N</c> = code supérieur ou égal
    /// à N). Codes documentés (Microsoft Learn, « PnPUtil Return Values ») : 0 réussi, 3010 réussi avec redémarrage nécessaire,
    /// 259 aucun périphérique mis à jour (pilote plus récent ou équivalent déjà en place, ou aucun ne correspond) ; tout le
    /// reste, y compris un code négatif, est un échec.
    /// </summary>
    internal static string Outcome(string success, string restart, string? unchanged, string failure)
    {
        var fail = "(echo " + RepairConsole.Escape(failure) + ")";
        // Chaîne entre parenthèses : sans elles, la suite de la ligne (« & echo … ») ferait partie du dernier « else ».
        return "(if not errorlevel 0 " + fail
            + " else if errorlevel 3011 " + fail
            + " else if errorlevel 3010 (echo " + RepairConsole.Escape(restart) + ")"
            + " else if errorlevel 260 " + fail
            + " else if errorlevel 259 " + (unchanged is null ? fail : "(echo " + RepairConsole.Escape(unchanged) + ")")
            + " else if errorlevel 1 " + fail
            + " else (echo " + RepairConsole.Escape(success) + "))";
    }

    /// <summary>Chemin sans caractère qui casserait la ligne de commande (guillemet, %, !, &amp;, |, &lt;, &gt;, ^).</summary>
    private static bool SafePath(string path) =>
        path.Length is > 0 and < 250 && path.IndexOfAny(['"', '%', '!', '&', '|', '<', '>', '^', '\r', '\n']) < 0;

    private static string Quote(string value) => "\"" + value + "\"";

    [GeneratedRegex(@"^[A-Za-z0-9_\\&.\-#{}]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeInstanceId();
}
