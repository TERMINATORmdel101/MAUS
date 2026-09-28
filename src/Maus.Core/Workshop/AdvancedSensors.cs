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
        var cpu = readings.Where(r => r.Group == ReadingGroup.Cpu && r.Kind == ReadingKind.Temperature && r.Value is > 0 and < 150
            && !r.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)).ToList();
        return Named(cpu, "Core (Tctl/Tdie)", "Core (Tctl)", "Core (Tdie)", "CPU Package", "Package", "Core Max")
            ?? (cpu.Count > 0 ? cpu.Max(r => r.Value) : null);
    }

    /// <summary>
    /// Limite de température lue dans le processeur (Intel : registre TjMax, exposé par LibreHardwareMonitor comme
    /// « Distance to TjMax ») : température d'un cœur + distance à la limite. <c>null</c> si le processeur ne la donne pas.
    /// </summary>
    public static int? CpuTjMax(IReadOnlyList<HardwareReading> readings)
    {
        const string suffix = " Distance to TjMax";
        var cpu = readings.Where(r => r.Group == ReadingGroup.Cpu && r.Kind == ReadingKind.Temperature).ToList();
        foreach (var distance in cpu.Where(r => r.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            var core = distance.Name[..^suffix.Length];
            if (cpu.FirstOrDefault(r => r.Name.Equals(core, StringComparison.OrdinalIgnoreCase)) is { } temperature
                && (int)Math.Round(temperature.Value + distance.Value) is var tj and >= 70 and <= 115)
            {
                return tj;
            }
        }

        return null;
    }

    /// <summary>Puissance consommée par le processeur (boîtier), en watts.</summary>
    public static double? CpuPower(IReadOnlyList<HardwareReading> readings) =>
        Named(readings.Where(r => r.Group == ReadingGroup.Cpu && r.Kind == ReadingKind.Power && r.Value is > 0 and < 1000).ToList(), "Package", "CPU Package");

    /// <summary>
    /// Tension de la mémoire mesurée par la puce de surveillance de la carte mère, si LibreHardwareMonitor sait laquelle de
    /// ses entrées la porte sur ce modèle (capteurs nommés « DRAM », « VDIMM », « DIMM »… dans ses tables par carte mère).
    /// Sur une carte non décrite, ses entrées restent anonymes (« Voltage #5 ») : aucune n'est devinée.
    /// </summary>
    public static HardwareReading? DramVoltage(IReadOnlyList<HardwareReading> readings) =>
        readings.FirstOrDefault(r => r.Group == ReadingGroup.Motherboard && r.Kind == ReadingKind.Voltage && r.Value is > 0.8 and < 2.2
            && (r.Name.Equals("DRAM", StringComparison.OrdinalIgnoreCase)
                || r.Name.Equals("VDIMM", StringComparison.OrdinalIgnoreCase)
                || r.Name.StartsWith("DIMM", StringComparison.OrdinalIgnoreCase)));

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

/// <summary>
/// Ajoute les capteurs avancés (PawnIO) aux mesures sans pilote, sans jamais les faire attendre : le pilote s'ouvre en
/// arrière-plan, chaque lecture du pilote a un temps limité, et une lecture qui traîne laisse passer les mesures sans
/// pilote. Des valeurs du pilote trop anciennes ne sont plus affichées : jamais une température figée présentée comme actuelle.
/// </summary>
public sealed class CombinedSensorSource : ISensorSource
{
    /// <summary>Âge au-delà duquel les dernières valeurs du pilote ne sont plus affichées.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(6);

    private readonly ISensorSource _basic;
    private readonly Task<IAdvancedSensors> _opening;
    private readonly TimeSpan _wait;
    private Task<IReadOnlyList<HardwareReading>>? _reading;
    private IReadOnlyList<HardwareReading> _latest = [];
    private DateTimeOffset _latestAt = DateTimeOffset.MinValue;
    private DateTimeOffset _readingStartedAt;

    /// <summary>Capteurs déjà ouverts.</summary>
    public CombinedSensorSource(ISensorSource basic, IAdvancedSensors advanced, TimeSpan? wait = null)
        : this(basic, Task.FromResult(advanced), wait)
    {
    }

    /// <summary>Capteurs ouverts en arrière-plan par <paramref name="open"/> (l'ouverture du pilote peut prendre du temps).</summary>
    public CombinedSensorSource(ISensorSource basic, Func<IAdvancedSensors> open, TimeSpan? wait = null)
        : this(basic, Task.Run(open), wait)
    {
    }

    private CombinedSensorSource(ISensorSource basic, Task<IAdvancedSensors> opening, TimeSpan? wait)
    {
        _basic = basic;
        _opening = opening;
        _wait = wait ?? TimeSpan.FromSeconds(1.5);
    }

    /// <summary>Le pilote est encore en cours d'ouverture.</summary>
    public bool IsOpening => !_opening.IsCompleted;

    /// <summary>Raison de l'échec d'ouverture du pilote, ou <c>null</c>.</summary>
    public string? Failure => _opening.IsFaulted ? _opening.Exception?.InnerException?.Message : null;

    /// <summary>La dernière lecture du pilote ne répond pas depuis plus de <see cref="MaxAge"/>.</summary>
    public bool IsStalled { get; private set; }

    public SensorSnapshot Sample()
    {
        var snapshot = _basic.Sample();
        if (!_opening.IsCompletedSuccessfully)
        {
            return snapshot;
        }

        if (_reading is null)
        {
            _reading = Task.Run(_opening.Result.Read);
            _readingStartedAt = snapshot.At;
        }

        if (Completes(_reading, _wait))
        {
            if (_reading.IsCompletedSuccessfully)
            {
                _latest = _reading.Result;
                _latestAt = snapshot.At;
            }
            else
            {
                // Pilote retiré ou en panne : les mesures sans pilote continuent.
                _latest = [];
            }

            _reading = null;
        }

        IsStalled = _reading is not null && snapshot.At - _readingStartedAt > MaxAge;
        if (_latest.Count == 0 || snapshot.At - _latestAt > MaxAge)
        {
            return snapshot;
        }

        return snapshot with
        {
            CpuTemperatureC = AdvancedReadings.CpuTemperature(_latest),
            CpuPowerWatts = AdvancedReadings.CpuPower(_latest),
            CpuVoltage = AdvancedReadings.CpuVoltage(_latest),
            CpuTjMaxC = AdvancedReadings.CpuTjMax(_latest),
            Readings = _latest,
        };
    }

    public void Dispose()
    {
        if (_opening.IsCompletedSuccessfully)
        {
            // Une lecture en cours finit avant la fermeture du pilote (quelques secondes au plus).
            if (_reading is { } reading)
            {
                Completes(reading, TimeSpan.FromSeconds(5));
            }

            _opening.Result.Dispose();
        }

        _basic.Dispose();
    }

    private static bool Completes(Task task, TimeSpan timeout)
    {
        try
        {
            return task.Wait(timeout);
        }
        catch (AggregateException)
        {
            return true;
        }
    }
}
