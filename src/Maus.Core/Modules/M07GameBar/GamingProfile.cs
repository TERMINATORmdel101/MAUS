using System.Runtime.InteropServices;
using Maus.Core.Platform;

namespace Maus.Core.Modules.M07GameBar;

/// <summary>Profils proposés à l'utilisateur ; celui qui est détecté est présélectionné, l'utilisateur garde le dernier mot.</summary>
internal enum GamingProfile
{
    /// <summary>Profil 1 : ni app Xbox ni Game Pass. Captures, enregistrement en arrière-plan et bouton de manette coupés.</summary>
    NoXbox = 1,

    /// <summary>Profil 2 : app Xbox ou Game Pass. Seul l'enregistrement en arrière-plan est coupé.</summary>
    XboxApp = 2,

    /// <summary>Profil 3 : Ryzen X3D à deux CCD. Game Bar et Mode Jeu conservés pour placer les jeux sur le V-Cache.</summary>
    X3D = 3,
}

/// <summary>Paquets de jeu installés pour l'utilisateur courant.</summary>
internal sealed record GamingPackages(
    InstalledPackage? GameBar,
    InstalledPackage? XboxApp,
    InstalledPackage? GamingServices,
    InstalledPackage? IdentityProvider)
{
    /// <summary>L'app Xbox ou les Services de jeu indiquent un usage du Game Pass.</summary>
    public bool UsesXboxApp => XboxApp is not null || GamingServices is not null;

    /// <summary>Lit l'inventaire ; <c>null</c> s'il est illisible (droits, API indisponible).</summary>
    public static GamingPackages? TryRead(IPackageInventory inventory)
    {
        IReadOnlyList<InstalledPackage> packages;
        try
        {
            packages = inventory.GetUserPackages();
        }
        catch (MausAccessDeniedException)
        {
            return null;
        }
        catch (DataSourceUnavailableException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }

        return new GamingPackages(
            Find(packages, GameBarKeys.GameBarPackage),
            Find(packages, GameBarKeys.XboxAppPackage),
            Find(packages, GameBarKeys.GamingServicesPackage),
            Find(packages, GameBarKeys.IdentityProviderPackage));
    }

    /// <summary>Profil présélectionné : X3D d'abord, puis app Xbox ; <c>null</c> si l'inventaire est illisible.</summary>
    public static GamingProfile? Propose(bool asymmetricX3D, GamingPackages? packages) =>
        asymmetricX3D ? GamingProfile.X3D
        : packages is null ? null
        : packages.UsesXboxApp ? GamingProfile.XboxApp
        : GamingProfile.NoXbox;

    /// <summary>
    /// Profil retenu : celui choisi par l'utilisateur s'il en a choisi un (1, 2 ou 3), sinon le profil présélectionné.
    /// L'utilisateur garde le dernier mot, y compris sur un Ryzen X3D (avec avertissement).
    /// </summary>
    public static (GamingProfile? Profile, bool ChosenByUser) Choose(bool asymmetricX3D, GamingPackages? packages, int? choice) =>
        choice is >= 1 and <= 3
            ? ((GamingProfile)choice.Value, true)
            : (Propose(asymmetricX3D, packages), false);

    private static InstalledPackage? Find(IReadOnlyList<InstalledPackage> packages, string name) =>
        packages.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
