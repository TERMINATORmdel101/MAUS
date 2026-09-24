using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Maus.Core;

namespace Maus.App.Views;

/// <summary>Couleur du voyant selon le verdict : vert, bleu, orange, rouge ou gris.</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    private static readonly Brush Ok = Freeze(0x2E, 0x7D, 0x32);
    private static readonly Brush Improvable = Freeze(0x1E, 0x6F, 0xD9);
    private static readonly Brush Warning = Freeze(0xE0, 0x86, 0x00);
    private static readonly Brush Problem = Freeze(0xC6, 0x28, 0x28);
    private static readonly Brush Info = Freeze(0x00, 0x83, 0x8F);
    private static readonly Brush Unknown = Freeze(0x80, 0x80, 0x80);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        FindingStatus.Ok => Ok,
        FindingStatus.Improvable => Improvable,
        FindingStatus.Warning => Warning,
        FindingStatus.Problem => Problem,
        FindingStatus.Info => Info,
        _ => Unknown,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
