using System.Text;
using Maus.Core;
using Maus.Core.Engine;
using Maus.Core.Reporting;

// MAUS en ligne de commande : audit en lecture seule, utile pour tester et comparer avant/après.
//   maus                  audit complet, rapport texte
//   maus --json           audit complet, rapport JSON
//   maus --module M06     un seul module (répétable)
Console.OutputEncoding = Encoding.UTF8;

var json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
var only = args
    .Select((arg, index) => (arg, index))
    .Where(x => x.arg.Equals("--module", StringComparison.OrdinalIgnoreCase) && x.index + 1 < args.Length)
    .Select(x => args[x.index + 1])
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

if (args.Any(a => a is "-h" or "--help" or "/?"))
{
    Console.WriteLine("Usage : maus [--json] [--module Mxx]...");
    Console.WriteLine("Audit en lecture seule : MAUS ne modifie rien sur ce PC.");
    return 0;
}

var engine = AuditEngine.CreateWithBuiltInModules();
if (only.Count > 0)
{
    engine = new AuditEngine(engine.Modules.Where(m => only.Contains(m.Id)));
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

var context = AuditContext.CreateDefault();
var progress = json ? null : new Progress<ModuleResult>(r => Console.Error.WriteLine($"  … {r.ModuleId} terminé ({r.Duration.TotalSeconds:0.0} s)"));
var results = await engine.RunAsync(context, progress, cancellationToken: cancellation.Token);
var report = AuditReport.Create(context, results);

if (json)
{
    Console.WriteLine(report.ToJson());
}
else
{
    TextReport.Write(report, Console.Out, useColor: !Console.IsOutputRedirected);
}

return results.Any(r => r.WorstStatus == FindingStatus.Problem) ? 2 : 0;
