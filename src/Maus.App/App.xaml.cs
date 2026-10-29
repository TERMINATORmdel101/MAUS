using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Maus.App.Appearance;
using Maus.Core;
using Maus.Core.Diagnostics;
using Maus.Core.Engine;
using Maus.Core.Localization;
using Maus.Core.Preferences;
using Maus.Core.Workshop;

namespace Maus.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Filet de sécurité : fil de l'interface, autres fils (l'erreur ferme MAUS, la trace reste) et tâches jamais attendues.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Réglages régionaux de Windows (séparateur de liste, virgule décimale), gardés pour les exports CSV avant que
        // UseLanguage ne remplace la culture du fil par celle de la langue de MAUS.
        Texts.UseRegionalCulture(CultureInfo.CurrentCulture);

        base.OnStartup(e);

        if (e.Args.Length >= 4 && e.Args[0].Equals(EngineArgument, StringComparison.OrdinalIgnoreCase))
        {
            // Moteur administrateur (0.8.0) : aucune fenêtre ; il sert l'interface qui l'a lancé, puis s'arrête avec elle.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            RunEngineAsync(e.Args[1], e.Args[2], e.Args[3]).Forget("moteur", _ => Shutdown(1));
            return;
        }

        if (e.Args.Contains(ScheduledAudit.Argument, StringComparer.OrdinalIgnoreCase))
        {
            // Audit planifié : lancé avec les droits administrateur, il lit les choix lui-même (le benchmark reçoit sa langue).
            var own = FilePreferencesStore.CreateDefault().Load();
            UseLanguage(own.Language);
            AppearanceManager.DetectComponents();
            AppearanceManager.Apply(own);
        }

        if (e.Args.Contains(ScheduledAudit.Argument, StringComparer.OrdinalIgnoreCase))
        {
            // Audit planifié : pas de fenêtre, sauf si un problème rouge mérite d'être signalé.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Une erreur qui échapperait à l'audit ne doit pas laisser un MAUS invisible tourner indéfiniment.
            RunScheduledAuditAsync().Forget("audit hebdomadaire", _ => Shutdown(1));
            return;
        }

        if (e.Args.Length > 0 && e.Args[0].Equals("--benchmark", StringComparison.OrdinalIgnoreCase))
        {
            // Benchmark visuel : processus séparé lancé par l'Atelier, avec sa propre fenêtre plein écran (pas de WPF).
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(Maus.Bench.BenchmarkProgram.Run(e.Args[1..]));
            return;
        }

        s_interactive = true;

        // MAUS s'arrête quand sa fenêtre principale se ferme, même si une fenêtre annexe traîne encore.
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        if (!AcquireSingleInstance())
        {
            Shutdown(0);
            return;
        }

        // 0.8.1 : aucune autorisation demandée à l'ouverture ; le moteur administrateur attend la première action qui en a besoin.
        EngineHost.Configure();

        // Le moteur s'est coupé lui-même après des connexions refusées : l'utilisateur est prévenu (rien d'autre n'est fait).
        EngineHost.StoppedForSecurity += (_, _) => Dispatcher.InvokeAsync(() =>
            MessageBox.Show(Ui.EngineSecurityStop, "MAUS", MessageBoxButton.OK, MessageBoxImage.Warning));
        var preferences = LoadPreferences();
        // Fichier des choix d'affichage absent : toute première ouverture (pas de « Quoi de neuf »).
        s_firstOpening = (EngineHost.Preferences as SplitPreferencesStore)?.State == PreferencesFileState.Absent;
        UseLanguage(preferences.Language);
        AppearanceManager.DetectComponents();
        AppearanceManager.Apply(preferences);

        // Écran de démarrage : le logo s'affiche tout de suite, pendant que la fenêtre principale se prépare.
        SplashScreen? splash = new SplashScreen(typeof(App).Assembly, "Assets/splash.png");
        splash.Show(autoClose: false);

        if (MustAskDisclaimer(preferences))
        {
            splash.Close(TimeSpan.Zero);
            splash = null;
            if (!AskDisclaimer())
            {
                Shutdown(0);
                return;
            }

            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        ShowMainWindow(splash);
        UiWatchdog.Start(Dispatcher);
    }

    /// <summary>
    /// Argument du processus « moteur » : <c>--engine &lt;canal&gt; &lt;processus de l'interface&gt; &lt;empreinte&gt;</c>
    /// (empreinte SHA-256 de la clé de signature des messages, <see cref="Maus.Core.Engine.Broker.EngineKey"/>).
    /// </summary>
    internal const string EngineArgument = "--engine";

    private static UserPreferences LoadPreferences()
    {
        try
        {
            return EngineHost.Preferences.Load();
        }
        catch (Exception ex) when (ex is IOException or Maus.Core.Engine.Broker.EngineException or Maus.Core.Fixes.JournalUnsafeException)
        {
            return UserPreferences.Default;
        }
    }

    /// <summary>Le processus moteur : sert le canal privé jusqu'à la fermeture de l'interface, puis rend tout à Windows.</summary>
    private async Task RunEngineAsync(string pipe, string clientPid, string keyFingerprint)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(pipe, "^MAUS-[0-9a-f]{32}$")
            || !int.TryParse(clientPid, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || !Maus.Core.Engine.Broker.EngineKey.IsFingerprint(keyFingerprint))
        {
            Shutdown(2);
            return;
        }

        var engine = new Maus.Core.Engine.Broker.LocalMausEngine { ProtectedProcessId = pid };
        var server = new Maus.Core.Engine.Broker.EngineServer(engine, pipe, pid, keyFingerprint);
        try
        {
            await server.RunAsync(CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            await Task.Run(engine.Dispose).ConfigureAwait(true);

            // Arrêt par sécurité : le code de sortie permet à l'interface de prévenir l'utilisateur.
            Shutdown(server.StoppedForSecurity ? Maus.Core.Engine.Broker.EngineServer.SecurityStopExitCode : 0);
        }
    }

    /// <summary>Tenu tant que MAUS est ouvert (signale aux lancements suivants qu'une fenêtre existe déjà).</summary>
    private static Mutex? s_singleInstance;

    /// <summary>Une fenêtre de MAUS est (ou va être) affichée : une erreur fatale s'annonce par un message.</summary>
    private static volatile bool s_interactive;

    /// <summary>
    /// Une seule fenêtre de MAUS à la fois : deux MAUS ouverts se disputeraient le pilote et le bus des barrettes. Prend le
    /// verrou <c>Local\MAUS.FenetrePrincipale</c>, ou ramène devant le MAUS déjà ouvert.
    /// </summary>
    /// <returns><c>true</c> si ce MAUS peut afficher sa fenêtre.</returns>
    private static bool AcquireSingleInstance()
    {
        s_singleInstance = new Mutex(true, @"Local\MAUS.FenetrePrincipale", out var first);
        return first || TakeOverFromWindowlessInstance();
    }

    /// <summary>
    /// Avertissements à montrer : jamais acceptés, ou texte changé depuis. Un fichier des choix présent mais momentanément
    /// illisible n'est pas un premier lancement : la fenêtre ne revient pas pour autant.
    /// </summary>
    private static bool MustAskDisclaimer(UserPreferences preferences) =>
        preferences.DisclaimerAccepted < Maus.Core.Legal.Disclaimer.Version
        && (EngineHost.Preferences as SplitPreferencesStore)?.State != PreferencesFileState.Unavailable;

    /// <summary>
    /// Avertissements au premier lancement (et après chaque changement de fond du texte).
    /// </summary>
    /// <returns><c>false</c> si l'utilisateur ne les accepte pas : MAUS doit alors se fermer.</returns>
    private bool AskDisclaimer()
    {
        // Pendant cette fenêtre, fermer celle-ci ne doit pas être pris pour la fermeture de MAUS par Windows.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (new Views.DisclaimerWindow().ShowDialog() != true)
        {
            return false;
        }

        try
        {
            EngineHost.Preferences.Update(p => p with { DisclaimerAccepted = Maus.Core.Legal.Disclaimer.Version });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Maus.Core.Engine.Broker.EngineException)
        {
            // Accord non enregistré : les avertissements reviendront au prochain lancement, MAUS s'ouvre quand même.
        }

        return true;
    }

    /// <summary>
    /// Un autre MAUS tourne. S'il a une fenêtre, elle revient devant et ce lancement s'arrête. S'il n'en a aucune (MAUS
    /// bloqué), l'utilisateur peut le fermer et continuer avec celui-ci.
    /// </summary>
    /// <returns><c>true</c> si ce MAUS peut démarrer.</returns>
    private static bool TakeOverFromWindowlessInstance()
    {
        if (Maus.Core.Platform.SingleInstance.BringExistingToFront())
        {
            return false;
        }

        var answer = MessageBox.Show(
            Texts.T("Un autre MAUS est déjà en cours d'exécution, mais sans fenêtre : il est sans doute bloqué, et il peut empêcher la lecture de la mémoire. Le fermer et ouvrir celui-ci ?"),
            "MAUS",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes)
        {
            return false;
        }

        Maus.Core.Platform.SingleInstance.CloseWindowlessOthers();
        try
        {
            // Le MAUS fermé tenait ce verrou : Windows le rend « abandonné », il est alors à nous.
            return s_singleInstance!.WaitOne(TimeSpan.FromSeconds(10));
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    /// <summary>
    /// Fin de MAUS : le processus se termine vraiment. Un fil d'une bibliothèque tierce ne doit jamais le garder en vie
    /// sans fenêtre, verrous du matériel pris (constaté chez le porteur : un MAUS invisible bloquait le bus des barrettes).
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnExit(e);
        PresentMonCapture.WaitForBackgroundStops(TimeSpan.FromSeconds(8));
        Environment.Exit(e.ApplicationExitCode);
    }

    private static void ShowMainWindow(SplashScreen? splash = null)
    {
        var window = new MainWindow();
        Current.MainWindow = window;
        if (splash is not null)
        {
            // L'écran de démarrage s'efface en douceur une fois la fenêtre dessinée. Secours : une fenêtre ouverte réduite
            // n'est pas dessinée tout de suite, l'écran de démarrage ne doit pas rester affiché pour autant.
            var closed = false;
            var fallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            void CloseSplash(TimeSpan fade)
            {
                fallback.Stop();
                if (!closed)
                {
                    closed = true;
                    splash.Close(fade);
                }
            }

            window.ContentRendered += (_, _) => CloseSplash(TimeSpan.FromMilliseconds(350));
            fallback.Tick += (_, _) => CloseSplash(TimeSpan.Zero);
            fallback.Start();
        }

        window.Show();

        // Une fois la fenêtre dessinée : les nouveautés d'une nouvelle version, puis (MAUS sans droits administrateur) la
        // proposition d'activer toutes les fonctionnalités. La demande d'autorisation de Windows ne vient qu'après « Oui ».
        void Dialogs(object? sender, EventArgs e)
        {
            window.ContentRendered -= Dialogs;
            window.Dispatcher.BeginInvoke(() => StartupDialogsAsync().Forget("fenêtres d'ouverture"));
        }

        window.ContentRendered += Dialogs;
    }

    /// <summary>Toute première ouverture de MAUS sur ce compte (fichier des choix d'affichage absent).</summary>
    private static bool s_firstOpening;

    private static async Task StartupDialogsAsync()
    {
        var preferences = LoadPreferences();
        if (!string.Equals(preferences.LastSeenVersion, AppVersion.Display, StringComparison.Ordinal))
        {
            if (!s_firstOpening)
            {
                Views.WhatsNewWindow.ShowFor(AppVersion.Display);
            }

            try
            {
                await Task.Run(() => EngineHost.Preferences.Update(p => p with { LastSeenVersion = AppVersion.Display }));
            }
            catch (Exception ex) when (ex is IOException or Maus.Core.Engine.Broker.EngineException or Maus.Core.Fixes.JournalUnsafeException)
            {
                // Non enregistré : les nouveautés reviendront à la prochaine ouverture.
            }
        }

        if (EngineHost.OnDemand is not null && preferences.OfferAllFeatures)
        {
            await OfferAllFeaturesAsync();
        }
    }

    private static async Task OfferAllFeaturesAsync()
    {
        var (accept, dontAskAgain) = Views.ElevationOfferWindow.Ask();
        if (dontAskAgain)
        {
            try
            {
                await Task.Run(() => EngineHost.Preferences.Update(p => p with { OfferAllFeatures = false }));
            }
            catch (Exception ex) when (ex is IOException or Maus.Core.Engine.Broker.EngineException or Maus.Core.Fixes.JournalUnsafeException)
            {
                // Choix non enregistré : la proposition reviendra à la prochaine ouverture.
            }
        }

        if (accept && EngineHost.OnDemand is { } onDemand)
        {
            try
            {
                await onDemand.EnsureElevatedAsync(CancellationToken.None);
            }
            catch (Maus.Core.Engine.Broker.EngineException ex)
            {
                MessageBox.Show(ex.Message, "MAUS", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    /// <summary>
    /// Audit hebdomadaire lancé par le Planificateur de tâches : lecture seule, score gardé dans l'historique (sauf score
    /// partiel), notification seulement s'il y a un problème rouge, avec les modules non vérifiés. Rien n'est envoyé.
    /// </summary>
    private async Task RunScheduledAuditAsync()
    {
        try
        {
            var context = await Task.Run(AuditContext.CreateDefault);
            var results = await AuditEngine.CreateWithBuiltInModules().RunAsync(context);

            // Un score partiel (module en erreur ou en délai dépassé) n'est pas enregistré : il paraîtrait meilleur que la réalité.
            if (ScoreTrends.HealthEntry(results, DateTimeOffset.Now) is { } entry)
            {
                await Task.Run(() =>
                {
                    try
                    {
                        BenchmarkHistory.CreateHealth().Add(entry);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Historique non écrit : sans conséquence pour l'audit.
                    }
                });
            }

            var problems = ScheduledAudit.WorthNotifying(results);
            if (problems.Count == 0)
            {
                Shutdown(0);
                return;
            }

            var notification = new Views.NotificationWindow(ScheduledAudit.NotificationText(problems, results));
            notification.Closed += (_, _) =>
            {
                if (notification.OpenRequested)
                {
                    OpenAfterScheduledAudit();
                }
                else
                {
                    Shutdown(0);
                }
            };
            notification.Show();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CrashLog.Write(ex, "Audit hebdomadaire interrompu : MAUS s'arrête");
            Shutdown(1);
        }
    }

    /// <summary>
    /// « Ouvrir » après l'audit hebdomadaire : comme un lancement normal (une seule fenêtre de MAUS, avertissements acceptés),
    /// puis l'audit est refait dans la fenêtre.
    /// </summary>
    private void OpenAfterScheduledAudit()
    {
        s_interactive = true;
        if (!AcquireSingleInstance())
        {
            // Un MAUS est déjà ouvert : sa fenêtre revient devant, celui-ci s'arrête.
            Shutdown(0);
            return;
        }

        if (MustAskDisclaimer(LoadPreferences()) && !AskDisclaimer())
        {
            Shutdown(0);
            return;
        }

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        ShowMainWindow();
        UiWatchdog.Start(Dispatcher);
        if (Current.MainWindow?.DataContext is ViewModels.MainViewModel model && model.RunAuditCommand.CanExecute(null))
        {
            model.RunAuditCommand.Execute(null);
        }
    }

    /// <summary>Active la langue choisie (ou celle de Windows), y compris pour les dates et les nombres.</summary>
    public static void UseLanguage(string? code)
    {
        Texts.Use(code);
        CultureInfo.DefaultThreadCurrentCulture = Texts.Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Texts.Culture;
        CultureInfo.CurrentCulture = Texts.Culture;
        CultureInfo.CurrentUICulture = Texts.Culture;
    }

    /// <summary>Change de langue : la fenêtre est recréée dans la nouvelle langue, puis l'audit est relancé s'il avait été fait.</summary>
    public static void SwitchLanguage(string code, bool rerunAudit)
    {
        UseLanguage(code);
        var old = Current.MainWindow;
        var window = new MainWindow();
        Current.MainWindow = window;
        window.Show();
        old?.Close();
        if (rerunAudit && window.DataContext is ViewModels.MainViewModel model && model.RunAuditCommand.CanExecute(null))
        {
            model.RunAuditCommand.Execute(null);
        }
    }

    /// <summary>
    /// Filet de sécurité : une erreur imprévue s'affiche en clair et laisse une trace,
    /// au lieu de fermer MAUS sans explication.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var logPath = CrashLog.Write(e.Exception, "Erreur sur le fil de l'interface : MAUS s'arrête");
        ShowCrashMessage(e.Exception, logPath);
        e.Handled = true;
        Shutdown(1);
    }

    /// <summary>
    /// Erreur sur un autre fil (mesure, test, bibliothèque tierce) : Windows ferme MAUS juste après, rien ne peut l'empêcher.
    /// La trace est écrite avant, y compris pendant l'audit hebdomadaire sans fenêtre ; le message ne s'affiche que si une
    /// fenêtre de MAUS était ouverte.
    /// </summary>
    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception
            ?? new InvalidOperationException(Convert.ToString(e.ExceptionObject, CultureInfo.InvariantCulture));
        var logPath = CrashLog.Write(exception, "Erreur sur un fil d'arrière-plan : MAUS s'arrête");
        if (s_interactive && e.IsTerminating)
        {
            ShowCrashMessage(exception, logPath);
        }
    }

    /// <summary>
    /// Tâche en erreur que personne n'a attendue : MAUS continue, l'erreur est notée dans le journal au lieu de disparaître.
    /// </summary>
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Maus.Core.Diagnostics.Breadcrumbs.Add("tâche d'arrière-plan en erreur, jamais attendue (" + e.Exception.InnerException?.GetType().Name + ")");
        CrashLog.Write(e.Exception, "Tâche d'arrière-plan en erreur, jamais attendue : MAUS continue");
        e.SetObserved();
    }

    private static void ShowCrashMessage(Exception exception, string? logPath) =>
        MessageBox.Show(
            Texts.T("MAUS a rencontré une erreur inattendue et doit s'arrêter. Les corrections déjà faites sont enregistrées : l'onglet Historique permet de les annuler au prochain lancement.") +
            "\n\n" + exception.Message +
            (logPath is null ? string.Empty : "\n\n" + Texts.T("Détails enregistrés dans :") + "\n" + logPath),
            Texts.T("MAUS — erreur"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
}
