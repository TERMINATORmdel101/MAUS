using System.Text;
using System.Text.RegularExpressions;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M20Software;

/// <summary>Logiciel pour lequel winget connaît une version plus récente.</summary>
public sealed record SoftwareUpdate(string Name, string Id, string Version, string Available, string Source)
{
    /// <summary>
    /// Identifiant complet et sûr à passer à <c>winget upgrade --id</c> : winget tronque les identifiants trop longs (« … »),
    /// et seuls lettres, chiffres, point, tiret, plus et soulignement sont admis dans la ligne de commande.
    /// </summary>
    public bool CanTarget => Id.Length > 0 && Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+');
}

/// <summary>
/// winget (Programme d'installation d'application de Microsoft) : liste des mises à jour disponibles, en lecture seule.
/// MAUS n'utilise que l'exécutable du paquet, dans <c>Program Files\WindowsApps</c> (protégé par Windows), jamais l'alias
/// du profil de l'utilisateur, modifiable sans droits administrateur.
/// </summary>
public static partial class Winget
{
    public const string PackageName = "Microsoft.DesktopAppInstaller";

    /// <summary>Codes de winget signifiant « rien à mettre à jour » (APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND, UPDATE_NOT_APPLICABLE ; à vérifier).</summary>
    internal const int NoPackageFound = unchecked((int)0x8A150014);
    internal const int NoApplicableUpdate = unchecked((int)0x8A15002B);

    /// <summary>Liste seule : sans identifiant ni « --all », <c>winget upgrade</c> n'installe rien.</summary>
    public static readonly IReadOnlyList<string> ListArguments = ["upgrade", "--source", "winget", "--disable-interactivity"];

    /// <summary>Chemin de winget.exe dans le dossier protégé du paquet, ou <c>null</c>.</summary>
    public static string? Locate(IPackageInventory packages, IFileSystemReader files) =>
        Locate(packages, files, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

    internal static string? Locate(IPackageInventory packages, IFileSystemReader files, string programFiles)
    {
        if (packages.Find(PackageName) is not { InstallPath: { Length: > 0 } folder })
        {
            return null;
        }

        var path = folder.TrimEnd('\\') + @"\winget.exe";
        return ReadOnlyCommandRunner.IsInProtectedAppsFolder(path, programFiles) && files.FileExists(path) ? path : null;
    }

    public static async Task<IReadOnlyList<SoftwareUpdate>> ListUpgradesAsync(ICommandRunner commands, string wingetPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await commands.RunAsync(wingetPath, ListArguments, timeout, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            throw new DataSourceUnavailableException(T("winget n'a pas répondu en {0:0} s (connexion lente ?).", timeout.TotalSeconds));
        }

        var updates = Parse(result.StandardOutput);
        if (updates.Count == 0 && result.ExitCode is not (0 or NoPackageFound or NoApplicableUpdate) && !HasTable(result.StandardOutput))
        {
            throw new DataSourceUnavailableException(T("winget a échoué (code 0x{0:X8}).", result.ExitCode));
        }

        return updates;
    }

    /// <summary>
    /// Lit le tableau de <c>winget upgrade</c> (en-têtes traduits selon la langue de Windows) : les colonnes commencent là
    /// où commencent les mots de l'en-tête, au-dessus de la ligne de tirets. Seul le premier tableau compte (le second liste
    /// les logiciels épinglés, que winget ne met pas à jour sans demande explicite).
    /// </summary>
    public static IReadOnlyList<SoftwareUpdate> Parse(string output)
    {
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(Clean).ToList();
        var separator = lines.FindIndex(IsSeparator);
        if (separator < 1)
        {
            return [];
        }

        var header = lines[separator - 1];
        var starts = new List<int>();
        for (var i = 0; i < header.Length; i++)
        {
            if (header[i] != ' ' && (i == 0 || header[i - 1] == ' '))
            {
                starts.Add(i);
            }
        }

        if (starts.Count < 4)
        {
            return [];
        }

        var updates = new List<SoftwareUpdate>();
        for (var i = separator + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.Length <= starts[2])
            {
                break;
            }

            string Cell(int column)
            {
                if (column >= starts.Count || starts[column] >= line.Length)
                {
                    return string.Empty;
                }

                var end = column + 1 < starts.Count ? Math.Min(starts[column + 1], line.Length) : line.Length;
                return line[starts[column]..end].Trim();
            }

            var id = Cell(1);
            if (id.Length == 0 || id.Contains(' ', StringComparison.Ordinal))
            {
                continue;
            }

            updates.Add(new SoftwareUpdate(Cell(0), id, Cell(2), Cell(3), starts.Count > 4 ? Cell(4) : string.Empty));
        }

        return updates;
    }

    /// <summary>
    /// Arguments de <c>cmd.exe</c> pour mettre à jour les logiciels choisis, un par un, dans une fenêtre visible.
    /// winget reste interactif : l'utilisateur accepte lui-même les conditions des éditeurs.
    /// </summary>
    public static string UpgradeConsoleArguments(string wingetPath, IEnumerable<SoftwareUpdate> selection)
    {
        if (wingetPath.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException(T("Chemin de winget invalide."), nameof(wingetPath));
        }

        // /s : cmd retire seulement la première et la dernière paire de guillemets, les chemins entre guillemets restent intacts.
        var line = new StringBuilder("/s /k \"title MAUS & echo ").Append(RepairConsoleEscape(UpgradeIntro));
        foreach (var update in selection.Where(u => u.CanTarget))
        {
            line.Append(" & echo. & echo ").Append(RepairConsoleEscape(T("Mise à jour : {0}", update.Name)))
                .Append(" & \"").Append(wingetPath).Append("\" upgrade --id ").Append(update.Id).Append(" --exact --source winget");
        }

        return line.Append(" & echo. & echo ").Append(RepairConsoleEscape(UpgradeDone)).Append('"').ToString();
    }

    public static string UpgradeIntro => T("Mise à jour des logiciels par winget, l'outil de Microsoft. Répondez à ses questions dans cette fenêtre (conditions des éditeurs) ; certains installateurs ouvrent leur propre fenêtre.");

    public static string UpgradeDone => T("Terminé. Lisez les messages ci-dessus puis relancez l'audit de MAUS. Vous pouvez fermer cette fenêtre.");

    private static string RepairConsoleEscape(string text) => M02Repair.RepairConsole.Escape(text);

    private static bool HasTable(string output) => output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(Clean).Any(IsSeparator);

    private static bool IsSeparator(string line) => line.Length >= 10 && line.All(c => c is '-' or '─');

    /// <summary>Retire les séquences de couleur et l'animation d'attente : seul compte le texte après le dernier retour chariot.</summary>
    private static string Clean(string line)
    {
        line = AnsiEscape().Replace(line, string.Empty);
        var lastReturn = line.LastIndexOf('\r');
        return (lastReturn >= 0 ? line[(lastReturn + 1)..] : line).TrimEnd();
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex AnsiEscape();
}
