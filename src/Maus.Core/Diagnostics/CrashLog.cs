using System.Globalization;
using System.Text;

namespace Maus.Core.Diagnostics;

/// <summary>
/// Journal des erreurs imprévues, dans <c>%LOCALAPPDATA%\MAUS\logs\erreur-AAAAMMJJ-HHMMSS.txt</c> : la version de MAUS,
/// ce qui était en cours, l'erreur complète et les dernières actions (<see cref="Breadcrumbs"/>). Local, jamais envoyé.
/// </summary>
public static class CrashLog
{
    /// <summary>Dossier des journaux de diagnostic de MAUS.</summary>
    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MAUS", "logs");

    /// <summary>Écrit l'erreur dans le journal.</summary>
    /// <param name="exception">L'erreur imprévue.</param>
    /// <param name="what">Ce qui était en cours (texte technique, non traduit).</param>
    /// <returns>Le chemin du fichier, ou <c>null</c> s'il n'a pas pu être écrit.</returns>
    public static string? Write(Exception exception, string what) => Write(Folder, exception, what, DateTime.Now);

    internal static string? Write(string folder, Exception exception, string what, DateTime now)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "erreur-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
            var text = new StringBuilder()
                .Append("MAUS ").Append(AppVersion.Display).Append(" · ").AppendLine(now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                .AppendLine(what)
                .AppendLine()
                .AppendLine(exception.ToString())
                .AppendLine()
                .AppendLine("Dernières actions :");
            foreach (var step in Breadcrumbs.Snapshot())
            {
                text.Append("  ").AppendLine(step);
            }

            // Ajouté à la suite : deux erreurs dans la même seconde restent lisibles toutes les deux.
            File.AppendAllText(path, text.AppendLine().ToString());
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }
}
