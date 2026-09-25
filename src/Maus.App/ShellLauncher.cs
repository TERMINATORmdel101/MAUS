using System.Diagnostics;
using System.IO;

namespace Maus.App;

/// <summary>
/// Ouvre une page web ou un dossier sans les droits administrateur de MAUS : la demande passe par explorer.exe,
/// qui la confie à l'Explorateur déjà ouvert dans la session (non élevé). À vérifier sur chaque version de Windows.
/// </summary>
public static class ShellLauncher
{
    public static void OpenUrl(Uri url)
    {
        if (url.Scheme is not ("https" or "http"))
        {
            return;
        }

        Start($"\"{url.AbsoluteUri}\"");
    }

    /// <summary>Ouvre une page des Paramètres de Windows (adresse « ms-settings: »).</summary>
    public static void OpenSettings(string page)
    {
        if (page.StartsWith("ms-settings:", StringComparison.Ordinal))
        {
            Start(page);
        }
    }

    public static void OpenFolder(string path)
    {
        if (Directory.Exists(path))
        {
            Start($"\"{path}\"");
        }
    }

    public static void ShowInFolder(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            Start($"/select,\"{path}\"");
        }
    }

    private static void Start(string arguments)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(Path.Combine(windows, "explorer.exe"), arguments) { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Rien à ouvrir : l'Explorateur est absent ou refuse la demande. MAUS ne relance rien avec ses propres droits.
        }
    }
}
