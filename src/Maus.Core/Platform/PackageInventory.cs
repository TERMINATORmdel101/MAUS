using static Maus.Core.Localization.Texts;

namespace Maus.Core.Platform;

/// <summary>Application empaquetée (Microsoft Store, MSIX) installée pour l'utilisateur courant.</summary>
public sealed record InstalledPackage(string Name, string FamilyName, string Version, string Publisher, string? InstallPath = null);

/// <summary>Inventaire des applications empaquetées (Game Bar, app Xbox, Copilot, utilitaires constructeur…).</summary>
public interface IPackageInventory
{
    IReadOnlyList<InstalledPackage> GetUserPackages();
}

public static class PackageInventoryExtensions
{
    /// <summary>Premier paquet dont le nom commence par <paramref name="namePrefix"/>, par exemple « Microsoft.XboxGamingOverlay ».</summary>
    public static InstalledPackage? Find(this IPackageInventory inventory, string namePrefix) =>
        inventory.GetUserPackages().FirstOrDefault(p => p.Name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Lecture par <c>PackageManager.FindPackagesForUser</c>, mise en cache pour la durée de l'audit.</summary>
public sealed class WinRtPackageInventory : IPackageInventory
{
    private readonly Lazy<List<InstalledPackage>> _packages = new(Load);

    public IReadOnlyList<InstalledPackage> GetUserPackages() => _packages.Value;

    private static List<InstalledPackage> Load()
    {
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            return manager.FindPackagesForUser(string.Empty)
                .Select(p => new InstalledPackage(
                    p.Id.Name,
                    p.Id.FamilyName,
                    $"{p.Id.Version.Major}.{p.Id.Version.Minor}.{p.Id.Version.Build}.{p.Id.Version.Revision}",
                    p.Id.Publisher,
                    InstallPathOf(p)))
                .ToList();
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new MausAccessDeniedException(T("Inventaire des applications refusé."), ex);
        }
    }

    /// <summary>Dossier d'installation ; un paquet abîmé peut refuser de le donner.</summary>
    private static string? InstallPathOf(Windows.ApplicationModel.Package package)
    {
        try
        {
            return package.InstalledPath;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FileNotFoundException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
