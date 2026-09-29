using System.Runtime.InteropServices;
using System.Windows;

namespace Maus.App.Views;

/// <summary>
/// Aperçu modifiable du résumé pour demander de l'aide : l'utilisateur voit exactement ce qu'il partagera,
/// complète la ligne « Mon problème », puis copie. MAUS n'envoie rien lui-même.
/// </summary>
public partial class HelpSummaryWindow : Window
{
    public HelpSummaryWindow(string summary)
    {
        InitializeComponent();
        Summary.Text = summary;
        Loaded += (_, _) =>
        {
            // Curseur placé à la fin de la première ligne, prêt pour décrire le problème.
            var firstLineEnd = summary.IndexOf('\n', StringComparison.Ordinal);
            Summary.Focus();
            Summary.CaretIndex = firstLineEnd > 0 ? firstLineEnd - (summary[firstLineEnd - 1] == '\r' ? 1 : 0) : 0;
        };
    }

    private void OnCopy(object sender, RoutedEventArgs e) => CopyStatus.Text = Copy() ? Ui.Copied : Ui.CopyFailed;

    /// <summary>
    /// Signalement sur GitHub : le résumé relu par l'utilisateur est copié, puis la page « nouveau signalement » du dépôt
    /// s'ouvre dans le navigateur, où il le colle. Rien ne passe par l'adresse de la page, et MAUS n'envoie rien lui-même.
    /// </summary>
    private void OnGitHub(object sender, RoutedEventArgs e)
    {
        var copied = Copy();
        ShellLauncher.OpenUrl(Maus.Core.ProjectLinks.NewIssue);
        CopyStatus.Text = copied ? Ui.GitHubPaste : Ui.CopyFailed;
    }

    private bool Copy()
    {
        try
        {
            Clipboard.SetText(Summary.Text);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }
}
