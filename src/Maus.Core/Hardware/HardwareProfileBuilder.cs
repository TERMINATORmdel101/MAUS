using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Hardware;

public static class HardwareProfileBuilder
{
    public static HardwareProfile Build(ICimReader cim, IRegistryReader registry)
    {
        var system = TryFirst(cim, "SELECT Manufacturer, Model, PCSystemTypeEx, PartOfDomain FROM Win32_ComputerSystem");
        var enclosure = TryFirst(cim, "SELECT ChassisTypes FROM Win32_SystemEnclosure");
        var board = TryFirst(cim, "SELECT Manufacturer, Product FROM Win32_BaseBoard");
        var hasBattery = TryQuery(cim, "SELECT DeviceID FROM Win32_Battery").Count > 0;

        return new HardwareProfile
        {
            FormFactor = FormFactorClassifier.Classify(
                enclosure?.GetInt64Array("ChassisTypes") ?? [],
                system?.GetInt64("PCSystemTypeEx"),
                hasBattery),
            HasBattery = hasBattery,
            Manufacturer = system?.GetString("Manufacturer")?.Trim() ?? string.Empty,
            Model = system?.GetString("Model")?.Trim() ?? string.Empty,
            BoardManufacturer = board?.GetString("Manufacturer")?.Trim() ?? string.Empty,
            BoardProduct = board?.GetString("Product")?.Trim() ?? string.Empty,
            Cpu = ReadCpu(cim),
            Gpus = ReadGpus(cim),
            IsManaged = system?.GetBool("PartOfDomain") == true || HasOrganizationEnrollment(registry),
        };
    }

    private static CpuInfo ReadCpu(ICimReader cim)
    {
        var cpu = TryFirst(cim, "SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
        if (cpu is null)
        {
            return CpuInfo.Unknown;
        }

        var name = cpu.GetString("Name")?.Trim() ?? "Inconnu";
        return new CpuInfo(
            name,
            CpuClassifier.VendorOf(cpu.GetString("Manufacturer"), name),
            (int)(cpu.GetInt64("NumberOfCores") ?? 0),
            (int)(cpu.GetInt64("NumberOfLogicalProcessors") ?? 0),
            (int)(cpu.GetInt64("MaxClockSpeed") ?? 0));
    }

    private static List<GpuInfo> ReadGpus(ICimReader cim) =>
        TryQuery(cim, "SELECT Name, PNPDeviceID, DriverVersion, DriverDate FROM Win32_VideoController")
            .Where(row => row.GetString("PNPDeviceID")?.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) == true)
            .Select(row =>
            {
                var name = row.GetString("Name")?.Trim() ?? "GPU inconnu";
                var pnp = row.GetString("PNPDeviceID") ?? string.Empty;
                var vendor = GpuClassifier.VendorOf(pnp, name);
                return new GpuInfo(name, vendor, row.GetString("DriverVersion"), row.GetDateTime("DriverDate"), pnp, GpuClassifier.IsLikelyIntegrated(vendor, name));
            })
            .ToList();

    private const string EnrollmentsKey = @"SOFTWARE\Microsoft\Enrollments";

    /// <summary>
    /// Pseudo-inscriptions que Windows 11 crée de lui-même sur tout PC personnel : elles ne signifient
    /// aucune gestion par une organisation.
    /// </summary>
    private static readonly HashSet<string> BuiltInEnrollmentProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Local Authority",
        "Deploy Authority",
        "Cloud Authority",
    };

    /// <summary>
    /// Vrai si une inscription sous <c>HKLM\SOFTWARE\Microsoft\Enrollments</c> porte le <c>ProviderID</c>
    /// d'un vrai service de gestion (Intune « MS DM Server » ou autre MDM).
    /// </summary>
    internal static bool HasOrganizationEnrollment(IRegistryReader registry)
    {
        IReadOnlyList<string> enrollments;
        try
        {
            enrollments = registry.GetSubKeyNames(RegistryHive.LocalMachine, EnrollmentsKey);
        }
        catch (MausAccessDeniedException)
        {
            return false;
        }

        foreach (var id in enrollments)
        {
            try
            {
                var provider = registry.GetString(RegistryHive.LocalMachine, $@"{EnrollmentsKey}\{id}", "ProviderID")?.Trim();
                if (!string.IsNullOrEmpty(provider) && !BuiltInEnrollmentProviders.Contains(provider))
                {
                    return true;
                }
            }
            catch (MausAccessDeniedException)
            {
                // Inscription illisible : ignorée.
            }
        }

        return false;
    }

    private static CimRow? TryFirst(ICimReader cim, string wql)
    {
        var rows = TryQuery(cim, wql);
        return rows.Count > 0 ? rows[0] : null;
    }

    private static IReadOnlyList<CimRow> TryQuery(ICimReader cim, string wql)
    {
        try
        {
            return cim.Query(wql);
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or DataSourceUnavailableException)
        {
            return [];
        }
    }
}
