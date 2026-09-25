namespace Maus.Core.Modules.M12Startup;

/// <summary>Dossiers Démarrage, variables d'environnement et raccourcis : accès isolés pour rester testables.</summary>
internal interface IStartupEnvironment
{
    /// <summary>Dossier Démarrage de l'utilisateur (shell:startup).</summary>
    string? UserStartupFolder { get; }

    /// <summary>Dossier Démarrage commun à tous les utilisateurs (shell:common startup).</summary>
    string? CommonStartupFolder { get; }

    /// <summary>Développe les variables d'environnement (%windir%, %ProgramFiles%…).</summary>
    string Expand(string text);

    /// <summary>Chemin cible d'un raccourci .lnk, ou <c>null</c> s'il est illisible ou sans chemin local.</summary>
    string? ResolveShortcut(string shortcutPath);
}

/// <summary>Implémentation réelle, en lecture seule.</summary>
internal sealed class WindowsStartupEnvironment : IStartupEnvironment
{
    /// <summary>Un raccourci dépasse rarement quelques kilo-octets : au-delà, le fichier n'est pas lu.</summary>
    private const long MaxShortcutBytes = 1024 * 1024;

    public string? UserStartupFolder => Folder(Environment.SpecialFolder.Startup);

    public string? CommonStartupFolder => Folder(Environment.SpecialFolder.CommonStartup);

    public string Expand(string text) => Environment.ExpandEnvironmentVariables(text);

    public string? ResolveShortcut(string shortcutPath)
    {
        try
        {
            var info = new FileInfo(shortcutPath);
            if (!info.Exists || info.Length > MaxShortcutBytes)
            {
                return null;
            }

            return StartupParsers.ParseShortcutTarget(File.ReadAllBytes(shortcutPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? Folder(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        return string.IsNullOrEmpty(path) ? null : path;
    }
}
