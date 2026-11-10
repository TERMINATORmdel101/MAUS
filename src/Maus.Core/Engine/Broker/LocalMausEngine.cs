using Maus.Core.Fixes;
using Maus.Core.Preferences;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Engine.Broker;

/// <summary>
/// Le moteur dans le processus courant : les tests, la fenêtre lancée en administrateur, et le processus « moteur » administrateur lui-même
/// (qui sert ces opérations à l'interface par <see cref="EngineServer"/>). Il garde son propre audit : les corrections sont
/// toujours préparées d'après lui, jamais d'après des constats venus de l'extérieur.
/// </summary>
public sealed partial class LocalMausEngine : IMausEngine
{
    private readonly AuditEngine _audit;
    private readonly Func<AuditContext> _createContext;
    private readonly Func<AuditContext, FixContext> _createFixContext;
    private readonly IPreferencesStore _preferences;
    private readonly IJournalStore _journal;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AuditContext? _context;
    private IReadOnlyList<ModuleResult> _results = [];

    /// <summary>Langue dans laquelle <see cref="_results"/> est écrit ; <c>null</c> sans audit, ou s'il mêle plusieurs langues.</summary>
    private string? _auditLanguage;

    public LocalMausEngine(
        AuditEngine? audit = null,
        Func<AuditContext>? createContext = null,
        Func<AuditContext, FixContext>? createFixContext = null,
        IPreferencesStore? preferences = null,
        IJournalStore? journal = null)
    {
        _audit = audit ?? AuditEngine.CreateWithBuiltInModules();
        _createContext = createContext ?? AuditContext.CreateDefault;
        _createFixContext = createFixContext ?? FixContext.CreateDefault;
        _preferences = preferences ?? FilePreferencesStore.CreateDefault();
        _journal = journal ?? FileJournalStore.CreateDefault();
    }

    /// <summary>
    /// Confirmation affichée par le moteur lui-même avant une action sensible (<see cref="IEngineConfirmation"/>) ; posée
    /// seulement pour le moteur séparé (<c>MAUS.exe --engine</c>). <c>null</c> : MAUS administrateur dans un seul processus,
    /// où la confirmation de la fenêtre suffit (rien ne sépare alors la fenêtre du moteur).
    /// </summary>
    public IEngineConfirmation? Confirmation { get; init; }

    /// <summary>Vrai si aucune confirmation n'est demandée ici, ou si l'utilisateur répond « Oui » dans celle du moteur.</summary>
    private bool Confirmed(string title, string message) => Confirmation?.Confirm(title, message) ?? true;

    /// <summary>Comme l'autre, et un « Oui » n'agit plus si la demande a été abandonnée pendant la question.</summary>
    /// <exception cref="OperationCanceledException">L'interface a abandonné la demande.</exception>
    private bool Confirmed(string title, string message, CancellationToken cancellationToken)
    {
        var yes = Confirmed(title, message);
        if (yes)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return yes;
    }

