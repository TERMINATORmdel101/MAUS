using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using static Maus.Core.Localization.Texts;

namespace Maus.App.Views;

/// <summary>
/// À l'ouverture (0.8.1, demande du porteur ; textes simplifiés en 0.8.3) : MAUS tourne en mode limité, sans droits
/// administrateur, et propose les deux façons de l'utiliser, côte à côte. La demande d'autorisation de Windows ne vient
/// qu'après « Toutes les fonctionnalités » : c'est l'utilisateur qui la déclenche.
/// </summary>
public sealed class ElevationOfferWindow : Window
{
    private readonly CheckBox _dontAsk = new() { Content = T("Ne plus me poser la question (modifiable dans les Paramètres)"), Margin = new Thickness(0, 6, 0, 14) };

    private ElevationOfferWindow()
    {
        Title = T("Comment utiliser MAUS ?");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Owner = Application.Current?.MainWindow;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

        var heading = new TextBlock
        {
            Text = T("MAUS peut fonctionner de deux façons :"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };

        var limited = Choice("Grey",
            T("Mode limité, sans droits administrateur"),
            T("Vous voyez votre matériel, les mesures en direct, les processus et l'espace disque. Pour le reste, Windows vous demandera l'autorisation au moment où vous en aurez besoin."));
        var full = Choice("Mint",
            T("Toutes les fonctionnalités, avec les droits administrateur"),
            T("Toute l'application est disponible : audit complet, corrections (toujours annulables), réparation de Windows, capteurs avancés, fiche mémoire… Windows vous demande votre autorisation, puis les droits se referment seuls après 15 minutes sans utilisation."));

        var yes = new Button { Content = T("Toutes les fonctionnalités"), IsDefault = true, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
        yes.SetResourceReference(StyleProperty, "PrimaryButton");
        yes.Click += (_, _) => DialogResult = true;
        var no = new Button { Content = T("Mode limité"), IsCancel = true, Padding = new Thickness(16, 6, 16, 6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(heading);
        panel.Children.Add(limited);
        panel.Children.Add(full);
        panel.Children.Add(_dontAsk);
        panel.Children.Add(buttons);
        Content = panel;
    }

    /// <summary>Carte pastel d'une des deux façons d'utiliser MAUS : un titre de couleur, puis une phrase simple.</summary>
    private static Border Choice(string tone, string title, string text)
    {
        var name = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Pastel" + tone + "Ink");
        var detail = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel();
        content.Children.Add(name);
        content.Children.Add(detail);

        var card = new Border { Child = content, Margin = new Thickness(0, 0, 0, 10) };
        card.SetResourceReference(StyleProperty, "SoftCard");
        card.SetResourceReference(Border.BackgroundProperty, "Pastel" + tone);
        card.SetResourceReference(Border.BorderBrushProperty, "Pastel" + tone + "Stroke");
        AutomationProperties.SetName(card, title + ". " + text);
        return card;
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
