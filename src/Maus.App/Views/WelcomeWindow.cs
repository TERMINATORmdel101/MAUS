using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using static Maus.Core.Localization.Texts;

namespace Maus.App.Views;

/// <summary>
/// Un seul écran d'ouverture au premier lancement (0.8.6) : l'essentiel des avertissements en trois lignes (le texte complet
/// à un clic), la case d'acceptation, puis les deux façons d'utiliser MAUS. Avant, trois fenêtres se succédaient.
/// </summary>
public sealed class WelcomeWindow : Window
{
    /// <param name="Accepted">Avertissements acceptés ; sinon MAUS se ferme.</param>
    /// <param name="AllFeatures"><c>true</c> toutes les fonctionnalités, <c>false</c> mode limité, <c>null</c> si le choix n'était pas proposé.</param>
    /// <param name="DontAskAgain">Ne plus proposer le choix du mode à l'ouverture.</param>
    public sealed record Answer(bool Accepted, bool? AllFeatures, bool DontAskAgain);

    private readonly CheckBox _accept = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly CheckBox _dontAsk = new() { Content = T("Ne plus me poser la question (modifiable dans les Paramètres)"), Margin = new Thickness(0, 6, 0, 14) };
    private readonly List<Button> _continueButtons = [];
    private bool? _allFeatures;

    private WelcomeWindow(bool offerModes)
    {
        Title = T("Bienvenue dans MAUS");
        Width = 640;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/maus.ico"));
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

        var panel = new StackPanel { Margin = new Thickness(24, 24, 24, 0) };
        panel.Children.Add(new TextBlock { Text = Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) }.Sized("FontSizePageTitle"));
        panel.Children.Add(new TextBlock { Text = Ui.Tagline, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });

        // Avertissements : trois lignes, le texte complet à un clic, la case d'acceptation.
        var warnings = new StackPanel();
        warnings.Children.Add(new TextBlock { Text = Maus.Core.Legal.Disclaimer.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) }.Sized("FontSizeCardTitle"));
        foreach (var line in Maus.Core.Legal.Disclaimer.Summary)
        {
            warnings.Children.Add(new TextBlock { Text = "• " + line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        }

        var full = new Button { Content = T("Lire le texte complet des avertissements"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 6, 0, 0) };
        full.SetResourceReference(StyleProperty, "SoftButton");
        full.Click += (_, _) => new DisclaimerWindow() { Owner = this }.ShowDialog();
        warnings.Children.Add(full);
        _accept.Content = Maus.Core.Legal.Disclaimer.Acceptance;
        AutomationProperties.SetName(_accept, Maus.Core.Legal.Disclaimer.Acceptance);
        _accept.Checked += (_, _) => RefreshButtons();
        _accept.Unchecked += (_, _) => RefreshButtons();
        warnings.Children.Add(_accept);
        var card = new Border { Child = warnings, Margin = new Thickness(0, 0, 0, 14) };
        card.SetResourceReference(StyleProperty, "SoftCard");
        card.SetResourceReference(Border.BackgroundProperty, "PastelSand");
        card.SetResourceReference(Border.BorderBrushProperty, "PastelSandStroke");
        panel.Children.Add(card);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 8, 24, 24) };
        if (offerModes)
        {
            panel.Children.Add(new TextBlock { Text = T("Comment utiliser MAUS ?"), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) }.Sized("FontSizeCardTitle"));
            panel.Children.Add(ElevationOfferWindow.Choice("Grey", ElevationOfferWindow.LimitedTitle, ElevationOfferWindow.LimitedText));
            panel.Children.Add(ElevationOfferWindow.Choice("Mint", ElevationOfferWindow.FullTitle, ElevationOfferWindow.FullTextHere));
            panel.Children.Add(_dontAsk);
            buttons.Children.Add(Continue(T("Toutes les fonctionnalités"), true, primary: true));
            buttons.Children.Add(Continue(T("Mode limité"), false, primary: false));
        }
        else
        {
            buttons.Children.Add(Continue(Ui.Continue, null, primary: true));
        }

        var quit = new Button { Content = Ui.Quit, IsCancel = true, Padding = new Thickness(16, 6, 16, 6) };
        quit.SetResourceReference(StyleProperty, "SoftButton");
        buttons.Children.Add(quit);

        // Sur un petit écran, seul le texte défile : les boutons pour continuer restent toujours en vue.
        var text = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Controls.PageScroll.SetEnabled(text, true);
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(buttons, 1);
        layout.Children.Add(text);
        layout.Children.Add(buttons);
        Content = layout;
        RefreshButtons();
    }

    private Button Continue(string label, bool? allFeatures, bool primary)
    {
        var button = new Button { Content = label, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0), IsDefault = primary };
        button.SetResourceReference(StyleProperty, primary ? "PrimaryButton" : "SoftButton");
        button.Click += (_, _) =>
        {
            _allFeatures = allFeatures;
            DialogResult = true;
        };
        _continueButtons.Add(button);
        return button;
    }

    /// <summary>Rien ne continue tant que les avertissements ne sont pas acceptés.</summary>
    private void RefreshButtons()
    {
        foreach (var button in _continueButtons)
        {
            button.IsEnabled = _accept.IsChecked == true;
        }
    }

    /// <summary>Montre l'écran d'ouverture.</summary>
    /// <param name="offerModes">MAUS tourne sans droits : proposer le mode limité ou toutes les fonctionnalités.</param>
    public static Answer Ask(bool offerModes)
    {
        var window = new WelcomeWindow(offerModes);
        var accepted = window.ShowDialog() == true;
        return new Answer(accepted, accepted ? window._allFeatures : null, window._dontAsk.IsChecked == true);
    }
}

internal static class TextBlockExtensions
{
    /// <summary>Taille de texte prise dans l'échelle du thème (<c>FontSizeCardTitle</c>, <c>FontSizePageTitle</c>…).</summary>
    public static TextBlock Sized(this TextBlock text, string resourceKey)
    {
        text.SetResourceReference(TextBlock.FontSizeProperty, resourceKey);
        return text;
    }
}
