using Maus.Core.Platform;
using Microsoft.Win32;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Hardware;

public static class HardwareProfileBuilder
{
    public static HardwareProfile Build(ICimReader cim, IRegistryReader registry)
    {
        ArgumentNullException.ThrowIfNull(cim);
        ArgumentNullException.ThrowIfNull(registry);

        var system = TryFirst(cim, "SELECT Manufacturer, Model, PCSystemTypeEx, PartOfDomain FROM Win32_ComputerSystem");
        var enclosure = TryFirst(cim, "SELECT ChassisTypes FROM Win32_SystemEnclosure");
        var board = TryFirst(cim, "SELECT Manufacturer, Product FROM Win32_BaseBoard");
        var hasBattery = cim.QueryOrEmpty("SELECT DeviceID FROM Win32_Battery").Count > 0;
        var partOfDomain = system?.GetBool("PartOfDomain");
        var enrolled = HasOrganizationEnrollment(registry);
        var managed = partOfDomain == true || enrolled == true;

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
            IsManaged = managed,
            ManagementUnknown = !managed && (partOfDomain is null || enrolled is null),
        };
    }

    private static CpuInfo ReadCpu(ICimReader cim)
    {
        var cpu = TryFirst(cim, "SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
        if (cpu is null)
        {
            return CpuInfo.Unknown;
        }

        var name = cpu.GetString("Name")?.Trim() ?? string.Empty;
        return new CpuInfo(
            name,
            CpuClassifier.VendorOf(cpu.GetString("Manufacturer"), name),
            (int)(cpu.GetInt64("NumberOfCores") ?? 0),
            (int)(cpu.GetInt64("NumberOfLogicalProcessors") ?? 0));
    }

    internal static List<GpuInfo> ReadGpus(ICimReader cim) =>
        cim.QueryOrEmpty("SELECT Name, PNPDeviceID, DriverVersion, DriverDate, InfFilename FROM Win32_VideoController")
            .Where(row => row.GetString("PNPDeviceID")?.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) == true)
            .Select(ToGpuInfo)
            .ToList();

    /// <summary>Carte graphique décrite par une ligne de <c>Win32_VideoController</c>.</summary>
    internal static GpuInfo ToGpuInfo(CimRow row)
    {
        var name = row.GetString("Name")?.Trim() ?? T("GPU inconnu");
        var pnp = row.GetString("PNPDeviceID") ?? string.Empty;
        var vendor = GpuClassifier.VendorOf(pnp, name);
        return new GpuInfo(name, vendor, row.GetString("DriverVersion"), row.GetDateTime("DriverDate"), pnp, GpuClassifier.IsLikelyIntegrated(vendor, name), row.GetString("InfFilename"));
    }

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
    /// d'un vrai service de gestion (Intune « MS DM Server » ou autre MDM). Faux si tout a été lu sans en trouver, <c>null</c>
    /// si une partie était illisible sans preuve ailleurs : une clé absente veut dire « aucune inscription », un refus ne dit rien.
    /// </summary>
    internal static bool? HasOrganizationEnrollment(IRegistryReader registry)
    {
        IReadOnlyList<string> enrollments;
        try
        {
            enrollments = registry.GetSubKeyNames(RegistryHive.LocalMachine, EnrollmentsKey);
        }
        catch (MausAccessDeniedException)
        {
            return null;
        }

        var unreadable = false;
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
                // Inscription illisible : la suite peut encore prouver la gestion.
                unreadable = true;
            }
        }

        return unreadable ? null : false;
    }

    private static CimRow? TryFirst(ICimReader cim, string wql) => cim.QueryOrEmpty(wql).FirstOrNull();
}
