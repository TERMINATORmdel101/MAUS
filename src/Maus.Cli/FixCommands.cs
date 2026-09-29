using System.Globalization;
using Maus.Core;
using Maus.Core.Engine;
using Maus.Core.Fixes;
using Maus.Core.Reporting;
using static Maus.Core.Localization.Texts;

namespace Maus.Cli;

/// <summary>Commandes de corrections (V0.2) : aperçu, application après confirmation, journal, annulation.</summary>
internal static class FixCommands
{
    public static void PrintPlan(IReadOnlyList<PlannedChange> plan, TextWriter output)
    {
        if (plan.Count == 0)
        {
            output.WriteLine(T("Aucune correction à proposer."));
            return;
        }

        foreach (var group in plan.GroupBy(c => c.ModuleId))
        {
            output.WriteLine();
            output.WriteLine($"== {group.Key} ==");
            foreach (var change in group)
            {
                var mark = change.Advanced ? T("[avancé]") : change.Recommended ? T("[recommandé]") : T("[au choix]");
                output.WriteLine($"  {change.Id,-34} {mark} {change.Title}");
                output.WriteLine($"      {change.Description}");
                if (change.Warning is not null)
                {
                    output.WriteLine(T("      ATTENTION : {0}", change.Warning));
                }

                if (change.Risk is not null)
                {
                    output.WriteLine(T("      Risque : {0}", change.Risk));
                }

                foreach (var write in change.Writes)
                {
                    output.WriteLine($"      {write.Key} -> {SettingValue.Display(write.Value)}");
                }

                if (change.Effect != ChangeEffect.Immediate)
                {
                    output.WriteLine($"      ({Labels.Of(change.Effect)})");
                }
            }
        }

        output.WriteLine();
        output.WriteLine(T("Pour appliquer : maus --apply <identifiant> [...]  ou  maus --apply-recommended"));
    }

    public static (int ExitCode, ApplyResult? Result) Apply(FixContext context, IReadOnlyList<PlannedChange> selected, ApplyOptions options, bool assumeYes, TextWriter output)
    {
        var engine = new FixEngine(context);
        if (engine.GetBlockingReason() is { } blocked)
        {
            output.WriteLine(blocked);
            return (3, null);
        }

        if (selected.Count == 0)
        {
            output.WriteLine(T("Aucune correction ne correspond à la sélection (déjà conforme, ou identifiant inconnu : voir maus --plan)."));
            return (1, null);
        }

        output.WriteLine(T("Corrections sélectionnées :"));
        foreach (var change in selected)
        {
            output.WriteLine($"  - {change.Title} ({change.Id})");
        }

        output.WriteLine(options.CreateRestorePoint
            ? T("Un point de restauration sera créé et vérifié avant toute modification.")
            : T("Aucun point de restauration ne sera créé (le journal permettra quand même d'annuler)."));
        output.WriteLine(Maus.Core.Legal.Disclaimer.ChangeReminder);

        if (!assumeYes && !Confirm(T("Appliquer ces corrections ? (o/N) ")))
        {
            output.WriteLine(T("Annulé : rien n'a été modifié."));
            return (1, null);
        }

        var result = engine.Apply(selected, options);
        if (result.RestorePoint is { } point)
        {
            output.WriteLine(point.Message);
        }

        if (result.Blocked)
        {
            output.WriteLine(result.BlockedReason);
            if (result.RestorePoint?.Status == RestorePointStatus.ProtectionDisabled)
            {
                output.WriteLine(T("Relancez avec --enable-protection pour activer la protection du système, ou --without-restore-point pour continuer sans."));
            }

            return (3, result);
        }

        foreach (var outcome in result.Changes)
        {
            output.WriteLine($"  [{Labels.Of(outcome.Status)}] {outcome.Title} : {outcome.Message}");
        }

        if (result.RequiredEffect != ChangeEffect.Immediate)
        {
            output.WriteLine(T("Certaines corrections ont un {0}.", Labels.Of(result.RequiredEffect)));
        }

        if (result.Session is { CanRevert: true } session)
        {
            output.WriteLine(T("Séance enregistrée : {0}. Pour tout annuler : maus --revert {0}", session.Id));
        }

        return (result.Changes.Any(c => c.Status == ChangeStatus.Failed) ? 2 : 0, result);
    }

