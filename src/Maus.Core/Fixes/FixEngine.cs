using Maus.Core.Engine;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Fixes;

/// <summary>
/// Étapes Apply, Verify et Revert, communes à tous les modules. Déroulé d'une séance : garde-fous (PC géré, droits),
/// point de restauration vérifié, puis pour chaque correction : valeurs d'origine journalisées, écriture, relecture.
/// Une correction dont une écriture échoue ou n'est pas relue à l'identique est entièrement défaite.
/// </summary>
public sealed class FixEngine
{
    private readonly FixContext _context;

    public FixEngine(FixContext context)
    {
        _context = context;
    }

    /// <summary>Étape Plan de tous les modules corrigeables, à partir des résultats d'un audit.</summary>
    public static IReadOnlyList<PlannedChange> Plan(AuditEngine engine, IReadOnlyList<ModuleResult> results, AuditContext context)
    {
        var changes = new List<PlannedChange>();
        foreach (var module in engine.Modules.OfType<IFixableModule>())
        {
            var result = results.FirstOrDefault(r => r.ModuleId == module.Id);
            if (result is not null && result.Error is null)
            {
                // Un constat marqué « voulu » ne produit jamais de correction.
                var wanted = result.Findings.Where(f => f.AcknowledgedFrom is not null).Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
                changes.AddRange(module.Plan(context, result.Findings).Where(c => !wanted.Contains(c.FindingId ?? c.Id)));
            }
        }

        return changes;
    }

    /// <summary>Raison qui interdit toute correction sur ce PC, ou <c>null</c>.</summary>
    public string? GetBlockingReason()
    {
        if (_context.Audit.Hardware.IsManaged)
        {
            return T("Ce PC est géré par une organisation (domaine ou MDM) : MAUS n'y applique aucune correction. Adressez-vous à votre service informatique.");
        }

        return _context.Audit.IsElevated
            ? null
            : T("Les corrections demandent les droits administrateur. Relancez MAUS en tant qu'administrateur.");
    }

