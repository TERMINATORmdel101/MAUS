using System.Windows;
using System.Windows.Media;

namespace Maus.App.Controls;

/// <summary>Jauge de sécurité : zone normale (vert), élevée (or), dangereuse (rouge), et un repère sur la valeur actuelle.</summary>
public sealed class SafetyGauge : FrameworkElement
{
    public static readonly DependencyProperty MinimumProperty = Register(nameof(Minimum), 0.0);
    public static readonly DependencyProperty MaximumProperty = Register(nameof(Maximum), 100.0);
    public static readonly DependencyProperty ElevatedProperty = Register(nameof(Elevated), 70.0);
    public static readonly DependencyProperty DangerProperty = Register(nameof(Danger), 90.0);
    public static readonly DependencyProperty ValueProperty = Register(nameof(Value), 0.0);

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Elevated
    {
        get => (double)GetValue(ElevatedProperty);
        set => SetValue(ElevatedProperty, value);
    }

    public double Danger
    {
        get => (double)GetValue(DangerProperty);
        set => SetValue(DangerProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 16);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var range = Maximum - Minimum;
        if (range <= 0 || ActualWidth <= 0)
        {
            return;
        }

        double X(double value) => Math.Clamp((value - Minimum) / range, 0, 1) * ActualWidth;
        const double barTop = 4;
        const double barHeight = 8;
        drawingContext.PushClip(new RectangleGeometry(new Rect(0, barTop, ActualWidth, barHeight), 4, 4));
        drawingContext.DrawRectangle(Palette.Green, null, new Rect(0, barTop, X(Elevated), barHeight));
        drawingContext.DrawRectangle(Palette.Gold, null, new Rect(X(Elevated), barTop, Math.Max(0, X(Danger) - X(Elevated)), barHeight));
        drawingContext.DrawRectangle(Palette.Red, null, new Rect(X(Danger), barTop, Math.Max(0, ActualWidth - X(Danger)), barHeight));
        drawingContext.Pop();

        var marker = X(Value);
        var foreground = (Brush?)TryFindResource("TextFillColorPrimaryBrush") ?? Brushes.Black;
        drawingContext.DrawRoundedRectangle(foreground, null, new Rect(Math.Clamp(marker - 1.5, 0, ActualWidth - 3), 0, 3, 16), 1.5, 1.5);
    }

    private static DependencyProperty Register(string name, double value) => DependencyProperty.Register(
        name, typeof(double), typeof(SafetyGauge), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));
}
