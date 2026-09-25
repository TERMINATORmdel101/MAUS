using Maus.Core.Engine;
using Maus.Core.Platform;

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
            return "Ce PC est géré par une organisation (domaine ou MDM) : MAUS n'y applique aucune correction. Adressez-vous à votre service informatique.";
        }

        return _context.Audit.IsElevated
            ? null
            : "Les corrections demandent les droits administrateur. Relancez MAUS en tant qu'administrateur.";
    }

    public ApplyResult Apply(IReadOnlyList<PlannedChange> changes, ApplyOptions options)
    {
        if (GetBlockingReason() is { } blocked)
        {
            return ApplyResult.Block(blocked);
        }

        if (changes.Count == 0)
        {
            return ApplyResult.Block("Aucune correction sélectionnée.");
        }

        RestorePointOutcome? restorePoint = null;
        if (options.CreateRestorePoint)
        {
            restorePoint = new RestorePointCreator(_context.SystemRestore, _context.Settings)
                .Create(options.RestorePointDescription, options.EnableProtectionIfNeeded);
            if (!restorePoint.Succeeded && !options.ProceedWithoutRestorePoint)
            {
                return ApplyResult.Block(restorePoint.Message + " Aucune modification n'a été faite.", restorePoint);
            }
        }

        var session = new JournalSession
        {
            Id = JournalSession.NewId(_context.Audit.Now),
            CreatedAt = _context.Audit.Now,
            WindowsBuild = _context.Audit.Windows.Build,
            RestorePoint = restorePoint?.Point,
            RestorePointNote = restorePoint switch
            {
                null => "Point de restauration non demandé par l'utilisateur.",
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
            return new RevertResult(null, [], "L'annulation demande les droits administrateur. Relancez MAUS en tant qu'administrateur.");
        }

        var session = _context.Journal.Load(sessionId);
        if (session is null)
        {
            return new RevertResult(null, [], $"Séance {sessionId} introuvable dans le journal (ou fichier non fiable, ignoré).");
        }

        var outcomes = new List<RevertOutcome>();
        var targets = Enumerable.Reverse(session.Entries)
            .Where(e => e.State == EntryState.Applied && (changeId is null || e.ChangeId == changeId))
            .ToList();
        if (changeId is not null && targets.Count == 0)
        {
            return new RevertResult(session, [], $"La correction {changeId} n'a rien à annuler dans cette séance.");
        }

        foreach (var entry in targets)
        {
            outcomes.Add(RevertOne(entry, force));
        }

        if (!session.CanRevert)
        {
            session.RevertedAt = _context.Audit.Now;
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
                "MAUS a été lancé avec un autre compte administrateur : ce réglage irait dans le mauvais profil. Relancez MAUS depuis votre propre session.");
        }

        if (change.Writes.Any(w => written.Contains(w.Key.Identity)))
        {
            return Outcome(ChangeStatus.Skipped, "Ce réglage est déjà modifié par une autre correction de cette séance.");
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
            catch (Exception ex) when (ex is MausAccessDeniedException or InvalidOperationException)
            {
                return Outcome(ChangeStatus.Skipped, $"Valeur d'origine illisible ({write.Key}) : rien n'a été modifié.");
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
            return Outcome(ChangeStatus.Skipped, "Déjà en place : rien à modifier.");
        }

        session.Entries.AddRange(entries);
        _context.Journal.Save(session);

        foreach (var entry in entries)
        {
            try
            {
                _context.Settings.Write(entry.Key, entry.After);
                entry.State = EntryState.Applied;
                entry.AppliedAt = _context.Audit.Now;
            }
            catch (Exception ex) when (ex is MausAccessDeniedException or InvalidOperationException or UnauthorizedAccessException)
            {
                entry.Error = ex.Message;
                RollBack(entries);
                _context.Journal.Save(session);
                return Outcome(ChangeStatus.Failed, $"Écriture refusée ({entry.Key}) : valeurs d'origine remises.");
            }
        }

        // Verify : relecture de l'état effectif.
        foreach (var entry in entries)
        {
            var actual = TryRead(entry.Key, out var readable);
            if (!readable || !SettingValue.AreEquivalent(actual, entry.After))
            {
                entry.Error = $"Relu : {SettingValue.Display(actual)}, attendu : {SettingValue.Display(entry.After)}";
                RollBack(entries);
                _context.Journal.Save(session);
                return Outcome(ChangeStatus.Failed, $"Windows n'a pas gardé la valeur ({entry.Key}) : valeurs d'origine remises.");
            }
        }

        foreach (var entry in entries)
        {
            written.Add(entry.Key.Identity);
        }

        _context.Journal.Save(session);
        return Outcome(ChangeStatus.Applied, "Appliqué et vérifié.");
    }

    private RevertOutcome RevertOne(JournalEntry entry, bool force)
    {
        RevertOutcome Outcome(RevertStatus status, string message) => new(entry.ChangeId, entry.ChangeTitle, entry.Key.Describe(), status, message);

        if (entry.Key.IsUserScoped && _context.ElevatedAsAnotherUser)
        {
            return Outcome(RevertStatus.Skipped, "MAUS a été lancé avec un autre compte administrateur : relancez-le depuis votre propre session pour annuler ce réglage.");
        }

        var current = TryRead(entry.Key, out var readable);
        if (!readable)
        {
            return Outcome(RevertStatus.Failed, "Valeur actuelle illisible : rien n'a été modifié.");
        }

        if (!force && !SettingValue.AreEquivalent(current, entry.After))
        {
            entry.State = EntryState.RevertSkipped;
            return Outcome(RevertStatus.ChangedSince,
                $"La valeur a changé depuis la correction ({SettingValue.Display(current)}) : laissée telle quelle.");
        }

        try
        {
            Restore(entry);
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or InvalidOperationException or UnauthorizedAccessException)
        {
            entry.Error = ex.Message;
            return Outcome(RevertStatus.Failed, $"Restauration refusée : {ex.Message}");
        }

        var restored = TryRead(entry.Key, out readable);
        if (!readable || !SettingValue.AreEquivalent(restored, entry.Before))
        {
            entry.Error = $"Relu après restauration : {SettingValue.Display(restored)}";
            return Outcome(RevertStatus.Failed, "La valeur d'origine n'a pas pu être relue après restauration.");
        }

        entry.State = EntryState.Reverted;
        entry.RevertedAt = _context.Audit.Now;
        return Outcome(RevertStatus.Reverted, $"Valeur d'origine remise ({SettingValue.Display(entry.Before)}).");
    }

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
                catch (Exception ex) when (ex is MausAccessDeniedException or InvalidOperationException or UnauthorizedAccessException)
                {
                    entry.Error = (entry.Error is null ? string.Empty : entry.Error + " ; ") + "retour arrière refusé : " + ex.Message;
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

    private SettingValue? TryRead(SettingKey key, out bool readable)
    {
        try
        {
            readable = true;
            return _context.Settings.Read(key);
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or InvalidOperationException)
        {
            readable = false;
            return null;
        }
    }
}