    public static void PrintVerification(IReadOnlyList<VerifiedOutcome> verified, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine(T("Vérification par un nouvel audit :"));
        foreach (var item in verified.Where(v => v.Outcome.Status == ChangeStatus.Applied))
        {
            var mark = item.Check switch
            {
                EffectCheck.Confirmed => T("confirmé"),
                EffectCheck.PendingRestart => T("en attente"),
                EffectCheck.NoEffect => "SANS EFFET",
                _ => T("non vérifié"),
            };
            output.WriteLine($"  [{mark}] {item.Outcome.Title} : {item.Message}");
        }
    }

    public static int PrintJournal(IJournalStore journal, TextWriter output)
    {
        var sessions = journal.List();
        if (sessions.Count == 0)
        {
            output.WriteLine(T("Journal vide : MAUS n'a encore rien modifié sur ce PC."));
            return 0;
        }

        foreach (var session in sessions)
        {
            var state = session.RevertedAt is not null ? T("annulée") : session.CanRevert ? T("active") : T("sans modification en cours");
            output.WriteLine($"{session.Id}  {session.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}  {state}  ({session.Entries.Count(e => e.State is EntryState.Applied or EntryState.Reverted or EntryState.RevertSkipped)} valeur(s))");
            foreach (var entry in session.Entries)
            {
                output.WriteLine($"    [{entry.State}] {entry.ChangeId} · {entry.ChangeTitle} : {entry.Key} {SettingValue.Display(entry.Before)} -> {SettingValue.Display(entry.After)}");
                if (entry.EffectNote is not null)
                {
                    output.WriteLine($"        {entry.EffectNote}");
                }
            }
        }

        return 0;
    }

    public static int Revert(FixContext context, string sessionId, string? changeId, bool force, bool assumeYes, TextWriter output)
    {
        var what = changeId is null ? T("de la séance {0}", sessionId) : T("de la correction {0} (séance {1})", changeId, sessionId);
        if (!assumeYes && !Confirm(T("Remettre les valeurs d'origine {0} ? (o/N) ", what)))
        {
            output.WriteLine(T("Annulé : rien n'a été modifié."));
            return 1;
        }

        var result = new FixEngine(context).Revert(sessionId, force, changeId);
        if (result.Error is not null)
        {
            output.WriteLine(result.Error);
            return 3;
        }

        foreach (var entry in result.Entries)
        {
            output.WriteLine($"  [{Labels.Of(entry.Status)}] {entry.Title} ({entry.Setting}) : {entry.Message}");
        }

        if (result.Entries.Any(e => e.Status == RevertStatus.ChangedSince))
        {
            output.WriteLine(T("Des valeurs ont changé depuis la correction et ont été laissées telles quelles. --force les remet quand même à leur valeur d'origine."));
        }

        return result.Completed ? 0 : 2;
    }

    public static IReadOnlyList<PlannedChange> Plan(AuditEngine engine, IReadOnlyList<ModuleResult> results, AuditContext context) =>
        FixEngine.Plan(engine, results, context);

    private static readonly HashSet<string> YesAnswers = new(["o", "oui", "y", "yes", "s", "si", "sí"], StringComparer.OrdinalIgnoreCase);

    public static bool Confirm(string question)
    {
        Console.Write(question);
        var answer = Console.ReadLine()?.Trim();
        // Oui en français, anglais ou espagnol, quelle que soit la langue affichée.
        return answer is not null && YesAnswers.Contains(answer);
    }
}
