using System.Text;
using Maus.Cli;
using Maus.Core;
using Maus.Core.Engine;
using Maus.Core.Fixes;
using Maus.Core.Platform;
using Maus.Core.Preferences;
using Maus.Core.Reporting;
using static Maus.Core.Localization.Texts;

// MAUS en ligne de commande : audit en lecture seule, utile pour tester et comparer avant/après.
//   maus                  audit complet, rapport texte
//   maus --json           audit complet, rapport JSON
//   maus --html FICHIER   audit complet, rapport HTML (avant/après si des corrections sont appliquées)
//   maus --module M06     un seul module (répétable)
//   maus --summary        résumé court à coller sur un forum pour demander de l'aide (sans données personnelles)
//   maus --lang en        langue des textes : fr, en ou es
// Corrections (V0.2, droits administrateur requis pour appliquer ou annuler) :
//   maus --plan                       corrections proposées, sans rien modifier
//   maus --apply ID [ID...]           applique les corrections choisies, après confirmation, puis vérifie par un nouvel audit
//   maus --apply-recommended          applique les corrections recommandées (pré-cochées)
//   maus --journal                    séances de corrections enregistrées
//   maus --revert SEANCE [--change ID] [--force]   remet les valeurs d'origine d'une séance, ou d'une seule correction
//   maus --restart-explorer           redémarre l'Explorateur (corrections de la barre des tâches)
// Choix de l'utilisateur (droits administrateur) :
//   maus --ack ID / --unack ID        marque un constat « voulu » (ou retire la marque)
//   maus --set gamebar=1|2|3|auto     profil Game Bar
//   maus --set alimentation=performance|partout|autonomie|auto   choix du portable
//   maus --prefs                      affiche vos choix
Console.OutputEncoding = Encoding.UTF8;

bool Has(string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);

string? ValueOf(string flag)
{
    var index = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) ? args[index + 1] : null;
}

// Langue : --lang fr|en|es, sinon celle choisie dans l'application, sinon celle de Windows.
var preferencesStore = FilePreferencesStore.CreateDefault();
Maus.Core.Localization.Texts.Use(ValueOf("--lang") ?? preferencesStore.Load().Language);

var json = Has("--json");
var only = args
    .Select((arg, index) => (arg, index))
    .Where(x => x.arg.Equals("--module", StringComparison.OrdinalIgnoreCase) && x.index + 1 < args.Length)
    .Select(x => args[x.index + 1])
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

if (Has("--version"))
{
    Console.WriteLine("MAUS " + Maus.Core.AppVersion.Display);
    return 0;
}

if (args.Any(a => a is "-h" or "--help" or "/?"))
{
    Console.WriteLine(T("Usage : maus [--json | --html FICHIER] [--module Mxx]..."));
    Console.WriteLine(T("Audit en lecture seule : MAUS ne modifie rien sur ce PC."));
    Console.WriteLine(T("  --lang fr|en|es                         langue des textes (par défaut : celle de l'application, sinon de Windows)"));
    Console.WriteLine(T("  --summary                               résumé à coller sur un forum pour demander de l'aide (sans données personnelles)"));
    Console.WriteLine(T("  --version                               version de MAUS"));
    Console.WriteLine();
    Console.WriteLine(T("Corrections (droits administrateur requis pour appliquer ou annuler) :"));
    Console.WriteLine(T("  maus --plan [--module Mxx]              corrections proposées, sans rien modifier"));
    Console.WriteLine(T("  maus --apply ID [ID...]                 applique les corrections choisies, après confirmation"));
    Console.WriteLine(T("  maus --apply-recommended [--module Mxx] applique les corrections recommandées"));
    Console.WriteLine(T("       [--without-restore-point] [--enable-protection] [--yes] [--html FICHIER]"));
    Console.WriteLine(T("  maus --journal                          séances de corrections enregistrées"));
    Console.WriteLine(T("  maus --revert SEANCE [--change ID] [--force] [--yes]"));
    Console.WriteLine(T("                                          remet les valeurs d'origine (séance entière ou une correction)"));
    Console.WriteLine(T("  maus --restart-explorer                 redémarre l'Explorateur"));
    Console.WriteLine();
    Console.WriteLine(T("Vos choix (droits administrateur) :"));
    Console.WriteLine(T("  maus --ack ID | --unack ID              marque un constat « voulu » (ou retire la marque)"));
    Console.WriteLine(T("  maus --set gamebar=1|2|3|auto           profil Game Bar"));
    Console.WriteLine(T("  maus --set alimentation=performance|partout|autonomie|auto"));
    Console.WriteLine(T("  maus --prefs                            affiche vos choix"));
    return 0;
}


