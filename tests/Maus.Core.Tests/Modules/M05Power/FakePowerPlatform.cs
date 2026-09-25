using Maus.Core.Modules.M05Power;

namespace Maus.Core.Tests.Modules.M05Power;

/// <summary>API d'alimentation simulées : chaque valeur se règle, <see cref="Failure"/> fait échouer tous les appels.</summary>
internal sealed class FakePowerPlatform : IPowerPlatform, Maus.Core.Platform.IPowerSchemeAccessor
{
    public Guid? ActiveScheme { get; set; }

    public PowerCapabilities? Capabilities { get; set; } = Caps();

    public EffectivePowerMode? EffectiveMode { get; set; } = EffectivePowerMode.Balanced;

    public int? EfficiencyClasses { get; set; } = 1;

    /// <summary>Exception levée par chaque appel (par exemple DLL introuvable).</summary>
    public Exception? Failure { get; set; }

    public static PowerCapabilities Caps(bool aoAc = false, bool hiberFile = false, bool s3 = true, bool ups = false, bool batteries = false) =>
        new(LidPresent: false, SystemS3: s3, SystemS4: hiberFile, HiberFilePresent: hiberFile, UpsPresent: ups, AoAc: aoAc, SystemBatteriesPresent: batteries, BatteriesAreShortTerm: false);

    public Guid? GetActiveScheme() => Failure is null ? ActiveScheme : throw Failure;

    public PowerCapabilities? GetCapabilities() => Failure is null ? Capabilities : throw Failure;

    public Task<EffectivePowerMode?> GetEffectivePowerModeAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        Failure is null ? Task.FromResult(EffectiveMode) : throw Failure;

    public int? GetEfficiencyClassCount() => Failure is null ? EfficiencyClasses : throw Failure;

    /// <summary>Modes présents sur le PC simulé : les autres sont refusés, comme par <c>PowerSetActiveScheme</c>.</summary>
    public HashSet<Guid> InstalledSchemes { get; } = [PowerSchemes.Balanced, PowerSchemes.HighPerformance];

    public bool SetActiveScheme(Guid scheme)
    {
        if (!InstalledSchemes.Contains(scheme))
        {
            return false;
        }

        ActiveScheme = scheme;
        return true;
    }
}
