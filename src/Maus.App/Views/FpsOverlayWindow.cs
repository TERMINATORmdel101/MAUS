using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Maus.Core.Platform;
using Maus.Core.Preferences;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.App.Views;

/// <summary>
/// Compteur d'images par seconde posé au-dessus du jeu (demande du porteur, 06/10/2026) : fenêtre transparente aux clics,
/// toujours au premier plan, jamais activée, dans un coin de l'écran principal. Taille et transparence réglables.
/// Aucune injection dans le jeu (voir <see cref="OverlayWindowStyle"/>).
/// </summary>
public sealed class FpsOverlayWindow : Window
{
    private const double EdgeGap = 14;

    private readonly TextBlock _fps = new() { FontSize = 26, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
    private readonly TextBlock _unit = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(6, 0, 0, 4), VerticalAlignment = VerticalAlignment.Bottom, Text = "FPS" };
    private readonly TextBlock _detail = new() { FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE7, 0xEE)) };
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly DispatcherTimer _topmost = new() { Interval = TimeSpan.FromSeconds(2) };
    private OverlayCorner _corner;

    public FpsOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "MAUS — FPS";

        var big = new StackPanel { Orientation = Orientation.Horizontal };
        big.Children.Add(_fps);
        big.Children.Add(_unit);
        var panel = new StackPanel();
        panel.Children.Add(big);
        panel.Children.Add(_detail);
        Content = new Border
        {
            // Fond sombre fixe : lisible sur n'importe quelle image de jeu, quel que soit le thème de MAUS.
            Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x12, 0x16, 0x1F)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 6, 12, 8),
            LayoutTransform = _scale,
            Child = panel,
        };

        _fps.Text = "—";
        _detail.Text = T("en attente d'un jeu…");
        SizeChanged += (_, _) => Place();
        _topmost.Tick += (_, _) => OverlayWindowStyle.KeepOnTop(new WindowInteropHelper(this).Handle);
        Closed += (_, _) => _topmost.Stop();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (!OverlayWindowStyle.MakeClickThrough(new WindowInteropHelper(this).Handle))
        {
            Maus.Core.Diagnostics.Breadcrumbs.Add("compteur au-dessus du jeu : clics non transparents (refus de Windows)");
        }
        _topmost.Start();
    }

    /// <summary>Taille, transparence et coin choisis par l'utilisateur.</summary>
    public void Apply(double scale, double opacity, OverlayCorner corner)
    {
        _scale.ScaleX = _scale.ScaleY = scale;
        Opacity = opacity;
        _corner = corner;
        Place();
    }

    /// <summary>Dernier bilan : chiffre de la dernière seconde en grand, puis les dix dernières secondes ; <c>null</c> = pas encore de jeu.</summary>
    public void Display(FrameSummary? frames)
    {
        if (frames is null)
        {
            _fps.Text = "—";
            _detail.Text = T("en attente d'un jeu…");
            return;
        }

        _fps.Text = frames.LiveFps.ToString("0", Culture);
        var parts = new List<string> { T("moy. 10 s : {0:0}", frames.AverageFps), T("1 % : {0:0}", frames.Low1Fps) };
        if (frames.Low01Fps is { } low01)
        {
            parts.Add(T("0,1 % : {0:0}", low01));
        }

        if (frames.GpuBusyShare is { } busy)
        {
            parts.Add(T("carte graphique : {0:0} %", busy * 100));
        }

        _detail.Text = string.Join("  ·  ", parts);
    }

    /// <summary>Coin de l'écran principal entier (le jeu en plein écran couvre aussi la barre des tâches).</summary>
    private void Place()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        Left = _corner is OverlayCorner.TopRight or OverlayCorner.BottomRight ? SystemParameters.PrimaryScreenWidth - width - EdgeGap : EdgeGap;
        Top = _corner is OverlayCorner.BottomLeft or OverlayCorner.BottomRight ? SystemParameters.PrimaryScreenHeight - height - EdgeGap : EdgeGap;
    }
}
