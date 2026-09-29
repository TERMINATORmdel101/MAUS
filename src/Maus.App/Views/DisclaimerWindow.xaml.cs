using System.Windows;

namespace Maus.App.Views;

/// <summary>
/// Avertissements du premier lancement (demande du porteur) : MAUS est fourni sans garantie et s'utilise sous la
/// responsabilité de l'utilisateur. « Continuer » ne s'active qu'une fois la case cochée ; « Quitter » ferme MAUS.
/// En relecture (depuis les Paramètres), seul le bouton « Fermer » reste.
/// </summary>
public partial class DisclaimerWindow : Window
{
    public DisclaimerWindow(bool reviewOnly = false)
    {
        InitializeComponent();
        if (reviewOnly)
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Accept.Visibility = Visibility.Collapsed;
            QuitButton.Visibility = Visibility.Collapsed;
            Continue.Content = Ui.CloseWindow;
            Continue.IsEnabled = true;
        }
    }

    private void OnAcceptChanged(object sender, RoutedEventArgs e) => Continue.IsEnabled = Accept.IsChecked == true;

    private void OnContinue(object sender, RoutedEventArgs e) => DialogResult = true;
}
