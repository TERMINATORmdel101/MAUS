using System.Windows;
using System.Windows.Controls;
using Maus.App.ViewModels;
using Maus.Core.Diagnostics;

namespace Maus.App.Views.Follow;

/// <summary>Carte « Avant et après » de la page « Suivi » : relit le journal et le carnet quand elle s'affiche, sans rien mesurer ni demander.</summary>
public partial class BeforeAfterPanel : UserControl
{
    public BeforeAfterPanel()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibleChanged;
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && DataContext is MainViewModel viewModel)
        {
            viewModel.RefreshBeforeAfterAsync().Forget("avant et après : lecture");
        }
    }
}
