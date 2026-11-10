using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Maus.App.ViewModels;
using Maus.Core.Diagnostics;

namespace Maus.App.Views.Follow;

/// <summary>Carte « Windows a-t-il défait mes réglages ? » de la page « Suivi ».</summary>
public partial class WindowsUndidPanel : UserControl
{
    public WindowsUndidPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ElevationState.Current.PropertyChanged += OnElevationChanged;
        Show();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => ElevationState.Current.PropertyChanged -= OnElevationChanged;

    /// <summary>Droits ouverts ou refermés pendant que la carte est affichée : ce qu'elle peut dire change.</summary>
    private void OnElevationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ElevationState.IsFull))
        {
            Show();
        }
    }

    private void Show()
    {
        if (DataContext is MainViewModel model)
        {
            model.ShowWindowsUndidAsync().Forget("Suivi : les corrections tiennent-elles");
        }
    }
}
