using Maus.Core;
using Maus.Core.Fixes;
using Maus.Core.Reporting;
using static Maus.Core.Localization.Texts;

namespace Maus.Cli;

/// <summary>Commandes de corrections (V0.2) : aperçu, application après confirmation, journal, annulation.</summary>
internal static class FixCommands
{
    /// <param name="privacy">Masque les textes affichés, jamais l'identifiant : <c>--apply</c> l'attend tel quel. <c>null</c> : rien n'est masqué.</param>
    public static void PrintPlan(IReadOnlyList<PlannedChange> plan, TextWriter output, PrivacyFilter? privacy = null)
    {
        if (plan.Count == 0)
        {
            output.WriteLine(T("Aucune correction à proposer."));
            return;
        }

        string Shown(string text) => privacy?.Mask(text) ?? text;

        var titles = DistinctTitles(plan);
        foreach (var group in plan.GroupBy(c => c.ModuleId))
        {
            output.WriteLine();
            output.WriteLine($"== {group.Key} ==");
            foreach (var change in group)
            {
                var mark = change.Advanced ? T("[avancé]") : change.Recommended ? T("[recommandé]") : T("[au choix]");
                output.WriteLine($"  {change.Id,-34} {mark} {Shown(titles[change.Id])}");
                output.WriteLine($"      {Shown(change.Description)}");
                if (!string.IsNullOrEmpty(change.Gain))
                {
                    output.WriteLine("      " + T("Gain : {0}", Shown(change.Gain)));
                }

                if (change.Warning is not null)
                {
                    output.WriteLine(T("      ATTENTION : {0}", Shown(change.Warning)));
                }

                if (change.Risk is not null)
                {
                    output.WriteLine(T("      Risque : {0}", Shown(change.Risk)));
                }

                foreach (var write in change.Writes)
                {
                    output.WriteLine(Shown($"      {write.Key} -> {SettingValue.Display(write.Value)}"));
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

    /// <summary>
    /// Le titre de chaque correction, par identifiant. Deux corrections peuvent porter le même titre (deux réglages d'un même conseil, deux
    /// entrées d'un même logiciel) : on y ajoute le nom du réglage écrit, ou à défaut l'identifiant, pour voir ce qui les sépare.
    /// </summary>
    private static Dictionary<string, string> DistinctTitles(IReadOnlyList<PlannedChange> plan)
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var same in plan.GroupBy(c => c.Title, StringComparer.OrdinalIgnoreCase))
        {
            var names = same.Select(c => c.Writes.Select(w => w.Key.Name).FirstOrDefault(n => !string.IsNullOrEmpty(n))).ToList();
            var namesDiffer = names.All(n => n is not null) && names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Count;
            foreach (var (change, name) in same.Zip(names))
            {
                titles[change.Id] = names.Count == 1 ? change.Title : $"{change.Title} ({(namesDiffer ? name : change.Id)})";
            }
        }

        return titles;
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
            if (change.Warning is not null)
            {
                output.WriteLine(T("      ATTENTION : {0}", change.Warning));
            }
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
            else if (result.RestorePoint is { Succeeded: false })
            {
                output.WriteLine(T("Relancez avec --without-restore-point pour continuer sans point de restauration (déconseillé)."));
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

        // « Déjà en place » n'est pas une erreur pour un script ; une correction refusée ou qui a échoué, si.
        return (result.Changes.Any(c => c.Status == ChangeStatus.Failed || c.NotAllowed) ? 2 : 0, result);
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
                EffectCheck.NoEffect => T("SANS EFFET"),
                _ => T("non vérifié"),
            };
            output.WriteLine($"  [{mark}] {item.Outcome.Title} : {item.Message}");
        }
    }

    /// <param name="accountSid">Compte de cette session Windows : le journal est commun à tout le PC.</param>
    public static int PrintJournal(IJournalStore journal, TextWriter output, string? accountSid = null)
    {
        var listing = journal.Browse();
        var sessions = listing.Sessions;
        if (listing.UnreadableNotice is { } notice)
        {
            output.WriteLine(notice);
        }

        if (sessions.Count == 0 && listing.UnreadableNotice is null)
        {
            output.WriteLine(T("Journal vide : MAUS n'a encore rien modifié sur ce PC."));
            return 0;
        }

        foreach (var session in sessions)
        {
            var values = session.Entries.Count(e => e.State is EntryState.Applied or EntryState.Reverted or EntryState.RevertSkipped);
            output.WriteLine(T("{0}  {1:g}  {2}  ({3} valeur(s))", session.Id, session.CreatedAt.ToLocalTime(), session.StateLabel, values));
            if (session.IsFromAnotherAccount(accountSid))
            {
                output.WriteLine("    " + T("faite par un autre compte"));
            }

            foreach (var entry in session.Entries)
            {
                output.WriteLine($"    [{Labels.Of(entry.State)}] {entry.ChangeId} · {entry.ChangeTitle} : {entry.Key} {SettingValue.Display(entry.Before)} -> {SettingValue.Display(entry.After)}");
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
        // Avant la question : répondre « oui » pour apprendre ensuite que c'est impossible ne sert à rien.
        if (!context.Audit.IsElevated)
        {
            output.WriteLine(T("L'annulation demande les droits administrateur. Relancez MAUS en tant qu'administrateur."));
            return 3;
        }

        JournalSession? session;
        try
        {
            session = context.Journal.Load(sessionId);
        }
        catch (JournalUnsafeException ex)
        {
            output.WriteLine(ex.Message);
            return 3;
        }

        // Dit aussi avec --yes : le journal est commun au PC, cette séance peut être celle d'un autre compte.
        if (session?.IsFromAnotherAccount(context.UserSid) == true)
        {
            output.WriteLine(JournalSession.AnotherAccountNotice);
        }

        // Dit aussi avec --yes : la valeur qui revient est écrite dans la sortie de l'outil.
        if (session is not null && SensitiveSettings.Pending(session, changeId) is { Count: > 0 } sensitive)
        {
            output.WriteLine(SensitiveSettings.Question(sensitive));
        }

        var what = changeId is null ? T("de la séance {0}", sessionId) : T("de la correction {0} (séance {1})", changeId, sessionId);
        if (!assumeYes && !Confirm(T("Remettre les valeurs d'origine {0} ? (o/N) ", what)))
        {
            output.WriteLine(T("Annulé : rien n'a été modifié."));
            return 1;
        }

        var result = new FixEngine(context).Revert(sessionId, force, changeId);
        if (result.Error is null && result.Entries.Count == 0)
        {
            output.WriteLine(T("Rien à annuler dans cette séance."));
        }

        foreach (var entry in result.Entries)
        {
            output.WriteLine($"  [{Labels.Of(entry.Status)}] {entry.Title} ({entry.Setting}) : {entry.Message}");
        }

        if (result.Entries.Any(e => e.Status == RevertStatus.ChangedSince))
        {
            output.WriteLine(T("Des valeurs ont changé depuis la correction et ont été laissées telles quelles. --force les remet quand même à leur valeur d'origine."));
        }

        if (result.Error is not null)
        {
            output.WriteLine(result.Error);
            return 3;
        }

        return result.Completed ? 0 : 2;
    }

    private static readonly HashSet<string> YesAnswers = new(["o", "oui", "y", "yes", "s", "si", "sí"], StringComparer.OrdinalIgnoreCase);

    public static bool Confirm(string question)
    {
        Console.Write(question);
        var answer = Console.ReadLine()?.Trim();
        // Oui en français, anglais ou espagnol, quelle que soit la langue affichée.
        return answer is not null && YesAnswers.Contains(answer);
    }
}
