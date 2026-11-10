using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Reporting;

/// <summary>
/// Masque ce qui identifie la personne ou le PC dans un texte partagé : dossier du profil, adresses e-mail, identifiant de compte (SID),
/// domaine Windows, nom d'utilisateur, nom du PC, adresses MAC et numéros de série annoncés comme tels. Un nom complet ou une adresse IP
/// ne sont pas reconnus (<see cref="HelpSummary.AnonymizationNote"/> le dit à l'utilisateur).
/// </summary>
public sealed partial class PrivacyFilter
{
    private readonly List<(Regex Pattern, string Replacement, string Label)> _rules = [];

    private readonly List<string> _removed = [];

    /// <summary>Ce qui a réellement été retiré jusqu'ici (la catégorie, jamais la valeur).</summary>
    public IReadOnlyList<string> Removed => _removed;

    /// <param name="userName">Nom du compte Windows (remplacé par « [utilisateur] »).</param>
    /// <param name="machineName">Nom du PC (remplacé par « [nom-du-pc] »).</param>
    /// <param name="profilePath">Dossier du profil, par exemple « C:\Users\Alex » (remplacé par « %USERPROFILE% »).</param>
    /// <param name="domainName">Domaine Windows du compte (remplacé par « [domaine] »).</param>
    public PrivacyFilter(string? userName, string? machineName, string? profilePath, string? domainName = null)
    {
        if (!string.IsNullOrWhiteSpace(profilePath))
        {
            _rules.Add((new Regex(Regex.Escape(profilePath.TrimEnd('\\', '/')) + @"(?![\p{L}\p{N}.])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "%USERPROFILE%", T("dossier du profil")));
        }

        // L'adresse avant le nom : masquer d'abord sa partie locale, souvent le nom du compte, laisserait son domaine.
        _rules.Add((EmailPattern(), T("[e-mail]"), T("adresse e-mail")));
        _rules.Add((SidPattern(), "[SID]", T("identifiant de compte (SID)")));
        AddDomainQualifiedAccount(userName);
        AddAccountInPaths(userName);

        // Le PC avant le compte : son nom contient souvent celui du compte (« PC-ALEX »).
        AddWord(machineName, T("[nom-du-pc]"), T("nom du PC"));
        AddWord(userName, T("[utilisateur]"), T("nom d'utilisateur"));
        if (!string.Equals(domainName, machineName, StringComparison.OrdinalIgnoreCase))
        {
            AddWord(domainName, T("[domaine]"), T("domaine Windows"));
        }
        _rules.Add((MacPattern(), T("[adresse-mac]"), T("adresse MAC")));
        _rules.Add((SerialPattern(), T("[numéro-de-série]"), T("numéro de série")));

        // Le dossier de tout autre compte du PC : son nom n'est connu d'aucune des règles ci-dessus.
        _rules.Add((new Regex(@"[A-Za-z]:[\\/]Users[\\/][^\\/:*?""<>|;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            @"%SystemDrive%\Users\" + T("[compte]"), T("dossier d'un autre compte")));
    }

    /// <summary>Filtre de la session en cours.</summary>
    public static PrivacyFilter ForCurrentUser() => new(
        Environment.UserName,
        Environment.MachineName,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.UserDomainName);

    public string Mask(string text)
    {
        foreach (var (pattern, replacement, label) in _rules)
        {
            text = pattern.Replace(text, _ =>
            {
                if (!_removed.Contains(label, StringComparer.Ordinal))
                {
                    _removed.Add(label);
                }

                return replacement;
            });
        }

        return text;
    }

    /// <summary>
    /// Le nom, pas trop court : un compte nommé « PC » ne doit pas effacer « PC fixe ». Collé à des chiffres, il reste reconnu
    /// (« alex2024 ») ; collé à des lettres, seulement à partir de <see cref="LongEnoughToBeGlued"/> : « Jean » n'efface pas « Jeannette ».
    /// </summary>
    private void AddWord(string? word, string replacement, string label)
    {
        if (string.IsNullOrWhiteSpace(word) || word.Trim().Length < 3)
        {
            return;
        }

        var name = word.Trim();
        var pattern = name.Length >= LongEnoughToBeGlued
            ? Regex.Escape(name)
            : $@"(?<!\p{{L}}){Regex.Escape(name)}(?!\p{{L}})";
        _rules.Add((new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), replacement, label));
    }

    /// <summary><c>DOMAINE\compte</c>, quelle que soit la longueur du nom. Un élément d'un chemin (<c>D:\Jeux\compte</c>) n'en est pas un.</summary>
    private void AddDomainQualifiedAccount(string? userName)
    {
        if (!string.IsNullOrWhiteSpace(userName))
        {
            _rules.Add((new Regex($@"(?<![\\/:\p{{L}}\p{{N}}_.-])[\p{{L}}\p{{N}}_.-]+\\{Regex.Escape(userName.Trim())}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                T("[domaine]") + @"\" + T("[utilisateur]"), T("domaine Windows")));
        }
    }

    /// <summary>Compte de une ou deux lettres : trop court pour un mot du texte, il reste reconnu comme élément d'un chemin.</summary>
    private void AddAccountInPaths(string? userName)
    {
        if (!string.IsNullOrWhiteSpace(userName) && userName.Trim().Length < 3)
        {
            _rules.Add((new Regex($@"(?<=[\\/]){Regex.Escape(userName.Trim())}(?![\p{{L}}\p{{N}}.])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                T("[utilisateur]"), T("nom d'utilisateur")));
        }
    }

    private const int LongEnoughToBeGlued = 6;

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(\.[\w-]+)+", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    /// <summary>Compte local ou de domaine (S-1-5-21), compte Microsoft (S-1-11-96), compte Entra (S-1-12-1) ; souvent collé à un nom de tâche.</summary>
    [GeneratedRegex(@"S-1-(?:5-21|11-96|12-1)(?:-\d+)+", RegexOptions.CultureInvariant)]
    private static partial Regex SidPattern();

    /// <summary>Six octets séparés par « : » ou « - » (pas douze chiffres collés : trop de références de pièces y ressemblent).</summary>
    [GeneratedRegex(@"(?<![0-9A-Fa-f][:-]?)(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}(?![:-]?[0-9A-Fa-f])", RegexOptions.CultureInvariant)]
    private static partial Regex MacPattern();

    /// <summary>La valeur qui suit « numéro de série : », « serial number = », « S/N : »… ; le libellé reste, seule la valeur disparaît.</summary>
    [GeneratedRegex(@"(?<=(?<![\p{L}\p{N}])(?:num[ée]ro\s+de\s+s[ée]rie|n[°o]\s*de\s*s[ée]rie|n[úu]mero\s+de\s+serie|serial(?:\s*(?:number|no\.?|n°))?|s/n)\s*[:=]\s*)[^\s,;·|]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SerialPattern();
}

/// <summary>
/// Résumé court à coller sur un forum ou à envoyer à un proche pour demander de l'aide : configuration, Windows et points
/// à régler. Sans numéro de série, nom d'utilisateur, nom du PC ni adresse e-mail ; l'utilisateur le relit avant de le copier.
/// </summary>
public static partial class HelpSummary
{
    /// <summary>Nombre maximal de constats détaillés : au-delà, le résumé ne serait plus lu.</summary>
    public const int MaxDetailed = 20;

    private const int MaxValueLength = 140;

    public static string Build(AuditReport report, PrivacyFilter privacy)
    {
        ArgumentNullException.ThrowIfNull(privacy);
        ArgumentNullException.ThrowIfNull(report);

        var text = new StringBuilder();
        void Line(string value = "") => text.AppendLine(privacy.Mask(value));

        Line(T("Mon problème : (décrivez-le ici : ce qui se passe, depuis quand, dans quel jeu ou logiciel)"));
        Line();
        Line(T("Configuration (relevée par MAUS {0}, le {1}) :", report.MausVersion, report.CreatedAt.ToString("d", Culture)));
        Line(T("- Windows : {0} {1} (build {2})", report.Windows.ProductName, report.Windows.DisplayVersion, report.Windows.FullBuild));
        Line(T("- PC : {0}", Machine(report)));
        if (Value(report, "M08.bios-age", privacy) is { } bios)
        {
            Line(T("- BIOS : {0}", bios));
        }

        var cpu = report.Hardware.Cpu;
        Line(cpu.Cores > 0
            ? T("- Processeur : {0} ({1} cœurs, {2} threads)", cpu.Name.Trim(), cpu.Cores, cpu.LogicalProcessors)
            : T("- Processeur : {0}", cpu.Name.Trim()));
        if (Value(report, "M10.capacity", privacy) is { } memory)
        {
            Line(T("- Mémoire : {0}", memory));
        }

        if (Value(report, "M10.xmp", privacy) is { } speed)
        {
            Line(T("- Vitesse de la mémoire : {0}", speed));
        }

        foreach (var gpu in report.Hardware.Gpus)
        {
            Line(gpu.DriverVersion is { Length: > 0 } driver
                ? T("- Carte graphique : {0} (pilote {1}{2})", gpu.Name.Trim(), driver, gpu.DriverDate is { } date ? T(" du {0}", date.ToString("d", Culture)) : string.Empty)
                : T("- Carte graphique : {0}", gpu.Name.Trim()));
        }

        var findings = report.Modules.SelectMany(m => m.Findings).ToList();
        var breakdown = HealthScore.Explain(report.Modules);
        var score = breakdown.Score;
        Line(T("- Score de santé MAUS : {0}/100 ({1})", score, HealthScore.Describe(score)));
        if (HealthScore.PartialNote(breakdown) is { } partial)
        {
            Line("- " + partial);
        }
        if (!report.IsElevated)
        {
            Line(T("- Audit sans droits administrateur : certains contrôles sont restés indéterminés."));
        }

        if (Value(report, "M02.recent-events", privacy) is { } recent)
        {
            Line(T("- Ces 30 derniers jours : {0}", recent));
        }

        var important = findings
            .Where(f => f.Status is FindingStatus.Problem or FindingStatus.Warning)
            .OrderByDescending(f => f.Status)
            .ThenByDescending(f => f.Severity)
            .ToList();
        Line();
        if (important.Count == 0)
        {
            Line(T("Aucun problème ni point à surveiller détecté par l'audit."));
        }
        else
        {
            Line(T("Points relevés par l'audit :"));
            foreach (var finding in important.Take(MaxDetailed))
            {
                Line(Item(finding, finding.Status == FindingStatus.Problem ? T("PROBLÈME") : T("à surveiller"), privacy));
            }

            if (important.Count > MaxDetailed)
            {
                Line(T("- … et {0} autre(s)", important.Count - MaxDetailed));
            }
        }

        var improvable = findings.Count(f => f.Status == FindingStatus.Improvable);
        var unknown = findings.Count(f => f.Status == FindingStatus.Unknown);
        if (improvable > 0 || unknown > 0)
        {
            Line(T("Aussi : {0} optimisation(s) possible(s), {1} contrôle(s) indéterminé(s).", improvable, unknown));
        }

        var wanted = findings.Where(f => f.AcknowledgedFrom is not null).ToList();
        if (wanted.Count > 0)
        {
            Line();
            Line(T("Réglages modifiés volontairement (choix de l'utilisateur) :"));
            foreach (var finding in wanted.Take(MaxDetailed))
            {
                Line(Item(finding, null, privacy));
            }

            if (wanted.Count > MaxDetailed)
            {
                Line(T("- … et {0} autre(s)", wanted.Count - MaxDetailed));
            }
        }

        Line();
        Line(T("Résumé préparé par MAUS (logiciel libre) : sans numéro de série, nom d'utilisateur, nom du PC ni adresse e-mail."));
        return text.ToString();
    }

    /// <summary>Ce que l'anonymisation a retiré du résumé, à afficher à côté du texte.</summary>
    public static string AnonymizationNote(PrivacyFilter privacy)
    {
        ArgumentNullException.ThrowIfNull(privacy);
        var done = privacy.Removed.Count == 0
            ? T("Anonymisation : aucun nom d'utilisateur, nom de PC ni adresse e-mail n'a été trouvé dans ce résumé.")
            : T("Anonymisation : MAUS a retiré de ce résumé : {0}.", string.Join(", ", privacy.Removed));
        return done + " " + T("MAUS ne repère pas un nom complet ni une adresse IP, et ne les masque donc pas : relisez le texte avant de le partager.");
    }

    /// <summary>
    /// Le résumé relu par l'utilisateur, prêt à coller sur un forum en Markdown : titres en gras, listes conservées, caractères
    /// de mise en forme neutralisés.
    /// </summary>
    public static string ToMarkdown(string summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var lines = summary.Split('\n').Select(line =>
        {
            var trimmed = line.TrimEnd();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                return "- " + MarkdownEscape(trimmed[2..]);
            }

            return trimmed.EndsWith(':') ? "**" + MarkdownEscape(trimmed) + "**" : MarkdownEscape(trimmed);
        });
        return string.Join('\n', lines);
    }

    /// <summary>« @ » et « # » aussi : sur GitHub, ils préviendraient une personne ou renverraient à un ticket.</summary>
    [GeneratedRegex(@"[\\`*_\[\]<>@#]", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownSpecial();

    private static string MarkdownEscape(string text) => MarkdownSpecial().Replace(text, @"\$0");

    private static string Machine(AuditReport report)
    {
        var hardware = report.Hardware;
        var kind = Labels.Of(hardware.FormFactor);
        if (Value(report, "M08.board") is { } board)
        {
            return $"{kind} · {board}";
        }

        var name = hardware.IsLaptop
            ? $"{hardware.Manufacturer} {hardware.Model}"
            : $"{hardware.BoardManufacturer} {hardware.BoardProduct}";
        return string.IsNullOrWhiteSpace(name) ? kind : $"{kind} · {name.Trim()}";
    }

    private static string Item(Finding finding, string? label, PrivacyFilter privacy)
    {
        var line = new StringBuilder("- ");
        if (label is not null)
        {
            line.Append('[').Append(label).Append("] ");
        }

        // Sur une seule ligne : un titre lu sur le PC ne fabrique pas une fausse ligne du résumé.
        line.Append(OneLine(privacy.Mask(finding.Title)));
        // Masqué avant d'être coupé : un nom coupé en deux ne serait plus reconnu.
        if (Shorten(finding.Current is { } value ? privacy.Mask(value) : null) is { } current)
        {
            line.Append(T(" : {0}", current));
        }

        return line.Append(CultureInfo.InvariantCulture, $" ({finding.Id})").ToString();
    }

    private static string? Value(AuditReport report, string findingId, PrivacyFilter? privacy = null) =>
        report.Modules.SelectMany(m => m.Findings).FirstOrDefault(f => f.Id == findingId && f.Status != FindingStatus.Unknown) is { Current: { } current }
            ? Shorten(privacy?.Mask(current) ?? current)
            : null;

    private static string OneLine(string value) =>
        string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string? Shorten(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var single = OneLine(value);
        return single.Length <= MaxValueLength ? single : string.Concat(single.AsSpan(0, MaxValueLength - 1), "…");
    }
}
