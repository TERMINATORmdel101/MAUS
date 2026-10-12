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
/// <item>paramètres vérifiés (identifiants bornés), opération inconnue refusée ;</item>
/// <item>le moteur s'arrête avec l'interface.</item>
/// </list>
/// </summary>
public sealed class EngineServer(IMausEngine engine, string pipeName, int clientProcessId)
{
    private const int MaxIdLength = 200;
    private const int MaxIds = 500;
    private static readonly SecurityIdentifier Network = new(WellKnownSidType.NetworkSid, null);

    /// <summary>Sert les demandes jusqu'à la fin de l'interface (ou l'annulation).</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = WatchClientAsync(stop);
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
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                if (EngineProtocol.ClientProcessId(pipe.SafePipeHandle) != clientProcessId)
                {
                    Breadcrumbs.Add("moteur : connexion refusée (autre processus que l'interface)");
                    return;
                }

                if (await EngineProtocol.ReadAsync<EngineRequest>(pipe, cancellationToken).ConfigureAwait(false) is not { } request)
                {
                    return;
                }

                Texts.Use(request.Language);
                using var writer = new SerialWriter(pipe);
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
                throw new EngineException("Opération inconnue : " + request.Op);
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

    /// <summary>Écritures d'une connexion, une à la fois : les modules qui finissent ensemble ne mélangent pas leurs messages.</summary>
    private sealed class SerialWriter(Stream pipe) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task WriteAsync(EngineResponse response, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EngineProtocol.WriteAsync(pipe, response, cancellationToken).ConfigureAwait(false);
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
