using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Maus.App.ViewModels;
using Maus.Core.Diagnostics;
using Maus.Core.Engine.Broker;
using Maus.Core.Preferences;

namespace Maus.App;

/// <summary>
/// Le moteur de MAUS pour toute l'interface. Depuis la 0.8.1, MAUS s'ouvre sans droits administrateur : le moteur
/// administrateur n'est lancé qu'à la première action qui en a besoin (<see cref="OnDemandMausEngine"/>). MAUS lancé en
/// administrateur : le moteur reste dans ce processus.
/// </summary>
public static class EngineHost
{
    private static IMausEngine? s_current;
    private static IPreferencesStore? s_preferences;

    public static IMausEngine Current
    {
        get => s_current ??= new LocalMausEngine();
        set => s_current = value;
    }

    /// <summary>
    /// Fin de MAUS : le moteur rend ce qu'il tient (mesure de latence, capteurs). L'attente est bornée : une bibliothèque de
    /// capteurs bloquée ne doit jamais garder le processus en vie sans fenêtre.
    /// </summary>
    internal static void Close()
    {
        if (s_current is not { } engine)
        {
            return;
        }

        try
        {
            if (!Task.Run(engine.Dispose).Wait(TimeSpan.FromSeconds(6)))
            {
                Breadcrumbs.Add("moteur : fermeture pas encore finie à la sortie");
            }
        }
        catch (AggregateException ex)
        {
            Breadcrumbs.Add("moteur : fermeture incomplète (" + ex.InnerException?.GetType().Name + ")");
        }
    }

    /// <summary>Moteur lancé à la demande ; <c>null</c> si MAUS a été lancé en administrateur.</summary>
    public static OnDemandMausEngine? OnDemand => s_current as OnDemandMausEngine;

    /// <summary>
    /// Les droits administrateur sont là sans rien demander : MAUS lancé en administrateur, ou moteur administrateur en marche.
    /// </summary>
    public static bool IsElevated => OnDemand?.IsElevatedRunning ?? Environment.IsPrivilegedProcess;

    /// <summary>
    /// Les droits administrateur viennent de s'ouvrir (moteur lancé) ou de se refermer (moteur arrêté : 15 minutes sans
    /// demande, arrêt par sécurité, fermeture). Appelé sur n'importe quel fil.
    /// </summary>
    public static event EventHandler? ElevationChanged;

    /// <summary>Étape de la demande de droits : l'interface réagit dès le clic, sans attendre le moteur. Appelé sur n'importe quel fil.</summary>
    public static event EventHandler<ElevationStage>? StageChanged;

    internal static void ReportStage(ElevationStage stage) => StageChanged?.Invoke(null, stage);

    /// <summary>
    /// Le moteur s'est arrêté par sécurité : un programme a essayé de le commander (<see cref="EngineServer.MaxRefusals"/>
    /// connexions refusées). Appelé sur n'importe quel fil.
    /// </summary>
    public static event EventHandler? StoppedForSecurity;

    /// <summary>Dernier code de sortie du moteur : 0 pour un arrêt normal (inactivité, ou droits refermés).</summary>
    internal static int? LastEngineExitCode { get; private set; }

