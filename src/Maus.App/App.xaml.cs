using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Maus.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Thème Fluent de Windows 11, clair ou sombre selon le système.
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        base.OnStartup(e);
    }

    /// <summary>
    /// Filet de sécurité : une erreur imprévue s'affiche en clair et laisse une trace,
    /// au lieu de fermer MAUS sans explication.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var logPath = WriteCrashLog(e.Exception);
        MessageBox.Show(
            $"MAUS a rencontré une erreur inattendue et doit s'arrêter. Rien n'a été modifié sur votre PC.\n\n{e.Exception.Message}" +
            (logPath is null ? string.Empty : $"\n\nDétails enregistrés dans :\n{logPath}"),
            "MAUS — erreur",
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
