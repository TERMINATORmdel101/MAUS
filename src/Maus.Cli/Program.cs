using System.Text;
using Maus.Cli;
using Maus.Core;
using Maus.Core.Engine;
using Maus.Core.Fixes;
using Maus.Core.Reporting;

// MAUS en ligne de commande : audit en lecture seule, utile pour tester et comparer avant/après.
//   maus                  audit complet, rapport texte
//   maus --json           audit complet, rapport JSON
//   maus --module M06     un seul module (répétable)
// Corrections (V0.2, droits administrateur requis pour appliquer ou annuler) :
//   maus --plan                       corrections proposées, sans rien modifier
//   maus --apply ID [ID...]           applique les corrections choisies, après confirmation
//   maus --apply-recommended          applique les corrections recommandées (pré-cochées)
//   maus --journal                    séances de corrections enregistrées
//   maus --revert SEANCE [--force]    remet les valeurs d'origine d'une séance
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
    Console.WriteLine();
    Console.WriteLine("Corrections (droits administrateur requis pour appliquer ou annuler) :");
    Console.WriteLine("  maus --plan [--module Mxx]             corrections proposées, sans rien modifier");
    Console.WriteLine("  maus --apply ID [ID...]                applique les corrections choisies, après confirmation");
    Console.WriteLine("  maus --apply-recommended [--module Mxx] applique les corrections recommandées");
    Console.WriteLine("       [--without-restore-point] [--enable-protection] [--yes]");
    Console.WriteLine("  maus --journal                         séances de corrections enregistrées");
    Console.WriteLine("  maus --revert SEANCE [--force] [--yes] remet les valeurs d'origine d'une séance");
    return 0;
}

bool Has(string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);

string? ValueOf(string flag)
{
    var index = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) ? args[index + 1] : null;
}

if (Has("--journal"))
{
    return FixCommands.PrintJournal(FileJournalStore.CreateDefault(), Console.Out);
}

if (Has("--revert"))
{
    if (ValueOf("--revert") is not { } sessionId)
    {
        Console.WriteLine("Indiquez la séance à annuler : maus --revert SEANCE (voir maus --journal).");
        return 1;
    }

    var revertContext = FixContext.CreateDefault(AuditContext.CreateDefault());
    return FixCommands.Revert(revertContext, sessionId, Has("--force"), Has("--yes"), Console.Out);
}

var applyIds = args
    .SkipWhile(a => !a.Equals("--apply", StringComparison.OrdinalIgnoreCase))
    .Skip(1)
    .TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal))
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
var fixMode = Has("--plan") || Has("--apply") || Has("--apply-recommended");

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
if (fixMode)
{
    var audit = await engine.RunAsync(context, cancellationToken: cancellation.Token);
    var plan = FixCommands.Plan(engine, audit, context);
    if (Has("--plan"))
    {
        FixCommands.PrintPlan(plan, Console.Out);
        return 0;
    }

    var selected = Has("--apply-recommended")
        ? plan.Where(c => c.Recommended && !c.Advanced).ToList()
        : plan.Where(c => applyIds.Contains(c.Id)).ToList();
    var options = new ApplyOptions
    {
        CreateRestorePoint = !Has("--without-restore-point"),
        EnableProtectionIfNeeded = Has("--enable-protection"),
        ProceedWithoutRestorePoint = Has("--without-restore-point"),
    };
    return FixCommands.Apply(FixContext.CreateDefault(context), selected, options, Has("--yes"), Console.Out);
}

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
