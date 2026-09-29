using System.IO;
using System.Windows;

namespace Maus.App.Views;

/// <summary>
/// « À propos » : le logo, la version, qui a fait MAUS, la licence, la confidentialité, les avertissements et les
/// composants tiers, avec l'accès aux licences livrées, à la page du projet et au signalement d'un problème.
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Appearance.Motion.Enter(Page, offset: 12);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnProject(object sender, RoutedEventArgs e) => ShellLauncher.OpenUrl(Maus.Core.ProjectLinks.Repository);

    private void OnReport(object sender, RoutedEventArgs e) => ShellLauncher.OpenUrl(Maus.Core.ProjectLinks.NewIssue);

    /// <summary>Dossier des licences livré avec MAUS (licence, composants tiers, LGPL-2.1, marque).</summary>
    private void OnLicenses(object sender, RoutedEventArgs e) => ShellLauncher.OpenFolder(Path.Combine(AppContext.BaseDirectory, "licenses"));
}
