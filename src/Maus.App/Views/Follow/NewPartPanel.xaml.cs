using System.Windows;
using System.Windows.Controls;
using Maus.App.ViewModels;

namespace Maus.App.Views.Follow;

/// <summary>Carte « J'ai changé une pièce » de la page « Suivi ».</summary>
public partial class NewPartPanel : UserControl
{
    public NewPartPanel()
    {
        InitializeComponent();
    }

    /// <summary>Ne relit que le fichier de la fiche : le matériel n'est lu que par un audit, la fiche du PC ou le bouton.</summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.LoadNewPartAsync();
        }
    }
}
