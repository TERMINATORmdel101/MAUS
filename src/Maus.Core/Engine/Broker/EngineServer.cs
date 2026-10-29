using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Maus.Core.Diagnostics;
using Maus.Core.Fixes;
using Maus.Core.Localization;
using Maus.Core.Preferences;

namespace Maus.Core.Engine.Broker;

/// <summary>
/// Le moteur administrateur côté canal : il ne sert que l'interface qui l'a lancé, et seulement la liste fermée des
/// opérations de <see cref="IMausEngine"/>.
/// <list type="bullet">
/// <item>canal nommé au nom aléatoire, réservé au compte de l'utilisateur (refusé au réseau), première instance obligatoire :
/// un programme qui aurait créé un canal du même nom avant le moteur le fait échouer, au lieu d'être servi à sa place ;</item>
/// <item>chaque connexion vient du processus de l'interface (identifiant vérifié par Windows), sinon elle est fermée ;</item>
/// <item>l'interface présente d'abord la clé dont le moteur a reçu l'empreinte à son lancement ; ensuite chaque demande est
/// signée avec cette clé et porte un numéro neuf (<see cref="EngineKey"/>, <see cref="ReplayWindow"/>) : une demande
/// copiée, rejouée ou modifiée est fermée sans réponse ; chaque réponse est signée et liée au numéro de sa demande ;</item>
/// <item>paramètres vérifiés (identifiants bornés), opération inconnue refusée ;</item>
/// <item>le moteur s'arrête avec l'interface, ou après <see cref="IdleTimeout"/> sans demande : les droits administrateur ne
/// restent pas ouverts plus longtemps que nécessaire (l'action suivante les redemande).</item>
/// </list>
/// </summary>
public sealed partial class EngineServer(IMausEngine engine, string pipeName, int clientProcessId, string keyFingerprint)
{
    private const int MaxIdLength = 200;
    private const int MaxIds = 500;
    private static readonly SecurityIdentifier Network = new(WellKnownSidType.NetworkSid, null);

    /// <summary>Numéros de séquence déjà servis : une demande rejouée est refusée.</summary>
    private readonly ReplayWindow _replay = new();

    /// <summary>Clé présentée par l'interface (empreinte vérifiée) ; <c>null</c> tant qu'elle ne l'a pas présentée.</summary>
    private EngineKey? _key;

    /// <summary>Demandes en cours : le moteur ne s'arrête jamais au milieu de l'une d'elles.</summary>
    private int _busy;

    /// <summary>Fin de la dernière demande (ou démarrage), en millisecondes depuis le démarrage de Windows.</summary>
    private long _lastActivity = Environment.TickCount64;

