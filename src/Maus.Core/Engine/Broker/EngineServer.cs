using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Maus.Core.Diagnostics;
using Maus.Core.Fixes;
using Maus.Core.Localization;
using Maus.Core.Platform;
using Maus.Core.Preferences;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Engine.Broker;

/// <summary>
/// Le moteur administrateur côté canal : il ne sert que l'interface qui l'a lancé, et seulement la liste fermée des
/// opérations de <see cref="IMausEngine"/>.
/// <list type="bullet">
/// <item>canal nommé au nom aléatoire, réservé au compte du moteur et à la personne devant l'écran (refusé au réseau), première instance obligatoire :
/// un programme qui aurait créé un canal du même nom avant le moteur le fait échouer, au lieu d'être servi à sa place ;</item>
/// <item>chaque connexion vient du processus de l'interface (identifiant vérifié par Windows), sinon elle est fermée ;</item>
/// <item>l'interface présente d'abord la clé dont le moteur a reçu l'empreinte à son lancement ; ensuite chaque demande est
/// signée avec cette clé et porte un numéro neuf (<see cref="EngineKey"/>, <see cref="ReplayWindow"/>) : une demande
/// copiée, rejouée ou modifiée est fermée sans réponse ; chaque réponse est signée et liée au numéro de sa demande ;</item>
/// <item>paramètres vérifiés (identifiants bornés), opération inconnue refusée ;</item>
/// <item>au bout de <see cref="MaxRefusals"/> connexions refusées (autre processus, mauvaise clé, demande rejouée ou
/// modifiée), le moteur s'arrête de lui-même : ses droits administrateur se referment (<see cref="StoppedForSecurity"/>) ;</item>
/// <item>le moteur s'arrête avec l'interface, après <see cref="IdleTimeout"/> sans demande, ou après <see cref="UnclaimedTimeout"/>
/// si l'interface ne s'est jamais présentée : les droits administrateur ne restent pas ouverts plus longtemps que nécessaire
/// (l'action suivante les redemande). Une opération déjà commencée finit d'abord (<see cref="DrainTimeout"/>), sauf à l'arrêt de sécurité.</item>
/// </list>
/// </summary>
public sealed partial class EngineServer(IMausEngine engine, string pipeName, int clientProcessId, string keyFingerprint)
{
    /// <summary>Connexions refusées tolérées avant l'arrêt du moteur par sécurité (demande du porteur, 29/10/2026).</summary>
    public const int MaxRefusals = 3;

    /// <summary>Code de sortie du processus moteur arrêté par sécurité (lu par l'interface pour prévenir l'utilisateur).</summary>
    public const int SecurityStopExitCode = 10;

    private const int MaxIdLength = 200;
    private const int MaxIds = 500;
    private const int MaxTextLength = 2000;
    private static readonly SecurityIdentifier Network = new(WellKnownSidType.NetworkSid, null);

    /// <summary>Numéros de séquence déjà servis : une demande rejouée est refusée.</summary>
    private readonly ReplayWindow _replay = new();

    /// <summary>Clé présentée par l'interface (empreinte vérifiée) ; <c>null</c> tant qu'elle ne l'a pas présentée.</summary>
    private EngineKey? _key;

    /// <summary>
    /// Demandes en cours : l'arrêt pour inactivité attend leur fin ; l'arrêt de sécurité et la fin de l'interface, eux, les annulent.
    /// </summary>
    private int _busy;

    /// <summary>Connexions refusées depuis le lancement.</summary>
    private int _refusals;

    /// <summary>Arrêt du moteur (fin de l'interface, inactivité, ou sécurité).</summary>
    private CancellationTokenSource? _stop;

    /// <summary>
    /// Vrai si le moteur s'est arrêté parce qu'un programme a essayé de le commander (<see cref="MaxRefusals"/> connexions
    /// refusées) : il ne punit personne, il referme seulement ses droits administrateur, et l'interface prévient l'utilisateur.
    /// </summary>
    public bool StoppedForSecurity { get; private set; }

    /// <summary>Fin de la dernière demande (ou démarrage), en millisecondes depuis le démarrage de Windows.</summary>
    private long _lastActivity = Environment.TickCount64;

