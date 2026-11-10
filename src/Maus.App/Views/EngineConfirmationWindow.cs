using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Maus.Core.Engine.Broker;
using static Maus.Core.Localization.Texts;

namespace Maus.App.Views;

/// <summary>
/// Question du moteur administrateur (0.8.6), dans une fenêtre propre au moteur, à la place de la boîte de message de Windows.
/// Toujours au premier plan, au centre de l'écran. Ses deux boutons ne répondent qu'au bout de 2 secondes au premier plan, et de
/// nouveau 2 secondes après chaque retour au premier plan (<see cref="EngineConfirmationGate"/>) ; aucun raccourci d'une seule
/// touche, aucun bouton par défaut ; « Non » prend le focus. Échap, la croix et Alt+F4 valent « Non », comme l'absence de
/// réponse au bout de 2 minutes. « Oui » se choisit d'un clic, ou au clavier (Tab puis Espace ou Entrée) après 2 secondes
/// sans autre touche. Le thème de la fenêtre principale n'est pas chargé dans le processus du moteur : couleurs du système.
/// </summary>
internal sealed class EngineConfirmationWindow : Window
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMilliseconds(200);

    private readonly EngineConfirmationGate _gate;
    private readonly Button _yes;
    private readonly Button _no;
    private readonly DispatcherTimer _timer;
    private readonly string _yesText = T("Oui");
    private readonly string _noText = T("Non");
    private bool _closing;

    private EngineConfirmationWindow(string title, string message, EngineConfirmationGate gate)
    {
        _gate = gate;

        // WPF prend la première fenêtre créée pour la fenêtre principale : la fermer ne doit rien arrêter.
        if (ReferenceEquals(Application.Current?.MainWindow, this))
        {
            Application.Current!.MainWindow = null;
        }

        Title = T("MAUS (administrateur) : {0}", title);
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowActivated = true;
        ShowInTaskbar = true;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;
        SetResourceReference(BackgroundProperty, SystemColors.WindowBrushKey);
        SetResourceReference(ForegroundProperty, SystemColors.WindowTextBrushKey);
        AutomationProperties.SetName(this, Title);
        AutomationProperties.SetHelpText(this, message);

        var heading = new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        var question = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        var notice = new TextBlock
        {
            Text = T("Cette confirmation vient du moteur administrateur de MAUS. Les programmes sans droits administrateur ne peuvent pas cliquer dans cette fenêtre. Si vous n'avez rien demandé, répondez Non.")
                + Environment.NewLine + T("Les boutons répondent au bout de 2 secondes : un clic ou une frappe destinés à une autre fenêtre ne répondent pas à votre place."),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Margin = new Thickness(0, 14, 0, 0),
        };
        notice.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.GrayTextBrushKey);

        _yes = AnswerButton(_yesText);
        AutomationProperties.SetHelpText(_yes, T("Au clavier, « Oui » répond à Espace ou à Entrée 2 secondes après la dernière touche pressée."));
        _no = AnswerButton(_noText);
        _no.Margin = new Thickness(10, 0, 0, 0);
        _yes.Click += (_, _) =>
        {
            if (_gate.Yes())
            {
                Close();
            }
        };
        _no.Click += (_, _) =>
        {
            _gate.No();
            Close();
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(_yes);
        buttons.Children.Add(_no);

        var body = new StackPanel();
        body.Children.Add(question);
        body.Children.Add(notice);
        var layout = new DockPanel { Margin = new Thickness(24, 20, 24, 20) };
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(heading);
        layout.Children.Add(buttons);
        layout.Children.Add(new ScrollViewer { Content = body, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        Content = layout;

        _timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = RefreshEvery };
        _timer.Tick += (_, _) => Refresh();
        Activated += (_, _) =>
        {
            _gate.Activated();
            Refresh();
        };
        Deactivated += (_, _) =>
        {
            _gate.Deactivated();
            Refresh();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) =>
        {
            _timer.Start();
            Activate();
            Refresh();
        };

        // La croix, Alt+F4 ou toute autre fermeture : « Non », sauf si « Oui » a déjà été choisi.
        Closing += (_, _) =>
        {
            _closing = true;
            _gate.No();
        };
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Pose la question sur le fil de l'application et attend la réponse ; <c>true</c> seulement pour « Oui ».</summary>
    internal static bool Ask(string title, string message)
    {
        var clock = Stopwatch.StartNew();
        var gate = new EngineConfirmationGate(() => clock.Elapsed);
        var application = Application.Current;

        // La fermeture de cette fenêtre ne doit jamais arrêter le moteur.
        var mode = application?.ShutdownMode;
        if (application is not null)
        {
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        try
        {
            new EngineConfirmationWindow(title, message, gate).ShowDialog();
        }
        finally
        {
            if (application is not null && mode is { } previous)
            {
                application.ShutdownMode = previous;
            }
        }

        return gate.Answer == true;
    }

    /// <summary>Aucun « _ » dans le texte : pas de touche d'accès ; ni bouton par défaut ni bouton d'annulation.</summary>
    private static Button AnswerButton(string text)
    {
        var button = new Button { Content = text, MinWidth = 110, Padding = new Thickness(14, 5, 14, 5), IsEnabled = false };
        AutomationProperties.SetName(button, text);
        return button;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            e.Handled = true;
            _gate.No();
            if (!_closing)
            {
                Close();
            }

            return;
        }

        // Espace ou Entrée sur « Oui » : seulement après une pause du clavier (Tab, puis 2 secondes sans autre touche).
        if (key is Key.Space or Key.Enter && _yes.IsKeyboardFocused)
        {
            if (_gate.AcceptsKeyboardYes)
            {
                return;
            }

            e.Handled = true;
        }

        _gate.KeyPressed();
        Refresh();
    }

    private void Refresh()
    {
        if (_closing)
        {
            return;
        }

        if (_gate.Tick() is not null)
        {
            // Délai dépassé (« Non ») : la fenêtre se ferme d'elle-même.
            Close();
            return;
        }

        // Un retour au premier plan manqué par les évènements de la fenêtre se rattrape ici.
        if (IsActive != _gate.IsForeground)
        {
            if (IsActive)
            {
                _gate.Activated();
            }
            else
            {
                _gate.Deactivated();
            }
        }

        var enabled = _gate.ButtonsEnabled;
        if (enabled != _yes.IsEnabled)
        {
            _yes.IsEnabled = _no.IsEnabled = enabled;
            if (enabled)
            {
                _no.Focus();
            }
            else
            {
                // Les touches continuent d'arriver à la fenêtre (Échap = « Non »).
                Focus();
            }
        }

        var wait = _gate.SecondsBeforeButtons ?? 0;
        var yesWait = _yes.IsKeyboardFocused ? _gate.SecondsBeforeKeyboardYes ?? 0 : wait;
        _no.Content = wait > 0 ? T("Non ({0})", wait) : _noText;
        _yes.Content = yesWait > 0 ? T("Oui ({0})", yesWait) : _yesText;
    }
}
