using System.Text;
using System.Text.RegularExpressions;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M20Software;

/// <summary>Logiciel pour lequel winget connaît une version plus récente.</summary>
public sealed record SoftwareUpdate(string Name, string Id, string Version, string Available)
{
    /// <summary>
    /// Identifiant complet et sûr à passer à <c>winget upgrade --id</c> : winget tronque les identifiants trop longs (« … »),
    /// et seuls lettres, chiffres, point, tiret, plus et soulignement sont admis dans la ligne de commande.
    /// </summary>
    public bool CanTarget => Id.Length > 0 && !Id.Equals(Winget.OwnPackageId, StringComparison.OrdinalIgnoreCase) && Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+');
}

/// <summary>
/// winget (Programme d'installation d'application de Microsoft) : liste des mises à jour disponibles, en lecture seule.
/// MAUS n'utilise que l'exécutable du paquet, dans <c>Program Files\WindowsApps</c> (protégé par Windows), jamais l'alias
/// du profil de l'utilisateur, modifiable sans droits administrateur.
/// </summary>
public static partial class Winget
{
    /// <summary>MAUS lui-même : son installateur refuse de tourner tant que MAUS est ouvert, sa mise à jour échouerait d'ici.</summary>
    internal const string OwnPackageId = "TERMINATORmdel101.MAUS";

    private const string PackageFamily = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe";

    /// <summary>Codes de winget signifiant « rien à mettre à jour » : APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND et UPDATE_NOT_APPLICABLE (winget-cli, doc/windows/package-manager/winget/returnCodes.md).</summary>
    internal const int NoPackageFound = unchecked((int)0x8A150014);
    internal const int NoApplicableUpdate = unchecked((int)0x8A15002B);

    /// <summary>Liste seule : sans identifiant ni « --all », <c>winget upgrade</c> n'installe rien.</summary>
    public static readonly IReadOnlyList<string> ListArguments = ["upgrade", "--source", "winget", "--disable-interactivity"];

    /// <summary>Chemin de winget.exe dans le dossier protégé du paquet, ou <c>null</c>.</summary>
    public static string? Locate(IPackageInventory packages, IFileSystemReader files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return Locate(packages, files, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
    }

    internal static string? Locate(IPackageInventory packages, IFileSystemReader files, string programFiles)
    {
        // Par famille exacte : son suffixe dépend de l'éditeur et ne s'imite pas. L'emplacement seul ne prouve rien, un paquet
        // signé par n'importe qui peut s'installer sous WindowsApps.
        if (packages.GetUserPackages().FirstOrDefault(p => string.Equals(p.FamilyName, PackageFamily, StringComparison.OrdinalIgnoreCase)) is not { InstallPath: { Length: > 0 } folder })
        {
            return null;
        }

        var path = folder.TrimEnd('\\') + @"\winget.exe";
        return ReadOnlyCommandRunner.IsInProtectedAppsFolder(path, programFiles) && files.FileExists(path) ? path : null;
    }

    public static async Task<IReadOnlyList<SoftwareUpdate>> ListUpgradesAsync(ICommandRunner commands, string wingetPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commands);

        var result = await commands.RunAsync(wingetPath, ListArguments, timeout, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            throw new DataSourceUnavailableException(T("winget n'a pas répondu en {0:0} s (connexion lente ?).", timeout.TotalSeconds));
        }

        var updates = Parse(result.StandardOutput);
        if (updates.Count == 0 && HasTable(result.StandardOutput))
        {
            // Un tableau dont aucune ligne n'est comprise ne veut pas dire « tout est à jour ».
            throw new DataSourceUnavailableException(T("La liste de winget n'a pas pu être lue."));
        }

        if (updates.Count == 0 && result.ExitCode is not (0 or NoPackageFound or NoApplicableUpdate))
        {
            throw new DataSourceUnavailableException(T("winget a échoué (code 0x{0:X8}).", result.ExitCode));
        }

        return updates;
    }

    /// <summary>
    /// Lit le tableau de <c>winget upgrade</c> (en-têtes traduits selon la langue de Windows) : les colonnes commencent là
    /// où commencent les mots de l'en-tête, au-dessus de la ligne de tirets, comptés en cellules d'affichage comme le fait winget
    /// (UTF8ColumnWidth, TableOutput.cpp). Seul le premier tableau compte (le second liste les logiciels épinglés, que winget ne
    /// met pas à jour sans demande explicite). Un en-tête qui n'a ni 4 ni 5 mots laisserait les colonnes au hasard : la liste est
    /// alors déclarée illisible.
    /// </summary>
    /// <exception cref="DataSourceUnavailableException">En-tête non reconnu.</exception>
    public static IReadOnlyList<SoftwareUpdate> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var lines = SplitLines(output);
        var separator = FindMainTable(lines);
        if (separator < 1)
        {
            return [];
        }

        var header = InCells(lines[separator - 1]);
        var starts = new List<int>();
        for (var i = 0; i < header.Length; i++)
        {
            if (header[i] != ' ' && (i == 0 || header[i - 1] == ' '))
            {
                starts.Add(i);
            }
        }

        if (starts.Count is not (4 or 5))
        {
            throw new DataSourceUnavailableException(T("La liste de winget n'a pas pu être lue."));
        }

        var updates = new List<SoftwareUpdate>();
        for (var i = separator + 1; i < lines.Count; i++)
        {
            var line = InCells(lines[i]);
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
                return line[starts[column]..end].Replace(CellFiller, string.Empty, StringComparison.Ordinal).Trim();
            }

