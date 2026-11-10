using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Maus.App.Appearance;
using Maus.App.ViewModels.Workshop;

namespace Maus.App.Views;

/// <summary>
/// Le détail d'un score du benchmark et la carte de score en grand, posés par-dessus la fenêtre (demande du porteur du 1/11/2026).
/// Échap, un clic à côté ou « Fermer » referment. Tout s'affiche sans mouvement si les animations sont coupées.
/// </summary>
public partial class BenchmarkOverlay : UserControl
{
    private Brush _barBrush = Brushes.Transparent;
    private IInputElement? _focusBefore;

    public BenchmarkOverlay()
    {
        InitializeComponent();
    }

    public void ShowDetail(BenchmarkDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var (tint, bar, glyph) = detail.Kind switch
        {
            "gpu" => ("PastelBlue", "MausBlue", ""),
            "cpu" => ("PastelMint", "MausGreen", ""),
            "rt" => ("PastelSand", "MausGold", ""),
            _ => ("PastelLilac", "MausAccent", ""),
        };
        _barBrush = (Brush)FindResource(bar);
        DetailHeader.SetResourceReference(Border.BackgroundProperty, tint);
        DetailBadge.SetResourceReference(Border.BackgroundProperty, tint + "Stroke");
        DetailGlyph.SetResourceReference(TextBlock.ForegroundProperty, tint + "Ink");
        DetailGlyph.Text = glyph;
        DetailTitle.SetResourceReference(TextBlock.ForegroundProperty, tint + "Ink");
        DetailTitle.Text = detail.Title;
        DetailSubtitle.Text = detail.Subtitle;
        ScoreGlow.Color = _barBrush is SolidColorBrush solid ? solid.Color : Colors.White;
        Motion.SetRollingText(DetailScore, null);
        Motion.SetRollingText(DetailScore, detail.Score);
        DetailWarning.Text = detail.Warning;
        DetailWarning.Visibility = detail.Warning.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailHealthText.Text = detail.Health;
        DetailHealth.Visibility = detail.Health.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailChips.ItemsSource = detail.Sensors;
        DetailRows.ItemsSource = detail.Rows;
        DetailTrend.Values = detail.Trend;
        DetailTrend.Visibility = detail.Trend.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        DetailNote.Text = detail.Note;
        CopyDetail.Visibility = detail.Kind == "all" ? Visibility.Visible : Visibility.Collapsed;
        Open(DetailShadow, PicturePanel, CloseDetail);
    }

    public void ShowPicture(ImageSource? source)
    {
        Picture.Source = source;
        Open(PicturePanel, DetailShadow, ClosePicture);
    }

    private void Open(FrameworkElement panel, FrameworkElement other, Button close)
    {
        _focusBefore = Keyboard.FocusedElement;
        other.Visibility = Visibility.Collapsed;
        panel.Visibility = Visibility.Visible;
        Visibility = Visibility.Visible;
        if (Motion.IsEnabled)
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(180))));
            Motion.Pop(panel);
        }

        Dispatcher.BeginInvoke(() => close.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Close()
    {
        if (!Motion.IsEnabled)
        {
            Closed();
            return;
        }

        var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(130)));
        fade.Completed += (_, _) => Closed();
        BeginAnimation(OpacityProperty, fade);
    }

    private void Closed()
    {
        BeginAnimation(OpacityProperty, null);
        Visibility = Visibility.Collapsed;
        DetailRows.ItemsSource = null;
        DetailChips.ItemsSource = null;
        Picture.Source = null;
        _focusBefore?.Focus();
        _focusBefore = null;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnBackdropClick(object sender, MouseButtonEventArgs e) => Close();

    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // Les raccourcis de la fenêtre changeraient de page derrière ce panneau.
            e.Handled = true;
        }
    }

    private void OnBarLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ProgressBar bar && bar.Tag is double fill)
        {
            bar.Foreground = _barBrush;
            var index = DetailRows.ItemContainerGenerator.IndexFromContainer(ItemsControl.ContainerFromElement(DetailRows, bar));
            Motion.FillTo(bar, fill, TimeSpan.FromMilliseconds(120 + (Math.Max(0, index) * 90)));
        }
    }

    private void OnMarkLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border { DataContext: BenchmarkDetailRow row, Child: TextBlock text } chip)
        {
            chip.Visibility = row.Mark.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            chip.SetResourceReference(Border.BackgroundProperty, row.IsStrong ? "PastelMint" : "PastelSand");
            text.SetResourceReference(TextBlock.ForegroundProperty, row.IsStrong ? "PastelMintInk" : "PastelSandInk");
        }
    }

    /// <summary>Coins arrondis : le contenu (bande de couleurs, image) ne doit pas déborder de son cadre.</summary>
    private void OnRoundedSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            var radius = ReferenceEquals(element, Picture) ? 12 : 16;
            element.Clip = new RectangleGeometry(new Rect(e.NewSize), radius, radius);
        }
    }
}
