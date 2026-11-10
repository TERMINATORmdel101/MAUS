using System.Windows;
using System.Windows.Controls;
using Maus.App.ViewModels;
using Maus.Core.Diagnostics;

namespace Maus.App.Views.Follow;

/// <summary>Carte « Carnet de santé » de la page « Suivi » : relit le carnet quand elle s'affiche, sans rien mesurer.</summary>
public partial class HealthLogPanel : UserControl
{
    public HealthLogPanel()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibleChanged;
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && DataContext is MainViewModel viewModel)
        {
            viewModel.RefreshHealthLogAsync().Forget("carnet de santé : lecture");
        }
    }
}