    /// <summary>Arrêt du moteur après ce délai sans aucune demande (15 minutes, demande du porteur du 22/10/2026).</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Arrêt du moteur si l'interface ne s'est jamais présentée avec sa clé : personne ne peut s'en servir, et l'interface ne
    /// peut pas l'arrêter elle-même, faute de droits sur lui.
    /// </summary>
    public TimeSpan UnclaimedTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Attente des opérations déjà commencées quand le moteur s'arrête, l'écriture d'une correction par exemple : elle ne
    /// s'interrompt pas sans risque. Aucune demande nouvelle n'est acceptée pendant ce temps, et l'arrêt de sécurité n'attend pas.
    /// </summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>Temps laissé à une connexion pour envoyer sa demande.</summary>
    public TimeSpan RequestReadTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>1 quand <c>engine.close</c> a été accepté.</summary>
    private int _closing;

    /// <summary>Sert les demandes jusqu'à la fin de l'interface, l'annulation ou <see cref="IdleTimeout"/> sans demande.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _stop = stop;
        var watchers = new[] { WatchClientAsync(stop), WatchIdleAsync(stop) };
        var serving = new List<Task>();
        try
        {
            using var self = WindowsIdentity.GetCurrent();
            var rules = PipeRules(self.User ?? throw new EngineException(T("Compte de l'utilisateur introuvable.")), SessionUser.Sid());
            var first = true;
            while (!stop.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;
                try
                {
                    pipe = Create(first, rules);
                }
                catch (Exception ex) when (!first && ex is IOException or UnauthorizedAccessException)
                {
                    // Plus d'instance suivante possible : le moteur s'arrête par la voie normale, sans couper ce qui est en cours.
                    Breadcrumbs.Add("moteur : canal impossible à rouvrir (" + ex.GetType().Name + ")");
                    break;
                }

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
                catch (IOException)
                {
                    // Un client parti pendant la connexion ne doit pas arrêter le moteur : on rouvre le canal, sans s'emballer.
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }

                serving.RemoveAll(task => task.IsCompleted);
                serving.Add(ServeAsync(pipe, stop.Token));
            }

            await WaitForRequestsAsync(serving).ConfigureAwait(false);
        }
        finally
        {
            // Les surveillances s'arrêtent avant la libération de « stop », dont elles se servent.
            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(watchers).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ce qui ne s'annule pas (écriture d'une correction, d'une annulation ou d'un point de restauration) finit avant la fin du
    /// processus, sinon son <c>finally</c> ne passerait jamais. L'arrêt de sécurité n'attend pas : il referme les droits tout de suite.
    /// </summary>
    private async Task WaitForRequestsAsync(List<Task> serving)
    {
        if (StoppedForSecurity || serving.Count == 0)
        {
            return;
        }

        var all = Task.WhenAll(serving);
        if (await Task.WhenAny(all, Task.Delay(DrainTimeout, CancellationToken.None)).ConfigureAwait(false) != all)
        {
            Breadcrumbs.Add("moteur : arrêt alors qu'une opération est toujours en cours");
        }
    }