            // Une ligne du tableau a une espace devant chaque colonne, et un identifiant du catalogue contient un point : le
            // bas du tableau (« 3 mises à niveau disponibles ») n'en est pas une.
            var id = Cell(1);
            if (id.Length == 0 || id.Contains(' ', StringComparison.Ordinal) || !id.Contains('.', StringComparison.Ordinal) || id.StartsWith('-')
                || line[starts[1] - 1] != ' ' || line[starts[2] - 1] != ' ')
            {
                continue;
            }

            updates.Add(new SoftwareUpdate(Cell(0), id, Cell(2), Cell(3)));
        }

        return updates;
    }

    /// <summary>
    /// Arguments de <c>cmd.exe</c> pour mettre à jour les logiciels choisis, un par un, dans une fenêtre visible.
    /// winget reste interactif : l'utilisateur accepte lui-même les conditions des éditeurs.
    /// </summary>
    public static string UpgradeConsoleArguments(string wingetPath, IEnumerable<SoftwareUpdate> selection)
    {
        ArgumentNullException.ThrowIfNull(wingetPath);

        if (wingetPath.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException(T("Chemin de winget invalide."), nameof(wingetPath));
        }

        // /s : cmd retire seulement la première et la dernière paire de guillemets, les chemins entre guillemets restent intacts.
        var line = new StringBuilder("/s /k \"title MAUS & echo ").Append(RepairConsoleEscape(UpgradeIntro));
        var left = 0;
        foreach (var update in selection.Where(u => u.CanTarget))
        {
            var step = $" & echo. & echo {RepairConsoleEscape(T("Mise à jour : {0}", update.Name))} & \"{wingetPath}\" upgrade --id {update.Id} --exact --source winget";
            if (left > 0 || line.Length + step.Length > MaxCommandLine)
            {
                left++;
                continue;
            }

            line.Append(step);
        }

        if (left > 0)
        {
            line.Append(" & echo. & echo ").Append(RepairConsoleEscape(T("Encore {0} logiciel(s) à mettre à jour : relancez la mise à jour depuis MAUS une fois celle-ci finie.", left)));
        }

        return line.Append(" & echo. & echo ").Append(RepairConsoleEscape(UpgradeDone)).Append('"').ToString();
    }

    /// <summary>Une ligne de cmd.exe s'arrête à 8191 caractères : de la marge pour le début et la fin.</summary>
    private const int MaxCommandLine = 7500;

    public static string UpgradeIntro => T("Mise à jour des logiciels par winget, l'outil de Microsoft. Répondez à ses questions dans cette fenêtre (conditions des éditeurs) ; certains installateurs ouvrent leur propre fenêtre.");

    public static string UpgradeDone => T("Terminé. Lisez les messages ci-dessus puis relancez l'audit de MAUS. Vous pouvez fermer cette fenêtre.");

    private static string RepairConsoleEscape(string text) => M02Repair.RepairConsole.Escape(text);

    private static List<string> SplitLines(string output) => output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(Clean).ToList();

    private static bool HasTable(string output) => FindMainTable(SplitLines(output)) >= 0;

    /// <summary>
    /// Ligne de tirets de la liste normale, ou -1. Liste normale vide : winget écrit seulement le tableau des logiciels à mise à jour
    /// explicite, qui se reconnaît à son titre juste au-dessus de l'en-tête, terminé par deux-points (WorkflowBase.cpp, ReportListResult).
    /// </summary>
    private static int FindMainTable(List<string> lines)
    {
        var separator = lines.FindIndex(IsSeparator);
        return separator >= 2 && lines[separator - 2] is { Length: > 0 } title && title[^1] is ':' or '：' ? -1 : separator;
    }

    /// <summary>Case vide après un caractère large : ainsi, une position dans le texte est une cellule d'affichage.</summary>
    private const string CellFiller = "￿";

    private static string InCells(string line)
    {
        if (line.All(char.IsAscii))
        {
            return line;
        }

        var cells = new StringBuilder(line.Length + 8);
        foreach (var rune in line.EnumerateRunes())
        {
            var wide = IsWide(rune.Value);
            if (rune.Utf16SequenceLength == 1)
            {
                cells.Append(rune.ToString()).Append(wide ? CellFiller : string.Empty);
            }
            else
            {
                // Deux unités de texte : c'est bien la place d'un caractère large ; un caractère étroit est remplacé pour garder l'alignement.
                cells.Append(wide ? rune.ToString() : "�");
            }
        }

        return cells.ToString();
    }

    /// <summary>Plages larges (W et F) de la largeur Est-Asiatique (Unicode, UAX #11), qui occupent 2 cellules.</summary>
    private static bool IsWide(int c) => c is (>= 0x1100 and <= 0x115F) or (>= 0x2E80 and <= 0x303E) or (>= 0x3041 and <= 0x33FF)
        or (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF) or (>= 0xA000 and <= 0xA4CF) or (>= 0xAC00 and <= 0xD7A3)
        or (>= 0xF900 and <= 0xFAFF) or (>= 0xFE30 and <= 0xFE6F) or (>= 0xFF00 and <= 0xFF60) or (>= 0xFFE0 and <= 0xFFE6)
        or (>= 0x1F300 and <= 0x1F64F) or (>= 0x1F900 and <= 0x1F9FF) or (>= 0x20000 and <= 0x3FFFD);

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
