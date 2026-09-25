using System.Windows;
using Maus.Core.Preferences;

namespace Maus.App.Views;

/// <summary>Question posée au premier lancement sur un portable (décision du projet) ; la proposition recommandée est pré-cochée.</summary>
public partial class LaptopChoiceWindow : Window
{
    public LaptopChoiceWindow()
    {
        InitializeComponent();
    }

    public LaptopPowerChoice Choice { get; private set; } = LaptopPowerChoice.Performance;

    private void OnValidate(object sender, RoutedEventArgs e)
    {
        Choice = Everywhere.IsChecked == true
            ? LaptopPowerChoice.PerformanceEverywhere
            : Battery.IsChecked == true ? LaptopPowerChoice.Battery : LaptopPowerChoice.Performance;
        DialogResult = true;
    }
}
