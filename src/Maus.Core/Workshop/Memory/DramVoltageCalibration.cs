using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Workshop.Memory;

/// <summary>
/// Entrée de la puce de surveillance de la carte mère choisie par l'utilisateur comme tension de la mémoire, après l'avoir
/// comparée à la tension qu'il a réglée dans le BIOS. Elle ne vaut que pour cette carte mère.
/// </summary>
public sealed record DramVoltageCalibration(string Board, string Hardware, string Sensor, double Factor, double SetVolts, DateTimeOffset At);

/// <summary>Entrée candidate : mesure brute, facteur (1 ou 2) et tension obtenue.</summary>
public sealed record DramVoltageCandidate(HardwareReading Reading, double Factor, double Volts);

/// <summary>
/// Étalonnage de la tension de la mémoire sur une carte mère que LibreHardwareMonitor ne décrit pas : aucune entrée n'est
/// devinée, l'utilisateur choisit celle qui correspond à la tension qu'il a lui-même réglée dans le BIOS.
/// </summary>
public static class DramVoltageCalibrations
{
    /// <summary>Écart toléré entre la tension réglée et la mesure (régulateur, précision de la puce de mesure).</summary>
    public const double Tolerance = 0.03;

    /// <summary>
    /// Facteurs essayés : mesure directe, ou divisée par deux par un pont de résistances (courant sur les puces Nuvoton,
    /// dont la plage de mesure s'arrête vers 2 V).
    /// </summary>
    private static readonly double[] Factors = [1, 2];

    /// <summary>
    /// Entrées anonymes de la carte mère (« Voltage #5 ») dont la mesure, directe ou doublée, est à 3 % de
    /// <paramref name="setVolts"/>. Les entrées déjà nommées (Vcore, +3.3V…) sont connues pour mesurer autre chose.
    /// </summary>
    public static IReadOnlyList<DramVoltageCandidate> Candidates(IReadOnlyList<HardwareReading> readings, double setVolts)
    {
        if (setVolts is < 0.8 or > 2.2)
        {
            return [];
        }

        return readings
            .Where(r => r.Group == ReadingGroup.Motherboard && r.Kind == ReadingKind.Voltage && r.Value > 0
                && r.Name.StartsWith("Voltage #", StringComparison.OrdinalIgnoreCase))
            .SelectMany(r => Factors.Select(f => new DramVoltageCandidate(r, f, r.Value * f)))
            .Where(c => Math.Abs(c.Volts - setVolts) / setVolts <= Tolerance)
            .OrderBy(c => Math.Abs(c.Volts - setVolts))
            .ToList();
    }

    /// <summary>Tension de la mémoire d'après l'étalonnage, si la carte mère et l'entrée sont les mêmes.</summary>
    public static double? Apply(IReadOnlyList<HardwareReading> readings, DramVoltageCalibration calibration, string? board) =>
        board is not null && string.Equals(board, calibration.Board, StringComparison.OrdinalIgnoreCase)
        && readings.FirstOrDefault(r => r.Group == ReadingGroup.Motherboard && r.Kind == ReadingKind.Voltage
            && r.Hardware == calibration.Hardware && r.Name == calibration.Sensor) is { } reading
            ? reading.Value * calibration.Factor
            : null;

    /// <summary>Carte mère (fabricant et modèle, d'après le BIOS), pour lier l'étalonnage à cette carte.</summary>
    public static string? Board(IRegistryReader registry)
    {
        const string key = @"HARDWARE\DESCRIPTION\System\BIOS";
        try
        {
            var maker = registry.GetValue(RegistryHive.LocalMachine, key, "BaseBoardManufacturer") as string;
            var product = registry.GetValue(RegistryHive.LocalMachine, key, "BaseBoardProduct") as string;
            return string.IsNullOrWhiteSpace(product) ? null : $"{maker?.Trim()} {product.Trim()}".Trim();
        }
        catch (MausAccessDeniedException)
        {
            return null;
        }
    }
}
