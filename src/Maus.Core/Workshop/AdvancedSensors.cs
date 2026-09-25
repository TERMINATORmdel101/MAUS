using Maus.Core.Modules.M02Repair;
using Maus.Core.Platform;
using Microsoft.Win32;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

public enum ReadingKind
{
    Temperature,
    Voltage,
    Power,
    Fan,
    Clock,
    Load,
    Current,
    Other,
}

/// <summary>Famille du composant mesuré (pour regrouper l'affichage).</summary>
public enum ReadingGroup
{
    Cpu,
    Motherboard,
    Memory,
    Other,
}

/// <summary>Une mesure lue par le pilote PawnIO (par exemple « Core (Tctl/Tdie) », 71,3 °C).</summary>
public sealed record HardwareReading(string Hardware, ReadingGroup Group, string Name, ReadingKind Kind, double Value);

/// <summary>Capteurs qui demandent un pilote : température interne et tension du processeur, sondes de la carte mère, ventilateurs.</summary>
public interface IAdvancedSensors : IDisposable
{
    IReadOnlyList<HardwareReading> Read();
}

/// <summary>
/// PawnIO : pilote libre et signé d'accès au matériel (utilisé par LibreHardwareMonitor, FanControl, OpenRGB). MAUS ne
/// l'installe qu'à la demande de l'utilisateur, par winget, dans une fenêtre visible, et sait le désinstaller.
/// </summary>
public static class PawnIo
{
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";
    public const string WingetId = "namazso.PawnIO";

    /// <summary>Installé ? et version (lu dans la liste des programmes installés, sans charger le pilote).</summary>
    public static (bool Installed, string? Version) State(IRegistryReader registry)
    {
        try
        {
            return registry.KeyExists(RegistryHive.LocalMachine, UninstallKey)
                ? (true, registry.GetValue(RegistryHive.LocalMachine, UninstallKey, "DisplayVersion") as string)
                : (false, null);
        }
        catch (MausAccessDeniedException)
        {
            return (false, null);
        }
    }

    public static string InstallConsoleArguments(string wingetPath) => ConsoleArguments(wingetPath, "install", InstallIntro);

    public static string UninstallConsoleArguments(string wingetPath) => ConsoleArguments(wingetPath, "uninstall", UninstallIntro);

    public static string InstallIntro => T("Installation du pilote PawnIO par winget. Répondez à ses questions dans cette fenêtre, puis relancez MAUS pour voir les nouveaux capteurs.");

    public static string UninstallIntro => T("Désinstallation du pilote PawnIO par winget. Relancez ensuite MAUS.");

    private static string ConsoleArguments(string wingetPath, string verb, string intro)
    {
        if (wingetPath.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException(T("Chemin de winget invalide."), nameof(wingetPath));
        }

        return $"/s /k \"title MAUS & echo {RepairConsole.Escape(intro)} & echo. & \"{wingetPath}\" {verb} --id {WingetId} --exact --source winget\"";
    }
}

/// <summary>Valeurs utiles tirées des mesures brutes, avec les noms de capteurs des processeurs AMD et Intel.</summary>
public static class AdvancedReadings
{
    /// <summary>Température interne du processeur : Tctl/Tdie (AMD), boîtier (Intel), sinon la plus haute des sondes du processeur.</summary>
    public static double? CpuTemperature(IReadOnlyList<HardwareReading> readings)
    {
        var cpu = readings.Where(r => r.Group == ReadingGroup.Cpu && r.Kind == ReadingKind.Temperature && r.Value is > 0 and < 150).ToList();
        return Named(cpu, "Core (Tctl/Tdie)", "Core (Tctl)", "Core (Tdie)", "CPU Package", "Package", "Core Max")
            ?? (cpu.Count > 0 ? cpu.Max(r => r.Value) : null);
    }

    /// <summary>Puissance consommée par le processeur (boîtier), en watts.</summary>
    public static double? CpuPower(IReadOnlyList<HardwareReading> readings) =>
        Named(readings.Where(r => r.Group == ReadingGroup.Cpu && r.Kind == ReadingKind.Power && r.Value is > 0 and < 1000).ToList(), "Package", "CPU Package");

    /// <summary>Tension des cœurs : mesurée par le régulateur si elle est publiée, sinon la plus haute tension demandée (VID).</summary>
    public static double? CpuVoltage(IReadOnlyList<HardwareReading> readings)
    {
        var volts = readings.Where(r => r.Group == ReadingGroup.Cpu && r.Kind == ReadingKind.Voltage && r.Value is > 0.2 and < 2.5).ToList();
        return Named(volts, "Core (SVI3 TFN)", "Core (SVI2 TFN)", "CPU Core", "Core")
            ?? (volts.Where(r => r.Name.Contains("VID", StringComparison.OrdinalIgnoreCase)).Select(r => (double?)r.Value).Max());
    }

    private static double? Named(IReadOnlyList<HardwareReading> readings, params string[] names)
    {
        foreach (var name in names)
        {
            // Nom exact : « CPU Core #1 VID » ne doit pas passer pour « CPU Core ».
            if (readings.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } found)
            {
                return found.Value;
            }
        }

        return null;
    }
}

/// <summary>Ajoute les capteurs avancés (PawnIO) aux mesures sans pilote.</summary>
public sealed class CombinedSensorSource(ISensorSource basic, IAdvancedSensors advanced) : ISensorSource
{
    public SensorSnapshot Sample()
    {
        var snapshot = basic.Sample();
        IReadOnlyList<HardwareReading> readings;
        try
        {
            readings = advanced.Read();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Pilote retiré ou en panne : les mesures sans pilote continuent.
            return snapshot;
        }

        return snapshot with
        {
            CpuTemperatureC = AdvancedReadings.CpuTemperature(readings),
            CpuPowerWatts = AdvancedReadings.CpuPower(readings),
            CpuVoltage = AdvancedReadings.CpuVoltage(readings),
            Readings = readings,
        };
    }

    public void Dispose()
    {
        advanced.Dispose();
        basic.Dispose();
    }
}
