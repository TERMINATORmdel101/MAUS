using System.Security.AccessControl;
using System.Security.Principal;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Fixes;

/// <summary>
/// Réserve le dossier du journal à SYSTEM et aux Administrateurs (ACL protégée, sans héritage).
/// Un utilisateur standard peut créer des dossiers dans <c>%ProgramData%</c> : un dossier pré-créé par lui,
/// ou un lien symbolique, est détecté et corrigé, ou refusé. Un dossier pré-créé avec des droits hostiles (qui refusent
/// même la lecture ou la modification des droits) est repris avec les privilèges d'un administrateur (0.8.6).
/// Seul l'espace de <see cref="SystemPaths.ProgramData"/> est accepté, et les dossiers au-dessus de <c>MAUS</c> sont contrôlés :
/// ni lien, ni propriétaire d'un autre compte, ni droit qui permettrait à un autre compte de les déplacer ou d'en changer les droits.
/// </summary>
public sealed class WindowsDirectoryProtector : IDirectoryProtector
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);
    private static readonly SecurityIdentifier OwnerRights = new("S-1-3-4");

    private const FileSystemRights GenericWrite = (FileSystemRights)0x40000000;
    private const FileSystemRights GenericAll = (FileSystemRights)0x10000000;

    /// <summary>Droits qui donnent la main sur le contenu d'un fichier, ou sur le fichier lui-même.</summary>
    private const FileSystemRights WritingRights = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.WriteAttributes | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | GenericWrite | GenericAll;

    /// <summary>
    /// Droits qui, sur un dossier parent, permettraient de déplacer ce qu'il contient ou d'en changer les droits. Créer un dossier
    /// n'en fait pas partie : Windows l'accorde à tous à la racine du lecteur et dans <c>%ProgramData%</c>.
    /// </summary>
    private const FileSystemRights ControllingRights = FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | GenericAll;

    public void EnsureProtected(string directory)
    {
        var root = SystemPaths.ProgramData;
        if (!SystemPaths.IsUnder(directory, root))
        {
            throw new JournalUnsafeException(T("Le dossier {0} n'est pas sous {1}, l'emplacement donné par Windows : MAUS refuse de s'en servir.", directory, root));
        }

        // Le dossier parent (…\MAUS) est protégé aussi : sinon il pourrait être remplacé par un lien.
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory));
        var isInMausFolder = parent is not null && Path.GetFileName(parent).Equals("MAUS", StringComparison.OrdinalIgnoreCase);
        RequireSafeAncestors(isInMausFolder ? parent! : directory);
        if (isInMausFolder)
        {
            Protect(parent!);
        }

        Protect(directory);
    }

    /// <summary>
    /// Sans droits administrateur, ce dossier ne se lit pas et ne se protège pas : il n'y a rien à lire, comme s'il était absent.
    /// </summary>
    public bool EnsureReadable(string directory)
    {
        if (!Environment.IsPrivilegedProcess || !Directory.Exists(directory))
        {
            return false;
        }

        EnsureProtected(directory);
        return true;
    }

    /// <summary>Contrôle les dossiers au-dessus de <paramref name="directory"/>, jusqu'à la racine du lecteur.</summary>
    internal static void RequireSafeAncestors(string directory)
    {
        for (var ancestor = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory)); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
        {
            try
            {
                var info = new DirectoryInfo(ancestor);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new JournalUnsafeException(T("Le dossier {0} est un lien : MAUS refuse de s'en servir. Supprimez-le puis relancez MAUS.", ancestor));
                }

                if (!IsSafeAncestor(info.GetAccessControl()))
                {
                    throw new JournalUnsafeException(T("D'autres comptes peuvent déplacer le dossier {0} ou en changer les droits : MAUS refuse d'y garder ses fichiers. Rendez à ce dossier les droits d'origine de Windows, puis relancez MAUS.", ancestor));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException or InvalidOperationException)
            {
                throw new JournalUnsafeException(T("Impossible de protéger le dossier du journal ({0}) : {1}", ancestor, ex.Message), ex);
            }
        }
    }

    /// <summary>Propriétaire de confiance, et aucun autre compte qui puisse déplacer ce dossier, son contenu ou ses droits.</summary>
    internal static bool IsSafeAncestor(DirectorySecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        return owner is not null && IsOperatingSystem(owner)
            && security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .All(rule => rule.AccessControlType == AccessControlType.Deny
                    || rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)
                    || IsOperatingSystem(rule.IdentityReference)
                    || rule.IdentityReference.Equals(CreatorOwner)
                    || rule.IdentityReference.Equals(OwnerRights)
                    || (rule.FileSystemRights & ControllingRights) == 0);
    }

    private static bool IsOperatingSystem(IdentityReference identity) =>
        identity.Equals(System) || identity.Equals(Administrators) || identity.Equals(TrustedInstaller);

    /// <exception cref="IOException">
    /// Propriétaire momentanément illisible : ce n'est pas la preuve d'un fichier douteux, l'appelant retente puis laisse le
    /// fichier intact (<see cref="Platform.AtomicFile.ReadText"/>), au lieu de le prendre pour un fichier à remplacer.
    /// </exception>
    public bool IsTrusted(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            return IsTrustedFile(info.GetAccessControl(), AtomicFile.LinkCount(file));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException)
        {
            // Propriétaire illisible même pour un administrateur : fichier douteux, ignoré.
            return false;
        }
    }

    /// <summary>Propriétaire de confiance, aucun droit d'écriture hors SYSTEM et Administrateurs, et un seul nom sur le disque.</summary>
    internal static bool IsTrustedFile(FileSecurity security, uint links)
    {
        ArgumentNullException.ThrowIfNull(security);
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        return links == 1 && owner is not null && (owner.Equals(System) || owner.Equals(Administrators))
            && security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .All(rule => rule.AccessControlType == AccessControlType.Deny
                    || rule.IdentityReference.Equals(System)
                    || rule.IdentityReference.Equals(Administrators)
                    || (rule.FileSystemRights & WritingRights) == 0);
    }

    /// <summary>
    /// Dossier rempli par le moteur dans l'espace protégé (sauvegarde de pilote, 0.8.6) : lui, ses sous-dossiers et chacun de
    /// leurs fichiers ont pour propriétaire SYSTEM ou les Administrateurs, ne donnent le droit d'écrire à aucun autre compte et ne
    /// sont pas des liens (un fichier n'a qu'un seul nom). Un élément illisible fait refuser l'ensemble.
    /// </summary>
    public bool IsTrustedTree(string directory) => IsTrustedTree(directory, IsTrustedFolder, IsTrusted);

    /// <summary>Parcourt le dossier sans jamais suivre un lien ; tout élément refusé, ou illisible, fait refuser l'ensemble.</summary>
    internal static bool IsTrustedTree(string directory, Func<string, bool> trustedFolder, Func<string, bool> trustedFile)
    {
        ArgumentNullException.ThrowIfNull(trustedFolder);
        ArgumentNullException.ThrowIfNull(trustedFile);

        // Fichiers cachés ou système compris (pnputil les verrait), et un sous-dossier illisible n'est pas sauté en silence.
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        var pending = new Stack<string>();
        pending.Push(directory);
        try
        {
            while (pending.TryPop(out var folder))
            {
                if (!trustedFolder(folder))
                {
                    return false;
                }

                foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        return false;
                    }

                    if (entry.Attributes.HasFlag(FileAttributes.Directory))
                    {
                        pending.Push(entry.FullName);
                    }
                    else if (!trustedFile(entry.FullName))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PrivilegeNotHeldException)
        {
            return false;
        }
    }

    /// <summary>Le dossier lui-même : ni lien, ni propriétaire d'un autre compte, ni droit d'écriture pour un autre compte.</summary>
    private static bool IsTrustedFolder(string directory)
    {
        var info = new DirectoryInfo(directory);
        return info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint) && IsTrustedFolder(info.GetAccessControl());
    }

    /// <summary>
    /// Propriétaire SYSTEM ou Administrateurs, et aucune autorisation d'un autre compte qui permette d'y créer, d'y modifier ou
    /// d'y supprimer quelque chose, ou d'en changer les droits (même une autorisation qui ne vaut que pour ce qui serait créé dedans).
    /// </summary>
    internal static bool IsTrustedFolder(DirectorySecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        return owner is not null && (owner.Equals(System) || owner.Equals(Administrators))
            && security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .All(rule => rule.AccessControlType == AccessControlType.Deny
                    || rule.IdentityReference.Equals(System)
                    || rule.IdentityReference.Equals(Administrators)
                    || (rule.FileSystemRights & WritingRights) == 0);
    }

    /// <summary>
    /// Après une écriture du moteur dans ce dossier (sauvegarde de pilote) : comme <see cref="ProtectFile"/> pour le dossier, ses
    /// sous-dossiers et leurs fichiers, que Windows peut avoir donnés au compte qui les a créés. Les liens ne sont pas suivis.
    /// </summary>
    /// <exception cref="JournalUnsafeException">Un élément ne peut pas être donné aux Administrateurs.</exception>
    public void ProtectTree(string directory)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        var pending = new Stack<string>();
        pending.Push(directory);
        try
        {
            while (pending.TryPop(out var folder))
            {
                var info = new DirectoryInfo(folder);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                GiveToAdministrators(info);
                foreach (var entry in info.EnumerateFileSystemInfos("*", options))
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    if (entry is DirectoryInfo child)
                    {
                        pending.Push(child.FullName);
                    }
                    else
                    {
                        ProtectFile(entry.FullName);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException or InvalidOperationException)
        {
            throw new JournalUnsafeException(T("Impossible de protéger le dossier du journal ({0}) : {1}", directory, ex.Message), ex);
        }
    }

    private static void GiveToAdministrators(DirectoryInfo info)
    {
        var security = info.GetAccessControl();
        if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && (owner.Equals(System) || owner.Equals(Administrators)))
        {
            return;
        }

        security.SetOwner(Administrators);
        info.SetAccessControl(security);
    }

    public void ProtectFile(string file)
    {
        try
        {
            var info = new FileInfo(file);
            var security = info.GetAccessControl();
            if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && (owner.Equals(System) || owner.Equals(Administrators)))
            {
                return;
            }

            security.SetOwner(Administrators);
            info.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException or InvalidOperationException)
        {
            throw new JournalUnsafeException(T("Impossible de protéger le fichier du journal ({0}) : {1}", file, ex.Message), ex);
        }
    }

    private static void Protect(string directory)
    {
        var info = new DirectoryInfo(directory);
        try
        {
            if (!info.Exists)
            {
                CreateReserved(info);
                return;
            }

            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new JournalUnsafeException(T("Le dossier {0} est un lien : MAUS refuse de s'en servir. Supprimez-le puis relancez MAUS.", directory));
            }

            if (!IsProtected(info))
            {
                if (!HasTrustedOwner(info))
                {
                    // Créé par un autre compte : ses poignées déjà ouvertes garderaient leurs droits après une reprise sur place.
                    // Mis de côté et jamais relu ; un dossier neuf le remplace. Si une poignée empêche de le déplacer, MAUS refuse.
                    Directory.Move(directory, directory + ".douteux-" + Guid.NewGuid().ToString("N"));
                    Diagnostics.Breadcrumbs.Add("dossier protégé : dossier d'un autre compte mis de côté");
                    CreateReserved(new DirectoryInfo(directory));
                    return;
                }

                TakeOver(info);
                if (!IsProtected(info))
                {
                    throw NotReserved(directory);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException or InvalidOperationException)
        {
            throw new JournalUnsafeException(T("Impossible de protéger le dossier du journal ({0}) : {1}", directory, ex.Message), ex);
        }
    }

    private static JournalUnsafeException NotReserved(string directory) =>
        new(T("Le dossier {0} n'a pas pu être réservé aux administrateurs : MAUS refuse de s'en servir. Supprimez-le puis relancez MAUS.", directory));

    /// <summary>Crée le dossier réservé, puis relit ses droits : un autre programme a pu le créer juste avant.</summary>
    private static void CreateReserved(DirectoryInfo info)
    {
        info.Create(BuildSecurity());
        info.Refresh();
        if (!IsProtected(info))
        {
            throw NotReserved(info.FullName);
        }
    }

    private static bool HasTrustedOwner(DirectoryInfo info)
    {
        try
        {
            var owner = info.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            return owner is not null && (owner.Equals(System) || owner.Equals(Administrators));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException)
        {
            return false;
        }
    }

    /// <summary>Droits lisibles et déjà corrects ; un dossier dont les droits ne se lisent même pas n'est pas protégé.</summary>
    private static bool IsProtected(DirectoryInfo info)
    {
        try
        {
            return IsProtected(info.GetAccessControl());
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reprend un dossier créé par un autre compte : d'abord sans privilège (dossier simplement mal réglé), puis, si les
    /// droits refusent la modification, avec SeTakeOwnershipPrivilege et SeRestorePrivilege activés le temps de l'opération :
    /// le propriétaire d'abord (un administrateur peut toujours se l'attribuer avec ce privilège), puis les droits, que le
    /// propriétaire peut toujours changer. Sans droits administrateur, rien n'est activé et l'exception d'origine ressort.
    /// </summary>
    private static void TakeOver(DirectoryInfo info)
    {
        try
        {
            info.SetAccessControl(BuildSecurity());
            return;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException)
        {
            // Droits hostiles : seconde tentative, avec les privilèges.
        }

        using var privileges = TokenPrivileges.Enable(TokenPrivileges.TakeOwnership, TokenPrivileges.Restore);
        var owner = new DirectorySecurity();
        owner.SetOwner(Administrators);
        info.SetAccessControl(owner);
        info.SetAccessControl(BuildSecurity());
    }

    private static DirectorySecurity BuildSecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(Administrators);
        foreach (var sid in new[] { System, Administrators })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security;
    }

    /// <summary>Propriétaire de confiance, héritage coupé et aucune autorisation pour un autre compte.</summary>
    private static bool IsProtected(DirectorySecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if (owner is null || !(owner.Equals(System) || owner.Equals(Administrators)) || !security.AreAccessRulesProtected)
        {
            return false;
        }

        return security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .All(rule => rule.AccessControlType == AccessControlType.Deny
                || rule.IdentityReference.Equals(System)
                || rule.IdentityReference.Equals(Administrators));
    }
}
