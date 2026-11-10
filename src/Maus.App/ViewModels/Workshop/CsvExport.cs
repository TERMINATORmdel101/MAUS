using System.IO;
using System.Text;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>Enregistrement d'un tableau CSV choisi par l'utilisateur, dans le format qu'Excel attend.</summary>
internal static class CsvExport
{
    /// <summary>Demande où enregistrer, puis écrit. Annulé : rien n'est appelé.</summary>
    /// <param name="fileName">Nom proposé.</param>
    /// <param name="content">Le tableau, calculé hors du fil de l'interface.</param>
    /// <param name="saved">Appelé avec le chemin du fichier écrit.</param>
    /// <param name="failed">Appelé si l'écriture échoue.</param>
    public static async Task SaveAsync(string fileName, Func<string> content, Action<string> saved, Action<Exception> failed)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = fileName,
            DefaultExt = ".csv",
            Filter = T("Tableau CSV") + " (*.csv)|*.csv",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            // Encodage avec BOM : Excel reconnaît alors les accents des en-têtes.
            await File.WriteAllTextAsync(dialog.FileName, await Task.Run(content), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            saved(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed(ex);
        }
    }
}
