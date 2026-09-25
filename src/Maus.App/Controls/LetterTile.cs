using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Maus.App.Controls;

/// <summary>Tuile de lettre rivetée, à la manière du logo (lettre blanche sur plaque de couleur, deux rivets dorés).</summary>
public sealed class LetterTile : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(LetterTile), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(LetterTile), new FrameworkPropertyMetadata(Palette.Blue, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        var rect = new Rect((ActualWidth - size) / 2, (ActualHeight - size) / 2, size, size);
        drawingContext.DrawRoundedRectangle(Fill, null, rect, size * 0.18, size * 0.18);
        var rivet = Math.Max(1.2, size * 0.06);
        drawingContext.DrawEllipse(Palette.Rivet, null, new Point(rect.Left + (size * 0.17), rect.Top + (size * 0.17)), rivet, rivet);
        drawingContext.DrawEllipse(Palette.Rivet, null, new Point(rect.Right - (size * 0.17), rect.Top + (size * 0.17)), rivet, rivet);

        var text = new FormattedText(Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal),
            size * 0.55, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawingContext.DrawText(text, new Point(rect.Left + ((size - text.Width) / 2), rect.Top + ((size - text.Height) / 2)));
    }
}