    /// <summary>Le processus moteur s'est terminé avec ce code de sortie (<c>null</c> si Windows ne l'a pas donné).</summary>
    internal static void OnEngineExited(int? exitCode)
    {
        LastEngineExitCode = exitCode;
        Breadcrumbs.Add("moteur administrateur terminé (code " + (exitCode?.ToString(CultureInfo.InvariantCulture) ?? "?") + ")");
        ElevationChanged?.Invoke(null, EventArgs.Empty);
        if (exitCode == EngineServer.SecurityStopExitCode)
        {
            StoppedForSecurity?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>Choix de l'utilisateur : affichage dans son propre fichier, choix protégés par le moteur.</summary>
    public static IPreferencesStore Preferences
    {
        get => s_preferences ??= new EnginePreferencesStore(Current);
        set => s_preferences = value;
    }

    /// <summary>
    /// Prépare le moteur et les choix au démarrage de l'interface. Rien n'est lancé et aucune autorisation n'est demandée.
    /// </summary>
    public static void Configure()
    {
        Current = Environment.IsPrivilegedProcess
            ? new LocalMausEngine()
            : new OnDemandMausEngine(new LocalMausEngine(preferences: new MemoryPreferencesStore()), EngineLauncher.StartAsync);
        var split = new SplitPreferencesStore(new FilePreferencesStore(FilePreferencesStore.UserDirectory, new UserDirectoryProtector()), new EngineMachinePreferences());
        Preferences = split;
        if (OnDemand is { } onDemand)
        {
            // Le moteur vient de démarrer : les choix protégés (constats « voulus », profil Game Bar) deviennent lisibles.
            onDemand.ElevatedStarted += (_, _) => ElevationChanged?.Invoke(null, EventArgs.Empty);
            onDemand.ElevatedStarted += (_, _) => Task.Run(() =>
            {
                try
                {
                    split.Refresh();
                }
                catch (Exception ex) when (ex is EngineException or IOException)
                {
                    Breadcrumbs.Add("choix protégés illisibles après le démarrage du moteur (" + ex.GetType().Name + ")");
                }
            });
        }
    }

    /// <summary>Choix protégés, lus et écrits par le moteur.</summary>
    private sealed class EngineMachinePreferences : IMachinePreferences
    {
        public bool IsAvailable => IsElevated;

        public UserPreferences Load() => new EnginePreferencesStore(Current).Load();

        public void Save(UserPreferences preferences) => new EnginePreferencesStore(Current).Save(preferences);

        public void CopyIfRunning(UserPreferences preferences)
        {
            if (OnDemand is not { } onDemand)
            {
                Save(preferences);
                return;
            }

            var state = onDemand.SavePreferencesIfRunningAsync(preferences, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
            if (state?.NotSaved is { } reason)
            {
                throw new PreferencesNotSavedException(reason, state.Preferences);
            }
        }
    }
}

/// <summary>Lance le moteur administrateur avec la demande d'autorisation de Windows, et s'y relie.</summary>
internal static class EngineLauncher
{
    /// <summary>Code Windows d'une demande d'autorisation refusée (ERROR_CANCELLED).</summary>
    private const int Cancelled = 1223;

    /// <exception cref="EngineDeclinedException">L'utilisateur a refusé l'autorisation.</exception>
    public static async Task<ElevatedEngineSession> StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await StartEngineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            EngineHost.ReportStage(ElevationStage.None);
            throw;
        }
    }

    private static Task<ElevatedEngineSession> StartEngineAsync(CancellationToken cancellationToken) => Task.Run(async () =>
    {
        // La demande d'autorisation bloque l'appel jusqu'à la réponse : elle se fait hors du fil de l'interface.
        var exe = Environment.ProcessPath ?? throw new EngineException(Maus.Core.Localization.Texts.T("MAUS.exe introuvable."));

        // MAUS lancé depuis un dossier modifiable sans droits (Téléchargements, Bureau, clé USB…) : un autre programme pourrait
        // y avoir remplacé MAUS.exe, et c'est lui que Windows lancerait avec les droits administrateur. L'utilisateur décide,
        // prévenu (demande du porteur du 29/10/2026 : « une sécurité de plus »).
        if (!Maus.Core.Engine.ScheduledAudit.IsInProtectedFolder(exe, Maus.Core.Engine.ScheduledAudit.ProtectedRoots()) && !ConfirmUnprotectedLocation(exe))
        {
            Breadcrumbs.Add("moteur : droits administrateur non demandés (MAUS hors d'un dossier protégé)");
            throw new EngineDeclinedException(Maus.Core.Localization.Texts.T("Droits administrateur non demandés. Pour les utiliser sans risque, installez MAUS avec son installateur, par winget ou par le Microsoft Store. MAUS reste en mode limité."));
        }

        var pipe = "MAUS-" + Guid.NewGuid().ToString("N");

        // Clé de signature des messages, propre à ce moteur : seule son empreinte passe sur la ligne de commande (lisible par
        // d'autres programmes) ; la clé elle-même est présentée par le canal privé à la première demande.
        var key = EngineKey.Create();
        Process engine;
        EngineHost.ReportStage(ElevationStage.Asking);
        try
        {
            engine = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                // Le moteur ne prend pas pour dossier courant celui de la fenêtre (0.8.6).
                WorkingDirectory = Maus.Core.Platform.SystemTool.WorkingFolder,
                Arguments = $"{App.EngineArgument} {pipe} {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)} {key.Fingerprint()}",
            }) ?? throw new EngineException(Maus.Core.Localization.Texts.T("Le moteur de MAUS n'a pas démarré."));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Cancelled)
        {
            Breadcrumbs.Add("moteur : autorisation administrateur refusée");
            throw new EngineDeclinedException(EngineDeclinedException.DefaultMessage, ex);
        }
        catch (Win32Exception ex)
        {
            throw new EngineException(Maus.Core.Localization.Texts.T("Le moteur de MAUS n'a pas démarré : {0}", Maus.Core.Diagnostics.ErrorText.For(ex)), ex);
        }

        EngineHost.ReportStage(ElevationStage.Starting);
        var remote = new RemoteMausEngine(pipe, engine.Id, key);
        try
        {
            var ping = await PingUntilExitAsync(remote, engine, cancellationToken).ConfigureAwait(false);
            if (!ping.IsElevatedEngine(engine.Id))
            {
                throw new EngineException(Maus.Core.Localization.Texts.T("Le moteur de MAUS n'a pas répondu comme prévu (autre processus, ou sans droits administrateur) : MAUS reste en mode limité."));
            }
        }
        catch
        {
            engine.Dispose();
            throw;
        }

        Breadcrumbs.Add("moteur administrateur relié (processus " + engine.Id.ToString(CultureInfo.InvariantCulture) + ")");

        // Fin du moteur : l'interface repasse en mode limité, et prévient l'utilisateur s'il s'est arrêté par sécurité.
        engine.Exited += (_, _) => EngineHost.OnEngineExited(ExitCodeOf(engine));
        engine.EnableRaisingEvents = true;
        return new ElevatedEngineSession(remote, () => HasExited(engine));
    }, cancellationToken);

    /// <summary>Attend la première réponse du moteur, sans attendre jusqu'au bout du délai un moteur déjà arrêté.</summary>
    private static async Task<EnginePing> PingUntilExitAsync(RemoteMausEngine remote, Process engine, CancellationToken cancellationToken)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ping = remote.PingAsync(waiting.Token);
        while (await Task.WhenAny(ping, Task.Delay(250, CancellationToken.None)).ConfigureAwait(false) != ping)
        {
            if (HasExited(engine))
            {
                await waiting.CancelAsync().ConfigureAwait(false);
                break;
            }
        }

        try
        {
            return await ping.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or EngineException && !cancellationToken.IsCancellationRequested && HasExited(engine))
        {
            throw new EngineException(Maus.Core.Localization.Texts.T("Le moteur de MAUS s'est arrêté dès son lancement : MAUS reste en mode limité."), ex);
        }
    }

    private static bool HasExited(Process engine)
    {
        try
        {
            return engine.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return true;
        }
    }

    /// <summary>L'utilisateur a déjà accepté, pendant cette séance, de donner les droits à un MAUS hors d'un dossier protégé.</summary>
    private static volatile bool s_unprotectedAccepted;

    /// <summary>
    /// Avertit que MAUS n'est pas dans un dossier protégé et demande s'il faut quand même lui donner les droits administrateur
    /// (réponse par défaut : non). Posée une fois par séance ; appelée hors du fil de l'interface.
    /// </summary>
    private static bool ConfirmUnprotectedLocation(string exe)
    {
        if (s_unprotectedAccepted)
        {
            return true;
        }

        var folder = Path.GetDirectoryName(exe) ?? exe;
        var accepted = System.Windows.Application.Current?.Dispatcher.Invoke(() => Views.QuestionWindow.Ask(
            Maus.Core.Localization.Texts.T("MAUS n'est pas dans un dossier protégé"),
            [
                Maus.Core.Localization.Texts.T("Ce MAUS est lancé depuis {0}, un dossier que d'autres programmes peuvent modifier sans droits administrateur. Un virus pourrait y avoir remplacé MAUS.exe : c'est alors à lui que Windows donnerait les droits administrateur.", folder),
                Maus.Core.Localization.Texts.T("Pour être protégé, installez MAUS avec son installateur (dossier Program Files), par winget ou par le Microsoft Store."),
            ],
            Maus.Core.Localization.Texts.T("Donner quand même les droits administrateur à ce MAUS ?"),
            Maus.Core.Localization.Texts.T("Oui, donner les droits"),
            Maus.Core.Localization.Texts.T("Non, rester en mode limité"))) == true;
        s_unprotectedAccepted = accepted;
        return accepted;
    }

    /// <summary>Code de sortie du moteur, ou <c>null</c> si Windows ne le donne pas.</summary>
    private static int? ExitCodeOf(Process engine)
    {
        try
        {
            return engine.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }
}
