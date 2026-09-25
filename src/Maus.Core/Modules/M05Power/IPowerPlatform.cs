using System.Buffers.Binary;

namespace Maus.Core.Modules.M05Power;

/// <summary>Accès en lecture seule aux API d'alimentation de Windows (powrprof.dll, kernel32.dll). Aucune fonction « Set » n'est exposée.</summary>
internal interface IPowerPlatform
{
    /// <summary>GUID du mode de gestion actif (<c>PowerGetActiveScheme</c>), ou <c>null</c> si l'appel échoue.</summary>
    Guid? GetActiveScheme();

    /// <summary>Capacités d'alimentation (<c>CallNtPowerInformation(SystemPowerCapabilities)</c>), ou <c>null</c> si l'appel échoue.</summary>
    PowerCapabilities? GetCapabilities();

    /// <summary>
    /// Mode effectif signalé par <c>PowerRegisterForEffectivePowerModeNotifications</c> (Mode Jeu compris),
    /// ou <c>null</c> si l'API est indisponible ou ne répond pas à temps.
    /// </summary>
    Task<EffectivePowerMode?> GetEffectivePowerModeAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Nombre de classes d'efficacité distinctes parmi les cœurs ; plus d'une signale un processeur hybride. <c>null</c> si l'appel échoue.</summary>
    int? GetEfficiencyClassCount();
}

/// <summary>Valeurs de <c>EFFECTIVE_POWER_MODE</c> (version 2 de l'API).</summary>
internal enum EffectivePowerMode
{
    BatterySaver = 0,
    BetterBattery = 1,
    Balanced = 2,
    HighPerformance = 3,
    MaxPerformance = 4,
    GameMode = 5,
    MixedReality = 6,
}

/// <summary>Champs utiles de <c>SYSTEM_POWER_CAPABILITIES</c>.</summary>
internal sealed record PowerCapabilities(
    bool LidPresent,
    bool SystemS3,
    bool SystemS4,
    bool HiberFilePresent,
    bool UpsPresent,
    bool AoAc,
    bool SystemBatteriesPresent,
    bool BatteriesAreShortTerm)
{
    /// <summary>Taille de <c>SYSTEM_POWER_CAPABILITIES</c> en octets.</summary>
    public const int NativeSize = 76;

    /// <summary>
    /// Lit la structure native octet par octet (winnt.h, Windows 10 et plus) :
    /// LidPresent 2, SystemS3 5, SystemS4 6, HiberFilePresent 8, UpsPresent 12, AoAc 20, SystemBatteriesPresent 30, BatteriesAreShortTerm 31.
    /// </summary>
    public static PowerCapabilities? Parse(ReadOnlySpan<byte> buffer) =>
        buffer.Length < 32
            ? null
            : new PowerCapabilities(
                LidPresent: buffer[2] != 0,
                SystemS3: buffer[5] != 0,
                SystemS4: buffer[6] != 0,
                HiberFilePresent: buffer[8] != 0,
                UpsPresent: buffer[12] != 0,
                AoAc: buffer[20] != 0,
                SystemBatteriesPresent: buffer[30] != 0,
                BatteriesAreShortTerm: buffer[31] != 0);
}

internal static class ProcessorTopology
{
    private const int RelationProcessorCore = 0;

    /// <summary>
    /// Compte les classes d'efficacité distinctes dans un tampon <c>SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX</c>
    /// (Relationship à +0, Size à +4, puis <c>PROCESSOR_RELATIONSHIP</c> : Flags à +8, EfficiencyClass à +9).
    /// </summary>
    public static int CountEfficiencyClasses(ReadOnlySpan<byte> buffer)
    {
        var classes = new HashSet<byte>();
        var offset = 0;
        while (offset + 10 <= buffer.Length)
        {
            var relationship = BinaryPrimitives.ReadInt32LittleEndian(buffer[offset..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(buffer[(offset + 4)..]);
            if (relationship == RelationProcessorCore)
            {
                classes.Add(buffer[offset + 9]);
            }

            if (size == 0 || size > buffer.Length - offset)
            {
                break;
            }

            offset += (int)size;
        }

        return classes.Count;
    }
}
