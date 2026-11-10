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
public sealed class ElevationOfferWindow : MausDialog
{
    private readonly CheckBox _dontAsk = new() { Content = T("Ne plus me poser la question (modifiable dans les Paramètres)"), Margin = new Thickness(0, 6, 0, 14) };

    private ElevationOfferWindow()
        : base(T("Comment utiliser MAUS ?"), 560)
    {
        ShowInTaskbar = false;

        var heading = new TextBlock
        {
            Text = T("MAUS peut fonctionner de deux façons :"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };

        var limited = Choice("Grey", LimitedTitle, LimitedText);
        var full = Choice("Mint", FullTitle, FullTextHere);
        var yes = new Button { Content = T("Toutes les fonctionnalités"), IsDefault = true, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
        yes.SetResourceReference(StyleProperty, "PrimaryButton");
        yes.Click += (_, _) => DialogResult = true;
        var no = new Button { Content = T("Mode limité"), IsCancel = true, Padding = new Thickness(16, 6, 16, 6) };
        var buttons = ButtonRow(yes, no);

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(heading);
        panel.Children.Add(limited);
        panel.Children.Add(full);
        panel.Children.Add(_dontAsk);
        panel.Children.Add(buttons);
        Content = panel;
    }

    internal static string LimitedTitle => T("Mode limité, sans droits administrateur");

    internal static string LimitedText => T("Vous voyez votre matériel, les mesures en direct, les processus et l'espace disque, et l'audit marche, avec un score partiel. Les options qui ont besoin des droits administrateur sont grisées : le bouton « Toutes les fonctionnalités », en haut de la fenêtre, les active à tout moment.");

    internal static string FullTitle => T("Toutes les fonctionnalités, avec les droits administrateur");

    internal static string FullText => T("Toute l'application est disponible : audit complet, corrections (toujours annulables), réparation de Windows, capteurs avancés, fiche mémoire… Windows vous demande votre autorisation ; après 15 minutes sans utilisation, les droits se referment seuls et les options redeviennent grises.");

    /// <summary>Copie venue du Microsoft Store (signée par lui) ou d'ailleurs (pas encore signée).</summary>
    internal static bool IsUnsignedCopy(string? executable) =>
        executable is null || !executable.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    /// <summary>C'est le moment où l'on décide si l'on a confiance : ce que Windows va afficher est dit avant qu'il l'affiche.</summary>
    internal static string FullTextHere => IsUnsignedCopy(Environment.ProcessPath)
        ? FullText + " " + T("Sa fenêtre affichera « Éditeur : Inconnu » en jaune : cette copie de MAUS n'est pas encore signée. Si vous doutez, répondez non : MAUS reste utilisable en mode limité.")
        : FullText;

    /// <summary>Carte pastel d'une des deux façons d'utiliser MAUS : un titre de couleur, puis une phrase simple.</summary>
    internal static Border Choice(string tone, string title, string text)
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