    public async Task<AuditRun> RunAuditAsync(IProgress<ModuleResult>? progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var language = Localization.Texts.Language;
            var context = await Task.Run(_createContext, cancellationToken).ConfigureAwait(false);
            var results = await _audit.RunAsync(context, progress, cancellationToken: cancellationToken, skip: context.Preferences.SkippedModules).ConfigureAwait(false);
            _context = context;
            _results = results;
            _auditLanguage = language == Localization.Texts.Language ? language : null;
            return new AuditRun(await SnapshotAsync(context).ConfigureAwait(false), results);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ModuleResult?> RunModuleAsync(string moduleId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_context is null || _audit.Modules.FirstOrDefault(m => m.Id == moduleId) is not { } module)
            {
                return null;
            }

            // Les choix de l'utilisateur ont pu changer (c'est souvent la raison de la relance) : relus avant le module.
            _context = _context.WithPreferences(_preferences.Load());
            var result = await AuditEngine.RunModuleAsync(module, _context, cancellationToken).ConfigureAwait(false);
            _results = [.. _results.Where(r => r.ModuleId != module.Id), result];
            if (_auditLanguage != Localization.Texts.Language)
            {
                _auditLanguage = null;
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PlannedChange>> PlanAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_context is not { } audited)
            {
                return [];
            }

            // Hors du fil de l'appelant : le plan relit le système, et c'est le fil de la fenêtre pour MAUS lancé en administrateur.
            return await Task.Run(() =>
            {
                var context = audited.WithPreferences(_preferences.Load());
                _context = context;
                return FixEngine.Plan(_audit, _results, context);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ApplyResult> ApplyAsync(IReadOnlyList<string> changeIds, ApplyOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changeIds);
        ArgumentNullException.ThrowIfNull(options);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_context is not { } audited)
            {
                return new ApplyResult(null, [], null, T("Aucun audit : lancez d'abord l'audit."));
            }

            // Les corrections sont reprises dans le plan du moteur : un identifiant inconnu est ignoré, jamais inventé.
            var wanted = new HashSet<string>(changeIds, StringComparer.Ordinal);
            return await Task.Run(() =>
            {
                var context = audited.WithPreferences(_preferences.Load());
                _context = context;
                var selected = FixEngine.Plan(_audit, _results, context).Where(c => wanted.Contains(c.Id)).ToList();

                // Questions écrites par le moteur d'après son propre plan, jamais d'après un texte reçu de l'interface.
                var questions = selected.Where(c => c.EngineQuestion is not null).Select(c => c.EngineQuestion!).ToList();
                if (questions.Count > 0 && !Confirmed(T("logiciel de sécurité au démarrage"), string.Join(Environment.NewLine, questions), cancellationToken))
                {
                    return ApplyResult.Block(T("Réponse Non dans la fenêtre du moteur : aucune correction n'a été appliquée."));
                }

                return new FixEngine(_createFixContext(context)).Apply(selected, options);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordVerificationAsync(string sessionId, IReadOnlyList<VerifiedOutcome> verified, CancellationToken cancellationToken)
    {
        // Même verrou que l'application : le journal n'est jamais touché par deux demandes à la fois.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => new FixEngine(_createFixContext(_context ?? _createContext())).RecordVerification(sessionId, verified), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RevertResult> RevertAsync(string sessionId, string? changeId, bool force, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Une valeur sensible n'est remise qu'après un « Oui » dans la fenêtre du moteur, qui la montre telle qu'elle est journalisée.
            return await Task.Run(() => new FixEngine(_createFixContext(_context ?? _createContext())).Revert(
                sessionId, force, changeId, sensitive => Confirmed(T("annulation sensible"), SensitiveSettings.Question(sensitive), cancellationToken)), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<JournalListing> BrowseJournalAsync(CancellationToken cancellationToken) =>
        Task.Run(() => _journal.Browse(), cancellationToken);

    public Task<JournalSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.Run(() => _journal.Load(sessionId), cancellationToken);

    public Task<PreferencesState> LoadPreferencesAsync(CancellationToken cancellationToken) =>
        Task.Run(() => State(_preferences.Load()), cancellationToken);

    /// <summary>
    /// Marque déjà accordée pour cette valeur, ou simple reprise, après un changement de langue, d'une marque posée dans une
    /// autre langue : sa langue et son texte sont alors ceux de l'audit que le moteur a lui-même fait. Tout le reste demande la
    /// confirmation du moteur : une marque ne vaut que pour la valeur que l'utilisateur a vue.
    /// </summary>
    internal static bool IsKnownAcknowledgement(Acknowledgement mark, IEnumerable<Acknowledgement> known, IReadOnlyList<Finding> audited, string? auditLanguage) =>
        known.Any(old => old.FindingId == mark.FindingId
            && ((old.Current == mark.Current && old.Language == mark.Language)
                || (auditLanguage is not null && mark.Language == auditLanguage && old.Language is not null && old.Language != auditLanguage
                    && audited.FirstOrDefault(f => f.Id == mark.FindingId) is { } finding && Acknowledgement.Bounded(finding.Current) == mark.Current)));

    public Task<PreferencesState> SavePreferencesAsync(UserPreferences preferences, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                // Nouveau constat marqué « voulu » : l'audit de la semaine ne le signalera plus, confirmation du moteur.
                var current = _preferences.Load();
                var audited = _results.SelectMany(r => r.Findings).ToList();
                var added = preferences.Acknowledged.Where(a => !IsKnownAcknowledgement(a, current.Acknowledged, audited, _auditLanguage))
                    .Select(a => a.FindingId).Distinct(StringComparer.Ordinal).ToList();
                if (added.Count > 0 && !Confirmed(T("constat « voulu »"),
                        T("Marquer comme « voulu » : {0} ? MAUS ne le signalera plus, ni dans l'audit ni dans celui de la semaine, tant que la situation ne change pas.", FindingNames(added)), cancellationToken))
                {
                    return State(current) with { NotSaved = T("Marquage « voulu » annulé : rien n'a changé.") };
                }

                // « Jamais » cache tout d'un coup, même un problème rouge : confirmation du moteur comme pour un constat « voulu ».
                if (preferences.WeeklyNotification == WeeklyNotification.Never && current.WeeklyNotification != WeeklyNotification.Never
                    && !Confirmed(T("audit de la semaine"), T("Ne plus rien signaler après l'audit de la semaine, même un problème rouge ?"), cancellationToken))
                {
                    return State(current) with { NotSaved = T("Choix de notification annulé : rien n'a changé.") };
                }

                // Module décoché : l'audit ne le vérifiera plus, confirmation du moteur comme pour un constat « voulu ».
                var skipped = preferences.SkippedModules.Except(current.SkippedModules, StringComparer.Ordinal).ToList();
                if (skipped.Count > 0 && !Confirmed(T("module non audité"),
                        T("Ne plus auditer : {0} ? Ces modules ne seront plus vérifiés et le score sera partiel, jusqu'à ce que vous les recochiez. L'audit de la semaine reste complet.", ModuleNames(skipped)), cancellationToken))
                {
                    return State(current) with { NotSaved = T("Choix des modules annulé : rien n'a changé.") };
                }

                // Réglages inconnus de cette version : seuls ceux du fichier protégé comptent, jamais ceux reçus par le canal.
                var kept = preferences with { UnknownSettings = current.UnknownSettings };
                _preferences.Save(kept);
                return State(kept);
            }
            catch (PreferencesNotSavedException ex)
            {
                return State(ex.Kept) with { NotSaved = ex.Message };
            }
        }, cancellationToken);

    /// <summary>
    /// Noms lus dans l'audit du moteur, jamais le texte reçu de l'interface : cette question existe pour résister à une interface
    /// compromise, qui ne doit pas pouvoir y écrire ce qu'elle veut.
    /// </summary>
    private string FindingNames(IEnumerable<string> ids)
    {
        var findings = _results.SelectMany(r => r.Findings).ToList();
        return string.Join(", ", ids.Select(id => findings.Find(f => f.Id == id)?.Title ?? T("constat inconnu")).Distinct(StringComparer.Ordinal));
    }

    /// <summary>Comme <see cref="FindingNames"/>, d'après la liste des modules du moteur.</summary>
    private string ModuleNames(IEnumerable<string> ids) =>
        string.Join(", ", ids.Select(id => _audit.Modules.FirstOrDefault(m => m.Id == id)?.Title ?? T("module inconnu")).Distinct(StringComparer.Ordinal));

    public void Dispose()
    {
        DisposeWorkshop();
        _gate.Dispose();
    }

    private PreferencesState State(UserPreferences preferences) =>
        new(preferences, _preferences is FilePreferencesStore files ? files.State : PreferencesFileState.Read, _preferences.Notice);

    private async Task<EngineSnapshot> SnapshotAsync(AuditContext context)
    {
        var fix = await Task.Run(() => _createFixContext(context)).ConfigureAwait(false);
        var blocking = new FixEngine(fix).GetBlockingReason();
        return new EngineSnapshot(context.Now, context.Windows, context.Hardware, context.IsElevated, fix.ElevatedAsAnotherUser, blocking, context.Preferences);
    }
}
