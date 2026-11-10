using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.App.Views;

/// <summary>Test de l'écran : couleurs unies pour repérer les pixels morts, dégradé pour voir les défauts. La couleur ne change qu'au clic.</summary>
public sealed class ScreenTestWindow : Window
{
    private static readonly Brush[] Patterns =
    [
        Brushes.Black, Brushes.White, Brushes.Red, Brushes.Lime, Brushes.Blue, Brushes.Gray,
        new LinearGradientBrush(Colors.Black, Colors.White, 0),
    ];

    private readonly TextBlock _hint;
    private int _index;

    public ScreenTestWindow()
    {
        Title = T("Test de l'écran");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Background = Patterns[0];
        if (Application.Current?.MainWindow is { IsVisible: true } main)
        {
            Left = main.Left;
            Top = main.Top;
        }

        WindowState = WindowState.Maximized;
        _hint = new TextBlock
        {
            Text = T("Cherchez les points d'une autre couleur. Clic ou espace : couleur suivante. Échap : quitter."),
            Foreground = Brushes.White,
            Background = Brushes.DimGray,
            Padding = new Thickness(14, 8, 14, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 40),
        };
        Content = _hint;
        MouseDown += (_, _) => Next();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
            else
            {
                Next();
            }
        };
    }

    private void Next()
    {
        if (++_index >= Patterns.Length)
        {
            Close();
            return;
        }

        Background = Patterns[_index];
        _hint.Visibility = Visibility.Collapsed;
    }
}

/// <summary>
/// Test du clavier et de la souris. Seulement dans cette fenêtre, au premier plan, et rien n'est enregistré : les touches s'affichent,
/// puis tout disparaît à la fermeture. Le rythme de la souris vient de Raw Input, limité à la souris et à cette fenêtre.
/// </summary>
public sealed class InputTestWindow : Window
{
    private const int WmInput = 0x00FF;

    private readonly WrapPanel _keys = new() { Margin = new Thickness(0, 8, 0, 16) };
    private readonly HashSet<Key> _seen = [];
    private readonly TextBlock _mouse = new() { TextWrapping = TextWrapping.Wrap };
    private readonly MouseRate _rate = new();
    private readonly ClickBounce _bounce = new();
    private readonly int[] _buttons = new int[3];
    private int _wheel;
    private int _shownRate = -1;

    public InputTestWindow()
    {
        Title = T("Test du clavier et de la souris");
        Width = 640;
        Height = 460;
        Owner = Application.Current?.MainWindow is { IsVisible: true } main ? main : null;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = T("Appuyez sur chaque touche : elle s'affiche si elle répond. Bougez la souris vite, cliquez, tournez la molette. Rien n'est enregistré."), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_keys);
        panel.Children.Add(_mouse);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SourceInitialized += (_, _) =>
        {
            var source = (HwndSource)PresentationSource.FromVisual(this);
            source.AddHook(OnMessage);
            RawMouse.Listen(new WindowInteropHelper(this).Handle);
        };
        Closed += (_, _) => RawMouse.Stop();
        PreviewKeyDown += OnKey;
        PreviewMouseDown += OnButton;
        PreviewMouseWheel += (_, _) =>
        {
            _wheel++;
            ShowMouse();
        };
        ShowMouse();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.System)
        {
            return;
        }

        e.Handled = true;
        if (_seen.Add(e.Key))
        {
            _keys.Children.Add(new Border
            {
                Margin = new Thickness(0, 4, 6, 0),
                Padding = new Thickness(8, 2, 8, 2),
                CornerRadius = new CornerRadius(6),
                Background = (Brush)FindResource("MausAccentStrong"),
                Child = new TextBlock { Text = e.Key.ToString(), Foreground = Brushes.White },
            });
        }
    }

    private void OnButton(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            _bounce.Add(Environment.TickCount64);
            _buttons[0]++;
        }
        else if (e.ChangedButton == MouseButton.Right)
        {
            _buttons[1]++;
        }
        else if (e.ChangedButton == MouseButton.Middle)
        {
            _buttons[2]++;
        }

        ShowMouse();
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmInput)
        {
            _rate.Add(Environment.TickCount64);
            if (_rate.Best != _shownRate)
            {
                _shownRate = _rate.Best;
                ShowMouse();
            }
        }

        return 0;
    }

    private void ShowMouse() => _mouse.Text =
        T("Souris : {0} mesures par seconde au mieux.", Math.Max(0, _rate.Best)) + "\n"
        + T("Clics : gauche {0}, droit {1}, milieu {2}. Molette : {3}.", _buttons[0], _buttons[1], _buttons[2], _wheel) + "\n"
        + T("Doubles clics involontaires : {0} (deux clics en moins de {1} ms, seuil choisi par MAUS).", _bounce.Count, ClickBounce.LimitMilliseconds);
}
