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

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Summary.Text);
            CopyStatus.Text = Ui.Copied;
        }
        catch (ExternalException)
        {
            CopyStatus.Text = Ui.CopyFailed;
        }
    }
}
