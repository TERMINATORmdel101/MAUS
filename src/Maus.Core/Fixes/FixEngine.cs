using Maus.Core.Engine;
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
        ArgumentNullException.ThrowIfNull(engine);

        var changes = new List<PlannedChange>();
        foreach (var module in engine.Modules.OfType<IFixableModule>())
        {
            var result = results.FirstOrDefault(r => r.ModuleId == module.Id);
            if (result is not null && result.Error is null)
            {
                // Un constat marqué « voulu » ne produit jamais de correction.
                var wanted = result.Findings.Where(f => f.AcknowledgedFrom is not null).Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
                try
                {
                    changes.AddRange(module.Plan(context, result.Findings).Where(c => !wanted.Contains(c.FindingId ?? c.Id)));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Comme à l'audit : un module en panne n'enlève pas les corrections des autres.
                    Maus.Core.Diagnostics.Breadcrumbs.Add("plan du module " + module.Id + " impossible (" + ex.GetType().Name + ")");
                }
            }
        }

        return changes;
    }

    /// <summary>Raison qui interdit toute correction sur ce PC, ou <c>null</c>.</summary>
    public string? GetBlockingReason()
    {
        if (_context.Audit.Hardware.IsManaged)
        {
            return T("Ce PC est géré par une organisation (domaine d'entreprise ou gestion à distance) : MAUS n'y applique aucune correction. Adressez-vous à votre service informatique.");
        }

        if (_context.Audit.Hardware.ManagementUnknown)
        {
            return T("MAUS n'a pas pu lire si ce PC est géré par une organisation (domaine d'entreprise ou gestion à distance) : par prudence, il n'y applique aucune correction. Relancez l'audit ; si ce message revient, redémarrez le PC et vérifiez que le service « Infrastructure de gestion Windows » est démarré.");
        }

        return _context.Audit.IsElevated
            ? null
            : T("Les corrections demandent les droits administrateur. Relancez MAUS en tant qu'administrateur.");
    }

    public ApplyResult Apply(IReadOnlyList<PlannedChange> changes, ApplyOptions options)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(options);

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
            restorePoint = new RestorePointCreator(_context.SystemRestore, _context.Settings, memo: _context.RestoreFrequencyMemo)
                .Create(options.RestorePointDescription, options.EnableProtectionIfNeeded);
            if (!restorePoint.Succeeded && !options.ProceedWithoutRestorePoint)
            {
                return ApplyResult.Block(T("Aucune correction n'a été appliquée."), restorePoint);
            }
        }

        var session = new JournalSession
        {
            Id = JournalSession.NewId(Now),
            CreatedAt = Now,
            WindowsBuild = _context.Audit.Windows.Build,
            UserSid = _context.UserSid,
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Sans journal sûr, aucune écriture de plus : celles déjà faites sont défaites. Toute erreur compte, pas seulement
            // celles que le journal annonce : aucune ne doit sortir d'ici en laissant des valeurs modifiées.
            RollBackSession(session);
            if (session.HasFailedRollback)
            {
                _context.Notifier.Broadcast();
            }

            var undone = outcomes.Select(o => o.Status != ChangeStatus.Applied ? o : UndoneOutcome(session, o)).ToList();
            return new ApplyResult(null, undone, restorePoint, session.HasFailedRollback ? ex.Message + " " + T("Certaines valeurs n'ont pas pu être remises à leur état d'origine.") : ex.Message);
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
    /// <param name="confirmSensitive">
    /// Si elle est donnée, question posée avant d'écrire quand une valeur sensible (<see cref="SensitiveSettings"/>) serait
    /// vraiment remise ; « non » : rien n'est écrit.
    /// </param>
    public RevertResult Revert(string sessionId, bool force = false, string? changeId = null, Func<IReadOnlyList<JournalEntry>, bool>? confirmSensitive = null)
    {
        if (!_context.Audit.IsElevated)
        {
            return new RevertResult(null, [], T("L'annulation demande les droits administrateur. Relancez MAUS en tant qu'administrateur."));
        }

        JournalSession? session;
        try
        {
            session = _context.Journal.Load(sessionId);
        }
        catch (JournalUnsafeException ex)
        {
            // Rien n'est lu ni remis d'un journal dont le dossier n'est pas sûr.
            return new RevertResult(null, [], ex.Message);
        }

        if (session is null)
        {
            return new RevertResult(null, [], T("Séance {0} introuvable dans le journal (ou fichier non fiable, ignoré).", sessionId));
        }

        var outcomes = new List<RevertOutcome>();
        var targets = Enumerable.Reverse(session.Entries)
            // Une valeur laissée telle quelle parce qu'elle avait changé : --force la reprend, comme le message le conseille.
            .Where(e => (e.State.IsOutstanding() || (force && e.State == EntryState.RevertSkipped)) && (changeId is null || e.ChangeId == changeId))
            .ToList();
        if (changeId is not null && targets.Count == 0)
        {
            return new RevertResult(session, [], T("La correction {0} n'a rien à annuler dans cette séance.", changeId));
        }

        if (confirmSensitive is not null)
        {
            var sensitive = targets.Where(e => SensitiveSettings.IsSensitive(e) && WouldRestore(session, e, force)).ToList();
            if (sensitive.Count > 0 && !confirmSensitive(sensitive))
            {
                return new RevertResult(session, [], T("Vous avez répondu Non dans la fenêtre de confirmation : aucune valeur n'a été remise."));
            }
        }

        // Journal enregistré après chaque valeur remise. Sans journal sûr, aucune écriture de plus (comme pour Apply) ;
        // ce qui a déjà été remis reste remis : Windows est prévenu et le résultat le dit.
        string? journalError = null;
        try
        {
            foreach (var entry in targets)
            {
                outcomes.Add(RevertOne(session, entry, force));
                _context.Journal.Save(session);
            }

            if (!session.CanRevert)
            {
                session.RevertedAt = Now;
                _context.Journal.Save(session);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            journalError = T("L'annulation n'a pas pu être enregistrée dans le journal : {0}", ex.Message);
        }

        if (outcomes.Any(o => o.Status == RevertStatus.Reverted))
        {
            _context.Notifier.Broadcast();
        }

        return new RevertResult(session, outcomes, journalError);
    }

    /// <summary>Une correction réussie, défaite ensuite parce que le journal a lâché : elle ne compte plus comme appliquée.</summary>
    private static ChangeOutcome UndoneOutcome(JournalSession session, ChangeOutcome outcome) => outcome with
    {
        Status = ChangeStatus.Failed,
        Message = session.Entries.Any(e => e.ChangeId == outcome.ChangeId && e.State == EntryState.RollbackFailed)
            ? T("Le journal ne peut plus être écrit, et le retour arrière a échoué : la valeur modifiée est peut-être encore en place.")
            : T("Le journal ne peut plus être écrit : valeurs d'origine remises."),
    };

    private ChangeOutcome ApplyOne(JournalSession session, PlannedChange change, HashSet<string> written)
    {
        ChangeOutcome Outcome(ChangeStatus status, string message, bool notAllowed = false) => new(change.Id, change.Title, status, message, change.Effect, notAllowed);

        if (change.IsUserScoped && _context.ElevatedAsAnotherUser)
        {
            return Outcome(ChangeStatus.Skipped,
                T("MAUS a été lancé avec un autre compte administrateur : ce réglage irait dans le mauvais profil. Relancez MAUS depuis votre propre session."), notAllowed: true);
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
            bool containerExisted;
            try
            {
                before = _context.Settings.Read(write.Key);
                containerExisted = _context.Settings.ContainerExists(write.Key);
            }
            catch (Exception ex) when (IsSettingFailure(ex))
            {
                return Outcome(ChangeStatus.Skipped, T("Valeur d'origine illisible ({0}) : rien n'a été modifié.", write.Key), notAllowed: true);
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
                    ContainerExisted = containerExisted && !CreatedBySession(session, write.Key),
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

        ChangeOutcome Fail(JournalEntry failed, string error, string restored, string notRestored)
        {
            failed.Error = error;
            RollBack(entries);
            _context.Journal.Save(session);
            return Outcome(ChangeStatus.Failed, RolledBack(entries) ? restored : notRestored);
        }

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
                return Fail(entry, ex.Message,
                    T("Écriture refusée ({0}) : valeurs d'origine remises.", entry.Key),
                    T("Écriture refusée ({0}), et le retour arrière a échoué : la valeur modifiée est peut-être encore en place. La séance garde son bouton « Annuler » pour réessayer.", entry.Key));
            }
        }

        // Verify : relecture de l'état effectif.
        foreach (var entry in entries)
        {
            var actual = TryRead(entry.Key, out var readable);
            if (!readable || !SettingValue.AreEquivalent(actual, entry.After))
            {
                return Fail(entry, T("Relu : {0}, attendu : {1}", SettingValue.Display(actual), SettingValue.Display(entry.After)),
                    T("Windows n'a pas gardé la valeur ({0}) : valeurs d'origine remises.", entry.Key),
                    T("Windows n'a pas gardé la valeur ({0}), et le retour arrière a échoué : la séance garde son bouton « Annuler » pour réessayer.", entry.Key));
            }
        }

        foreach (var entry in entries)
        {
            written.Add(entry.Key.Identity);
        }

        _context.Journal.Save(session);
        return Outcome(ChangeStatus.Applied, T("Appliqué et vérifié."));
    }

    /// <summary>Mêmes conditions que <see cref="RevertOne"/> : cette valeur serait-elle vraiment écrite ?</summary>
    private bool WouldRestore(JournalSession session, JournalEntry entry, bool force)
    {
        if ((entry.Key.IsUserScoped && _context.ElevatedAsAnotherUser) || session.IsInAnotherProfile(entry, _context.UserSid))
        {
            return false;
        }

        var current = TryRead(entry.Key, out var readable);
        if (!readable || (!SettingValue.AreEquivalent(current, entry.After) && SettingValue.AreEquivalent(current, entry.Before)))
        {
            return false;
        }

        return force || entry.State == EntryState.RollbackFailed || SettingValue.AreEquivalent(current, entry.After);
    }

    private RevertOutcome RevertOne(JournalSession session, JournalEntry entry, bool force)
    {
        RevertOutcome Outcome(RevertStatus status, string message) => new(entry.ChangeId, entry.ChangeTitle, entry.Key.Describe(), status, message);

        if (entry.Key.IsUserScoped && _context.ElevatedAsAnotherUser)
        {
            return Outcome(RevertStatus.WrongAccount, T("MAUS a été lancé avec un autre compte administrateur : relancez-le depuis votre propre session pour annuler ce réglage."));
        }

        // Le journal est commun au PC : ici, HKCU serait le profil de ce compte, pas celui de la séance. Même --force ne passe pas.
        if (session.IsInAnotherProfile(entry, _context.UserSid))
        {
            return Outcome(RevertStatus.WrongAccount, T("Séance faite par un autre compte : ce réglage est dans son profil. Ouvrez MAUS depuis ce compte pour l'annuler."));
        }

        var current = TryRead(entry.Key, out var readable);
        if (!readable)
        {
            return Outcome(RevertStatus.Failed, T("Valeur actuelle illisible : rien n'a été modifié."));
        }

        // La valeur d'origine est déjà là, quel que soit l'état noté : il n'y a rien à remettre. Une écriture interrompue
        // (MAUS fermé, plantage, coupure) ou un retour arrière raté n'a finalement rien changé ; une correction appliquée
        // a été défaite par quelqu'un d'autre et compte comme annulée.
        if (!SettingValue.AreEquivalent(current, entry.After) && SettingValue.AreEquivalent(current, entry.Before))
        {
            switch (entry.State)
            {
                case EntryState.Pending:
                    entry.State = EntryState.Failed;
                    entry.Error = T("Correction interrompue avant l'écriture : la valeur d'origine était encore en place.");
                    return Outcome(RevertStatus.Skipped, T("Correction interrompue avant l'écriture : rien à annuler."));
                case EntryState.RollbackFailed:
                    entry.State = EntryState.Failed;
                    break;
                default:
                    entry.State = EntryState.Reverted;
                    entry.RevertedAt = Now;
                    break;
            }

            return Outcome(RevertStatus.Skipped, T("La valeur d'origine est déjà en place."));
        }

        // Après un retour arrière raté, la valeur peut être l'ancienne, la nouvelle ou entre les deux : on remet l'origine.
        if (!force && entry.State != EntryState.RollbackFailed && !SettingValue.AreEquivalent(current, entry.After))
        {
            entry.State = EntryState.RevertSkipped;
            return Outcome(RevertStatus.ChangedSince,
                T("La valeur a changé depuis la correction ({0}) : laissée telle quelle.", SettingValue.Display(current)));
        }

        if (!TryRestore(entry, out var refusal, out var reread))
        {
            if (refusal is not null)
            {
                entry.Error = refusal;
                return Outcome(RevertStatus.Failed, T("Restauration refusée : {0}", refusal));
            }

            entry.Error = T("Relu après restauration : {0}", SettingValue.Display(reread));
            return Outcome(RevertStatus.Failed, T("La valeur d'origine n'a pas pu être relue après restauration."));
        }

        entry.State = EntryState.Reverted;
        entry.RevertedAt = Now;
        return Outcome(RevertStatus.Reverted, T("Valeur d'origine remise ({0}).", SettingValue.Display(entry.Before)));
    }

    /// <summary>Heure réelle de l'opération (horloge du contexte), sinon celle de l'audit (tests).</summary>
    private DateTimeOffset Now => _context.Clock?.Invoke() ?? _context.Audit.Now;

    /// <summary>
    /// Défait les écritures d'une correction, dans l'ordre inverse, et relit chaque valeur remise. Une écriture dont le
    /// retour arrière échoue (ou ne se relit pas) passe en <see cref="EntryState.RollbackFailed"/> : elle reste visible et
    /// « Annuler » la reprendra, au lieu de devenir une modification invisible.
    /// </summary>
    private void RollBack(IEnumerable<JournalEntry> entries)
    {
        foreach (var entry in entries.Reverse())
        {
            if (entry.State != EntryState.Applied && entry.Error is null)
            {
                // Jamais écrite : la valeur d'origine n'a pas bougé.
                entry.State = EntryState.Failed;
                continue;
            }

            var current = TryRead(entry.Key, out var readable);
            if (entry.State != EntryState.Applied && readable && SettingValue.AreEquivalent(current, entry.Before))
            {
                // Écriture refusée d'emblée : la valeur d'origine est encore là, rien à remettre.
                entry.State = EntryState.Failed;
                continue;
            }

            if (TryRestore(entry, out var refusal, out var reread))
            {
                entry.State = EntryState.Failed;
                continue;
            }

            var problem = refusal is not null ? T("retour arrière refusé : ") + refusal : T("retour arrière non vérifié (relu : {0})", SettingValue.Display(reread));
            entry.Error = (entry.Error is null ? string.Empty : entry.Error + " ; ") + problem;
            entry.State = EntryState.RollbackFailed;
        }
    }

    /// <summary>Toutes les valeurs d'origine sont revenues (aucun retour arrière raté).</summary>
    private static bool RolledBack(IEnumerable<JournalEntry> entries) => entries.All(e => e.State != EntryState.RollbackFailed);

    private void RollBackSession(JournalSession session) =>
        RollBack(session.Entries.Where(e => e.State == EntryState.Applied).ToList());

    /// <summary>
    /// Remet la valeur d'origine, retire la clé si MAUS l'a créée et qu'elle est vide, puis relit. Rend faux si Windows refuse
    /// (<paramref name="refusal"/>) ou si la relecture ne donne pas la valeur d'origine (<paramref name="reread"/>).
    /// </summary>
    private bool TryRestore(JournalEntry entry, out string? refusal, out SettingValue? reread)
    {
        refusal = null;
        reread = null;
        try
        {
            _context.Settings.Write(entry.Key, entry.Before);
            if (!entry.ContainerExisted)
            {
                _context.Settings.RemoveContainerIfEmpty(entry.Key);
            }
        }
        catch (Exception ex) when (IsSettingFailure(ex))
        {
            refusal = ex.Message;
            return false;
        }

        reread = TryRead(entry.Key, out var readable);
        return readable && SettingValue.AreEquivalent(reread, entry.Before);
    }

    /// <summary>
    /// Une correction déjà appliquée de la séance a créé cette clé de registre : la correction suivante la voit existante, mais
    /// la clé n'existait pas avant MAUS. La dernière annulée la retire, quel que soit l'ordre des annulations.
    /// </summary>
    private static bool CreatedBySession(JournalSession session, SettingKey key) =>
        key.Kind == SettingKind.Registry && session.Entries.Any(e => e.State == EntryState.Applied && !e.ContainerExisted
            && e.Key.Kind == SettingKind.Registry
            && string.Equals(e.Key.Hive, key.Hive, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.Key.Path, key.Path, StringComparison.OrdinalIgnoreCase));

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
