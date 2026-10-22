using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using static Maus.Core.Localization.Texts;

namespace Maus.App.Views;

/// <summary>
/// À l'ouverture (0.8.1, demande du porteur) : MAUS tourne sans droits administrateur et propose de les activer pour toutes
/// ses fonctions. La demande d'autorisation de Windows ne vient qu'après « Oui » : c'est l'utilisateur qui la déclenche.
/// </summary>
public sealed class ElevationOfferWindow : Window
{
    private readonly CheckBox _dontAsk = new() { Content = T("Ne plus me le proposer à l'ouverture (réactivable dans les Paramètres)"), Margin = new Thickness(0, 12, 0, 14) };

    private ElevationOfferWindow()
    {
        Title = T("Toutes les fonctionnalités de MAUS");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Owner = Application.Current?.MainWindow;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

        var message = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = T("MAUS est ouvert sans les droits administrateur. Il peut déjà vous montrer votre matériel, les mesures en direct, les processus et l'espace disque.")
                + Environment.NewLine + Environment.NewLine
                + T("Pour profiter de toutes ses fonctionnalités (audit complet, corrections réversibles, réparation de Windows, capteurs avancés, fiche mémoire…), il faut les activer : Windows vous demandera votre autorisation.")
                + Environment.NewLine + Environment.NewLine
                + T("Sans elles, MAUS reste utilisable en lecture seule, et chaque action qui en a besoin vous redemandera l'autorisation au moment voulu. Les droits se referment seuls après 15 minutes sans utilisation."),
        };
        AutomationProperties.SetName(message, message.Text);

        var yes = new Button { Content = T("Oui, je veux toutes les fonctionnalités"), IsDefault = true, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
        yes.SetResourceReference(StyleProperty, "PrimaryButton");
        yes.Click += (_, _) => DialogResult = true;
        var no = new Button { Content = T("Non, continuer sans"), IsCancel = true, Padding = new Thickness(16, 6, 16, 6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(message);
        panel.Children.Add(_dontAsk);
        panel.Children.Add(buttons);
        Content = panel;
    }

    /// <summary>Montre la proposition.</summary>
    /// <returns><c>Accept</c> : l'utilisateur veut toutes les fonctionnalités ; <c>DontAskAgain</c> : ne plus la montrer.</returns>
    public static (bool Accept, bool DontAskAgain) Ask()
    {
        var window = new ElevationOfferWindow();
        var accept = window.ShowDialog() == true;
        return (accept, window._dontAsk.IsChecked == true);
    }
}
