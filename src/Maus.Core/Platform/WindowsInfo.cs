using Microsoft.Win32;

namespace Maus.Core.Platform;

/// <summary>Version et édition de Windows, lues dans le registre.</summary>
public sealed record WindowsInfo(string ProductName, string EditionId, string DisplayVersion, int Build, int Ubr)
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public bool IsWindows11 => Build >= 22000;

    /// <summary>Famille (Core) ou Pro et plus : conditionne l'effet des stratégies.</summary>
    public bool IsHomeEdition => EditionId.StartsWith("Core", StringComparison.OrdinalIgnoreCase);

    public bool IsEnterpriseOrEducation =>
        EditionId.Contains("Enterprise", StringComparison.OrdinalIgnoreCase) ||
        EditionId.Contains("Education", StringComparison.OrdinalIgnoreCase);

    public string FullBuild => $"{Build}.{Ubr}";

    public static WindowsInfo Read(IRegistryReader registry)
    {
        var build = int.TryParse(registry.GetString(RegistryHive.LocalMachine, CurrentVersionKey, "CurrentBuild"), out var b) ? b : 0;
        var productName = registry.GetString(RegistryHive.LocalMachine, CurrentVersionKey, "ProductName") ?? "Windows";

        // Windows 11 garde « Windows 10 » dans ProductName : on corrige d'après le numéro de build.
        if (build >= 22000)
        {
            productName = productName.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
        }

        return new WindowsInfo(
            productName,
            registry.GetString(RegistryHive.LocalMachine, CurrentVersionKey, "EditionID") ?? string.Empty,
            registry.GetString(RegistryHive.LocalMachine, CurrentVersionKey, "DisplayVersion") ?? string.Empty,
            build,
            registry.GetDword(RegistryHive.LocalMachine, CurrentVersionKey, "UBR") ?? 0);
    }
}
