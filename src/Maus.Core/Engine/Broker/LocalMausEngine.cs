using Maus.Core.Fixes;
using Maus.Core.Preferences;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Engine.Broker;

/// <summary>
/// Le moteur dans le processus courant : la ligne de commande, les tests, et le processus « moteur » administrateur lui-même
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

    public async Task<AuditRun> RunAuditAsync(IProgress<ModuleResult>? progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = await Task.Run(_createContext, cancellationToken).ConfigureAwait(false);
            var results = await _audit.RunAsync(context, progress, cancellationToken: cancellationToken).ConfigureAwait(false);
            _context = context;
            _results = results;
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
            if (_context is null)
            {
                return [];
            }

            _context = _context.WithPreferences(_preferences.Load());
            return FixEngine.Plan(_audit, _results, _context);
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
            if (_context is null)
            {
                return new ApplyResult(null, [], null, T("Aucun audit : lancez d'abord l'audit."));
            }

            // Les corrections sont reprises dans le plan du moteur : un identifiant inconnu est ignoré, jamais inventé.
            _context = _context.WithPreferences(_preferences.Load());
            var wanted = new HashSet<string>(changeIds, StringComparer.Ordinal);
            var selected = FixEngine.Plan(_audit, _results, _context).Where(c => wanted.Contains(c.Id)).ToList();
            var context = _context;
            return await Task.Run(() => new FixEngine(_createFixContext(context)).Apply(selected, options), cancellationToken).ConfigureAwait(false);
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
            return await Task.Run(() => new FixEngine(_createFixContext(_context ?? _createContext())).Revert(sessionId, force, changeId), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<JournalListing> BrowseJournalAsync(CancellationToken cancellationToken) =>
        Task.Run(() => _journal is FileJournalStore files ? files.Browse() : new JournalListing(_journal.List(), 0), cancellationToken);

    public Task<JournalSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.Run(() => _journal.Load(sessionId), cancellationToken);

    public Task<PreferencesState> LoadPreferencesAsync(CancellationToken cancellationToken) =>
        Task.Run(() => State(_preferences.Load()), cancellationToken);

    public Task<PreferencesState> SavePreferencesAsync(UserPreferences preferences, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                // Nouveau constat marqué « voulu » : l'audit de la semaine ne le signalera plus, confirmation du moteur.
                var current = _preferences.Load();
                var added = preferences.Acknowledged.Select(a => a.FindingId)
                    .Except(current.Acknowledged.Select(a => a.FindingId), StringComparer.Ordinal).ToList();
                if (added.Count > 0 && !Confirmed(T("constat « voulu »"),
                        T("Marquer comme « voulu » : {0} ? MAUS ne le signalera plus, ni dans l'audit ni dans celui de la semaine, tant que la situation ne change pas.", string.Join(", ", added))))
                {
                    return State(current) with { NotSaved = T("Marquage « voulu » annulé : rien n'a changé.") };
                }

                _preferences.Save(preferences);
                return State(preferences);
            }
            catch (PreferencesNotSavedException ex)
            {
                return State(ex.Kept) with { NotSaved = ex.Message };
            }
        }, cancellationToken);

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
