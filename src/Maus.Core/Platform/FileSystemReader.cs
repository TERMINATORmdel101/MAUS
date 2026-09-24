using System.Diagnostics;

namespace Maus.Core.Platform;

/// <summary>Accès aux fichiers en lecture seule (fichier hosts, minidumps, dossiers Démarrage…).</summary>
public interface IFileSystemReader
{
    bool FileExists(string path);

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

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string ReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new MausAccessDeniedException($"Lecture refusée : {path}", ex);
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
            throw new MausAccessDeniedException($"Lecture refusée : {directory}", ex);
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
