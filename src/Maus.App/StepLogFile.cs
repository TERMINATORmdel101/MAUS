using System.Globalization;
using System.IO;

namespace Maus.App;

/// <summary>
/// Journal d'étapes pour le diagnostic (par exemple une lecture du matériel qui bloque), dans <c>%LOCALAPPDATA%\MAUS\logs</c>.
/// Recommencé à chaque opération, écrit ligne par ligne pour rester lisible même si l'opération ne se termine jamais.
/// Il ne contient que des étapes techniques, rien de personnel, et n'est jamais envoyé.
/// </summary>
public sealed class StepLogFile
{
    private readonly Lock _gate = new();

    public StepLogFile(string fileName)
    {
        try
        {
            var directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MAUS", "logs");
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, fileName);
            File.WriteAllText(Path, $"MAUS {Maus.Core.AppVersion.Display} · {DateTime.Now.ToString("g", CultureInfo.InvariantCulture)}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Path = null;
        }
    }

    /// <summary>Chemin du fichier, ou <c>null</c> s'il n'a pas pu être créé.</summary>
    public string? Path { get; }

    public void Write(string step)
    {
        Maus.Core.Diagnostics.Breadcrumbs.Add(step);
        if (Path is null)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                File.AppendAllText(Path, $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}  {step}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Journal de diagnostic seulement : son échec ne gêne jamais la lecture.
            }
        }
    }
}
