using System.Windows;
using System.Windows.Input;
using Maus.App.Appearance;
using Maus.App.ViewModels.Workshop;
using Maus.App.Views;
using Maus.Core.Diagnostics;
using Maus.Core.Fixes;
using Maus.Core.Preferences;

namespace Maus.App.ViewModels;

/// <summary>Fenêtres annexes : paramètres (bouton engrenage) et fenêtre de surveillance indépendante.</summary>
public sealed partial class MainViewModel
{
    private ICommand? _openSettings;
    private ICommand? _openMonitor;
    private SettingsWindow? _settingsWindow;
    private MonitorWindow? _monitorWindow;

    public ICommand OpenSettingsCommand => _openSettings ??= new AsyncCommand(() =>
    {
        OpenSettings();
        return Task.CompletedTask;
    });

    public ICommand OpenMonitorCommand => _openMonitor ??= new AsyncCommand(() =>
    {
        OpenMonitor();
        return Task.CompletedTask;
    });

    /// <summary>« À propos » : logo, version, licence, confidentialité, avertissements, composants et signalement.</summary>
    public ICommand OpenAboutCommand => _openAbout ??= new AsyncCommand(() =>
    {
        new AboutWindow { Owner = Application.Current?.MainWindow }.ShowDialog();
        return Task.CompletedTask;
    });

    private ICommand? _openAbout;

    /// <summary>Ouvre les paramètres, ou les ramène devant s'ils sont déjà ouverts.</summary>
    public void OpenSettings()
    {
        if (_settingsWindow is { } open)
        {
            open.Activate();
            return;
        }

        var window = new SettingsWindow(new SettingsViewModel(PreferencesStore, OpenMonitor)) { Owner = Application.Current?.MainWindow };
        window.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = window;
        window.Show();
    }

    /// <summary>Ouvre la fenêtre de surveillance (une seule à la fois), qui partage les mesures de l'atelier.</summary>
    public void OpenMonitor()
    {
        if (_monitorWindow is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();
            return;
        }

        var window = new MonitorWindow(new MonitorViewModel(Workshop), AppearanceManager.Current.MonitorOnTop, SaveMonitorOnTop);
        window.Closed += (_, _) => _monitorWindow = null;
        _monitorWindow = window;
        window.Show();
    }

    /// <summary>Ferme les fenêtres annexes (fermeture de la fenêtre principale, changement de langue).</summary>
    private void CloseSideWindows()
    {
        _settingsWindow?.Close();
        _monitorWindow?.Close();
    }

    private void SaveMonitorOnTop(bool onTop)
    {
        if (AppearanceManager.Current.MonitorOnTop == onTop)
        {
            return;
        }

        AppearanceManager.Apply(AppearanceManager.Current with { MonitorOnTop = onTop });
        Task.Run(() =>
        {
            try
            {
                PreferencesStore.Update(p => p with { MonitorOnTop = onTop });
            }
            catch (Exception ex) when (ex is JournalUnsafeException or System.IO.IOException or UnauthorizedAccessException)
            {
                // Le choix vaut pour cette ouverture de MAUS.
            }
        }).Forget("surveillance : choix « au premier plan »", ReportChoiceError);
    }
}
