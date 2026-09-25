using System.Windows.Media;

namespace Maus.App.Controls;

/// <summary>Couleurs du logo : elles servent aussi de couleurs de voyants (vert conforme, bleu optimisation, or à surveiller, rouge problème).</summary>
public static class Palette
{
    public static readonly Color BlueColor = Color.FromRgb(0x1F, 0x4F, 0xBF);
    public static readonly Color RedColor = Color.FromRgb(0xC8, 0x10, 0x2E);
    public static readonly Color GreenColor = Color.FromRgb(0x0A, 0x9A, 0x5B);
    public static readonly Color GoldColor = Color.FromRgb(0xD4, 0x9A, 0x1E);
    public static readonly Color GreyColor = Color.FromRgb(0x8A, 0x8F, 0x9C);
    public static readonly Color RivetColor = Color.FromRgb(0xE8, 0xC4, 0x6A);

    public static SolidColorBrush Blue { get; } = Freeze(BlueColor);

    public static SolidColorBrush Red { get; } = Freeze(RedColor);

    public static SolidColorBrush Green { get; } = Freeze(GreenColor);

    public static SolidColorBrush Gold { get; } = Freeze(GoldColor);

    public static SolidColorBrush Grey { get; } = Freeze(GreyColor);

    public static SolidColorBrush Rivet { get; } = Freeze(RivetColor);

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