    public ApplyResult Apply(IReadOnlyList<PlannedChange> changes, ApplyOptions options)
    {
        if (GetBlockingReason() is { } blocked)
        {
            return ApplyResult.Block(blocked);
        }

        if (changes.Count == 0)
        {
            return ApplyResult.Block(T("Aucune correction sélectionnée."));
        }

        RestorePointOutcome? restorePoint = null;
        if (options.CreateRestorePoint)
        {
            restorePoint = new RestorePointCreator(_context.SystemRestore, _context.Settings)
                .Create(options.RestorePointDescription, options.EnableProtectionIfNeeded);
            if (!restorePoint.Succeeded && !options.ProceedWithoutRestorePoint)
            {
                return ApplyResult.Block(restorePoint.Message + T(" Aucune modification n'a été faite."), restorePoint);
            }
        }

        var session = new JournalSession
        {
            Id = JournalSession.NewId(Now),
            CreatedAt = Now,
            WindowsBuild = _context.Audit.Windows.Build,
            RestorePoint = restorePoint?.Point,
            RestorePointNote = restorePoint switch
            {
                null => T("Point de restauration non demandé par l'utilisateur."),
                { Succeeded: false } failed => failed.Message,
                _ => null,
            },
        };

        var outcomes = new List<ChangeOutcome>();
        var written = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var change in changes)
            {
                outcomes.Add(ApplyOne(session, change, written));
            }
        }
        catch (JournalUnsafeException ex)
        {
            // Sans journal sûr, aucune écriture de plus : celles déjà faites sont défaites.
            RollBackSession(session);
            return new ApplyResult(null, outcomes, restorePoint, ex.Message);
        }

        if (outcomes.Any(o => o.Status == ChangeStatus.Applied))
        {
            _context.Notifier.Broadcast();
        }

        return new ApplyResult(session, outcomes, restorePoint);
    }

    /// <summary>Note dans le journal l'effet réel de chaque correction, vu par le nouvel audit.</summary>
    public void RecordVerification(string sessionId, IReadOnlyList<VerifiedOutcome> verified)
    {
        var session = _context.Journal.Load(sessionId);
        if (session is null)
        {
            return;
        }

        foreach (var item in verified.Where(v => v.Check is EffectCheck.NoEffect or EffectCheck.PendingRestart))
        {
            foreach (var entry in session.Entries.Where(e => e.ChangeId == item.Outcome.ChangeId && e.State == EntryState.Applied))
            {
                entry.EffectNote = item.Message;
            }
        }

        _context.Journal.Save(session);
    }

    /// <summary>
    /// Remet les valeurs d'origine d'une séance entière, ou d'une seule de ses corrections (<paramref name="changeId"/>).
    /// </summary>
    public RevertResult Revert(string sessionId, bool force = false, string? changeId = null)
    {
        if (!_context.Audit.IsElevated)
        {
            return new RevertResult(null, [], T("L'annulation demande les droits administrateur. Relancez MAUS en tant qu'administrateur."));
        }

        var session = _context.Journal.Load(sessionId);
        if (session is null)
        {
            return new RevertResult(null, [], T("Séance {0} introuvable dans le journal (ou fichier non fiable, ignoré).", sessionId));
        }

        var outcomes = new List<RevertOutcome>();
        var targets = Enumerable.Reverse(session.Entries)
            .Where(e => e.State is EntryState.Applied or EntryState.Pending && (changeId is null || e.ChangeId == changeId))
            .ToList();
        if (changeId is not null && targets.Count == 0)
        {
            return new RevertResult(session, [], T("La correction {0} n'a rien à annuler dans cette séance.", changeId));
        }

        foreach (var entry in targets)
        {
            outcomes.Add(RevertOne(entry, force));
        }

        if (!session.CanRevert)
        {
            session.RevertedAt = Now;
        }

        _context.Journal.Save(session);
        if (outcomes.Any(o => o.Status == RevertStatus.Reverted))
        {
            _context.Notifier.Broadcast();
        }

        return new RevertResult(session, outcomes);
    }

    private ChangeOutcome ApplyOne(JournalSession session, PlannedChange change, HashSet<string> written)
    {
        ChangeOutcome Outcome(ChangeStatus status, string message) => new(change.Id, change.Title, status, message, change.Effect);

        if (change.IsUserScoped && _context.ElevatedAsAnotherUser)
        {
            return Outcome(ChangeStatus.Skipped,
                T("MAUS a été lancé avec un autre compte administrateur : ce réglage irait dans le mauvais profil. Relancez MAUS depuis votre propre session."));
        }

        if (change.Writes.Any(w => written.Contains(w.Key.Identity)))
        {
            return Outcome(ChangeStatus.Skipped, T("Ce réglage est déjà modifié par une autre correction de cette séance."));
        }

        // Valeurs d'origine, lues juste avant d'écrire.
        var entries = new List<JournalEntry>();
        foreach (var write in change.Writes)
        {
            SettingValue? before;
            try
            {
                before = _context.Settings.Read(write.Key);
            }
            catch (Exception ex) when (IsSettingFailure(ex))
            {
                return Outcome(ChangeStatus.Skipped, T("Valeur d'origine illisible ({0}) : rien n'a été modifié.", write.Key));
            }

            if (!SettingValue.AreEquivalent(before, write.Value))
            {
                entries.Add(new JournalEntry
                {
                    ChangeId = change.Id,
                    ModuleId = change.ModuleId,
                    ChangeTitle = change.Title,
                    Key = write.Key,
                    Before = before,
                    ContainerExisted = _context.Settings.ContainerExists(write.Key),
                    After = write.Value,
                });
            }
        }

        if (entries.Count == 0)
        {
            return Outcome(ChangeStatus.Skipped, T("Déjà en place : rien à modifier."));
        }

        session.Entries.AddRange(entries);
        _context.Journal.Save(session);

        foreach (var entry in entries)
        {
            try
            {
                _context.Settings.Write(entry.Key, entry.After);
                entry.State = EntryState.Applied;
                entry.AppliedAt = Now;
            }
            catch (Exception ex) when (IsSettingFailure(ex))
            {
                entry.Error = ex.Message;
                RollBack(entries);
                _context.Journal.Save(session);
                return Outcome(ChangeStatus.Failed, T("Écriture refusée ({0}) : valeurs d'origine remises.", entry.Key));
            }
        }

        // Verify : relecture de l'état effectif.
        foreach (var entry in entries)
        {
            var actual = TryRead(entry.Key, out var readable);
            if (!readable || !SettingValue.AreEquivalent(actual, entry.After))
            {
                entry.Error = T("Relu : {0}, attendu : {1}", SettingValue.Display(actual), SettingValue.Display(entry.After));
                RollBack(entries);
                _context.Journal.Save(session);
                return Outcome(ChangeStatus.Failed, T("Windows n'a pas gardé la valeur ({0}) : valeurs d'origine remises.", entry.Key));
            }
        }

        foreach (var entry in entries)
        {
            written.Add(entry.Key.Identity);
        }

        _context.Journal.Save(session);
        return Outcome(ChangeStatus.Applied, T("Appliqué et vérifié."));
    }

    private RevertOutcome RevertOne(JournalEntry entry, bool force)
    {
        RevertOutcome Outcome(RevertStatus status, string message) => new(entry.ChangeId, entry.ChangeTitle, entry.Key.Describe(), status, message);

        if (entry.Key.IsUserScoped && _context.ElevatedAsAnotherUser)
        {
            return Outcome(RevertStatus.Skipped, T("MAUS a été lancé avec un autre compte administrateur : relancez-le depuis votre propre session pour annuler ce réglage."));
        }

        var current = TryRead(entry.Key, out var readable);
        if (!readable)
        {
            return Outcome(RevertStatus.Failed, T("Valeur actuelle illisible : rien n'a été modifié."));
        }

        // Écriture interrompue (MAUS fermé, plantage, coupure) : si la valeur d'origine est encore en place, rien n'avait été
        // écrit ; si c'est la nouvelle valeur, elle se remet comme une correction appliquée (même condition, même relecture).
        if (entry.State == EntryState.Pending && !force && !SettingValue.AreEquivalent(current, entry.After) && SettingValue.AreEquivalent(current, entry.Before))
        {
            entry.State = EntryState.Failed;
            entry.Error = T("Correction interrompue avant l'écriture : la valeur d'origine était encore en place.");
            return Outcome(RevertStatus.Skipped, T("Correction interrompue avant l'écriture : rien à annuler."));
        }

        if (!force && !SettingValue.AreEquivalent(current, entry.After))
        {
            entry.State = EntryState.RevertSkipped;
            return Outcome(RevertStatus.ChangedSince,
                T("La valeur a changé depuis la correction ({0}) : laissée telle quelle.", SettingValue.Display(current)));
        }

        try
        {
            Restore(entry);
        }
        catch (Exception ex) when (IsSettingFailure(ex))
        {
            entry.Error = ex.Message;
            return Outcome(RevertStatus.Failed, T("Restauration refusée : {0}", ex.Message));
        }

        var restored = TryRead(entry.Key, out readable);
        if (!readable || !SettingValue.AreEquivalent(restored, entry.Before))
        {
            entry.Error = T("Relu après restauration : {0}", SettingValue.Display(restored));
            return Outcome(RevertStatus.Failed, T("La valeur d'origine n'a pas pu être relue après restauration."));
        }

        entry.State = EntryState.Reverted;
        entry.RevertedAt = Now;
        return Outcome(RevertStatus.Reverted, T("Valeur d'origine remise ({0}).", SettingValue.Display(entry.Before)));
    }

    /// <summary>Heure réelle de l'opération (horloge du contexte), sinon celle de l'audit (tests).</summary>
    private DateTimeOffset Now => _context.Clock?.Invoke() ?? _context.Audit.Now;

    /// <summary>Défait les écritures d'une correction, dans l'ordre inverse.</summary>
    private void RollBack(IEnumerable<JournalEntry> entries)
    {
        foreach (var entry in entries.Reverse())
        {
            if (entry.State == EntryState.Applied || entry.Error is not null)
            {
                try
                {
                    Restore(entry);
                }
                catch (Exception ex) when (IsSettingFailure(ex))
                {
                    entry.Error = (entry.Error is null ? string.Empty : entry.Error + " ; ") + T("retour arrière refusé : ") + ex.Message;
                }
            }

            entry.State = EntryState.Failed;
        }
    }

    private void RollBackSession(JournalSession session) =>
        RollBack(session.Entries.Where(e => e.State == EntryState.Applied).ToList());

    private void Restore(JournalEntry entry)
    {
        _context.Settings.Write(entry.Key, entry.Before);
        if (!entry.ContainerExisted)
        {
            _context.Settings.RemoveContainerIfEmpty(entry.Key);
        }
    }

    /// <summary>
    /// Échec d'une lecture ou d'une écriture de réglage. Toute erreur compte (droits, clé en cours de suppression, valeur
    /// refusée…), sauf le manque de mémoire : aucune ne doit sortir d'une correction sans que ses écritures soient défaites.
    /// </summary>
    private static bool IsSettingFailure(Exception ex) => ex is not OutOfMemoryException;

    private SettingValue? TryRead(SettingKey key, out bool readable)
    {
        try
        {
            readable = true;
            return _context.Settings.Read(key);
        }
        catch (Exception ex) when (IsSettingFailure(ex))
        {
            readable = false;
            return null;
        }
    }
}
