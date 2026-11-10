using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Maus.Core.Engine;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Platform;

public enum InstallationVerdict
{
    Ok,
    Warning,
    Info,
}

public sealed record InstallationLine(string Title, InstallationVerdict Verdict, string Detail);

/// <summary>Qui peut écrire dans un dossier, tel que ce compte peut le savoir.</summary>
public enum FolderAccess
{
    Absent,

    /// <summary>Ce compte ne peut même pas le lire.</summary>
    Denied,
    AdminsOnly,
    OpenToOthers,
    Unknown,
}

public interface IInstallationProbe
{
    /// <summary>Empreinte SHA-256 du fichier, en hexadécimal ; <c>null</c> s'il est absent ou illisible.</summary>
    string? HashOf(string file);

    FolderAccess AccessOf(string folder);
}

/// <summary>
/// « Vérifier mon installation » (0.8.6) : lecture seule, sans droits administrateur, sans réseau. Un fichier ne peut pas
/// contenir sa propre empreinte : MAUS.dll garde celles de ses bibliothèques (écrites à la compilation), et sa propre empreinte
/// est donnée pour être comparée à celle publiée avec la version. Ce contrôle rassure, il ne prouve pas : voir <see cref="Limit"/>.
/// </summary>
public static class InstallationCheck
{
    public const string ProgramFile = "MAUS.dll";

    public static string Limit => T("Ce contrôle n'est pas une preuve absolue : il repère un fichier abîmé ou remplacé par erreur, mais un programme qui a déjà les droits administrateur pourrait modifier MAUS et ce contrôle lui-même.");

    /// <param name="executable">Chemin complet de MAUS.exe.</param>
    /// <param name="protectedRoots">Dossiers réservés aux administrateurs.</param>
    /// <param name="expectedLibraries">Nom de chaque bibliothèque et empreinte gardée à la compilation.</param>
    /// <param name="journalFolder">Dossier du journal des corrections.</param>
    /// <param name="probe">Lectures sur le disque.</param>
    public static IReadOnlyList<InstallationLine> Run(string executable, IReadOnlyList<string> protectedRoots, IReadOnlyDictionary<string, string> expectedLibraries, string journalFolder, IInstallationProbe probe)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(expectedLibraries);
        ArgumentNullException.ThrowIfNull(probe);

        var folder = Path.GetDirectoryName(executable) ?? executable;
        return
        [
            CheckFolder(executable, folder, protectedRoots),
            CheckLibraries(folder, expectedLibraries, probe),
            CheckProgram(folder, probe),
            CheckJournal(journalFolder, probe),
        ];
    }

    private static InstallationLine CheckFolder(string executable, string folder, IReadOnlyList<string> protectedRoots)
    {
        var title = T("Dossier de MAUS");
        return ScheduledAudit.IsInProtectedFolder(executable, protectedRoots)
            ? new(title, InstallationVerdict.Ok, T("{0} : seul un administrateur peut y modifier des fichiers.", folder))
            : new(title, InstallationVerdict.Warning, T("{0} n'est pas un dossier protégé : un autre programme ouvert par ce compte pourrait y remplacer MAUS. Installez MAUS avec son installateur ou par le Microsoft Store.", folder));
    }

    private static InstallationLine CheckLibraries(string folder, IReadOnlyDictionary<string, string> expected, IInstallationProbe probe)
    {
        var title = T("Bibliothèques de MAUS");
        if (expected.Count == 0)
        {
            return new(title, InstallationVerdict.Info, T("Cette version ne contient pas d'empreintes à comparer."));
        }

        var different = expected
            .Where(library => !string.Equals(probe.HashOf(Path.Combine(folder, library.Key + ".dll")), library.Value, StringComparison.OrdinalIgnoreCase))
            .Select(library => library.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
        return different.Count == 0
            ? new(title, InstallationVerdict.Ok, T("{0} : identiques à ce qui a été compilé avec cette version.", string.Join(", ", expected.Keys.Order(StringComparer.Ordinal))))
            : new(title, InstallationVerdict.Warning, T("{0} : absent, abîmé ou modifié depuis la compilation. Réinstallez MAUS.", string.Join(", ", different)));
    }

    private static InstallationLine CheckProgram(string folder, IInstallationProbe probe)
    {
        var title = T("Empreinte du programme");
        return probe.HashOf(Path.Combine(folder, ProgramFile)) is { } hash
            ? new(title, InstallationVerdict.Info, T("{0} : {1}. Comparez-la à celle publiée avec cette version, sur la page des versions de MAUS.", ProgramFile, hash))
            : new(title, InstallationVerdict.Warning, T("{0} est absent ou illisible. Réinstallez MAUS.", ProgramFile));
    }

    private static InstallationLine CheckJournal(string folder, IInstallationProbe probe)
    {
        var title = T("Journal des corrections");
        return probe.AccessOf(folder) switch
        {
            FolderAccess.Absent => new(title, InstallationVerdict.Info, T("Pas encore créé : MAUS n'a encore rien modifié sur ce PC.")),
            FolderAccess.Denied or FolderAccess.AdminsOnly => new(title, InstallationVerdict.Ok, T("Réservé aux administrateurs : un programme sans droits ne peut pas y faire croire à une correction.")),
            FolderAccess.OpenToOthers => new(title, InstallationVerdict.Warning, T("D'autres comptes peuvent écrire dans {0}. MAUS n'utilise pas un journal qu'il ne juge pas fiable : supprimez ce dossier, il sera recréé.", folder)),
            _ => new(title, InstallationVerdict.Info, T("Droits du dossier illisibles pour le moment.")),
        };
    }
}

/// <summary>Lectures réelles : aucun fichier n'est modifié.</summary>
public sealed class WindowsInstallationProbe : IInstallationProbe
{
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    private const FileSystemRights Writing = FileSystemRights.Write | FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.AppendData
        | FileSystemRights.WriteData | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public string? HashOf(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public FolderAccess AccessOf(string folder)
    {
        try
        {
            // Pas Directory.Exists : il répond « absent » aussi pour un dossier auquel ce compte n'a pas accès.
            _ = File.GetAttributes(folder);
            var rules = new DirectoryInfo(folder).GetAccessControl().GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & Writing) != 0 && !IsTrusted(rule.IdentityReference))
                {
                    return FolderAccess.OpenToOthers;
                }
            }

            return FolderAccess.AdminsOnly;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return FolderAccess.Absent;
        }
        catch (UnauthorizedAccessException)
        {
            return FolderAccess.Denied;
        }
        catch (Exception ex) when (ex is IOException or PrivilegeNotHeldException)
        {
            return FolderAccess.Unknown;
        }
    }

    private static bool IsTrusted(IdentityReference identity) =>
        identity.Equals(System) || identity.Equals(Administrators) || string.Equals(identity.Value, TrustedInstaller, StringComparison.Ordinal);
}
