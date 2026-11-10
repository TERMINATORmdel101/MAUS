using System.Text;
using Maus.Cli;
using Maus.Core;
using Maus.Core.Engine;
using Maus.Core.Fixes;
using Maus.Core.Platform;
using Maus.Core.Preferences;
using Maus.Core.Reporting;
using static Maus.Core.Localization.Texts;

// MAUS en ligne de commande : audit, corrections et choix ; la liste des options est dans --help.
Console.OutputEncoding = Encoding.UTF8;

// Filet : une erreur imprévue donne une phrase et un code, pas une pile d'appels.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    if (e.ExceptionObject is OperationCanceledException)
    {
        Console.Error.WriteLine(T("Audit arrêté."));
        Environment.Exit(130);
    }

    Console.Error.WriteLine(T("Erreur inattendue : {0}", (e.ExceptionObject as Exception)?.Message ?? string.Empty));
    Environment.Exit(1);
};

// Mêmes protections de Windows que la fenêtre et le moteur : en administrateur, cet outil écrit lui-même les corrections.
if (Environment.IsPrivilegedProcess)
{
    SystemPaths.ResetEnvironment();
}

_ = ProcessElevation.IsElevated() ? ProcessHardening.HardenEngine() : ProcessHardening.HardenInterface();

// Benchmark visuel (essais) : même code que MAUS.exe --benchmark, sans l'interface WPF.
if (args.Length > 0 && args[0].Equals("--benchmark", StringComparison.OrdinalIgnoreCase))
{
    return Maus.Bench.BenchmarkProgram.Run(args[1..]);
}

var arguments = CliArguments.Parse(args);
bool Has(string flag) => arguments.Has(flag);
string? ValueOf(string flag) => arguments.Value(flag);

// Langue : --lang fr|en|es, sinon celle choisie dans l'application (fichier de l'utilisateur, puis fichier protégé), sinon celle de Windows.
var preferencesStore = FilePreferencesStore.CreateDefault();
Maus.Core.Localization.Texts.UseWithCulture(arguments.Language
    ?? new FilePreferencesStore(FilePreferencesStore.UserDirectory, new UserDirectoryProtector()).Load().Language
    ?? preferencesStore.Load().Language);

var json = Has("--json");

// Posé tôt : une annulation en cours (--revert) va au bout, Ctrl+C ne la coupe pas au milieu.
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
var only = arguments.Values("--module").ToHashSet(StringComparer.OrdinalIgnoreCase);

if (Has("--version"))
{
    Console.WriteLine("MAUS " + Maus.Core.AppVersion.Full);
    return 0;
}

if (Has("--help") || Has("-h") || Has("/?"))
{
    CliHelp.Write(AuditEngine.CreateWithBuiltInModules().Modules, Console.Out);
    return 0;
}

// Après --help et --version, avant tout audit : le message est écrit dans la langue choisie. Les refus vont sur la sortie
// d'erreur : « maus --json > audit.json » ne doit jamais donner un fichier qui contient une phrase.
if (arguments.Error is { } refused)
{
    Console.Error.WriteLine(refused);
    return 1;
}

if (Has("--prefs"))
{
    if (!ProcessElevation.IsElevated())
    {
        Console.WriteLine(T("Sans droits administrateur, les choix protégés ne sont pas lisibles : ils s'affichent ici avec leur valeur par défaut."));
    }

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
    if (!Maus.Core.Platform.ProcessElevation.IsElevated())
    {
        Console.Error.WriteLine(T("Lecture du journal réservée à l'administrateur : lancez cette commande avec les droits administrateur."));
        return 3;
    }

    return FixCommands.PrintJournal(FileJournalStore.CreateDefault(), Console.Out, Maus.Core.Platform.SessionUser.Sid());
}

