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
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SafetyGauge), new FrameworkPropertyMetadata(0.0, OnValueChanged));

    /// <summary>Couleur du repère : suit le thème, et redemande le dessin quand il change.</summary>
    private static readonly DependencyProperty MarkerBrushProperty = DependencyProperty.Register(
        "MarkerBrush", typeof(Brush), typeof(SafetyGauge), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public SafetyGauge() => SetResourceReference(MarkerBrushProperty, "TextFillColorPrimaryBrush");

    /// <summary>Position affichée du repère : elle glisse jusqu'à la nouvelle valeur au lieu de sauter.</summary>
    private static readonly DependencyProperty ShownValueProperty = Register("ShownValue", 0.0);

    private static void OnValueChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        var gauge = (SafetyGauge)element;
        if (!gauge.IsLoaded || !Appearance.Motion.IsEnabled)
        {
            gauge.BeginAnimation(ShownValueProperty, null);
            gauge.SetValue(ShownValueProperty, (double)e.NewValue);
            return;
        }

        gauge.BeginAnimation(ShownValueProperty, new System.Windows.Media.Animation.DoubleAnimation((double)e.NewValue, new Duration(TimeSpan.FromMilliseconds(450))));
    }

    /// <summary>Couleur progressive : vert, puis or en approchant du seuil, puis rouge.</summary>
    private Color Heat(double value)
    {
        static Color Mix(Color a, Color b, double t) => Color.FromRgb(
            (byte)(a.R + ((b.R - a.R) * t)), (byte)(a.G + ((b.G - a.G) * t)), (byte)(a.B + ((b.B - a.B) * t)));

        var green = ((SolidColorBrush)Palette.Green).Color;
        var gold = ((SolidColorBrush)Palette.Gold).Color;
        var red = ((SolidColorBrush)Palette.Red).Color;
        if (value <= Elevated)
        {
            var low = Math.Clamp((value - Minimum) / Math.Max(1, Elevated - Minimum), 0, 1);
            return Mix(green, gold, low * low);
        }

        return Mix(gold, red, Math.Clamp((value - Elevated) / Math.Max(1, Danger - Elevated), 0, 1));
    }

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
        ArgumentNullException.ThrowIfNull(drawingContext);

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

        var shown = (double)GetValue(ShownValueProperty);
        var marker = X(shown);
        var foreground = (Brush?)GetValue(MarkerBrushProperty) ?? Brushes.Black;
        if (!SystemParameters.HighContrast)
        {
            var dot = new SolidColorBrush(Heat(shown));
            dot.Freeze();
            drawingContext.DrawEllipse(dot, new Pen(foreground, 1), new Point(Math.Clamp(marker, 4, ActualWidth - 4), 8), 5, 5);
        }

        drawingContext.DrawRoundedRectangle(foreground, null, new Rect(Math.Clamp(marker - 1.5, 0, ActualWidth - 3), 0, 3, 16), 1.5, 1.5);
    }

    private static DependencyProperty Register(string name, double value) => DependencyProperty.Register(
        name, typeof(double), typeof(SafetyGauge), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));
}
