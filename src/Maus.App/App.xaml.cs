using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Maus.App.Appearance;
using Maus.Core;
using Maus.Core.Engine;
using Maus.Core.Localization;
using Maus.Core.Preferences;
using Maus.Core.Workshop;

namespace Maus.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        var preferences = FilePreferencesStore.CreateDefault().Load();
        UseLanguage(preferences.Language);

        // Thème Fluent de Windows 11 (clair, sombre ou comme Windows), couleur d'accentuation et animations choisies.
        AppearanceManager.DetectComponents();
        AppearanceManager.Apply(preferences);
        base.OnStartup(e);

        if (e.Args.Contains(ScheduledAudit.Argument, StringComparer.OrdinalIgnoreCase))
        {
            // Audit planifié : pas de fenêtre, sauf si un problème rouge mérite d'être signalé.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunScheduledAuditAsync();
            return;
        }

        ShowMainWindow();
    }

    private static void ShowMainWindow()
    {
        var window = new MainWindow();
        Current.MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Audit hebdomadaire lancé par le Planificateur de tâches : lecture seule, score gardé dans l'historique, notification
    /// seulement s'il y a un problème rouge. Rien n'est envoyé.
    /// </summary>
    private async Task RunScheduledAuditAsync()
    {
        try
        {
            var context = await Task.Run(AuditContext.CreateDefault);
            var results = await AuditEngine.CreateWithBuiltInModules().RunAsync(context);
            await Task.Run(() =>
            {
                try
                {
                    BenchmarkHistory.CreateHealth().Add(ScoreTrends.HealthEntry(results, DateTimeOffset.Now));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Historique non écrit : sans conséquence pour l'audit.
                }
            });

            var problems = ScheduledAudit.WorthNotifying(results);
            if (problems.Count == 0)
            {
                Shutdown(0);
                return;
            }

            var notification = new Views.NotificationWindow(ScheduledAudit.NotificationText(problems));
            notification.Closed += (_, _) =>
            {
                if (notification.OpenRequested)
                {
                    ShutdownMode = ShutdownMode.OnLastWindowClose;
                    ShowMainWindow();
                    if (Current.MainWindow?.DataContext is ViewModels.MainViewModel model && model.RunAuditCommand.CanExecute(null))
                    {
                        model.RunAuditCommand.Execute(null);
                    }
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
            WriteCrashLog(ex);
            Shutdown(1);
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
        var logPath = WriteCrashLog(e.Exception);
        MessageBox.Show(
            Texts.T("MAUS a rencontré une erreur inattendue et doit s'arrêter. Les corrections déjà faites sont enregistrées : l'onglet Historique permet de les annuler au prochain lancement.") +
            $"\n\n{e.Exception.Message}" +
            (logPath is null ? string.Empty : "\n\n" + Texts.T("Détails enregistrés dans :") + $"\n{logPath}"),
            Texts.T("MAUS — erreur"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(1);
    }

    private static string? WriteCrashLog(Exception exception)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MAUS", "logs");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"erreur-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, exception.ToString());
            return path;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
