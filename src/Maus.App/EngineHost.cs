using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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

    /// <summary>
    /// Le moteur s'est arrêté par sécurité : un programme a essayé de le commander (<see cref="EngineServer.MaxRefusals"/>
    /// connexions refusées). Appelé sur n'importe quel fil.
    /// </summary>
    public static event EventHandler? StoppedForSecurity;

    /// <summary>Le processus moteur s'est terminé avec ce code de sortie (<c>null</c> si Windows ne l'a pas donné).</summary>
    internal static void OnEngineExited(int? exitCode)
    {
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

    /// <summary>Fichier des choix d'affichage de l'utilisateur, écrit sans droits administrateur.</summary>
    public static string UserSettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MAUS", "settings");

    /// <summary>
    /// Prépare le moteur et les choix au démarrage de l'interface. Rien n'est lancé et aucune autorisation n'est demandée.
    /// </summary>
    public static void Configure()
    {
        Current = Environment.IsPrivilegedProcess
            ? new LocalMausEngine()
            : new OnDemandMausEngine(new LocalMausEngine(preferences: new MemoryPreferencesStore()), EngineLauncher.StartAsync);
        var split = new SplitPreferencesStore(new FilePreferencesStore(UserSettingsDirectory, new UserDirectoryProtector()), new EngineMachinePreferences());
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
    }
}

/// <summary>Lance le moteur administrateur avec la demande d'autorisation de Windows, et s'y relie.</summary>
internal static class EngineLauncher
{
    /// <summary>Code Windows d'une demande d'autorisation refusée (ERROR_CANCELLED).</summary>
    private const int Cancelled = 1223;

    /// <exception cref="EngineDeclinedException">L'utilisateur a refusé l'autorisation.</exception>
    public static Task<ElevatedEngineSession> StartAsync(CancellationToken cancellationToken) => Task.Run(async () =>
    {
        // La demande d'autorisation bloque l'appel jusqu'à la réponse : elle se fait hors du fil de l'interface.
        var exe = Environment.ProcessPath ?? throw new EngineException("MAUS.exe introuvable.");
        var pipe = "MAUS-" + Guid.NewGuid().ToString("N");

        // Clé de signature des messages, propre à ce moteur : seule son empreinte passe sur la ligne de commande (lisible par
        // d'autres programmes) ; la clé elle-même est présentée par le canal privé à la première demande.
        var key = EngineKey.Create();
        Process engine;
        try
        {
            engine = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
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
            throw new EngineException(Maus.Core.Localization.Texts.T("Le moteur de MAUS n'a pas démarré : {0}", ex.Message), ex);
        }

        var remote = new RemoteMausEngine(pipe, engine.Id, key);
        try
        {
            await remote.PingAsync(cancellationToken).ConfigureAwait(false);
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
        return new ElevatedEngineSession(remote, () =>
        {
            try
            {
                return engine.HasExited;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                return true;
            }
        });
    }, cancellationToken);

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
