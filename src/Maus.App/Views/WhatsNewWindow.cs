using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using static Maus.Core.Localization.Texts;

namespace Maus.App.Views;

/// <summary>
/// « Quoi de neuf » (demande du porteur, 27/10/2026) : à la première ouverture d'une nouvelle version, les nouveautés en
/// quelques lignes. Jamais lors d'une toute première installation, ni pour une version sans texte prévu.
/// </summary>
public sealed class WhatsNewWindow : Window
{
    private WhatsNewWindow(string version, IReadOnlyList<string> lines)
    {
        Title = T("Quoi de neuf dans MAUS {0}", version);
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Owner = Application.Current?.MainWindow;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

        var panel = new StackPanel { Margin = new Thickness(20) };
        var title = new TextBlock { Text = Title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(title);
        foreach (var line in lines)
        {
            var text = new TextBlock { Text = "• " + line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            AutomationProperties.SetName(text, line);
            panel.Children.Add(text);
        }

        var all = new Button { Content = T("Toutes les nouveautés"), Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
        all.Click += (_, _) => ShellLauncher.OpenUrl(new Uri(Maus.Core.ProjectLinks.Repository + "/releases/tag/" + version));
        var close = new Button { Content = T("Fermer"), IsDefault = true, IsCancel = true, Padding = new Thickness(16, 6, 16, 6) };
        close.SetResourceReference(StyleProperty, "PrimaryButton");
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(all);
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
        Content = panel;
    }

    /// <summary>Nouveautés de chaque version, en quelques lignes ; vide si rien n'est prévu pour cette version.</summary>
    public static IReadOnlyList<string> For(string version) => version switch
    {
        "0.8.5" =>
        [
            T("Le code de MAUS a été relu en entier : aucune faille grave trouvée, et le moteur administrateur est plus strict (chaque action sur le pilote graphique confirmée par lui, annulation forcée refusée, entrées bornées)."),
            T("La fenêtre ne se fige plus pendant l'installation de PawnIO, et un audit ne peut plus en doubler un autre."),
            T("Profils de corrections et mode d'alimentation traduits dans les trois langues ; les erreurs imprévues sont journalisées dans le dossier des journaux de MAUS."),
        ],
        // 0.8.3 n'a pas été publiée : ses nouveautés sont annoncées avec la 0.8.4.
        "0.8.4" or "0.8.3" =>
        [
            T("Accueil plus simple : le score, la prochaine étape et trois outils ; le reste est dans « Plus d'outils » (l'accueil complet reste au choix dans les Paramètres)."),
            T("Plus joli : score qui se remplit, « Votre PC en un coup d'œil », fond Mica de Windows 11 au choix, raccourcis Ctrl+1 à Ctrl+6."),
            T("Nouvelle fenêtre d'ouverture « Comment utiliser MAUS ? » : le mode limité, sans droits administrateur, ou toutes les fonctionnalités."),
            T("Mode limité plus clair : les options qui demandent les droits administrateur sont grisées, et le bandeau « Toutes les fonctionnalités » les active."),
            T("Plus de sécurité : messages signés entre la fenêtre et le moteur administrateur, protections de Windows sur MAUS, avertissement si MAUS n'est pas installé dans un dossier protégé."),
            T("Pour les actions sensibles (fermer un programme, pilote graphique, PawnIO, constat « voulu »), le moteur administrateur demande lui-même confirmation dans sa propre fenêtre."),
        ],
        "0.8.2" =>
        [
            T("Bouton « ? » en bas à gauche : le site d'aide de MAUS."),
            T("Pourquoi mon PC démarre lentement ? Pourquoi se réveille-t-il la nuit ? Les réponses de Windows dans l'audit (démarrage, alimentation)."),
            T("Écrans bleus : les 5 derniers, avec leur code et le lien vers l'explication de Microsoft."),
            T("Historique : vos points de restauration de Windows, et un bouton pour en créer un."),
            T("Corrections : désactiver une tâche planifiée lancée à l'ouverture de session, bloquer les instantanés de Recall, remettre les effets visuels aux valeurs de Windows."),
            T("Écran sous sa fréquence maximale : bouton « Essayer la fréquence maximale », l'ancien réglage revient seul après 15 secondes."),
            T("Batterie : cycles de charge et rapport de batterie de Windows ; Wi-Fi : réseaux voisins par canal."),
        ],
        _ => [],
    };

    /// <summary>Montre les nouveautés de <paramref name="version"/> s'il y en a.</summary>
    public static void ShowFor(string version)
    {
        var lines = For(version);
        if (lines.Count > 0)
        {
            new WhatsNewWindow(version, lines).ShowDialog();
        }
    }
}
