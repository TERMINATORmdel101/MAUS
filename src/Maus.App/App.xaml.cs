using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Maus.App.Appearance;
using Maus.Core;
using Maus.Core.Diagnostics;
using Maus.Core.Engine;
using Maus.Core.Localization;
using Maus.Core.Platform;
using Maus.Core.Preferences;
using Maus.Core.Workshop;

namespace Maus.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Processus élevé (moteur, fenêtre, benchmark) : rien de ce qu'il lit ou lance ne dépend de l'environnement du compte.
        if (Environment.IsPrivilegedProcess)
        {
            SystemPaths.ResetEnvironment();
        }

        // Filet de sécurité : fil de l'interface, autres fils (l'erreur ferme MAUS, la trace reste) et tâches jamais attendues.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Réglages régionaux de Windows (séparateur de liste, virgule décimale), gardés pour les exports CSV avant que
        // UseLanguage ne remplace la culture du fil par celle de la langue de MAUS.
        Texts.UseRegionalCulture(CultureInfo.CurrentCulture);

        base.OnStartup(e);

        if (e.Args.Length > 0 && e.Args[0].Equals(EngineArgument, StringComparison.OrdinalIgnoreCase))
        {
            // Moteur administrateur (0.8.0) : aucune fenêtre ; il sert l'interface qui l'a lancé, puis s'arrête avec elle.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Jamais une fenêtre à la place d'un moteur mal lancé.
            if (e.Args.Length != 4)
            {
                Shutdown(2);
                return;
            }

            RunEngineAsync(e.Args[1], e.Args[2], e.Args[3]).Forget("moteur", _ => Shutdown(1));
            return;
        }

        if (e.Args.Contains(ScheduledAudit.Argument, StringComparer.OrdinalIgnoreCase))
        {
            // Audit planifié : sans droits depuis la 0.8.6, il lit les choix lui-même (le benchmark reçoit sa langue).
            Breadcrumbs.Add("audit hebdomadaire : protections de Windows (" + string.Join(", ", ProcessHardening.HardenEngine()) + ")");
            if (ProcessElevation.IsElevated())
            {
                // Tâche d'une version précédente, pas encore recréée.
                Breadcrumbs.Add(CrashLog.UseProtectedFolder() ? "audit hebdomadaire : journal de diagnostic protégé" : "audit hebdomadaire : journal de diagnostic en mémoire seulement");
            }

            var own = ScheduledAuditPreferences();
            UseLanguage(own.Language);
            AppearanceManager.DetectComponents();
            AppearanceManager.Apply(own);

            // Audit planifié : pas de fenêtre, sauf si un problème rouge mérite d'être signalé.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Une erreur qui échapperait à l'audit ne doit pas laisser un MAUS invisible tourner indéfiniment.
            RunScheduledAuditAsync().Forget("audit hebdomadaire", _ => Shutdown(1));
            return;
        }

        if (e.Args.Length > 0 && e.Args[0].Equals("--benchmark", StringComparison.OrdinalIgnoreCase))
        {
            // Benchmark visuel : processus séparé lancé par l'Atelier, avec sa propre fenêtre plein écran (pas de WPF).
            // Lancé par le moteur, il a les droits administrateur : mêmes protections de Windows que le moteur.
            var hardening = ProcessElevation.IsElevated() ? ProcessHardening.HardenEngine() : ProcessHardening.HardenInterface();
            Breadcrumbs.Add("benchmark : protections de Windows (" + string.Join(", ", hardening) + ")");
            if (ProcessElevation.IsElevated())
            {
                Breadcrumbs.Add(CrashLog.UseProtectedFolder() ? "benchmark : journal de diagnostic protégé" : "benchmark : journal de diagnostic en mémoire seulement");
            }

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(Maus.Bench.BenchmarkProgram.Run(e.Args[1..]));
            return;
        }

        s_interactive = true;

        // Les protections ci-dessous refusent les bibliothèques venues du réseau, dont celles de WPF si MAUS.exe y est rangé :
        // mieux vaut le dire que laisser MAUS tomber plus loin. La protection reste, moteur compris.
        if (Environment.ProcessPath is { } executable && FinalPath.IsOnNetworkDrive(executable))
        {
            UseLanguage(null);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Views.QuestionWindow.Inform("MAUS", Texts.T("MAUS doit être lancé depuis un disque de ce PC, pas depuis le réseau : copiez son dossier sur ce PC, puis relancez-le."), Views.QuestionTone.Warning);
            Shutdown(3);
            return;
        }

        // Protections de Windows sur la fenêtre de MAUS (bibliothèques venues du réseau ou d'intégrité basse refusées).
        Breadcrumbs.Add("fenêtre : protections de Windows (" + string.Join(", ", ProcessElevation.IsElevated() ? ProcessHardening.HardenElevatedWindow() : ProcessHardening.HardenInterface()) + ")");
        if (ProcessElevation.IsElevated())
        {
            // MAUS lancé « en tant qu'administrateur » : son journal de diagnostic va, comme celui du moteur, dans le dossier protégé,
            // ou nulle part sur le disque si ce dossier ne peut pas être protégé.
            Breadcrumbs.Add(CrashLog.UseProtectedFolder() ? "fenêtre administrateur : journal de diagnostic protégé" : "fenêtre administrateur : journal de diagnostic en mémoire seulement");

            // Moteur précédent tué en pleine mesure de latence : sa session du noyau, restée ouverte, est refermée.
            Task.Run(EtwDriverLatencyTracer.CloseLeftoverSession).Forget("latence : session précédente");
        }


        // MAUS s'arrête quand sa fenêtre principale se ferme, même si une fenêtre annexe traîne encore.
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        // La question d'un autre MAUS bloqué arrive avant la lecture des choix : dans la langue de Windows.
        UseLanguage(null);
        if (!AcquireSingleInstance())
        {
            Shutdown(0);
            return;
        }

        // Écran de démarrage : le logo s'affiche tout de suite, pendant que la fenêtre principale se prépare.
        SplashScreen? splash = new SplashScreen(typeof(App).Assembly, "Assets/splash.png");
        splash.Show(autoClose: false);

        // 0.8.1 : aucune autorisation demandée à l'ouverture ; le moteur administrateur attend la première action qui en a besoin.
        EngineHost.Configure();

        // Le moteur s'est coupé lui-même après des connexions refusées : l'utilisateur est prévenu (rien d'autre n'est fait).
        EngineHost.StoppedForSecurity += (_, _) => Dispatcher.InvokeAsync(() =>
            Views.QuestionWindow.Inform("MAUS", Ui.EngineSecurityStop, Views.QuestionTone.Warning));
        var preferences = LoadPreferences();
        // Fichier des choix d'affichage absent : toute première ouverture (pas de « Quoi de neuf »).
        s_firstOpening = (EngineHost.Preferences as SplitPreferencesStore)?.State == PreferencesFileState.Absent;
        UseLanguage(preferences.Language);
        AppearanceManager.DetectComponents();
        AppearanceManager.Apply(preferences);

        if (MustAskDisclaimer(preferences))
        {
            splash.Close(TimeSpan.Zero);
            splash = null;
            if (!AskWelcome(preferences))
            {
                Shutdown(0);
                return;
            }

            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        // Avant la fenêtre : un démarrage figé se note aussi.
        UiWatchdog.Start(Dispatcher);
        ShowMainWindow(splash);
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
        if (!Maus.Core.Engine.Broker.EngineCommandLine.TryParse(pipe, clientPid, keyFingerprint, out var pid))
        {
            Shutdown(2);
            return;
        }

        // Protections de Windows sur le moteur, avant toute demande (Maus.Core.Platform.ProcessHardening).
        Breadcrumbs.Add("moteur : protections de Windows (" + string.Join(", ", ProcessHardening.HardenEngine()) + ")");
        // Journal de diagnostic du moteur dans %ProgramData%\MAUS\logs, réservé aux administrateurs (0.8.6) ; jamais dans le profil.
        Breadcrumbs.Add(CrashLog.UseProtectedFolder() ? "moteur : journal de diagnostic protégé" : "moteur : journal de diagnostic en mémoire seulement");
        var engine = new Maus.Core.Engine.Broker.LocalMausEngine
        {
            ProtectedProcessId = pid,
            // Questions du moteur dans sa propre fenêtre, sur ce fil (0.8.6) ; sans elle, la réponse est « Non ».
            Confirmation = new EngineConfirmationPrompt(),
        };

        // Moteur précédent tué en pleine mesure de latence : sa session du noyau, restée ouverte, est refermée.
        Task.Run(EtwDriverLatencyTracer.CloseLeftoverSession).Forget("latence : session précédente");
        var server = new Maus.Core.Engine.Broker.EngineServer(engine, pipe, pid, keyFingerprint);
        try
        {
            await server.RunAsync(CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            // Arrêt par sécurité : le code de sortie permet à l'interface de prévenir l'utilisateur, même si la fermeture se passe mal.
            var code = server.StoppedForSecurity ? Maus.Core.Engine.Broker.EngineServer.SecurityStopExitCode : 0;
            await Maus.Core.Engine.Broker.EngineServer.ReleaseWithinAsync(engine.Dispose, TimeSpan.FromSeconds(15)).ConfigureAwait(true);
            Shutdown(code);
        }
    }

    /// <summary>Tenu tant que MAUS est ouvert (signale aux lancements suivants qu'une fenêtre existe déjà).</summary>
    private static Mutex? s_singleInstance;
    private static int s_crashing;

    /// <summary>Une fenêtre de MAUS est (ou va être) affichée : une erreur fatale s'annonce par un message.</summary>
    private static volatile bool s_interactive;

    /// <summary>
    /// Une seule fenêtre de MAUS à la fois : deux MAUS ouverts se disputeraient le pilote et le bus des barrettes. Prend le
    /// verrou <c>Local\MAUS.FenetrePrincipale</c>, ou ramène devant le MAUS déjà ouvert.
    /// </summary>
    /// <returns><c>true</c> si ce MAUS peut afficher sa fenêtre.</returns>
    private static bool AcquireSingleInstance()
    {
        try
        {
            s_singleInstance = new Mutex(true, @"Local\MAUS.FenetrePrincipale", out var first);
            return first || TakeOverFromWindowlessInstance();
        }
        catch (UnauthorizedAccessException)
        {
            // Verrou créé par un MAUS lancé en administrateur : ce MAUS-ci ne peut ni l'ouvrir ni le fermer.
            Maus.Core.Platform.SingleInstance.BringExistingToFront();
            return false;
        }
    }

    /// <summary>
    /// Avertissements à montrer : jamais acceptés, ou texte changé depuis. Un fichier des choix présent mais momentanément
    /// illisible n'est pas un premier lancement : la fenêtre ne revient pas pour autant.
    /// </summary>
    private static bool MustAskDisclaimer(UserPreferences preferences) =>
        preferences.DisclaimerAccepted < Maus.Core.Legal.Disclaimer.Version
        && (EngineHost.Preferences as SplitPreferencesStore)?.State != PreferencesFileState.Unavailable;

    /// <summary>Choix du mode fait sur l'écran d'ouverture (0.8.6) ; <c>null</c> = pas proposé, la fenêtre habituelle le fera.</summary>
    private static bool? s_welcomeChoice;

    /// <summary>
    /// Écran d'ouverture du premier lancement (et après chaque changement de fond des avertissements) : avertissements et
    /// choix du mode en une seule fenêtre (0.8.6).
    /// </summary>
    /// <returns><c>false</c> si l'utilisateur n'accepte pas les avertissements : MAUS doit alors se fermer.</returns>
    private bool AskWelcome(UserPreferences preferences)
    {
        // Pendant cette fenêtre, fermer celle-ci ne doit pas être pris pour la fermeture de MAUS par Windows.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var answer = Views.WelcomeWindow.Ask(offerModes: EngineHost.OnDemand is not null && preferences.OfferAllFeatures);
        if (!answer.Accepted)
        {
            return false;
        }

        s_welcomeChoice = answer.AllFeatures;

        // Accord non enregistré : les avertissements reviendront au prochain lancement, MAUS s'ouvre quand même.
        TrySavePreferences(p => p with
        {
            DisclaimerAccepted = Maus.Core.Legal.Disclaimer.Version,
            OfferAllFeatures = !answer.DontAskAgain && p.OfferAllFeatures,
        });
        return true;
    }

    /// <summary>
    /// Enregistre un choix de l'ouverture. Un échec (fichier tenu par un antivirus, dossier refusé, moteur absent) ne doit
    /// jamais interrompre l'ouverture de MAUS : le choix sera simplement redemandé au prochain lancement.
    /// </summary>
    private static void TrySavePreferences(Func<UserPreferences, UserPreferences> change)
    {
        try
        {
            EngineHost.Preferences.Update(change);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Maus.Core.Fixes.JournalUnsafeException or Maus.Core.Engine.Broker.EngineException)
        {
            Maus.Core.Diagnostics.Breadcrumbs.Add("choix de l'ouverture non enregistré");
        }
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

        // Il démarre ou il se termine peut-être : on lui laisse un instant avant de le dire bloqué.
        try
        {
            if (s_singleInstance!.WaitOne(TimeSpan.FromSeconds(3)))
            {
                return true;
            }
        }
        catch (AbandonedMutexException)
        {
            return true;
        }

        if (Maus.Core.Platform.SingleInstance.BringExistingToFront())
        {
            return false;
        }

        // Tout début de l'ouverture : les préférences ne sont pas encore lues, le thème par défaut suffit pour cette question.
        AppearanceManager.Apply(UserPreferences.Default);
        if (!Views.QuestionWindow.Ask(Texts.T("Un autre MAUS est déjà ouvert"), Texts.T("Un autre MAUS est déjà en cours d'exécution, mais sans fenêtre : il est sans doute bloqué, et il peut empêcher la lecture de la mémoire. Le fermer et ouvrir celui-ci ?"), Views.QuestionTone.Warning, yesIsDefault: true))
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
        EngineHost.Close();
        PresentMonCapture.WaitForBackgroundStops(TimeSpan.FromSeconds(8));
        Environment.Exit(e.ApplicationExitCode);
    }

    private static void ShowMainWindow(SplashScreen? splash = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var window = new MainWindow();
        Breadcrumbs.Add("fenêtre : créée en " + clock.ElapsedMilliseconds + " ms");
        window.Loaded += (_, _) => Breadcrumbs.Add("fenêtre : Loaded à " + clock.ElapsedMilliseconds + " ms");
        window.ContentRendered += (_, _) => Breadcrumbs.Add("fenêtre : ContentRendered à " + clock.ElapsedMilliseconds + " ms");
        Current.MainWindow = window;
        if (splash is not null)
        {
            // L'écran de démarrage s'efface en douceur une fois la fenêtre dessinée. Secours : une fenêtre ouverte réduite
            // n'est pas dessinée tout de suite, l'écran de démarrage ne doit pas rester affiché pour autant.
            var closed = false;
            var fallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
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
        Breadcrumbs.Add("fenêtre : Show terminé à " + clock.ElapsedMilliseconds + " ms");

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
        if (Current.MainWindow?.DataContext is ViewModels.MainViewModel opened)
        {
            opened.CheckScheduleAtOpeningAsync().Forget("audit hebdomadaire : lecture à l'ouverture");
        }

        var preferences = LoadPreferences();
        if (!string.Equals(preferences.LastSeenVersion, AppVersion.Display, StringComparison.Ordinal))
        {
            // 0.8.4 : les nouveautés s'affichent en carte sur l'accueil (fermée par « Compris »), plus en fenêtre.
            if (!s_firstOpening && Current.MainWindow?.DataContext is ViewModels.MainViewModel home)
            {
                home.ShowWhatsNewFor(AppVersion.Display);
            }

            // Non enregistré : les nouveautés reviendront à la prochaine ouverture.
            await Task.Run(() => TrySavePreferences(p => p with { LastSeenVersion = AppVersion.Display }));
        }

        if (s_welcomeChoice is { } chosenOnWelcome)
        {
            // Le mode a été choisi sur l'écran d'ouverture : pas de seconde fenêtre.
            s_welcomeChoice = null;
            await ElevateIfWantedAsync(chosenOnWelcome);
        }
        else if (EngineHost.OnDemand is not null && preferences.OfferAllFeatures)
        {
            await OfferAllFeaturesAsync();
        }
    }

    private static async Task OfferAllFeaturesAsync()
    {
        var (accept, dontAskAgain) = Views.ElevationOfferWindow.Ask();
        if (dontAskAgain)
        {
            // Choix non enregistré : la proposition reviendra à la prochaine ouverture.
            await Task.Run(() => TrySavePreferences(p => p with { OfferAllFeatures = false }));
        }

        await ElevateIfWantedAsync(accept);
    }

    /// <summary>« Toutes les fonctionnalités » choisi : la demande d'autorisation de Windows vient maintenant, et seulement maintenant.</summary>
    private static async Task ElevateIfWantedAsync(bool accept)
    {
        if (accept && EngineHost.OnDemand is { } onDemand)
        {
            try
            {
                await onDemand.EnsureElevatedAsync(CancellationToken.None);
            }
            catch (Maus.Core.Engine.Broker.EngineException ex)
            {
                Views.QuestionWindow.Inform("MAUS", ex.Message, Views.QuestionTone.Information);
            }
        }
    }

    /// <summary>
    /// Audit hebdomadaire lancé par le Planificateur de tâches : lecture seule, score gardé dans l'historique (sauf score
    /// partiel), résumé gardé pour la carte « Dernier audit », notification seulement s'il y a un problème rouge, avec les
    /// modules non vérifiés. Rien n'est envoyé.
    /// </summary>
    /// <summary>Sans droits, le fichier protégé ne se lit pas : ses choix sont recopiés dans celui de l'utilisateur à chaque enregistrement.</summary>
    private static UserPreferences ScheduledAuditPreferences() => ProcessElevation.IsElevated()
        ? FilePreferencesStore.CreateDefault().Load()
        : new FilePreferencesStore(FilePreferencesStore.UserDirectory, new UserDirectoryProtector()).Load();

    private async Task RunScheduledAuditAsync()
    {
        try
        {
            if (ProcessElevation.IsElevated() && await TaskSchedulerClient.DropRightsOfOldTaskAsync())
            {
                Shutdown(0);
                return;
            }

            // Personne n'a cliqué : aucun accès réseau (Windows Update lu d'après sa dernière recherche, pas de liste winget).
            var preferences = ScheduledAuditPreferences();
            var context = await Task.Run(() => AuditContext.CreateDefault().WithoutNetwork().WithPreferences(preferences));
            var results = await AuditEngine.CreateWithBuiltInModules().RunAsync(context);

            // Un score partiel (module en erreur ou en délai dépassé, ou contrôles réservés aux administrateurs non lus) n'est pas
            // enregistré : il paraîtrait meilleur que la réalité.
            var complete = ProcessElevation.IsElevated();
            if (complete && ScoreTrends.HealthEntry(results, DateTimeOffset.Now) is { } entry)
            {
                await Task.Run(() =>
                {
                    try
                    {
                        BenchmarkHistory.CreateHealth().Add(entry with { Kind = ScoreTrends.WeeklyHealthKind });
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // Historique non écrit, ou fichier du profil abîmé : jamais une raison de se taire.
                    }
                });
            }

            // La notification mène à ce résumé : marqué « semaine » (sans réseau), il ne se compare pas aux audits lancés à la main.
            if (Maus.Core.Reporting.AuditSnapshot.ForWeeklyAudit(results, context.Windows.FullBuild, DateTimeOffset.Now, complete) is { } summary)
            {
                await Task.Run(() =>
                {
                    try
                    {
                        Maus.Core.Reporting.AuditSnapshotStore.CreateDefault().Add(summary);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // Résumé non écrit : la notification dit quand même ce qu'elle a trouvé.
                    }
                });
            }

            // Le carnet de santé note ce qui se lit sans droits ; rien de lisible, pas de ligne.
            await Task.Run(() =>
            {
                try
                {
                    Maus.Core.Follow.HealthLogStore.CreateDefault().AddAudit(results, DateTimeOffset.Now, weekly: true, elevated: complete);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Ligne non écrite : la notification dit quand même ce qu'elle a trouvé.
                }
            });

            var problems = ScheduledAudit.WorthNotifying(results);
            var level = context.Preferences.WeeklyNotification;
            if (level == WeeklyNotification.Never || (problems.Count == 0 && level != WeeklyNotification.Always))
            {
                Shutdown(0);
                return;
            }

            // Le score n'est cité que s'il est complet (même règle que l'historique).
            int? score = complete && Maus.Core.Reporting.HealthScore.UncheckedModules(results).Count == 0 ? Maus.Core.Reporting.HealthScore.Compute(results) : null;
            var text = problems.Count == 0 ? ScheduledAudit.SummaryText(score)
                : (score is { } value ? Texts.T("Score : {0} sur 100.", value) + Environment.NewLine : string.Empty) + ScheduledAudit.NotificationText(problems, results);
            var notification = new Views.NotificationWindow(problems.Count == 0 ? Ui.WeeklyAuditDoneTitle : Ui.NotificationTitle, text);
            // Écrite avant d'afficher : Windows arrête la tâche au bout de 30 minutes, sans laisser le temps de l'écrire ensuite.
            var missedAlert = problems.Count > 0 ? Maus.Core.Reporting.MissedAlertNote.CreateDefault() : null;
            missedAlert?.Write(DateTimeOffset.Now, problems.Count);
            notification.Closed += (_, _) =>
            {
                if (notification.Answered)
                {
                    missedAlert?.Clear();
                }

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
    /// « Ouvrir » après l'audit hebdomadaire : MAUS est relancé normalement, sans droits, par l'Explorateur (comme un
    /// double-clic), même si la tâche date d'une version qui lui donnait des droits ; ce processus s'arrête. L'utilisateur
    /// choisit ensuite « Toutes les fonctionnalités » s'il veut corriger. La fenêtre s'ouvre sur l'accueil, où est le résumé
    /// de cet audit, même si l'utilisateur l'avait laissée ailleurs.
    /// </summary>
    private void OpenAfterScheduledAudit()
    {
        try
        {
            new FilePreferencesStore(FilePreferencesStore.UserDirectory, new UserDirectoryProtector()).Update(p => p with { LastTab = 0 });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Maus.Core.Fixes.JournalUnsafeException)
        {
            Breadcrumbs.Add("onglet d'ouverture non enregistré après l'audit de la semaine");
        }

        if (Environment.ProcessPath is { } exe)
        {
            ShellLauncher.OpenFile(exe);
        }

        Shutdown(0);
    }

    /// <summary>Active la langue choisie (ou celle de Windows), y compris pour les dates et les nombres.</summary>
    public static void UseLanguage(string? code) => Texts.UseWithCulture(code);

    /// <summary>Change de langue : la fenêtre est recréée dans la nouvelle langue, puis l'audit est relancé s'il avait été fait.</summary>
    public static void SwitchLanguage(string code, bool rerunAudit)
    {
        UseLanguage(code);
        var old = Current.MainWindow;

        // La nouvelle fenêtre reprend la place et l'onglet de celle qu'elle remplace.
        if (old is MainWindow { DataContext: ViewModels.MainViewModel view } oldWindow)
        {
            AppearanceManager.Remember(AppearanceManager.Current with
            {
                MainWindow = oldWindow.CurrentPlacement(),
                LastTab = view.SelectedTab,
                LastWorkshopSection = view.Workshop.Section,
            });
        }

        var window = new MainWindow();
        Current.MainWindow = window;
        window.Show();
        old?.Close();
        // Moteur arrêté : cet audit se fait sans droits, il ne demande aucune autorisation.
        if (rerunAudit && window.DataContext is ViewModels.MainViewModel model && model.RerunAuditForLanguageCommand.CanExecute(null))
        {
            model.RerunAuditForLanguageCommand.Execute(null);
        }
    }

    /// <summary>
    /// Filet de sécurité : une erreur imprévue s'affiche en clair et laisse une trace,
    /// au lieu de fermer MAUS sans explication.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Une seconde erreur pendant le message de la première : notée seulement, MAUS s'arrête déjà.
        if (Interlocked.Exchange(ref s_crashing, 1) != 0)
        {
            CrashLog.Write(e.Exception, "Seconde erreur pendant l'arrêt de MAUS");
            e.Handled = true;
            return;
        }

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
