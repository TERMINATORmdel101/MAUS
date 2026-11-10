using System.IO;
using System.Reflection;
using System.Windows;
using Maus.Core.Engine;
using Maus.Core.Fixes;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.App.Views;

/// <summary>Une ligne de « Vérifier mon installation » : le verdict est écrit, pas seulement suggéré par une couleur.</summary>
public sealed record InstallationRow(string Heading, string Detail)
{
    public override string ToString() => Heading;

    /// <summary>Empreintes des bibliothèques, écrites dans MAUS.dll à la compilation (voir Maus.App.csproj).</summary>
    internal static Dictionary<string, string> ExpectedLibraries()
    {
        const string prefix = "Sha256.";
        return typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(a => a.Key.StartsWith(prefix, StringComparison.Ordinal) && a.Value is not null)
            .ToDictionary(a => a.Key[prefix.Length..], a => a.Value!, StringComparer.Ordinal);
    }

    internal static List<InstallationRow> Build(string executable, IInstallationProbe probe)
    {
        var lines = InstallationCheck.Run(executable, ScheduledAudit.ProtectedRoots(), ExpectedLibraries(), FileJournalStore.DefaultDirectory, probe);
        List<InstallationRow> rows = [.. lines.Select(line => new InstallationRow(line.Verdict switch
        {
            InstallationVerdict.Ok => T("Conforme : {0}", line.Title),
            InstallationVerdict.Warning => T("À vérifier : {0}", line.Title),
            _ => T("Information : {0}", line.Title),
        }, line.Detail))];
        rows.Add(new InstallationRow(T("Limite de ce contrôle"), InstallationCheck.Limit));
        return rows;
    }
}

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

    private void OnLatestRelease(object sender, RoutedEventArgs e) => ShellLauncher.OpenUrl(Maus.Core.ProjectLinks.LatestRelease);

    private void OnReport(object sender, RoutedEventArgs e) => ShellLauncher.OpenUrl(Maus.Core.ProjectLinks.NewIssue);

    private void OnSecurity(object sender, RoutedEventArgs e) => ShellLauncher.OpenUrl(Maus.Core.ProjectLinks.SecurityDesign);

    private void OnCheckInstallation(object sender, RoutedEventArgs e)
    {
        InstallationResults.ItemsSource = InstallationRow.Build(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "MAUS.exe"), new WindowsInstallationProbe());
        InstallationResults.Visibility = Visibility.Visible;
    }

    private void OnHelpSite(object sender, RoutedEventArgs e) => ShellLauncher.OpenUrl(Maus.Core.ProjectLinks.HelpSite);

    /// <summary>Dossier des licences livré avec MAUS (licence, composants tiers, LGPL-2.1, marque).</summary>
    private void OnLicenses(object sender, RoutedEventArgs e) => ShellLauncher.OpenFolder(Path.Combine(AppContext.BaseDirectory, "licenses"));
}
