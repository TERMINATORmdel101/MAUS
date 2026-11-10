using System.Globalization;
using System.Text;

namespace Maus.Core.Diagnostics;

/// <summary>
/// Journal des erreurs imprévues, dans <c>%LOCALAPPDATA%\MAUS\logs\erreur-AAAAMMJJ-HHMMSS.txt</c> : la version de MAUS,
/// ce qui était en cours, l'erreur complète et les dernières actions (<see cref="Breadcrumbs"/>). Local, jamais envoyé.
/// Un processus élevé (moteur, benchmark lancé par le moteur, fenêtre lancée en administrateur, audit planifié d'une ancienne
/// tâche) écrit le sien dans <c>%ProgramData%\MAUS\logs</c>, réservé aux administrateurs (<see cref="UseProtectedFolder"/>,
/// 0.8.6), et nulle part sur le disque si ce dossier ne peut pas être protégé.
/// </summary>
public static class CrashLog
{
    private static string? s_folder;

    /// <summary>Processus élevé sans dossier protégé : les traces restent en mémoire, rien n'est écrit sur le disque.</summary>
    private static bool s_memoryOnly;

    /// <summary>Dossier des journaux de diagnostic de MAUS ; <c>null</c> : rien ne s'écrit sur le disque dans ce processus.</summary>
    public static string? Folder => s_memoryOnly ? null : s_folder ?? UserFolder;

    /// <summary>Dossier des journaux d'un processus sans droits : dans le profil de l'utilisateur.</summary>
    public static string UserFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MAUS", "logs");

    /// <summary>Tests de l'interface : le journal va dans un dossier temporaire, jamais avec ceux du PC qui lance les tests.</summary>
    internal static void UseFolder(string folder)
    {
        s_folder = folder;
        s_memoryOnly = false;
    }

    /// <summary>Dossier des journaux d'un processus élevé : réservé à SYSTEM et aux administrateurs, comme le journal des corrections.</summary>
    public static string ProtectedFolder =>
        Path.Combine(Platform.SystemPaths.MausData, "logs");

    /// <summary>
    /// Processus élevé : journal dans <see cref="ProtectedFolder"/> au lieu du profil de l'utilisateur, où un programme sans
    /// droits pourrait le lire ou le détourner. Si le dossier ne peut pas être protégé, aucun journal n'est écrit sur le disque
    /// (0.8.6) : les dernières actions restent en mémoire.
    /// </summary>
    /// <returns>Vrai si le dossier protégé est en place.</returns>
    public static bool UseProtectedFolder() => UseProtectedFolder(new Fixes.WindowsDirectoryProtector(), ProtectedFolder);

    internal static bool UseProtectedFolder(Fixes.IDirectoryProtector protector, string folder)
    {
        ArgumentNullException.ThrowIfNull(protector);

        // Tant que le dossier protégé n'est pas prêt, même une erreur imprévue de la protection n'écrit rien dans le profil.
        s_memoryOnly = true;
        try
        {
            protector.EnsureProtected(folder);
            s_folder = folder;
            s_memoryOnly = false;
            return true;
        }
        catch (Fixes.JournalUnsafeException)
        {
            s_folder = null;
            return false;
        }
    }

    /// <summary>Écrit l'erreur dans le journal.</summary>
    /// <param name="exception">L'erreur imprévue.</param>
    /// <param name="what">Ce qui était en cours (texte technique, non traduit).</param>
    /// <returns>Le chemin du fichier, ou <c>null</c> s'il n'a pas pu être écrit (ou si rien ne s'écrit sur le disque).</returns>
    public static string? Write(Exception exception, string what)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Folder is { } folder ? Write(folder, exception, what, DateTime.Now) : null;
    }

    internal static string? Write(string folder, Exception exception, string what, DateTime now)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "erreur-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
            var text = new StringBuilder()
                .Append("MAUS ").Append(AppVersion.Full).Append(" · ").AppendLine(now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
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
