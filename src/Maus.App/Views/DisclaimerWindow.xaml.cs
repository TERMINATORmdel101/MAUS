using System.Windows;

namespace Maus.App.Views;

/// <summary>
/// Relecture des avertissements (depuis les Paramètres et l'écran d'ouverture, où ils sont acceptés) : un seul bouton
/// « Fermer », qui répond aussi à Entrée et à Échap.
/// </summary>
public partial class DisclaimerWindow : Window
{
    public DisclaimerWindow() => InitializeComponent();
}
