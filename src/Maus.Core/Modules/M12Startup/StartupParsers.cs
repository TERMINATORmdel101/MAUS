using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M12Startup;

/// <summary>État lu dans une valeur binaire <c>StartupApproved</c>.</summary>
internal sealed record ApprovalState(bool Enabled, DateTime? DisabledOnUtc);

/// <summary>Tâche planifiée tierce lancée à l'ouverture de session : chemin dans la bibliothèque et commande.</summary>
internal sealed record LogonTaskInfo(string Name, string? Command);

/// <summary>Analyses sans accès au système utilisées par le module 12.</summary>
internal static partial class StartupParsers
{
    private static readonly string[] ScriptHosts = ["wscript.exe", "cscript.exe", "mshta.exe"];
    private static readonly string[] ScriptExtensions = [".vbs", ".vbe", ".js", ".jse", ".ps1", ".hta", ".wsf"];
    private static readonly string[] PowerShellHosts = ["powershell.exe", "pwsh.exe"];

    /// <summary>
    /// Valeur <c>StartupApproved</c> : premier octet 02 ou 06 = activé, autre valeur = désactivé ;
    /// octets 4 à 11 = date de désactivation (FILETIME). Valeur absente = activé.
    /// </summary>
    public static ApprovalState ParseApproval(byte[]? value)
    {
        if (value is not { Length: > 0 })
        {
            return new ApprovalState(true, null);
        }

        var enabled = value[0] is 0x02 or 0x06;
        DateTime? disabledOn = null;
        if (!enabled && value.Length >= 12)
        {
            var fileTime = BinaryPrimitives.ReadInt64LittleEndian(value.AsSpan(4, 8));
            if (fileTime > 0 && fileTime < DateTime.MaxValue.ToFileTimeUtc())
            {
                disabledOn = DateTime.FromFileTimeUtc(fileTime);
            }
        }

        return new ApprovalState(enabled, disabledOn);
    }

    /// <summary>
    /// Sépare une ligne de commande en exécutable et arguments, avec ou sans guillemets :
    /// <c>"C:\A B\app.exe" -x</c>, <c>C:\A B\app.exe -x</c> ou <c>rundll32.exe x.dll,Entry</c>.
    /// </summary>
    public static (string Executable, string Arguments) SplitCommand(string? command)
    {
        var text = command?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end < 0
                ? (text.Trim('"'), string.Empty)
                : (text[1..end], text[(end + 1)..].Trim());
        }

        var match = UnquotedExecutable().Match(text);
        if (match.Success)
        {
            return (match.Groups["exe"].Value, match.Groups["args"].Value.TrimStart(',', ' ').Trim());
        }

