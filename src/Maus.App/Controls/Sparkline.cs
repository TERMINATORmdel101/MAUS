using System.Collections;
using System.Windows;
using System.Windows.Media;

namespace Maus.App.Controls;

/// <summary>Mini-courbe des dernières mesures (valeurs de 0 à <see cref="Maximum"/>).</summary>
public sealed class Sparkline : FrameworkElement
{
    /// <summary>Part de la courbe déjà dessinée, de 0 à 1.</summary>
    private static readonly DependencyProperty RevealProperty = DependencyProperty.Register(
        "Reveal", typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Palette.Blue, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Repères : l'indice de chaque valeur précédée d'un changement, marqué par un trait vertical entre elle et la précédente.</summary>
    public static readonly DependencyProperty MarksProperty = DependencyProperty.Register(
        nameof(Marks), typeof(IEnumerable), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Values
    {
        get => (IEnumerable?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>Valeur du haut du graphique ; 0 = échelle automatique.</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public IEnumerable? Marks
    {
        get => (IEnumerable?)GetValue(MarksProperty);
        set => SetValue(MarksProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Sparkline() => Loaded += (_, _) => Appearance.Motion.Reveal(this, RevealProperty);

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        var values = Values?.Cast<object>().Select(v => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture)).ToList() ?? [];
        if (values.Count < 2 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var top = Maximum > 0 ? Maximum : Math.Max(1, values.Max() * 1.15);
        var step = ActualWidth / (values.Count - 1);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var i = 0; i < values.Count; i++)
            {
                var point = new Point(i * step, ActualHeight - (Math.Clamp(values[i] / top, 0, 1) * (ActualHeight - 2)) - 1);
                if (i == 0)
                {
                    context.BeginFigure(point, false, false);
                }
                else
                {
                    context.LineTo(point, true, true);
                }
            }
        }

        geometry.Freeze();
        drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth * (double)GetValue(RevealProperty), ActualHeight)));
        var markPen = new Pen(Palette.Grey, 1) { DashStyle = DashStyles.Dash };
        foreach (var mark in Marks?.OfType<int>().Where(m => m > 0 && m < values.Count) ?? [])
        {
            var x = (mark - 0.5) * step;
            drawingContext.DrawLine(markPen, new Point(x, 0), new Point(x, ActualHeight));
        }

        drawingContext.DrawGeometry(null, new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round }, geometry);
        drawingContext.Pop();
    }
}
