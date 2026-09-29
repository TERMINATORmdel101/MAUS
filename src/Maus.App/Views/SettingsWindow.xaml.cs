using System.Windows;
using Maus.App.Appearance;
using Maus.App.ViewModels;

namespace Maus.App.Views;

/// <summary>Paramètres de MAUS (apparence, animations, fréquence des mesures, fenêtre de surveillance).</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        Loaded += (_, _) => Motion.Enter(Page, offset: 16);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>Relire les avertissements acceptés au premier lancement (lecture seule).</summary>
    private void OnDisclaimer(object sender, RoutedEventArgs e) => new DisclaimerWindow(reviewOnly: true) { Owner = this }.ShowDialog();
}
