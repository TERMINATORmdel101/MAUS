using System.Text;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Reporting;

/// <summary>Constats d'un audit en tableau CSV (séparateur « ; », reconnu par Excel en français) : une ligne par constat.</summary>
public static class FindingsCsv
{
    public static string Build(AuditReport report, PrivacyFilter privacy)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(privacy);

        var text = new StringBuilder();
        void Row(params string?[] cells) => text.Append(string.Join(';', cells.Select(c => CsvText.Cell(privacy.Mask(c ?? string.Empty))))).Append("\r\n");

        Row(T("Module"), T("Identifiant"), T("État"), T("Gravité"), T("Constat"), T("Constaté"), T("Attendu"), T("Conseil"));
        foreach (var module in report.Modules)
        {
            if (module.Error is { Length: > 0 })
            {
                Row(module.ModuleId, "", Labels.Of(FindingStatus.Unknown), "", module.Title, module.Error, null, null);
            }

            foreach (var finding in module.Findings)
            {
                Row(module.ModuleId, finding.Id, Labels.Of(finding.Status), HealthScore.GravityName(HealthScore.GravityOf(finding)), finding.Title, finding.Current, finding.Expected, AdviceWithNotes(finding));
            }
        }

        return text.ToString();
    }

    /// <summary>Le conseil, puis l'encadré et la note, dans la même cellule. ScreenOnlyNote n'est jamais écrite ici.</summary>
    private static string? AdviceWithNotes(Finding finding)
    {
        var parts = new[] { finding.Advice, finding.Callout, finding.SideNote }.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }
}
