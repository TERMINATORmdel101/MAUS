using System.Diagnostics;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Platform;

/// <summary>Accès aux fichiers en lecture seule (fichier hosts, minidumps, dossiers Démarrage…).</summary>
public interface IFileSystemReader
{
    bool FileExists(string path);

    /// <summary>
    /// Comme <see cref="FileExists"/>, en distinguant l'absence d'une lecture refusée : <c>null</c> quand on ne peut pas savoir
    /// (droits, lien que Windows refuse de suivre pour ce processus).
    /// </summary>
    bool? IsFilePresent(string path) => FileExists(path);

    bool DirectoryExists(string path);

    /// <exception cref="MausAccessDeniedException">Lecture refusée.</exception>
    string ReadAllText(string path);

    /// <summary>Fichiers du dossier correspondant au motif, ou liste vide si le dossier est absent.</summary>
    IReadOnlyList<string> EnumerateFiles(string directory, string searchPattern = "*");

    /// <summary>Éditeur (CompanyName) et nom de produit d'un exécutable, s'ils sont renseignés.</summary>
    (string? Company, string? Product, string? Version) GetVersionInfo(string path);

    /// <summary>Espace libre et total d'un lecteur, par exemple « C:\ ».</summary>
    (long FreeBytes, long TotalBytes)? GetDriveSpace(string root);
}

public sealed class LocalFileSystemReader : IFileSystemReader
{
    public bool FileExists(string path) => File.Exists(path);

    public bool? IsFilePresent(string path)
    {
        if (File.Exists(path))
        {
            return true;
        }

        // File.Exists répond « non » à toute erreur : seuls « fichier » ou « chemin introuvable » prouvent l'absence.
        try
        {
            File.GetAttributes(path);
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string ReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new MausAccessDeniedException(T("Lecture refusée : {0}", path), ex);
        }
    }

    public IReadOnlyList<string> EnumerateFiles(string directory, string searchPattern = "*")
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetFiles(directory, searchPattern) : [];
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new MausAccessDeniedException(T("Lecture refusée : {0}", directory), ex);
        }
    }

    public (string? Company, string? Product, string? Version) GetVersionInfo(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return (info.CompanyName, info.ProductName, info.ProductVersion);
        }
        catch (FileNotFoundException)
        {
            return (null, null, null);
        }
    }

    public (long FreeBytes, long TotalBytes)? GetDriveSpace(string root)
    {
        var drive = new DriveInfo(root);
        return drive.IsReady ? (drive.AvailableFreeSpace, drive.TotalSize) : null;
    }
}
