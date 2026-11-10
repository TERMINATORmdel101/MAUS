using System.Windows;
using System.Windows.Media;

namespace Maus.App.Controls;

/// <summary>Fin d'un test : une coche verte qui se dessine, ou un point d'exclamation orange si quelque chose cloche.</summary>
public sealed class DoneMark : FrameworkElement
{
    public static readonly DependencyProperty GoodProperty = DependencyProperty.Register(
        nameof(Good), typeof(bool), typeof(DoneMark), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Part déjà dessinée, de 0 à 1.</summary>
    private static readonly DependencyProperty DrawnProperty = DependencyProperty.Register(
        "Drawn", typeof(double), typeof(DoneMark), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Attention = CreateAttention();

    public DoneMark() => IsVisibleChanged += (_, e) =>
    {
        if ((bool)e.NewValue)
        {
            Appearance.Motion.Reveal(this, DrawnProperty);
        }
    };

    public bool Good
    {
        get => (bool)GetValue(GoodProperty);
        set => SetValue(GoodProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        var drawn = (double)GetValue(DrawnProperty);
        var pen = new Pen(Good ? Palette.Green : Attention, size / 11) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        Point At(double x, double y) => new(x * size, y * size);

        drawingContext.PushOpacity(Math.Min(1, drawn * 1.6));
        drawingContext.DrawEllipse(null, pen, At(0.5, 0.5), (size / 2) - (pen.Thickness / 2), (size / 2) - (pen.Thickness / 2));
        drawingContext.Pop();

        if (Good)
        {
            var (a, b, c) = (At(0.28, 0.52), At(0.44, 0.68), At(0.72, 0.36));
            drawingContext.DrawLine(pen, a, a + ((b - a) * Math.Min(1, drawn / 0.36)));
            if (drawn > 0.36)
            {
                drawingContext.DrawLine(pen, b, b + ((c - b) * ((drawn - 0.36) / 0.64)));
            }
        }
        else
        {
            var (top, bottom) = (At(0.5, 0.26), At(0.5, 0.58));
            drawingContext.DrawLine(pen, top, top + ((bottom - top) * drawn));
            if (drawn >= 0.9)
            {
                drawingContext.DrawLine(pen, At(0.5, 0.74), At(0.5, 0.74));
            }
        }
    }

    private static SolidColorBrush CreateAttention()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x1E));
        brush.Freeze();
        return brush;
    }
}
