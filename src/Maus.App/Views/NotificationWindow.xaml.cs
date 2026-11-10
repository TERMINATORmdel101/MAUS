using System.Windows;
using System.Windows.Threading;

namespace Maus.App.Views;

/// <summary>
/// Petite fenêtre en bas à droite après un audit automatique (problème rouge trouvé, ou résumé si l'utilisateur l'a demandé). Elle ne prend pas le clavier
/// et se ferme seule au bout de 30 minutes. « Ouvrir MAUS » affiche la fenêtre principale ; « Plus tard » ferme tout.
/// </summary>
public partial class NotificationWindow : Window
{
    private readonly DispatcherTimer _timeout = new() { Interval = TimeSpan.FromMinutes(30) };

    public NotificationWindow(string title, string message)
    {
        InitializeComponent();
        TitleText.Text = title;
        Message.Text = message;
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 16;
            Top = area.Bottom - ActualHeight - 16;
        };
        _timeout.Tick += (_, _) =>
        {
            _timeout.Stop();
            Close();
        };
        _timeout.Start();
    }

    /// <summary>Vrai si l'utilisateur a choisi d'ouvrir MAUS.</summary>
    public bool OpenRequested { get; private set; }

    /// <summary>Vrai si l'utilisateur a répondu, par « Ouvrir MAUS » ou « Plus tard », et non si la fenêtre s'est fermée seule.</summary>
    public bool Answered { get; private set; }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        OpenRequested = true;
        Answered = true;
        _timeout.Stop();
        Close();
    }

    private void OnLater(object sender, RoutedEventArgs e)
    {
        Answered = true;
        _timeout.Stop();
        Close();
    }
}