if (Has("--prefs"))
{
    return PreferenceCommands.Print(preferencesStore.Load(), Console.Out);
}

if (Has("--set"))
{
    return PreferenceCommands.Set(preferencesStore, ValueOf("--set"), Console.Out);
}

if (Has("--unack"))
{
    return PreferenceCommands.Unacknowledge(preferencesStore, ValueOf("--unack"), Console.Out);
}

if (Has("--journal"))
{
    return FixCommands.PrintJournal(FileJournalStore.CreateDefault(), Console.Out);
}

if (Has("--revert"))
{
    if (ValueOf("--revert") is not { } sessionId)
    {
        Console.WriteLine(T("Indiquez la séance à annuler : maus --revert SEANCE (voir maus --journal)."));
        return 1;
    }

    var revertContext = FixContext.CreateDefault(AuditContext.CreateDefault());
    return FixCommands.Revert(revertContext, sessionId, ValueOf("--change"), Has("--force"), Has("--yes"), Console.Out);
}

if (Has("--restart-explorer"))
{
    Console.WriteLine(ExplorerRestart.Warning);
    Console.WriteLine(Maus.Core.Legal.Disclaimer.OperationReminder);
    if (!Has("--yes") && !FixCommands.Confirm(T("Redémarrer l'Explorateur ? (o/N) ")))
    {
        return 1;
    }

    var restart = await new ExplorerRestart(new WindowsShellProcesses(new WindowsRegistryReader())).RunAsync();
    Console.WriteLine(restart.Message);
    return restart.Succeeded ? 0 : 2;
}

var applyIds = args
    .SkipWhile(a => !a.Equals("--apply", StringComparison.OrdinalIgnoreCase))
    .Skip(1)
    .TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal))
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
var fixMode = Has("--plan") || Has("--apply") || Has("--apply-recommended");
var htmlPath = ValueOf("--html");

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

if (Has("--ack"))
{
    var ackResults = await engine.RunAsync(context, cancellationToken: cancellation.Token);
    return PreferenceCommands.Acknowledge(preferencesStore, context, ackResults, ValueOf("--ack"), Has("--yes"), Console.Out);
}

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
    var fixContext = FixContext.CreateDefault(context);
    var (exitCode, applied) = FixCommands.Apply(fixContext, selected, options, Has("--yes"), Console.Out);
    if (applied is { Blocked: false, Session: { } session })
    {
        // Verify, second niveau : un nouvel audit relit l'état effectif.
        var afterContext = AuditContext.CreateDefault();
        var after = await engine.RunAsync(afterContext, cancellationToken: cancellation.Token);
        var verified = FixVerification.CompareWithAudit(selected, applied, after, afterContext.Windows);
        FixCommands.PrintVerification(verified, Console.Out);
        new FixEngine(fixContext).RecordVerification(session.Id, verified);

        if (htmlPath is not null)
        {
            WriteHtml(htmlPath, new HtmlReportInput
            {
                Before = AuditReport.Create(context, audit),
                After = AuditReport.Create(afterContext, after),
                Session = fixContext.Journal.Load(session.Id) ?? session,
                Outcomes = verified,
            });
        }
    }

    return exitCode;
}

if (Has("--summary"))
{
    var summaryResults = await engine.RunAsync(context, cancellationToken: cancellation.Token);
    Console.Write(HelpSummary.Build(AuditReport.Create(context, summaryResults), PrivacyFilter.ForCurrentUser()));
    return 0;
}

var progress = json ? null : new Progress<ModuleResult>(r => Console.Error.WriteLine(T("  … {0} terminé ({1:0.0} s)", r.ModuleId, r.Duration.TotalSeconds)));
var results = await engine.RunAsync(context, progress, cancellationToken: cancellation.Token);
var report = AuditReport.Create(context, results);

if (json)
{
    Console.WriteLine(report.ToJson());
}
else if (htmlPath is not null)
{
    WriteHtml(htmlPath, new HtmlReportInput { After = report });
}
else
{
    TextReport.Write(report, Console.Out, useColor: !Console.IsOutputRedirected);
}

return results.Any(r => r.WorstStatus == FindingStatus.Problem) ? 2 : 0;

static void WriteHtml(string path, HtmlReportInput input)
{
    File.WriteAllText(path, HtmlReport.Build(input), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    Console.WriteLine(T("Rapport HTML enregistré : {0}", Path.GetFullPath(path)));
}
