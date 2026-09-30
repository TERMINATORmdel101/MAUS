using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Reporting;

/// <summary>
/// Masque ce qui identifie la personne dans un texte partagé : dossier du profil, nom d'utilisateur, nom du PC, adresses e-mail.
/// </summary>
public sealed partial class PrivacyFilter
{
    private readonly List<(Regex Pattern, string Replacement)> _rules = [];

    /// <param name="userName">Nom du compte Windows (remplacé par « [utilisateur] »).</param>
    /// <param name="machineName">Nom du PC (remplacé par « [nom-du-pc] »).</param>
    /// <param name="profilePath">Dossier du profil, par exemple « C:\Users\Alex » (remplacé par « %USERPROFILE% »).</param>
    public PrivacyFilter(string? userName, string? machineName, string? profilePath)
    {
        if (!string.IsNullOrWhiteSpace(profilePath))
        {
            _rules.Add((new Regex(Regex.Escape(profilePath.TrimEnd('\\', '/')), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "%USERPROFILE%"));
        }

        AddWord(userName, T("[utilisateur]"));
        AddWord(machineName, T("[nom-du-pc]"));
        _rules.Add((EmailPattern(), T("[e-mail]")));
    }

    /// <summary>Filtre de la session en cours.</summary>
    public static PrivacyFilter ForCurrentUser() => new(
        Environment.UserName,
        Environment.MachineName,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public string Mask(string text)
    {
        foreach (var (pattern, replacement) in _rules)
        {
            text = pattern.Replace(text, replacement);
        }

        return text;
    }

    private void AddWord(string? word, string replacement)
    {
        // Mot entier seulement, et pas trop court : un compte nommé « PC » ne doit pas effacer « PC fixe ».
        if (string.IsNullOrWhiteSpace(word) || word.Trim().Length < 3)
        {
            return;
        }

        _rules.Add((new Regex($@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(word.Trim())}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), replacement));
    }

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(\.[\w-]+)+", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();
}

/// <summary>
/// Résumé court à coller sur un forum ou à envoyer à un proche pour demander de l'aide : configuration, Windows et points
/// à régler. Sans numéro de série, nom d'utilisateur, nom du PC ni adresse e-mail ; l'utilisateur le relit avant de le copier.
/// </summary>
public static class HelpSummary
{
    /// <summary>Nombre maximal de constats détaillés : au-delà, le résumé ne serait plus lu.</summary>
    public const int MaxDetailed = 20;

    private const int MaxValueLength = 140;

    public static string Build(AuditReport report, PrivacyFilter privacy)
    {
        var text = new StringBuilder();
        void Line(string value = "") => text.AppendLine(privacy.Mask(value));

        Line(T("Mon problème : (décrivez-le ici : ce qui se passe, depuis quand, dans quel jeu ou logiciel)"));
        Line();
        Line(T("Configuration (relevée par MAUS {0}, le {1}) :", report.MausVersion, report.CreatedAt.ToString("d", Culture)));
        Line(T("- Windows : {0} {1} (build {2})", report.Windows.ProductName, report.Windows.DisplayVersion, report.Windows.FullBuild));
        Line(T("- PC : {0}", Machine(report)));
        if (Value(report, "M08.bios-age") is { } bios)
        {
            Line(T("- BIOS : {0}", bios));
        }

        var cpu = report.Hardware.Cpu;
        Line(cpu.Cores > 0
            ? T("- Processeur : {0} ({1} cœurs, {2} threads)", cpu.Name.Trim(), cpu.Cores, cpu.LogicalProcessors)
            : T("- Processeur : {0}", cpu.Name.Trim()));
        if (Value(report, "M10.capacity") is { } memory)
        {
            Line(T("- Mémoire : {0}", memory));
        }

        if (Value(report, "M10.xmp") is { } speed)
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
                Line(Item(finding, finding.Status == FindingStatus.Problem ? T("PROBLÈME") : T("à surveiller")));
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
                Line(Item(finding, null));
            }
        }

        Line();
        Line(T("Résumé préparé par MAUS (logiciel libre) : sans numéro de série, nom d'utilisateur, nom du PC ni adresse e-mail."));
        return text.ToString();
    }

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

    private static string Item(Finding finding, string? label)
    {
        var line = new StringBuilder("- ");
        if (label is not null)
        {
            line.Append('[').Append(label).Append("] ");
        }

        line.Append(finding.Title);
        if (Shorten(finding.Current) is { } current)
        {
            line.Append(T(" : {0}", current));
        }

        return line.Append(CultureInfo.InvariantCulture, $" ({finding.Id})").ToString();
    }

    private static string? Value(AuditReport report, string findingId) =>
        report.Modules.SelectMany(m => m.Findings).FirstOrDefault(f => f.Id == findingId && f.Status != FindingStatus.Unknown) is { Current: { } current }
            ? Shorten(current)
            : null;

    private static string? Shorten(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var single = string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return single.Length <= MaxValueLength ? single : string.Concat(single.AsSpan(0, MaxValueLength - 1), "…");
    }
}
