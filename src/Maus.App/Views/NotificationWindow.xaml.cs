using System.Windows;
using System.Windows.Threading;

namespace Maus.App.Views;

/// <summary>
/// Petite fenêtre en bas à droite après un audit automatique qui a trouvé un problème rouge. Elle ne prend pas le clavier
/// et se ferme seule au bout de 30 minutes. « Ouvrir MAUS » affiche la fenêtre principale ; « Plus tard » ferme tout.
/// </summary>
public partial class NotificationWindow : Window
{
    private readonly DispatcherTimer _timeout = new() { Interval = TimeSpan.FromMinutes(30) };

    public NotificationWindow(string message)
    {
        InitializeComponent();
        Message.Text = message;
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 16;
            Top = area.Bottom - ActualHeight - 16;
        };
        _timeout.Tick += (_, _) => OnLater(this, new RoutedEventArgs());
        _timeout.Start();
    }

    /// <summary>Vrai si l'utilisateur a choisi d'ouvrir MAUS.</summary>
    public bool OpenRequested { get; private set; }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        OpenRequested = true;
        _timeout.Stop();
        Close();
    }

    private void OnLater(object sender, RoutedEventArgs e)
    {
        _timeout.Stop();
        Close();
    }
}
