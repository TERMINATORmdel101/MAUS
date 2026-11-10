using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Maus.App.Appearance;
using static Maus.Core.Localization.Texts;

namespace Maus.App.Views;

/// <summary>Ton d'un message : son icône et sa teinte.</summary>
public enum QuestionTone
{
    Information,
    Question,
    Warning,
    Error,
}

/// <summary>
/// Messages et questions aux couleurs de MAUS (demande du porteur du 1/11/2026). Échap et la fermeture de la fenêtre valent toujours
/// « non » ; le bouton principal est la réponse par défaut, celle qui répond à Entrée. Sans ressources utilisables (tout début de
/// l'application, erreur grave), le message de Windows reste la solution de repli : un avertissement doit toujours pouvoir s'afficher.
/// </summary>
public sealed partial class QuestionWindow : Window
{
    internal QuestionWindow(string title, IReadOnlyList<string> paragraphs, string? question, QuestionTone tone, string yes, string? no, bool yesIsDefault)
    {
        // WPF prend la première fenêtre créée pour la fenêtre principale : la fermer arrêterait MAUS.
        if (ReferenceEquals(Application.Current?.MainWindow, this))
        {
            Application.Current!.MainWindow = null;
        }

        Title = title;
        Width = 540;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && !ReferenceEquals(w, this)) ?? Application.Current?.MainWindow;
        if (owner is { IsVisible: true } && !ReferenceEquals(owner, this))
        {
            Owner = owner;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        AutomationProperties.SetName(this, title);
        AutomationProperties.SetHelpText(this, string.Join(" ", paragraphs));

        var body = new StackPanel();
        foreach (var paragraph in paragraphs)
        {
            body.Children.Add(new TextBlock { Text = paragraph, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 0, 0, 10) });
        }

        if (question is not null)
        {
            body.Children.Add(new TextBlock { Text = question, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) });
        }

        var text = new DockPanel();
        var heading = new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(heading, Dock.Top);
        text.Children.Add(heading);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        text.Children.Add(buttons);
        text.Children.Add(new ScrollViewer { Content = body, MaxHeight = 440, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        Button main;
        var accept = new Button { Content = yes, Style = TryFindResource(no is null || yesIsDefault ? "PrimaryButton" : "SoftButton") as Style };
        accept.Click += (_, _) => DialogResult = true;
        if (no is null)
        {
            accept.IsDefault = accept.IsCancel = true;
            main = accept;
            buttons.Children.Add(accept);
        }
        else
        {
            var decline = new Button { Content = no, IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Style = TryFindResource(yesIsDefault ? "SoftButton" : "PrimaryButton") as Style };
            main = yesIsDefault ? accept : decline;
            main.IsDefault = true;
            buttons.Children.Add(accept);
            buttons.Children.Add(decline);
        }

        var (glyphCode, tint) = tone switch
        {
            QuestionTone.Warning => ("", "PastelSand"),
            QuestionTone.Error => ("", "PastelRose"),
            QuestionTone.Question => ("", "PastelBlue"),
            _ => ("", "PastelBlue"),
        };
        var glyph = new TextBlock
        {
            Text = glyphCode,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, tint + "Ink");
        var badge = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top, Child = glyph };
        badge.SetResourceReference(Border.BackgroundProperty, tint);
        DockPanel.SetDock(badge, Dock.Left);
        var layout = new DockPanel { Margin = new Thickness(22, 20, 22, 18) };
        layout.Children.Add(badge);
        layout.Children.Add(text);

        var card = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Child = layout };
        card.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
        card.SetResourceReference(Border.BorderBrushProperty, tint + "Stroke");
        var shadow = new Border { CornerRadius = new CornerRadius(14), Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 3, Direction = 270, Opacity = 0.4 } };
        shadow.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
        var root = new Grid { Margin = new Thickness(18) };
        root.Children.Add(shadow);
        root.Children.Add(card);
        Content = root;

        MouseLeftButtonDown += OnDrag;
        Loaded += (_, _) =>
        {
            main.Focus();
            Motion.Enter(root, offset: 12);
        };
    }

    /// <summary>Question : <c>true</c> seulement si l'utilisateur a choisi « oui ».</summary>
    public static bool Ask(string title, string message, QuestionTone tone, bool yesIsDefault = false) =>
        Show(title, Paragraphs(message), null, tone, T("Oui"), T("Non"), yesIsDefault);

    /// <summary>Question avec ses propres boutons ; « non » est la réponse par défaut.</summary>
    public static bool Ask(string title, IReadOnlyList<string> paragraphs, string question, string yes, string no) =>
        Show(title, paragraphs, question, QuestionTone.Warning, yes, no, yesIsDefault: false);

    /// <summary>Simple message, avec un seul bouton.</summary>
    public static void Inform(string title, string message, QuestionTone tone) =>
        Show(title, Paragraphs(message), null, tone, T("OK"), null, yesIsDefault: true);

    /// <summary>Le texte, coupé à chaque ligne vide.</summary>
    internal static IReadOnlyList<string> Paragraphs(string message) =>
        BlankLine().Split(message.Trim()).Where(p => p.Length > 0).ToList();

    private static bool Show(string title, IReadOnlyList<string> paragraphs, string? question, QuestionTone tone, string yes, string? no, bool yesIsDefault)
    {
        var application = Application.Current;
        if (application is null || (application.Dispatcher.CheckAccess() && application.TryFindResource("PastelSand") is null))
        {
            return Fallback(title, paragraphs, question, tone, no is not null, yesIsDefault);
        }

        if (!application.Dispatcher.CheckAccess())
        {
            return application.Dispatcher.Invoke(() => Show(title, paragraphs, question, tone, yes, no, yesIsDefault));
        }

        // La fermeture de ce message ne doit jamais arrêter MAUS, même s'il est la seule fenêtre ouverte.
        var mode = application.ShutdownMode;
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            return new QuestionWindow(title, paragraphs, question, tone, yes, no, yesIsDefault).ShowDialog() == true;
        }
        catch (Exception ex) when (ex is XamlParseException or InvalidOperationException or ResourceReferenceKeyNotFoundException)
        {
            Maus.Core.Diagnostics.Breadcrumbs.Add("message de MAUS impossible à afficher (" + ex.GetType().Name + ")");
            return Fallback(title, paragraphs, question, tone, no is not null, yesIsDefault);
        }
        finally
        {
            application.ShutdownMode = mode;
        }
    }

    private static bool Fallback(string title, IReadOnlyList<string> paragraphs, string? question, QuestionTone tone, bool isQuestion, bool yesIsDefault)
    {
        var text = string.Join(Environment.NewLine + Environment.NewLine, question is null ? paragraphs : [.. paragraphs, question]);
        var image = tone switch
        {
            QuestionTone.Warning or QuestionTone.Error => MessageBoxImage.Warning,
            QuestionTone.Question => MessageBoxImage.Question,
            _ => MessageBoxImage.Information,
        };
        return isQuestion
            ? MessageBox.Show(text, title, MessageBoxButton.YesNo, image, yesIsDefault ? MessageBoxResult.Yes : MessageBoxResult.No) == MessageBoxResult.Yes
            : MessageBox.Show(text, title, MessageBoxButton.OK, image) == MessageBoxResult.OK;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    [GeneratedRegex(@"(?:\r?\n){2,}", RegexOptions.CultureInvariant)]
    private static partial Regex BlankLine();
}
