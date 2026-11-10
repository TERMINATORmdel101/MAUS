using System.Windows;
using System.Windows.Controls.Primitives;

namespace Maus.App.Controls;

/// <summary>
/// Grille de cases égales qui passe à la ligne quand la place manque : autant de colonnes que la largeur en permet, en lignes
/// complètes (quatre cases : quatre colonnes, puis deux, puis une). Sert quand l'affichage est agrandi ou la fenêtre étroite.
/// </summary>
public sealed class FittingGrid : UniformGrid
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(FittingGrid), new FrameworkPropertyMetadata(160.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Largeur en dessous de laquelle une case ne se lit plus.</summary>
    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    /// <summary>Le plus de colonnes possible sans laisser de ligne incomplète.</summary>
    internal static int ColumnsFor(int items, double width, double minItemWidth)
    {
        if (items <= 1 || !double.IsFinite(width) || minItemWidth <= 0)
        {
            return Math.Max(items, 1);
        }

        var fit = (int)(width / minItemWidth);
        for (var columns = Math.Min(fit, items); columns > 1; columns--)
        {
            if (items % columns == 0)
            {
                return columns;
            }
        }

        return 1;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        Columns = ColumnsFor(InternalChildren.Count, constraint.Width, MinItemWidth);
        return base.MeasureOverride(constraint);
    }
}
