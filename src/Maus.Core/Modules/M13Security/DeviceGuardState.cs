using Maus.Core.Platform;

namespace Maus.Core.Modules.M13Security;

/// <summary>État de la sécurité basée sur la virtualisation, lu dans <c>Win32_DeviceGuard</c>.</summary>
internal sealed record DeviceGuardState(
    long? VbsStatus,
    IReadOnlyList<long> ServicesRunning,
    IReadOnlyList<long> ServicesConfigured,
    IReadOnlyList<long> AvailableProperties)
{
    public const long CredentialGuard = 1;
    public const long MemoryIntegrity = 2;

    /// <summary>Propriété 7 de <c>AvailableSecurityProperties</c> : MBEC (Intel) ou GMET (AMD).</summary>
    public const long ModeBasedExecutionControl = 7;

    /// <summary>0 non activée, 1 activée mais pas en cours d'exécution, 2 en cours d'exécution.</summary>
    public bool IsVbsRunning => VbsStatus == 2;

    public bool IsMemoryIntegrityRunning => ServicesRunning.Contains(MemoryIntegrity);

    public bool IsMemoryIntegrityConfigured => ServicesConfigured.Contains(MemoryIntegrity);

    public bool IsCredentialGuardRunning => ServicesRunning.Contains(CredentialGuard);

    public bool HasModeBasedExecutionControl => AvailableProperties.Contains(ModeBasedExecutionControl);

    public static DeviceGuardState FromRow(CimRow row) => new(
        row.GetInt64("VirtualizationBasedSecurityStatus"),
        row.GetInt64Array("SecurityServicesRunning"),
        row.GetInt64Array("SecurityServicesConfigured"),
        row.GetInt64Array("AvailableSecurityProperties"));

    /// <summary>Nom en clair d'un service de <c>SecurityServicesRunning</c> ; 0 signifie « aucun ».</summary>
    public static string? ServiceLabel(long service) => service switch
    {
        1 => "Credential Guard",
        2 => "intégrité de la mémoire",
        3 => "System Guard (lancement sécurisé)",
        4 => "mesure du micrologiciel SMM",
        5 => "protection matérielle de la pile du noyau",
        6 => "protection de la pile du noyau (audit)",
        7 => "traduction de pagination protégée par l'hyperviseur",
        _ => null,
    };
}

/// <summary>Réglages de la VBS et de l'intégrité de la mémoire dans le registre (réglage local et stratégie).</summary>
internal sealed record VbsConfiguration(
    int? MemoryIntegrityEnabled,
    int? MemoryIntegrityLocked,
    int? VbsEnabled,
    int? VbsLocked,
    int? PolicyVbs,
    int? PolicyMemoryIntegrity)
{
    /// <summary>Activation demandée localement ou par stratégie (1 : avec verrou UEFI, 2 : sans verrou).</summary>
    public bool IsMemoryIntegrityRequested => MemoryIntegrityEnabled == 1 || PolicyMemoryIntegrity is 1 or 2;

    public bool IsImposedByPolicy => PolicyVbs is not null || PolicyMemoryIntegrity is not null;

    public bool IsUefiLocked => MemoryIntegrityLocked == 1 || VbsLocked == 1 || PolicyMemoryIntegrity == 1;
}
