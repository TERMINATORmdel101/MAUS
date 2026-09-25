using static Maus.Core.Localization.Texts;

namespace Maus.Core.Reporting;

/// <summary>Rapport lisible en console, avec voyants colorés.</summary>
public static class TextReport
{
    public static void Write(AuditReport report, TextWriter writer, bool useColor)
    {
        writer.WriteLine(T("MAUS {0} — audit en lecture seule, aucune modification", report.MausVersion));
        writer.WriteLine(T("{0} {1} (build {2}), édition {3}", report.Windows.ProductName, report.Windows.DisplayVersion, report.Windows.FullBuild, report.Windows.EditionId));
        writer.WriteLine($"{Labels.Of(report.Hardware.FormFactor)} · {report.Hardware.Cpu.Name} · {string.Join(", ", report.Hardware.Gpus.Select(g => g.Name))}");
        if (!report.IsElevated)
        {
            writer.WriteLine(T("Sans droits administrateur : certains contrôles restent indéterminés."));
        }

        foreach (var module in report.Modules)
        {
            writer.WriteLine();
            WriteColored(writer, useColor, module.WorstStatus, $"[{Symbol(module.WorstStatus)}] {module.ModuleId} — {module.Title}");
            writer.WriteLine($"  ({module.Duration.TotalSeconds:0.0} s)");
            if (module.Error is not null)
            {
                writer.WriteLine($"    ! {module.Error}");
            }

            foreach (var finding in module.Findings)
            {
                WriteColored(writer, useColor, finding.Status, $"    {Symbol(finding.Status)} {finding.Title}");
                var values = finding switch
                {
                    { Current: not null, Expected: not null } => $" : {finding.Current} (attendu : {finding.Expected})",
                    { Current: not null } => $" : {finding.Current}",
                    _ => string.Empty,
                };
                writer.WriteLine(values);
                if (finding.Status is FindingStatus.Improvable or FindingStatus.Warning or FindingStatus.Problem or FindingStatus.Unknown)
                {
                    writer.WriteLine($"        {finding.Explanation}");
                    if (finding.Advice is not null)
                    {
                        writer.WriteLine($"        → {finding.Advice}");
                    }
                }
            }
        }

        var all = report.Modules.SelectMany(m => m.Findings).ToList();
        writer.WriteLine();
        writer.WriteLine(T("Bilan : {0} problème(s), " +
                         "{1} point(s) à surveiller, " +
                         "{2} optimisation(s) possible(s), " +
                         "{3} conforme(s), " +
                         "{4} indéterminé(s).", all.Count(f => f.Status == FindingStatus.Problem), all.Count(f => f.Status == FindingStatus.Warning), all.Count(f => f.Status == FindingStatus.Improvable), all.Count(f => f.Status == FindingStatus.Ok), all.Count(f => f.Status == FindingStatus.Unknown)));
    }

    private static string Symbol(FindingStatus status) => status switch
    {
        FindingStatus.Ok => "OK",
        FindingStatus.Info => "i ",
        FindingStatus.Improvable => "->",
        FindingStatus.Warning => "!!",
        FindingStatus.Problem => "XX",
        _ => "??",
    };

    private static void WriteColored(TextWriter writer, bool useColor, FindingStatus status, string text)
    {
        if (!useColor)
        {
            writer.Write(text);
            return;
        }

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = status switch
        {
            FindingStatus.Ok => ConsoleColor.Green,
            FindingStatus.Improvable => ConsoleColor.Blue,
            FindingStatus.Warning => ConsoleColor.Yellow,
            FindingStatus.Problem => ConsoleColor.Red,
            FindingStatus.Unknown => ConsoleColor.DarkGray,
            _ => ConsoleColor.Cyan,
        };
        writer.Write(text);
        Console.ForegroundColor = previous;
    }
}
