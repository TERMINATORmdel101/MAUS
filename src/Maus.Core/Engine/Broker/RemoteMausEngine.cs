using System.IO.Pipes;
using System.Text.Json;
using Maus.Core.Fixes;
using Maus.Core.Localization;
using Maus.Core.Preferences;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Engine.Broker;

/// <summary>
/// Le moteur administrateur vu de l'interface : chaque opération ouvre le canal privé, vérifie que le processus en face est
/// bien le moteur lancé par l'interface (identifiant donné par Windows), envoie la demande signée avec un numéro neuf et lit
/// les réponses, refusées si elles ne sont pas signées par ce moteur pour cette demande.
/// </summary>
/// <param name="pipeName">Nom aléatoire du canal, choisi par l'interface au lancement du moteur.</param>
/// <param name="serverProcessId">Processus du moteur lancé par l'interface.</param>
/// <param name="key">Clé tirée par l'interface et passée au moteur à son lancement.</param>
public sealed partial class RemoteMausEngine(string pipeName, int serverProcessId, EngineKey key) : IMausEngine
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Dernier numéro de séquence utilisé : chaque demande prend le suivant.</summary>
    private long _sequence;

    /// <summary>1 quand le moteur a accepté la clé (présentée une fois, sur la première connexion).</summary>
    private int _introduced;

    /// <summary>Vérifie que le moteur répond, avec les droits administrateur.</summary>
    public async Task<EnginePing> PingAsync(CancellationToken cancellationToken) =>
        await CallAsync<EnginePing>("ping", null, null, cancellationToken).ConfigureAwait(false)
        ?? throw new EngineException(T("Le moteur de MAUS ne répond pas."));

    public async Task<AuditRun> RunAuditAsync(IProgress<ModuleResult>? progress, CancellationToken cancellationToken) =>
        await CallAsync<AuditRun>("audit", null, element =>
        {
            if (EngineProtocol.FromElement<ModuleResult>(element) is { } result)
            {
                progress?.Report(result);
            }
        }, cancellationToken).ConfigureAwait(false) ?? throw Empty();

    public Task<ModuleResult?> RunModuleAsync(string moduleId, CancellationToken cancellationToken) =>
        CallAsync<ModuleResult>("module", new { moduleId }, null, cancellationToken);

    public async Task<IReadOnlyList<PlannedChange>> PlanAsync(CancellationToken cancellationToken) =>
        await CallAsync<List<PlannedChange>>("plan", null, null, cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<ApplyResult> ApplyAsync(IReadOnlyList<string> changeIds, ApplyOptions options, CancellationToken cancellationToken) =>
        await CallAsync<ApplyResult>("apply", new { changeIds, options }, null, cancellationToken).ConfigureAwait(false) ?? throw Empty();

    public Task RecordVerificationAsync(string sessionId, IReadOnlyList<VerifiedOutcome> verified, CancellationToken cancellationToken) =>
        CallAsync<object>("verify", new { sessionId, verified }, null, cancellationToken);

    public async Task<RevertResult> RevertAsync(string sessionId, string? changeId, bool force, CancellationToken cancellationToken) =>
        await CallAsync<RevertResult>("revert", new { sessionId, changeId, force }, null, cancellationToken).ConfigureAwait(false) ?? throw Empty();

    public async Task<JournalListing> BrowseJournalAsync(CancellationToken cancellationToken) =>
        await CallAsync<JournalListing>("journal", null, null, cancellationToken).ConfigureAwait(false) ?? new JournalListing([], 0);

    public Task<JournalSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        CallAsync<JournalSession>("session", new { sessionId }, null, cancellationToken);

    public async Task<PreferencesState> LoadPreferencesAsync(CancellationToken cancellationToken) =>
        await CallAsync<PreferencesState>("prefs.load", null, null, cancellationToken).ConfigureAwait(false) ?? throw Empty();

    public async Task<PreferencesState> SavePreferencesAsync(UserPreferences preferences, CancellationToken cancellationToken) =>
        await CallAsync<PreferencesState>("prefs.save", new { preferences }, null, cancellationToken).ConfigureAwait(false) ?? throw Empty();

    public void Dispose()
    {
        // Rien à libérer : chaque opération ouvre et referme sa propre connexion.
    }

    /// <summary>Présente la clé au moteur, qui ne l'accepte que si elle a l'empreinte reçue à son lancement.</summary>
    private async Task IntroduceAsync(Stream pipe, CancellationToken cancellationToken)
    {
        await EngineProtocol.WriteAsync(pipe, key.Introduction(), cancellationToken).ConfigureAwait(false);
        var welcome = await EngineProtocol.ReadAsync<SignedMessage>(pipe, cancellationToken).ConfigureAwait(false)
            ?? throw new EngineException(T("Le moteur de MAUS a refusé la clé de cette fenêtre. Relancez MAUS."));
        if (!key.VerifyResponse(0, welcome))
        {
            throw new EngineException(T("Réponse refusée : elle n'est pas signée par le moteur de MAUS pour cette demande. Relancez MAUS."));
        }

        Volatile.Write(ref _introduced, 1);
    }

    private static EngineException Empty() => new(T("Le moteur de MAUS a répondu sans résultat."));

    private async Task<TResult?> CallAsync<TResult>(string op, object? args, Action<JsonElement?>? onProgress, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connect.CancelAfter(ConnectTimeout);
                await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new EngineUnavailableException(T("Le moteur de MAUS ne répond pas (il a peut-être été fermé) : relancez MAUS."), ex);
            }

            // Le processus en face doit être le moteur lancé par l'interface : un autre programme qui aurait pris ce nom de
            // canal ne reçoit rien et ne peut rien faire croire à l'interface.
            if (EngineProtocol.ServerProcessId(pipe.SafePipeHandle) != serverProcessId)
            {
                throw new EngineException(T("Le canal du moteur de MAUS est tenu par un autre programme : MAUS refuse de s'en servir. Relancez MAUS."));
            }

            var sequence = Interlocked.Increment(ref _sequence);
            var request = new EngineRequest(op, Texts.Language, args is null ? null : EngineProtocol.ToElement(args), sequence);
            try
            {
                if (Volatile.Read(ref _introduced) == 0)
                {
                    await IntroduceAsync(pipe, cancellationToken).ConfigureAwait(false);
                }

                await EngineProtocol.WriteAsync(pipe, key.SignRequest(JsonSerializer.Serialize(request, EngineProtocol.Json)), cancellationToken).ConfigureAwait(false);
                while (true)
                {
                    var signed = await EngineProtocol.ReadAsync<SignedMessage>(pipe, cancellationToken).ConfigureAwait(false)
                        ?? throw new EngineException(T("Le moteur de MAUS a coupé le canal sans répondre."));
                    if (!key.VerifyResponse(sequence, signed))
                    {
                        throw new EngineException(T("Réponse refusée : elle n'est pas signée par le moteur de MAUS pour cette demande. Relancez MAUS."));
                    }

                    var response = JsonSerializer.Deserialize<EngineResponse>(signed.Body, EngineProtocol.Json)
                        ?? throw new EngineException(T("Le moteur de MAUS a répondu sans résultat."));
                    switch (response.Kind)
                    {
                        case "progress":
                            onProgress?.Invoke(response.Payload);
                            continue;
                        case "result":
                            return EngineProtocol.FromElement<TResult>(response.Payload);
                        default:
                            throw new EngineException(response.Error ?? T("Le moteur de MAUS a refusé l'opération."));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            {
                throw new EngineException(T("La communication avec le moteur de MAUS a échoué : {0}", ex.Message), ex);
            }
        }
    }
}
