namespace Maus.Core.Modules.M07GameBar;

/// <summary>Clés de registre, paquets et catégories du Module 7, partagés avec les tests.</summary>
internal static class GameBarKeys
{
    // Catégories affichées dans l'interface.
    public const string Recording = "Enregistrement";
    public const string Overlay = "Superposition Game Bar";
    public const string GameMode = "Mode Jeu";
    public const string Profile = "Profil de jeu";

    // HKCU : réglages de l'utilisateur courant.
    public const string GameDvrUser = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    public const string GameConfigStore = @"System\GameConfigStore";
    public const string GameConfigStoreChildren = @"System\GameConfigStore\Children";
    public const string GameBarUser = @"Software\Microsoft\GameBar";

    // HKLM : verrou optionnel (Pro et plus).
    public const string GameDvrPolicy = @"SOFTWARE\Policies\Microsoft\Windows\GameDVR";

    // Paquets Microsoft Store.
    public const string GameBarPackage = "Microsoft.XboxGamingOverlay";
    public const string XboxAppPackage = "Microsoft.GamingApp";
    public const string GamingServicesPackage = "Microsoft.GamingServices";
    public const string IdentityProviderPackage = "Microsoft.XboxIdentityProvider";

    /// <summary>Identifiant Microsoft Store de « Game Bar », publiée par Microsoft.</summary>
    public const string GameBarStoreId = "9NZKPSTSNW4P";
}
