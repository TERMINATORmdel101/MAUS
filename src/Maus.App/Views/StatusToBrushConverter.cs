using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Maus.App.Controls;
using Maus.Core;

namespace Maus.App.Views;

/// <summary>Couleur du voyant selon le verdict, prise dans le logo : vert, bleu, or, rouge ; gris pour l'information et l'indéterminé.</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        FindingStatus.Ok => Palette.Green,
        FindingStatus.Improvable => Palette.Blue,
        FindingStatus.Warning => Palette.Gold,
        FindingStatus.Problem => Palette.Red,
        _ => Palette.Grey,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Couleur d'un score de santé : vert au-dessus de 75, or au-dessus de 50, rouge en dessous.</summary>
public sealed class ScoreToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        int and >= 75 => Palette.Green,
        int and >= 50 => Palette.Gold,
        int and >= 0 => Palette.Red,
        _ => ThemeText(),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>Pas encore de score : couleur du texte du thème (blanc en thème sombre), pas un gris qui passerait pour un verdict.</summary>
    internal static Brush ThemeText() =>
        System.Windows.Application.Current?.TryFindResource("TextFillColorPrimaryBrush") as Brush ?? Palette.Grey;
}

/// <summary>
/// Couleur d'une lettre de MAUS (M bleu, A rouge, U vert, S or), comme sur le logo ; les tuiles des composants de l'atelier
/// reprennent les mêmes couleurs (P processeur, C carte mère, R mémoire, G carte graphique, D disque, B batterie).
/// </summary>
public sealed class LetterToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string) switch
    {
        "M" or "P" or "W" => Palette.Blue,
        "A" or "G" or "E" => Palette.Red,
        "U" or "R" or "B" or "N" => Palette.Green,
        "S" or "C" => Palette.Gold,
        _ => (Brush)Palette.Grey,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Couleur de la pastille « confiance » d'un processus : vert pour Windows et les programmes installés, or pour un emplacement inhabituel.</summary>
public sealed class TrustToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Maus.Core.Workshop.ProcessTrust.Windows or Maus.Core.Workshop.ProcessTrust.Installed => Palette.Green,
        Maus.Core.Workshop.ProcessTrust.UserFolder => Palette.Blue,
        Maus.Core.Workshop.ProcessTrust.Unusual => Palette.Gold,
        _ => Palette.Grey,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