if (Has("--revert"))
{
    var revertContext = FixContext.CreateDefault(AuditContext.CreateDefault());
    return FixCommands.Revert(revertContext, ValueOf("--revert")!, ValueOf("--change"), Has("--force"), Has("--yes"), Console.Out);
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

var applyIds = arguments.Values("--apply").ToHashSet(StringComparer.OrdinalIgnoreCase);
var fixMode = Has("--plan") || Has("--apply") || Has("--apply-recommended");
var htmlPath = ValueOf("--html");

var engine = AuditEngine.CreateWithBuiltInModules();
if (only.Count > 0)
{
    var unknown = only.Except(engine.Modules.Select(m => m.Id), StringComparer.OrdinalIgnoreCase).ToList();
    if (unknown.Count > 0)
    {
        Console.Error.WriteLine(T("Module inconnu : {0}. Modules disponibles : {1}.", string.Join(", ", unknown), string.Join(", ", engine.Modules.Select(m => m.Id))));
        return 1;
    }

    // Un résumé ou un rapport HTML annonce un score de santé : les autres modules y restent, comptés comme non choisis, pour
    // que ce score se dise partiel.
    if (fixMode || (!Has("--summary") && htmlPath is null))
    {
        engine = new AuditEngine(engine.Modules.Where(m => only.Contains(m.Id)));
    }
}

// Un rapport impossible à écrire se dit tout de suite : ni audit perdu, ni correction appliquée pour rien.
if (htmlPath is not null && HtmlReportFile.Problem(htmlPath) is { } htmlProblem)
{
    Console.Error.WriteLine(T("Rapport HTML non enregistré : {0}", htmlProblem));
    return 3;
}

var context = AuditContext.CreateDefault();

// Module décoché dans la fenêtre : ni constats ni corrections ici non plus, sauf s'il est demandé par --module.
IReadOnlyCollection<string> skipped = only.Count > 0
    ? [.. engine.Modules.Select(m => m.Id).Where(id => !only.Contains(id))]
    : context.Preferences.SkippedModules;
using var progress = json ? null : new ConsoleProgress(engine.Modules.Select(m => m.Id).Except(skipped, StringComparer.OrdinalIgnoreCase), Console.Error, ConsoleProgress.DefaultPatience);
Task<IReadOnlyList<ModuleResult>> Audit(AuditContext auditContext) =>
    engine.RunAsync(auditContext, progress, skip: skipped, cancellationToken: cancellation.Token);

// Refus avant l'audit, qui peut durer plusieurs minutes.
if ((Has("--apply") || Has("--apply-recommended")) && new FixEngine(FixContext.CreateDefault(context)).GetBlockingReason() is { } blockedNow)
{
    Console.Error.WriteLine(blockedNow);
    return 3;
}

if (Has("--ack") && !context.IsElevated)
{
    Console.Error.WriteLine(T("Marquer un constat demande les droits administrateur. Relancez cette commande en tant qu'administrateur."));
    return 3;
}

if (Has("--ack"))
{
    var ackResults = await Audit(context);
    return PreferenceCommands.Acknowledge(preferencesStore, context, ackResults, ValueOf("--ack"), Has("--yes"), Console.Out);
}

if (fixMode)
{
    var audit = await Audit(context);
    var plan = FixEngine.Plan(engine, audit, context);
    if (Has("--plan"))
    {
        if (!context.IsElevated)
        {
            Console.WriteLine(T("Sans droits administrateur : certains contrôles restent indéterminés."));
        }

        // Une sortie qu'on colle volontiers : masquée comme le HTML et le résumé. Les identifiants restent entiers, --apply les attend tels quels.
        FixCommands.PrintPlan(plan, Console.Out, PrivacyFilter.ForCurrentUser());
        return 0;
    }

    var selected = Has("--apply-recommended")
        ? plan.Where(FixProfile.Recommended.Selects).ToList()
        : plan.Where(c => applyIds.Contains(c.Id)).ToList();
    foreach (var ignored in applyIds.Except(plan.Select(c => c.Id), StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine(T("Ignoré : {0} (déjà conforme, ou identifiant inconnu).", ignored));
    }

    if (selected.Count == 0 && Has("--apply-recommended"))
    {
        Console.WriteLine(T("Rien à appliquer : aucune correction recommandée sur ce PC."));
        return 0;
    }

    var options = new ApplyOptions
    {
        CreateRestorePoint = !Has("--without-restore-point"),
        EnableProtectionIfNeeded = Has("--enable-protection"),
    };
    var fixContext = FixContext.CreateDefault(context);
    var (exitCode, applied) = FixCommands.Apply(fixContext, selected, options, Has("--yes"), Console.Out);
    var beforeAfterSaved = true;
    if (applied is { Blocked: false, Session: { } session })
    {
        // Verify, second niveau : un nouvel audit relit l'état effectif. Rien d'appliqué : rien à relire, le rapport reprend l'audit déjà fait.
        var anyApplied = applied.AppliedCount > 0;
        var afterContext = anyApplied ? AuditContext.CreateDefault() : context;
        var after = anyApplied ? await Audit(afterContext) : audit;
        var verified = FixVerification.CompareWithAudit(selected, applied, after, afterContext.Windows);
        if (anyApplied)
        {
            FixCommands.PrintVerification(verified, Console.Out);
            new FixEngine(fixContext).RecordVerification(session.Id, verified);
        }

        if (htmlPath is not null)
        {
            beforeAfterSaved = HtmlReportFile.Write(htmlPath, new HtmlReportInput
            {
                Before = AuditReport.Create(context, audit),
                After = AuditReport.Create(afterContext, after),
                Session = fixContext.Journal.Load(session.Id) ?? session,
                Outcomes = verified,
            }, Console.Out, Console.Error);
        }
    }

    // Une correction qui a échoué compte plus qu'un rapport manquant : son code reste.
    return exitCode == 0 && !beforeAfterSaved ? 3 : exitCode;
}

if (Has("--summary"))
{
    var summaryResults = await Audit(context);
    Console.Write(HelpSummary.Build(AuditReport.Create(context, summaryResults), PrivacyFilter.ForCurrentUser()));
    return AuditExit.Of(summaryResults);
}

var results = await Audit(context);
var report = AuditReport.Create(context, results);
var reportSaved = true;

if (json)
{
    Console.WriteLine(report.ToJson(PrivacyFilter.ForCurrentUser()));
}
else if (htmlPath is not null)
{
    reportSaved = HtmlReportFile.Write(htmlPath, new HtmlReportInput { After = report }, Console.Out, Console.Error);
}
else
{
    TextReport.Write(report, Console.Out, useColor: !Console.IsOutputRedirected);
}

return reportSaved ? AuditExit.Of(results) : 3;
