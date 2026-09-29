using System.ComponentModel;
using System.Windows;
using static Maus.Core.Localization.Texts;

namespace Maus.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as ViewModels.MainViewModel)?.Shutdown();
    }

    /// <summary>
    /// Fermer pendant des corrections couperait une écriture en cours : on demande d'abord. Pas de refus pur et simple, une
    /// écriture bloquée ne doit pas rendre MAUS impossible à fermer ; l'écriture interrompue reste annulable dans l'Historique.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel { IsApplying: true }
            && MessageBox.Show(
                this,
                T("Des corrections sont en cours d'écriture. Fermer maintenant peut laisser une correction à moitié faite ; elle restera annulable dans l'Historique au prochain lancement.") + Environment.NewLine + Environment.NewLine + T("Fermer quand même ?"),
                "MAUS",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }
}
