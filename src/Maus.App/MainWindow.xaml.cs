using System.Windows;

namespace Maus.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as ViewModels.MainViewModel)?.Shutdown();
    }
}
