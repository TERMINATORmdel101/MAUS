using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Maus.App.Views;

/// <summary>Impression du rapport par la boîte de dialogue de Windows, qui propose aussi « Microsoft Print to PDF ».</summary>
internal static class ReportPrinter
{
    /// <summary>Renvoie <c>false</c> si l'utilisateur renonce.</summary>
    public static bool Print(string text)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        var document = new FlowDocument(new Paragraph(new Run(text)))
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            Foreground = Brushes.Black,
            Background = Brushes.White,
            PageWidth = dialog.PrintableAreaWidth,
            PageHeight = dialog.PrintableAreaHeight,
            PagePadding = new Thickness(48),
            ColumnWidth = dialog.PrintableAreaWidth,
        };
        dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, "MAUS");
        return true;
    }
}
