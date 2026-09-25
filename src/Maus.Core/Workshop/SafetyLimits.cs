using System.Text.RegularExpressions;
using Maus.Core.Rules;

namespace Maus.Core.Workshop;

/// <summary>Repères de tension d'une génération de RAM ; <see cref="DangerAboveMv"/> est absent quand aucune source n'en publie.</summary>
public sealed record MemoryVoltageLimit(int Generation, int NominalMv, int ElevatedAboveMv, int? DangerAboveMv, string Source);

public sealed record CpuTemperatureLimit(string Pattern, int MaxC, string Source);

/// <summary>Seuils de sécurité par famille de composant (catalogue <c>hw-safety-limits.json</c>).</summary>
public sealed record SafetyLimits
{
    public IReadOnlyList<MemoryVoltageLimit> MemoryVoltage { get; init; } = [];

    public IReadOnlyList<CpuTemperatureLimit> CpuMaxTemperature { get; init; } = [];

    public int BatteryWornBelowPercent { get; init; } = 80;

    /// <summary>Au repos, une carte à moins de ce nombre de degrés de son seuil de ralentissement refroidit mal.</summary>
    public int GpuIdleHotMarginC { get; init; } = 15;

    public static SafetyLimits Load() => EmbeddedCatalog.Load<SafetyLimits>("hw-safety-limits.json");

    public MemoryVoltageLimit? ForMemory(int? generation) => MemoryVoltage.FirstOrDefault(l => l.Generation == generation);

    public CpuTemperatureLimit? ForCpu(string cpuName) =>
        CpuMaxTemperature.FirstOrDefault(l => Regex.IsMatch(cpuName, l.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)));
}

public enum SafetyLevel
{
    Normal,
    Elevated,
    Dangerous,
}

public static class SafetyRules
{
    public static SafetyLevel MemoryVoltage(int millivolts, MemoryVoltageLimit limit) =>
        millivolts > limit.DangerAboveMv ? SafetyLevel.Dangerous
        : millivolts > limit.ElevatedAboveMv ? SafetyLevel.Elevated
        : SafetyLevel.Normal;
}