        var space = text.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? (text, string.Empty) : (text[..space], text[(space + 1)..].Trim());
    }

    /// <summary>Nom de fichier d'un chemin, sans lever d'exception sur les caractères invalides.</summary>
    public static string FileNameOf(string path)
    {
        var index = path.LastIndexOfAny(['\\', '/']);
        return index < 0 ? path : path[(index + 1)..];
    }

    /// <summary>Nom qui ressemble à un tirage aléatoire (GUID, suite hexadécimale, lettres et chiffres mêlés, sans voyelles).</summary>
    public static bool LooksRandom(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (GuidLike().IsMatch(stem))
        {
            return true;
        }

        if (stem.Length < 8 || !stem.All(char.IsAsciiLetterOrDigit))
        {
            return false;
        }

        if (stem.Length >= 12 && stem.All(char.IsAsciiHexDigit) && stem.Any(char.IsAsciiDigit))
        {
            return true;
        }

        var letters = stem.Count(char.IsAsciiLetter);
        var digits = stem.Length - letters;
        var vowels = stem.Count(c => "aeiouyAEIOUY".Contains(c, StringComparison.Ordinal));
        var switches = 0;
        for (var i = 1; i < stem.Length; i++)
        {
            if (char.IsAsciiDigit(stem[i]) != char.IsAsciiDigit(stem[i - 1]))
            {
                switches++;
            }
        }

        return (digits >= 3 && switches >= 4) || (letters >= 7 && vowels * 10 < letters);
    }

    /// <summary>
    /// Raison pour laquelle une entrée de démarrage paraît suspecte, ou <c>null</c> : dossier temporaire,
    /// script lancé par l'hôte de scripts, PowerShell caché ou encodé, nom aléatoire sans éditeur sous AppData.
    /// </summary>
    public static string? SuspicionReason(string executable, string arguments, string? company)
    {
        var path = executable.Replace('/', '\\');
        var fileName = FileNameOf(path);
        var all = $"{path} {arguments}";

        if (TempFolder().IsMatch(all))
        {
            return T("le programme est lancé depuis un dossier temporaire, emplacement habituel des logiciels malveillants");
        }

        if (ScriptHosts.Contains(fileName, StringComparer.OrdinalIgnoreCase))
        {
            return T("un script est lancé en arrière-plan par l'hôte de scripts de Windows");
        }

        if (PowerShellHosts.Contains(fileName, StringComparer.OrdinalIgnoreCase) && HiddenPowerShell().IsMatch(arguments))
        {
            return T("une commande PowerShell est lancée cachée ou encodée");
        }

        if (ScriptExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
        {
            return T("un script (.vbs, .js, .ps1…) est lancé directement au démarrage");
        }

        if (string.IsNullOrWhiteSpace(company)
            && path.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase)
            && LooksRandom(fileName))
        {
            return T("un programme au nom aléatoire, sans éditeur, est lancé depuis le dossier AppData");
        }

        return null;
    }

    /// <summary>
    /// Chemin cible d'un raccourci .lnk (format MS-SHLLINK) : <c>LocalBasePath</c> de la structure LinkInfo,
    /// suivi de <c>CommonPathSuffix</c>. Renvoie <c>null</c> pour un raccourci sans chemin local (application du Store, réseau).
    /// </summary>
    public static string? ParseShortcutTarget(ReadOnlySpan<byte> data)
    {
        const int headerSize = 0x4C;
        const uint hasLinkTargetIdList = 0x1;
        const uint hasLinkInfo = 0x2;
        const uint volumeIdAndLocalBasePath = 0x1;

        if (data.Length < headerSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != headerSize)
        {
            return null;
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[0x14..]);
        var offset = headerSize;
        if ((flags & hasLinkTargetIdList) != 0)
        {
            if (data.Length < offset + 2)
            {
                return null;
            }

            offset += 2 + BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        }

        if ((flags & hasLinkInfo) == 0 || data.Length < offset + 0x1C)
        {
            return null;
        }

        var linkInfo = data[offset..];
        var linkInfoSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(linkInfo);
        var linkInfoHeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(linkInfo[4..]);
        var linkInfoFlags = BinaryPrimitives.ReadUInt32LittleEndian(linkInfo[8..]);
        if ((linkInfoFlags & volumeIdAndLocalBasePath) == 0 || linkInfoSize > linkInfo.Length || linkInfoSize < 0x1C)
        {
            return null;
        }

        linkInfo = linkInfo[..linkInfoSize];
        var localBasePathOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(linkInfo[16..]);
        var commonPathSuffixOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(linkInfo[24..]);
        string? basePath = null;
        string? suffix = null;
        if (linkInfoHeaderSize >= 0x24 && linkInfo.Length >= 0x24)
        {
            var unicodeBase = (int)BinaryPrimitives.ReadUInt32LittleEndian(linkInfo[28..]);
            var unicodeSuffix = (int)BinaryPrimitives.ReadUInt32LittleEndian(linkInfo[32..]);
            basePath = unicodeBase > 0 ? ReadUnicodeString(linkInfo, unicodeBase) : null;
            suffix = unicodeSuffix > 0 ? ReadUnicodeString(linkInfo, unicodeSuffix) : null;
        }

        basePath ??= ReadAnsiString(linkInfo, localBasePathOffset);
        suffix ??= ReadAnsiString(linkInfo, commonPathSuffixOffset);
        if (string.IsNullOrEmpty(basePath))
        {
            return null;
        }

        return string.IsNullOrEmpty(suffix) ? basePath : Path.Combine(basePath, suffix);
    }

    /// <summary>
    /// Instance <c>MSFT_ScheduledTask</c> : renvoie la tâche si elle est hors dossier <c>\Microsoft\</c>, activée (<c>State</c> ≠ 1)
    /// et porte un déclencheur d'ouverture de session actif, sinon <c>null</c>. Les propriétés système (classe) n'étant pas lues,
    /// un déclencheur d'ouverture de session (<c>MSFT_TaskLogonTrigger</c>) se reconnaît à sa propriété <c>UserId</c>,
    /// sans la propriété <c>StateChange</c> propre au déclencheur de changement de session.
    /// </summary>
    public static LogonTaskInfo? ParseLogonTask(CimRow task)
    {
        const long disabledState = 1;
        var path = task.GetString("TaskPath") ?? @"\";
        if (path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) || task.GetInt64("State") == disabledState)
        {
            return null;
        }

        var atLogon = task.GetRows("Triggers").Any(t =>
            t.Names.Contains("UserId", StringComparer.OrdinalIgnoreCase)
            && !t.Names.Contains("StateChange", StringComparer.OrdinalIgnoreCase)
            && t.GetBool("Enabled") != false);
        if (!atLogon)
        {
            return null;
        }

        var exec = task.GetRows("Actions").FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.GetString("Execute")));
        var command = exec?.GetString("Execute")?.Trim();
        var arguments = exec?.GetString("Arguments")?.Trim();
        if (!string.IsNullOrEmpty(command) && !string.IsNullOrEmpty(arguments))
        {
            command = $"{command} {arguments}";
        }

        var name = $"{path}{task.GetString("TaskName")}".TrimStart('\\');
        return new LogonTaskInfo(name, string.IsNullOrEmpty(command) ? null : command);
    }

    /// <summary>
    /// « CN=Intel Corporation, O=…, C=US » devient « Intel Corporation ». Un éditeur réduit à un identifiant (GUID)
    /// n'apprend rien à l'utilisateur : <c>null</c>.
    /// </summary>
    public static string? PublisherName(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
        {
            return null;
        }

        var name = publisher.Trim();
        foreach (var part in publisher.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                name = trimmed[3..].Trim('"', ' ');
                break;
            }
        }

        return name.Length == 0 || GuidLike().IsMatch(name) ? null : name;
    }

    /// <summary>Durée en millisecondes lue dans un champ d'événement, affichée en secondes.</summary>
    public static double? ParseMilliseconds(IReadOnlyDictionary<string, string> data, string name) =>
        data.TryGetValue(name, out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) && ms >= 0
            ? ms / 1000d
            : null;

    /// <summary>Identifiant lisible pour un constat : minuscules, chiffres et tirets.</summary>
    public static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > 48)
        {
            slug = slug[..48].TrimEnd('-');
        }

        return slug.Length == 0 ? "x" : slug;
    }

    private static string? ReadUnicodeString(ReadOnlySpan<byte> data, int offset)
    {
        if (offset <= 0 || offset >= data.Length)
        {
            return null;
        }

        var span = data[offset..];
        var length = 0;
        while (length + 1 < span.Length && (span[length] != 0 || span[length + 1] != 0))
        {
            length += 2;
        }

        return Encoding.Unicode.GetString(span[..length]);
    }

    private static string? ReadAnsiString(ReadOnlySpan<byte> data, int offset)
    {
        if (offset <= 0 || offset >= data.Length)
        {
            return null;
        }

        var span = data[offset..];
        var end = span.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? span : span[..end]);
    }

    [GeneratedRegex(@"^(?<exe>.+?\.(?:exe|com|bat|cmd|vbs|vbe|js|jse|wsf|ps1|hta|scr|lnk))(?=[\s,]|$)(?<args>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex UnquotedExecutable();

    [GeneratedRegex(@"^\{?[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}?$", RegexOptions.IgnoreCase)]
    private static partial Regex GuidLike();

    [GeneratedRegex(@"\\(?:Temp|Tmp)\\", RegexOptions.IgnoreCase)]
    private static partial Regex TempFolder();

    [GeneratedRegex(@"(?:^|\s)[-/](?:w(?:indowstyle)?\s+h(?:idden)?|e(?:nc(?:odedcommand)?)?\s)", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenPowerShell();
}
