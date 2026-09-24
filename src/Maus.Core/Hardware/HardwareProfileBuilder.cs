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
            IsManaged = system?.GetBool("PartOfDomain") == true || HasMdmEnrollment(registry),
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

    /// <summary>Une inscription MDM active laisse un <c>ProviderID</c> sous <c>HKLM\SOFTWARE\Microsoft\Enrollments</c>.</summary>
    private static bool HasMdmEnrollment(IRegistryReader registry)
    {
        const string enrollments = @"SOFTWARE\Microsoft\Enrollments";
        try
        {
            return registry.GetSubKeyNames(RegistryHive.LocalMachine, enrollments)
                .Any(id => !string.IsNullOrWhiteSpace(registry.GetString(RegistryHive.LocalMachine, $@"{enrollments}\{id}", "ProviderID")));
        }
        catch (MausAccessDeniedException)
        {
            return false;
        }
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
