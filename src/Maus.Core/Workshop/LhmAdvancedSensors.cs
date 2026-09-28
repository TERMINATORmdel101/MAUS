using LibreHardwareMonitor.Hardware;

namespace Maus.Core.Workshop;

/// <summary>
/// Lecture par LibreHardwareMonitorLib (MPL-2.0), qui passe par le pilote PawnIO et ses modules signés. Seuls le
/// processeur et la carte mère sont ouverts : les cartes graphiques, les disques et l'occupation de la mémoire ont déjà
/// leurs mesures sans pilote. La mémoire n'est pas ouverte ici : LibreHardwareMonitor la lirait par le bus SMBus des
/// barrettes à chaque mesure, un bus lent et partagé avec d'autres logiciels, qui figeait les mesures en direct chez le
/// porteur (MSI Z390). À n'ouvrir que si PawnIO est installé et MAUS lancé en administrateur.
/// </summary>
public sealed class LhmAdvancedSensors : IAdvancedSensors
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsMotherboardEnabled = true,
    };

    public LhmAdvancedSensors()
    {
        _computer.Open();
    }

    public IReadOnlyList<HardwareReading> Read()
    {
        var readings = new List<HardwareReading>();
        foreach (var hardware in _computer.Hardware)
        {
            Collect(hardware, GroupOf(hardware.HardwareType), readings);
        }

        return readings;
    }

    public void Dispose() => _computer.Close();

    private static void Collect(IHardware hardware, ReadingGroup group, List<HardwareReading> readings)
    {
        hardware.Update();
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.Value is { } value && float.IsFinite(value))
            {
                readings.Add(new HardwareReading(hardware.Name, group, sensor.Name, KindOf(sensor.SensorType), value));
            }
        }

        foreach (var sub in hardware.SubHardware)
        {
            Collect(sub, group, readings);
        }
    }

    private static ReadingGroup GroupOf(HardwareType type) => type switch
    {
        HardwareType.Cpu => ReadingGroup.Cpu,
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => ReadingGroup.Motherboard,
        HardwareType.Memory => ReadingGroup.Memory,
        _ => ReadingGroup.Other,
    };

    private static ReadingKind KindOf(SensorType type) => type switch
    {
        SensorType.Temperature => ReadingKind.Temperature,
        SensorType.Voltage => ReadingKind.Voltage,
        SensorType.Power => ReadingKind.Power,
        SensorType.Fan => ReadingKind.Fan,
        SensorType.Clock => ReadingKind.Clock,
        SensorType.Load => ReadingKind.Load,
        SensorType.Current => ReadingKind.Current,
        _ => ReadingKind.Other,
    };
}