    private NamedPipeServerStream Create(bool first, PipeSecurity rules)
    {
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, options, 0, 0, rules);
    }

    /// <summary>
    /// Droits du canal : refusé au réseau ; le compte du moteur ; et, s'il diffère, la personne devant l'écran, en lecture et
    /// écriture seulement. Sur un compte standard, l'autorisation de Windows se valide avec le mot de passe d'un administrateur :
    /// le moteur tourne sous ce compte, et l'interface qui se connecte reste celle de la personne. Son processus et sa clé
    /// sont vérifiés comme pour toute connexion.
    /// </summary>
    internal static PipeSecurity PipeRules(SecurityIdentifier engineAccount, string? sessionAccount)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(Network, PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(engineAccount, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        if (sessionAccount is not null && sessionAccount != engineAccount.Value)
        {
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sessionAccount), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        }

        return security;
    }

    /// <summary>Aucune demande en cours ni reçue depuis <see cref="IdleTimeout"/> : le moteur s'arrête.</summary>
    private async Task WatchIdleAsync(CancellationTokenSource stop)
    {
        var started = Environment.TickCount64;
        var step = TimeSpan.FromMilliseconds(Math.Clamp(Math.Min(IdleTimeout.TotalMilliseconds, UnclaimedTimeout.TotalMilliseconds) / 4, 50, 30_000));
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(step, stop.Token).ConfigureAwait(false);

                // Sans clé, aucune opération ne peut être en cours : une connexion muette ne retient pas le moteur.
                if (Volatile.Read(ref _key) is null && Environment.TickCount64 - started >= UnclaimedTimeout.TotalMilliseconds)
                {
                    Breadcrumbs.Add("moteur : arrêt, l'interface ne s'est jamais présentée");
                    break;
                }

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
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Interface d'un autre compte : Windows peut refuser d'ouvrir son processus. Le moteur s'arrêtera sur le délai sans demande.
            Breadcrumbs.Add("moteur : fin de l'interface non surveillée (" + ex.NativeErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
            return;
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
                    Refuse("moteur : connexion refusée (autre processus que l'interface)");
                    return;
                }

                // La demande arrive tout de suite : une connexion muette est fermée, et elle ne retient pas le moteur.
                using var reading = CreateReadLimit(cancellationToken);
                if (await EngineProtocol.ReadAsync(pipe, reading.Token).ConfigureAwait(false) is not { } signed)
                {
                    return;
                }

                // Présentation de la clé (premier message d'une connexion) : acceptée seulement avec l'empreinte reçue au
                // lancement, puis la demande suit sur la même connexion.
                if (signed.Key is not null)
                {
                    if (Introduce(signed.Key) is not { } introduced)
                    {
                        Refuse("moteur : clé refusée (empreinte différente de celle reçue au lancement)");
                        return;
                    }

                    await EngineProtocol.WriteAsync(pipe, introduced.SignResponse(0, []), cancellationToken).ConfigureAwait(false);
                    if (await EngineProtocol.ReadAsync(pipe, reading.Token).ConfigureAwait(false) is not { } next)
                    {
                        return;
                    }

                    signed = next;
                }

                // Signature d'abord (le JSON n'est lu qu'ensuite), puis numéro neuf : rien n'est exécuté sinon.
                if (Volatile.Read(ref _key) is not { } key
                    || signed.Key is not null
                    || !key.VerifyRequest(signed)
                    || JsonSerializer.Deserialize<EngineRequest>(signed.Body, EngineProtocol.Json) is not { } request
                    || !_replay.TryAccept(request.Sequence))
                {
                    Refuse("moteur : demande refusée (signature invalide, ou numéro de séquence déjà servi)");
                    return;
                }

                await ServeRequestAsync(pipe, key, request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException)
            {
                // Trame ou message mal formé : il ne peut venir que d'un programme qui essaie, cela compte comme un refus.
                Refuse("moteur : message mal formé refusé");
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // Canal coupé ou arrêt : la connexion est fermée, le moteur continue pour les suivantes.
            }
        }
    }

    private CancellationTokenSource CreateReadLimit(CancellationToken cancellationToken)
    {
        var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(RequestReadTimeout);
        return limit;
    }

    /// <summary>Une demande acceptée : c'est elle, et non la connexion, qui occupe le moteur.</summary>
    private async Task ServeRequestAsync(NamedPipeServerStream pipe, EngineKey key, EngineRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _busy);
        try
        {
            // Après « engine.close », plus aucune demande ne commence : elle serait coupée par l'arrêt.
            if (Volatile.Read(ref _closing) != 0)
            {
                return;
            }

            Texts.Use(request.Language);
            using var writer = new SerialWriter(pipe, key, request.Sequence);

            // L'interface a abandonné la demande : ce qui peut encore s'arrêter s'arrête. Une écriture commencée va au bout :
            // Apply et Revert ne regardent ce jeton qu'avant leur Task.Run, et cesser d'attendre un outil de Windows ne le tue pas.
            using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var leaving = CancelWhenClientLeavesAsync(pipe, scope);
            try
            {
                EngineResponse response;
                try
                {
                    response = new EngineResponse("result", await DispatchAsync(request, writer, scope.Token).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (scope.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Disque plein, fichier refusé… : l'utilisateur doit le lire, sans quoi il ne verrait qu'un canal coupé.
                    // Le journal illisible est une IOException : elle vient de l'opération, pas du canal.
                    Breadcrumbs.Add("moteur : opération " + TraceName(request.Op) + " en erreur (" + ex.GetType().Name + ")");
                    response = new EngineResponse("error", Error: ex.Message);
                }

                try
                {
                    await writer.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException ex)
                {
                    await writer.WriteAsync(new EngineResponse("error", Error: ex.Message), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                await scope.CancelAsync().ConfigureAwait(false);
                await leaving.ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
            Interlocked.Decrement(ref _busy);
        }
    }

    /// <summary>Nom reçu tel qu'il peut aller dans une trace : court et sur une ligne.</summary>
    private static string TraceName(string? op) => op is null ? "(sans nom)" : SensitiveSettings.Shown(op.Length <= 40 ? op : op[..40]);

    /// <summary>
    /// Fin du moteur : ce qu'il tient est rendu dans une attente bornée, et une erreur ne l'empêche pas de se terminer. Une
    /// bibliothèque de capteurs bloquée ne doit jamais garder en vie un processus élevé qui ne sert plus.
    /// </summary>
    /// <returns><c>true</c> si tout a été rendu à temps et sans erreur.</returns>
    public static async Task<bool> ReleaseWithinAsync(Action release, TimeSpan limit)
    {
        ArgumentNullException.ThrowIfNull(release);
        try
        {
            await Task.Run(release).WaitAsync(limit).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            Breadcrumbs.Add("moteur : fermeture pas encore finie à la sortie");
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Breadcrumbs.Add("moteur : fermeture incomplète (" + ex.GetType().Name + ")");
            return false;
        }
    }

    /// <summary>
    /// Attend que le client ferme sa connexion pendant une opération : il n'envoie rien d'autre après sa demande. Un octet reçu
    /// quand même n'est ni lu comme une demande ni pris pour un départ : une connexion ne sert qu'une demande.
    /// </summary>
    private static async Task CancelWhenClientLeavesAsync(Stream pipe, CancellationTokenSource request)
    {
        try
        {
            if (await pipe.ReadAsync(new byte[1], request.Token).ConfigureAwait(false) > 0)
            {
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Canal rompu : le client est parti.
        }

        await request.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// L'utilisateur referme les droits : le moteur s'arrête juste après avoir répondu. Une demande en cours va jusqu'au bout
    /// (<c>false</c>) : écourter une correction laisserait une écriture à moitié faite.
    /// </summary>
    private bool CloseRights()
    {
        Interlocked.Exchange(ref _closing, 1);
        if (Volatile.Read(ref _busy) > 1)
        {
            Interlocked.Exchange(ref _closing, 0);
            return false;
        }

        Breadcrumbs.Add("moteur : droits refermés à la demande de l'utilisateur");
        _stop?.CancelAfter(TimeSpan.FromMilliseconds(200));
        return true;
    }

    /// <summary>
    /// Note une connexion refusée ; à la <see cref="MaxRefusals"/>e, le moteur s'arrête par sécurité. Rien d'autre n'est fait :
    /// pas d'action sur le PC, qui pourrait servir d'arme à un programme malveillant.
    /// </summary>
    private void Refuse(string reason)
    {
        Breadcrumbs.Add(reason);
        if (Interlocked.Increment(ref _refusals) < MaxRefusals)
        {
            return;
        }

        StoppedForSecurity = true;
        Breadcrumbs.Add("moteur : arrêt par sécurité après " + MaxRefusals.ToString(System.Globalization.CultureInfo.InvariantCulture) + " connexions refusées");
        try
        {
            _stop?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Moteur déjà arrêté.
        }
    }

    /// <summary>La clé présentée, si son empreinte est celle reçue au lancement ; une fois acceptée, elle ne change plus.</summary>
    private EngineKey? Introduce(byte[] secret)
    {
        if (EngineKey.Parse(secret) is not { } candidate || !candidate.HasFingerprint(keyFingerprint))
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
            case "engine.close":
                return EngineProtocol.ToElement(CloseRights());
            case "audit":
                // Chaque module terminé est envoyé tout de suite : la barre d'avancement de l'interface suit l'audit.
                var progress = new ChannelProgress<ModuleResult>(pipe, cancellationToken);
                var run = await engine.RunAuditAsync(progress, cancellationToken).ConfigureAwait(false);
                await progress.FlushAsync().ConfigureAwait(false);
                return EngineProtocol.ToElement(run);
            case "module":
                return EngineProtocol.ToElement(await engine.RunModuleAsync(Id(args, "moduleId"), cancellationToken).ConfigureAwait(false));
            case "plan":
                return EngineProtocol.ToElement(await engine.PlanAsync(cancellationToken).ConfigureAwait(false));
            case "apply":
                return EngineProtocol.ToElement(await engine.ApplyAsync(Ids(args, "changeIds"), Options(args), cancellationToken).ConfigureAwait(false));
            case "verify":
                await engine.RecordVerificationAsync(Id(args, "sessionId"), Verified(args), cancellationToken).ConfigureAwait(false);
                return null;
            case "revert":
                // Jamais de « force » par le canal : une valeur changée depuis la correction est laissée telle quelle. Seul
                // l'outil en ligne de commande, lancé par un administrateur, offre --force.
                return EngineProtocol.ToElement(await engine.RevertAsync(Id(args, "sessionId"), OptionalId(args, "changeId"), force: false, cancellationToken).ConfigureAwait(false));
            case "journal":
                return EngineProtocol.ToElement(await engine.BrowseJournalAsync(cancellationToken).ConfigureAwait(false));
            case "session":
                return EngineProtocol.ToElement(await engine.LoadSessionAsync(Id(args, "sessionId"), cancellationToken).ConfigureAwait(false));
            case "prefs.load":
                return EngineProtocol.ToElement(await engine.LoadPreferencesAsync(cancellationToken).ConfigureAwait(false));
            case "prefs.save":
                return EngineProtocol.ToElement(await engine.SavePreferencesAsync(CheckedPreferences(Arg<UserPreferences>(args, "preferences")), cancellationToken).ConfigureAwait(false));
            default:
                return await DispatchWorkshopAsync(request, pipe, cancellationToken).ConfigureAwait(false);
        }
    }

    private static TValue Arg<TValue>(JsonElement? args, string name) =>
        args is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var item)
            ? item.Deserialize<TValue>(EngineProtocol.Json) ?? throw new EngineException(T("Paramètre vide : {0}", name))
            : throw new EngineException(T("Paramètre manquant : {0}", name));

    private static string Id(JsonElement? args, string name) => Checked(Arg<string>(args, name));

    /// <summary>Absent ou nul : pas d'identifiant. Tout autre type est une erreur, jamais une demande plus large que voulu.</summary>
    private static string? OptionalId(JsonElement? args, string name)
    {
        if (args is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(name, out var item) || item.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return item.ValueKind == JsonValueKind.String ? Checked(item.GetString()!) : throw new EngineException(T("Identifiant refusé."));
    }

    private static IReadOnlyList<string> Ids(JsonElement? args, string name)
    {
        var ids = Arg<List<string>>(args, name);
        return ids.Count > MaxIds ? throw new EngineException(T("Trop d'identifiants.")) : [.. ids.Select(Checked)];
    }

    /// <summary>Options d'application : la description du point de restauration est un texte libre, borné comme un identifiant.</summary>
    private static ApplyOptions Options(JsonElement? args)
    {
        var options = Arg<ApplyOptions>(args, "options");
        return options.RestorePointDescription.Length is > 0 and <= MaxIdLength && !options.RestorePointDescription.Any(char.IsControl)
            ? options
            : throw new EngineException(T("Description refusée."));
    }

    /// <summary>Résultats de vérification : pas plus d'entrées que de corrections possibles, messages bornés.</summary>
    private static IReadOnlyList<VerifiedOutcome> Verified(JsonElement? args)
    {
        var verified = Arg<IReadOnlyList<VerifiedOutcome>>(args, "verified");
        return verified.Count <= MaxIds && verified.All(v => (v.Message?.Length ?? 0) <= MaxTextLength)
            ? verified
            : throw new EngineException(T("Vérification refusée."));
    }

    /// <summary>
    /// Choix à enregistrer (0.8.6) : liste des constats « voulus » bornée comme les identifiants, valeur constatée bornée comme
    /// un message, énumérations définies, profil Game Bar 1 à 3. Le moteur n'en garde que les choix protégés.
    /// </summary>
    internal static UserPreferences CheckedPreferences(UserPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        var acknowledged = preferences.Acknowledged ?? [];
        var skipped = preferences.SkippedModules ?? [];
        var kept = preferences.KeptRefreshRates ?? [];
        return skipped.Count <= MaxIds && skipped.All(id => id is { Length: > 0 and <= MaxIdLength } && !id.Any(char.IsControl))
            && kept.Count <= MaxIds && kept.All(k => k is { SourceName.Length: <= MaxIdLength, Label.Length: <= MaxIdLength })
            && acknowledged.Count <= MaxIds
            && acknowledged.All(a => a is { FindingId: { Length: > 0 and <= MaxIdLength } id } && !id.Any(char.IsControl)
                && (a.Current?.Length ?? 0) <= MaxTextLength && (a.Language is null || Texts.Languages.Any(l => l.Code == a.Language)))
            && Enum.IsDefined(preferences.LaptopPower)
            && Enum.IsDefined(preferences.WeeklyNotification)
            && Enum.IsDefined(preferences.Theme)
            && Enum.IsDefined(preferences.Accent)
            && Enum.IsDefined(preferences.Animations)
            && Enum.IsDefined(preferences.OverlayCorner)
            && Enum.IsDefined(preferences.SearchEngine)
            && preferences.GameBarProfile is null or >= 1 and <= 3
            ? preferences
            : throw new EngineException(T("Choix refusés."));
    }

    /// <summary>Identifiant de correction, de module ou de séance : court, sans caractère de contrôle ni chemin.</summary>
    private static string Checked(string id) =>
        id.Length is > 0 and <= MaxIdLength && !id.Any(c => char.IsControl(c) || c is '\\' or '/' or ':')
            ? id
            : throw new EngineException(T("Identifiant refusé."));

    /// <summary>
    /// Écritures d'une connexion, une à la fois : les modules qui finissent ensemble ne mélangent pas leurs messages. Chaque
    /// réponse est signée et liée au numéro de la demande.
    /// </summary>
    private sealed class SerialWriter(Stream pipe, EngineKey key, long sequence) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task WriteAsync(EngineResponse response, CancellationToken cancellationToken)
        {
            var signed = key.SignResponse(sequence, JsonSerializer.SerializeToUtf8Bytes(response, EngineProtocol.Json));
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

    /// <summary>Progression envoyée tout de suite, sur le fil qui l'émet (pas de contexte de synchronisation dans le moteur).</summary>
    private sealed class ChannelProgress<TValue>(SerialWriter pipe, CancellationToken cancellationToken) : IProgress<TValue>
    {
        private readonly List<Task> _writes = [];

        public void Report(TValue value)
        {
            lock (_writes)
            {
                _writes.Add(pipe.WriteAsync(new EngineResponse("progress", EngineProtocol.ToElement(value)), cancellationToken));
            }
        }

        /// <summary>Attend les envois en cours : le résultat ne part qu'après eux.</summary>
        public Task FlushAsync()
        {
            lock (_writes)
            {
                return Task.WhenAll(_writes.ToArray());
            }
        }
    }
}

/// <summary>Réponse au « ping » : version du moteur, son processus, et s'il a bien les droits administrateur.</summary>
public sealed record EnginePing(string Version, int ProcessId, bool IsElevated)
{
    /// <summary>Vrai si la réponse vient bien du moteur lancé par l'interface, avec les droits administrateur.</summary>
    public bool IsElevatedEngine(int processId) => IsElevated && ProcessId == processId;
}
