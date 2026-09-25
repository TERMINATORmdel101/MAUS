namespace Maus.Core.Platform;

/// <summary>Requêtes CIM/WMI en lecture seule.</summary>
public interface ICimReader
{
    /// <summary>Exécute une requête WQL « SELECT » et renvoie les instances.</summary>
    /// <exception cref="MausAccessDeniedException">Accès refusé (souvent : droits administrateur requis).</exception>
    /// <exception cref="DataSourceUnavailableException">Espace de noms ou classe absent sur ce PC.</exception>
    IReadOnlyList<CimRow> Query(string wql, string scope = CimScopes.Default);

    /// <summary>
    /// Appelle une méthode de lecture sur la première instance renvoyée par <paramref name="wql"/>
    /// (par exemple <c>Win32_PnPEntity.GetDeviceProperties</c>) et renvoie ses paramètres de sortie.
    /// Réservé aux méthodes sans effet de bord.
    /// </summary>
    CimRow? InvokeMethod(string wql, string method, IReadOnlyDictionary<string, object?>? parameters = null, string scope = CimScopes.Default);
}

public static class CimScopes
{
    public const string Default = @"root\cimv2";
    public const string Wmi = @"root\wmi";
    public const string DeviceGuard = @"root\Microsoft\Windows\DeviceGuard";
    public const string Defender = @"root\Microsoft\Windows\Defender";
    public const string Storage = @"root\Microsoft\Windows\Storage";
    public const string StandardCimv2 = @"root\StandardCimv2";
    public const string SecurityCenter2 = @"root\SecurityCenter2";
    public const string BitLocker = @"root\cimv2\Security\MicrosoftVolumeEncryption";
    public const string TaskScheduler = @"root\Microsoft\Windows\TaskScheduler";
    public const string SystemRestore = @"root\default";
}