    /// <summary>Arrêt du moteur après ce délai sans aucune demande (15 minutes, demande du porteur du 22/10/2026).</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Sert les demandes jusqu'à la fin de l'interface, l'annulation ou <see cref="IdleTimeout"/> sans demande.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = WatchClientAsync(stop);
        _ = WatchIdleAsync(stop);
        var first = true;
        while (!stop.IsCancellationRequested)
        {
            var pipe = Create(first);
            first = false;
            try
            {
                await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }

            _ = ServeAsync(pipe, stop.Token);
        }
    }

    private NamedPipeServerStream Create(bool first)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new EngineException("Compte de l'utilisateur introuvable.");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(Network, PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, options, 0, 0, security);
    }

    /// <summary>Aucune demande en cours ni reçue depuis <see cref="IdleTimeout"/> : le moteur s'arrête.</summary>
    private async Task WatchIdleAsync(CancellationTokenSource stop)
    {
        var step = TimeSpan.FromMilliseconds(Math.Clamp(IdleTimeout.TotalMilliseconds / 4, 50, 30_000));
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(step, stop.Token).ConfigureAwait(false);
                if (Volatile.Read(ref _busy) == 0 && Environment.TickCount64 - Interlocked.Read(ref _lastActivity) >= IdleTimeout.TotalMilliseconds)
                {
                    Breadcrumbs.Add("moteur : arrêt après " + IdleTimeout.TotalMinutes.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " min sans demande");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await stop.CancelAsync().ConfigureAwait(false);
    }

    private async Task WatchClientAsync(CancellationTokenSource stop)
    {
        try
        {
            using var client = System.Diagnostics.Process.GetProcessById(clientProcessId);
            await client.WaitForExitAsync(stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            // Interface déjà fermée (ou arrêt demandé) : le moteur s'arrête aussi.
        }

        await stop.CancelAsync().ConfigureAwait(false);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _busy);
        try
        {
            await ServeOneAsync(pipe, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
            Interlocked.Decrement(ref _busy);
        }
    }

    private async Task ServeOneAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                if (EngineProtocol.ClientProcessId(pipe.SafePipeHandle) != clientProcessId)
                {
                    Breadcrumbs.Add("moteur : connexion refusée (autre processus que l'interface)");
                    return;
                }

                if (await EngineProtocol.ReadAsync<SignedMessage>(pipe, cancellationToken).ConfigureAwait(false) is not { } signed)
                {
                    return;
                }

                // Présentation de la clé (premier message d'une connexion) : acceptée seulement avec l'empreinte reçue au
                // lancement, puis la demande suit sur la même connexion.
                if (signed.Key is not null)
                {
                    if (Introduce(signed.Key) is not { } introduced)
                    {
                        Breadcrumbs.Add("moteur : clé refusée (empreinte différente de celle reçue au lancement)");
                        return;
                    }

                    await EngineProtocol.WriteAsync(pipe, introduced.SignResponse(0, string.Empty), cancellationToken).ConfigureAwait(false);
                    if (await EngineProtocol.ReadAsync<SignedMessage>(pipe, cancellationToken).ConfigureAwait(false) is not { } next)
                    {
                        return;
                    }

                    signed = next;
                }

                // Signature d'abord (le texte n'est lu qu'ensuite), puis numéro neuf : rien n'est exécuté sinon.
                if (Volatile.Read(ref _key) is not { } key
                    || signed.Key is not null
                    || !key.VerifyRequest(signed)
                    || JsonSerializer.Deserialize<EngineRequest>(signed.Body, EngineProtocol.Json) is not { } request
                    || !_replay.TryAccept(request.Sequence))
                {
                    Breadcrumbs.Add("moteur : demande refusée (signature invalide, ou numéro de séquence déjà servi)");
                    return;
                }

                Texts.Use(request.Language);
                using var writer = new SerialWriter(pipe, key, request.Sequence);
                try
                {
                    var result = await DispatchAsync(request, writer, cancellationToken).ConfigureAwait(false);
                    await writer.WriteAsync(new EngineResponse("result", result), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not IOException)
                {
                    Breadcrumbs.Add("moteur : opération " + request.Op + " en erreur (" + ex.GetType().Name + ")");
                    await writer.WriteAsync(new EngineResponse("error", Error: ex.Message), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or JsonException)
            {
                // Canal coupé, message mal formé ou arrêt : la connexion est fermée, le moteur continue pour les suivantes.
            }
        }
    }

    /// <summary>La clé présentée, si son empreinte est celle reçue au lancement ; une fois acceptée, elle ne change plus.</summary>
    private EngineKey? Introduce(string text)
    {
        if (EngineKey.Parse(text) is not { } candidate || !candidate.HasFingerprint(keyFingerprint))
        {
            return null;
        }

        Interlocked.CompareExchange(ref _key, candidate, null);
        return Volatile.Read(ref _key);
    }

    private async Task<JsonElement?> DispatchAsync(EngineRequest request, SerialWriter pipe, CancellationToken cancellationToken)
    {
        var args = request.Args;
        switch (request.Op)
        {
            case "ping":
                return EngineProtocol.ToElement(new EnginePing(AppVersion.Full, Environment.ProcessId, Environment.IsPrivilegedProcess));
            case "audit":
                // Chaque module terminé est envoyé tout de suite : la barre d'avancement de l'interface suit l'audit.
                var writes = new List<Task>();
                var progress = new InlineProgress<ModuleResult>(result =>
                {
                    lock (writes)
                    {
                        writes.Add(pipe.WriteAsync(new EngineResponse("progress", EngineProtocol.ToElement(result)), cancellationToken));
                    }
                });
                var run = await engine.RunAuditAsync(progress, cancellationToken).ConfigureAwait(false);
                await Task.WhenAll(SnapshotOf(writes)).ConfigureAwait(false);
                return EngineProtocol.ToElement(run);
            case "module":
                return EngineProtocol.ToElement(await engine.RunModuleAsync(Id(args, "moduleId"), cancellationToken).ConfigureAwait(false));
            case "plan":
                return EngineProtocol.ToElement(await engine.PlanAsync(cancellationToken).ConfigureAwait(false));
            case "apply":
                return EngineProtocol.ToElement(await engine.ApplyAsync(Ids(args, "changeIds"), Arg<ApplyOptions>(args, "options"), cancellationToken).ConfigureAwait(false));
            case "verify":
                await engine.RecordVerificationAsync(Id(args, "sessionId"), Arg<IReadOnlyList<VerifiedOutcome>>(args, "verified"), cancellationToken).ConfigureAwait(false);
                return null;
            case "revert":
                return EngineProtocol.ToElement(await engine.RevertAsync(Id(args, "sessionId"), OptionalId(args, "changeId"), Arg<bool>(args, "force"), cancellationToken).ConfigureAwait(false));
            case "journal":
                return EngineProtocol.ToElement(await engine.BrowseJournalAsync(cancellationToken).ConfigureAwait(false));
            case "session":
                return EngineProtocol.ToElement(await engine.LoadSessionAsync(Id(args, "sessionId"), cancellationToken).ConfigureAwait(false));
            case "prefs.load":
                return EngineProtocol.ToElement(await engine.LoadPreferencesAsync(cancellationToken).ConfigureAwait(false));
            case "prefs.save":
                return EngineProtocol.ToElement(await engine.SavePreferencesAsync(Arg<UserPreferences>(args, "preferences"), cancellationToken).ConfigureAwait(false));
            default:
                return await DispatchWorkshopAsync(request, pipe, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task[] SnapshotOf(List<Task> writes)
    {
        lock (writes)
        {
            return [.. writes];
        }
    }

    private static T Arg<T>(JsonElement? args, string name) =>
        args is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var item)
            ? item.Deserialize<T>(EngineProtocol.Json) ?? throw new EngineException("Paramètre vide : " + name)
            : throw new EngineException("Paramètre manquant : " + name);

    private static string Id(JsonElement? args, string name) => Checked(Arg<string>(args, name));

    private static string? OptionalId(JsonElement? args, string name) =>
        args is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String
            ? Checked(item.GetString()!)
            : null;

    private static IReadOnlyList<string> Ids(JsonElement? args, string name)
    {
        var ids = Arg<List<string>>(args, name);
        return ids.Count > MaxIds ? throw new EngineException("Trop d'identifiants.") : [.. ids.Select(Checked)];
    }

    /// <summary>Identifiant de correction, de module ou de séance : court, sans caractère de contrôle ni chemin.</summary>
    private static string Checked(string id) =>
        id.Length is > 0 and <= MaxIdLength && !id.Any(c => char.IsControl(c) || c is '\\' or '/' or ':')
            ? id
            : throw new EngineException("Identifiant refusé.");

    /// <summary>
    /// Écritures d'une connexion, une à la fois : les modules qui finissent ensemble ne mélangent pas leurs messages. Chaque
    /// réponse est signée et liée au numéro de la demande.
    /// </summary>
    private sealed class SerialWriter(Stream pipe, EngineKey key, long sequence) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task WriteAsync(EngineResponse response, CancellationToken cancellationToken)
        {
            var signed = key.SignResponse(sequence, JsonSerializer.Serialize(response, EngineProtocol.Json));
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EngineProtocol.WriteAsync(pipe, signed, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose() => _gate.Dispose();
    }

    /// <summary>Progression signalée tout de suite sur le fil qui l'émet (pas de contexte de synchronisation dans le moteur).</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

/// <summary>Réponse au « ping » : version du moteur, son processus, et s'il a bien les droits administrateur.</summary>
public sealed record EnginePing(string Version, int ProcessId, bool IsElevated);
