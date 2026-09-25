using System.Runtime.InteropServices;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Modules.M05Power;

/// <summary>Utilitaire constructeur qui pilote aussi l'alimentation, et ses traces possibles sur le PC.</summary>
internal sealed record OemPowerUtility(string Name, string[] PackagePrefixes, string[] Services, string[] DisplayNames);

/// <summary>
/// Repère les utilitaires constructeur par trois voies : application du Store (préfixe du nom de paquet),
/// service Windows (clé sous <c>Services</c>) ou programme installé (nom affiché dans la liste de désinstallation).
/// </summary>
internal static class OemPowerUtilities
{
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    /// <summary>Programmes 64 bits puis 32 bits (WOW6432Node).</summary>
    private static readonly RegistryView[] Views = [RegistryView.Registry64, RegistryView.Registry32];

    public static readonly IReadOnlyList<OemPowerUtility> Known =
    [
        new("Armoury Crate (ASUS)", ["B9ECED6F.ArmouryCrate"], ["ArmouryCrateService"], ["Armoury Crate"]),
        new("Lenovo Vantage", ["E046963F.LenovoCompanion", "E046963F.LenovoSettingsforEnterprise", "LenovoCorporation.LenovoVantage"], ["LenovoVantageService"], ["Lenovo Vantage"]),
        new("Legion Space (Lenovo)", ["E046963F.LegionSpace"], [], ["Legion Space"]),
        new("MSI Center", ["9426MICRO-STARINTERNATION.MSICenter"], [], ["MSI Center"]),
        new("Alienware Command Center (Dell)", ["DellInc.AlienwareCommandCenter"], ["AWCCService"], ["Alienware Command Center"]),
        new("OMEN Gaming Hub (HP)", ["AD2F1837.OMENCommandCenter"], [], ["OMEN Gaming Hub", "OMEN Command Center"]),
    ];

    /// <summary>Noms des utilitaires trouvés. Une source illisible est ignorée : les autres suffisent souvent.</summary>
    public static IReadOnlyList<string> Detect(AuditContext context)
    {
        var packages = ReadPackageNames(context.Packages);
        var displayNames = ReadInstalledProgramNames(context.Registry);
        var found = new List<string>();
        foreach (var utility in Known)
        {
            var hasPackage = utility.PackagePrefixes.Any(prefix => packages.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
            var hasProgram = utility.DisplayNames.Any(name => displayNames.Any(d => d.Contains(name, StringComparison.OrdinalIgnoreCase)));
            if (hasPackage || hasProgram || utility.Services.Any(service => ServiceExists(context.Registry, service)))
            {
                found.Add(utility.Name);
            }
        }

        return found;
    }

    private static List<string> ReadPackageNames(IPackageInventory inventory)
    {
        try
        {
            return inventory.GetUserPackages().Select(p => p.Name).ToList();
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or DataSourceUnavailableException or COMException)
        {
            return [];
        }
    }

    private static List<string> ReadInstalledProgramNames(IRegistryReader registry)
    {
        var names = new List<string>();
        foreach (var view in Views)
        {
            foreach (var subKey in TryRead(() => registry.GetSubKeyNames(RegistryHive.LocalMachine, UninstallKey, view)) ?? [])
            {
                // Une entrée illisible est ignorée : les autres restent utiles.
                if (TryRead(() => registry.GetString(RegistryHive.LocalMachine, $@"{UninstallKey}\{subKey}", "DisplayName", view)) is { Length: > 0 } name)
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    private static T? TryRead<T>(Func<T?> read)
        where T : class
    {
        try
        {
            return read();
        }
        catch (MausAccessDeniedException)
        {
            return null;
        }
    }

    private static bool ServiceExists(IRegistryReader registry, string service)
    {
        try
        {
            return registry.KeyExists(RegistryHive.LocalMachine, $@"{ServicesKey}\{service}");
        }
        catch (MausAccessDeniedException)
        {
            return false;
        }
    }
}
