using System.Security.AccessControl;
using System.Security.Principal;

namespace Maus.Core.Fixes;

/// <summary>
/// Réserve le dossier du journal à SYSTEM et aux Administrateurs (ACL protégée, sans héritage).
/// Un utilisateur standard peut créer des dossiers dans <c>%ProgramData%</c> : un dossier pré-créé par lui,
/// ou un lien symbolique, est détecté et corrigé, ou refusé.
/// </summary>
public sealed class WindowsDirectoryProtector : IDirectoryProtector
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public void EnsureProtected(string directory)
    {
        // Le dossier parent (…\MAUS) est protégé aussi : sinon il pourrait être remplacé par un lien.
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory));
        if (parent is not null && Path.GetFileName(parent).Equals("MAUS", StringComparison.OrdinalIgnoreCase))
        {
            Protect(parent);
        }

        Protect(directory);
    }

    public bool IsTrusted(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            var owner = info.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            return owner is not null && (owner.Equals(System) || owner.Equals(Administrators));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException)
        {
            return false;
        }
    }

    private static void Protect(string directory)
    {
        var info = new DirectoryInfo(directory);
        try
        {
            if (!info.Exists)
            {
                info.Create(BuildSecurity());
                return;
            }

            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new JournalUnsafeException($"Le dossier {directory} est un lien : MAUS refuse de s'en servir. Supprimez-le puis relancez MAUS.");
            }

            if (!IsProtected(info.GetAccessControl()))
            {
                info.SetAccessControl(BuildSecurity());
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException or InvalidOperationException)
        {
            throw new JournalUnsafeException($"Impossible de protéger le dossier du journal ({directory}) : {ex.Message}", ex);
        }
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
