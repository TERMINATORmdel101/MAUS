using Maus.Core.Fixes;
using Maus.Core.Preferences;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Engine.Broker;

/// <summary>Le moteur administrateur en marche : son client, et de quoi savoir s'il s'est arrêté.</summary>
/// <param name="Engine">Client du moteur (canal privé).</param>
/// <param name="HasExited">Vrai quand le processus du moteur est terminé (15 minutes sans demande, ou arrêt).</param>
public sealed record ElevatedEngineSession(IMausEngine Engine, Func<bool> HasExited);

/// <summary>L'utilisateur a refusé l'autorisation administrateur : seule l'action demandée est annulée.</summary>
public sealed class EngineDeclinedException(string message, Exception? inner = null) : EngineException(message, inner)
{
    public static string DefaultMessage => T("Autorisation administrateur refusée : rien n'a été lancé. MAUS reste en mode limité, sans droits administrateur.");
}

/// <summary>
/// Le moteur vu d'une interface sans droits administrateur (demande du porteur du 22/10/2026, Microsoft Store) : rien n'est
/// lancé à l'ouverture de MAUS. Une action qui a besoin des droits administrateur (audit, corrections et annulations,
/// journal, réparation, pilotes, capteurs PawnIO, fiche mémoire, latence, compteur d'images, tâche de la semaine) démarre le
/// moteur avec la demande d'autorisation de Windows ; un refus annule cette action seulement
/// (<see cref="EngineDeclinedException"/>). Les lectures qui marchent sans droits passent par le moteur administrateur s'il
/// tourne déjà, sinon par <paramref name="limited"/>, sans rien demander. Le moteur s'arrête seul après 15 minutes sans
/// demande (<see cref="EngineServer.IdleTimeout"/>) : l'action suivante redemande l'autorisation.
/// </summary>
/// <param name="limited">Moteur dans le processus de l'interface, sans droits administrateur.</param>
/// <param name="start">Lance le moteur administrateur (demande d'autorisation de Windows).</param>
public sealed partial class OnDemandMausEngine(IMausEngine limited, Func<CancellationToken, Task<ElevatedEngineSession>> start) : IMausEngine
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ElevatedEngineSession? _session;

    /// <summary>Moteur qui a fait le dernier audit : les corrections se préparent d'après son audit à lui.</summary>
    private ElevatedEngineSession? _audited;

    /// <summary>Le moteur administrateur vient de démarrer (après l'autorisation de Windows).</summary>
    public event EventHandler? ElevatedStarted;

    /// <summary>Le moteur administrateur tourne : les lectures qui en profitent passent par lui, sans rien demander.</summary>
    public bool IsElevatedRunning => Live() is not null;

    /// <summary>Démarre le moteur administrateur s'il ne tourne pas (demande d'autorisation de Windows).</summary>
    /// <exception cref="EngineDeclinedException">Autorisation refusée.</exception>
    public async Task<IMausEngine> EnsureElevatedAsync(CancellationToken cancellationToken)
    {
        if (Live() is { } live)
        {
            return live.Engine;
        }

        ElevatedEngineSession session;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Deux actions en même temps : une seule demande d'autorisation.
            if (Live() is { } again)
            {
                return again.Engine;
            }

            session = await start(cancellationToken).ConfigureAwait(false);
            _session = session;
        }
        finally
        {
            _gate.Release();
        }

        ElevatedStarted?.Invoke(this, EventArgs.Empty);
        return session.Engine;
    }

    public async Task<AuditRun> RunAuditAsync(IProgress<ModuleResult>? progress, CancellationToken cancellationToken)
    {
        var engine = await EnsureElevatedAsync(cancellationToken).ConfigureAwait(false);
        var session = _session;
        var run = await Retry(engine, e => e.RunAuditAsync(progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        _audited = _session ?? session;
        return run;
    }

    public async Task<ModuleResult?> RunModuleAsync(string moduleId, CancellationToken cancellationToken) =>
        await (await AuditedAsync(cancellationToken).ConfigureAwait(false)).RunModuleAsync(moduleId, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<PlannedChange>> PlanAsync(CancellationToken cancellationToken) =>
        await (await AuditedAsync(cancellationToken).ConfigureAwait(false)).PlanAsync(cancellationToken).ConfigureAwait(false);

    public async Task<ApplyResult> ApplyAsync(IReadOnlyList<string> changeIds, ApplyOptions options, CancellationToken cancellationToken) =>
        await (await AuditedAsync(cancellationToken).ConfigureAwait(false)).ApplyAsync(changeIds, options, cancellationToken).ConfigureAwait(false);

    public Task RecordVerificationAsync(string sessionId, IReadOnlyList<VerifiedOutcome> verified, CancellationToken cancellationToken) =>
        Elevated(async e =>
        {
            await e.RecordVerificationAsync(sessionId, verified, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task<RevertResult> RevertAsync(string sessionId, string? changeId, bool force, CancellationToken cancellationToken) =>
        Elevated(e => e.RevertAsync(sessionId, changeId, force, cancellationToken), cancellationToken);

    public Task<JournalListing> BrowseJournalAsync(CancellationToken cancellationToken) =>
        Elevated(e => e.BrowseJournalAsync(cancellationToken), cancellationToken);

    public Task<JournalSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        Elevated(e => e.LoadSessionAsync(sessionId, cancellationToken), cancellationToken);

    public Task<PreferencesState> LoadPreferencesAsync(CancellationToken cancellationToken) =>
        Elevated(e => e.LoadPreferencesAsync(cancellationToken), cancellationToken);

    public Task<PreferencesState> SavePreferencesAsync(UserPreferences preferences, CancellationToken cancellationToken) =>
        Elevated(e => e.SavePreferencesAsync(preferences, cancellationToken), cancellationToken);

    public void Dispose()
    {
        limited.Dispose();
        _session?.Engine.Dispose();
        _gate.Dispose();
    }

    private ElevatedEngineSession? Live()
    {
        var session = _session;
        return session is not null && !session.HasExited() ? session : null;
    }

    /// <summary>
    /// Le moteur en marche, avec un audit à lui : s'il a redémarré depuis le dernier audit (arrêt après 15 minutes), il
    /// refait d'abord un audit, en lecture seule, pour préparer les corrections d'après l'état actuel du PC.
    /// </summary>
    private async Task<IMausEngine> AuditedAsync(CancellationToken cancellationToken)
    {
        var engine = await EnsureElevatedAsync(cancellationToken).ConfigureAwait(false);
        if (!ReferenceEquals(_audited, _session))
        {
            await engine.RunAuditAsync(null, cancellationToken).ConfigureAwait(false);
            _audited = _session;
        }

        return engine;
    }

    /// <summary>Opération qui demande les droits administrateur : le moteur est lancé au besoin.</summary>
    private async Task<TResult> Elevated<TResult>(Func<IMausEngine, Task<TResult>> call, CancellationToken cancellationToken)
    {
        var engine = await EnsureElevatedAsync(cancellationToken).ConfigureAwait(false);
        return await Retry(engine, call, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Le moteur s'est arrêté juste avant la demande (15 minutes sans demande) : rien n'a été envoyé, un nouveau moteur est
    /// lancé et la demande refaite, une seule fois.
    /// </summary>
    private async Task<TResult> Retry<TResult>(IMausEngine engine, Func<IMausEngine, Task<TResult>> call, CancellationToken cancellationToken)
    {
        try
        {
            return await call(engine).ConfigureAwait(false);
        }
        catch (EngineUnavailableException)
        {
            if (Live() is not null)
            {
                throw;
            }

            return await call(await EnsureElevatedAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        }
    }

    /// <summary>Lecture possible sans droits : par le moteur administrateur s'il tourne, sinon sans rien demander.</summary>
    private async Task<TResult> Available<TResult>(Func<IMausEngine, Task<TResult>> call)
    {
        if (Live() is { } live)
        {
            try
            {
                return await call(live.Engine).ConfigureAwait(false);
            }
            catch (EngineUnavailableException) when (Live() is null)
            {
                // Arrêté entre-temps : la lecture se fait sans droits.
            }
        }

        return await call(limited).ConfigureAwait(false);
    }
}
