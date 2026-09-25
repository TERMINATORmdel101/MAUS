using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Modules.M01Audit;

/// <summary>
/// Confirme <see cref="Hardware.HardwareProfile.IsManaged"/> avant de taire des écarts : Windows 11 crée de lui-même, sur tout PC personnel,
/// des pseudo-inscriptions sous <c>HKLM\SOFTWARE\Microsoft\Enrollments</c> (ProviderID « Local Authority », « Deploy Authority »,
/// « Cloud Authority ») qui ne signifient aucune gestion par une organisation.
/// </summary>
internal static class ManagedPcDetector
{
    private const string EnrollmentsKey = @"SOFTWARE\Microsoft\Enrollments";

    private static readonly HashSet<string> BuiltInProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Local Authority",
        "Deploy Authority",
        "Cloud Authority",
    };

    public static bool IsManaged(AuditContext context) =>
        context.Hardware.IsManaged && (IsDomainJoined(context.Cim) || HasOrganizationEnrollment(context.Registry));

    private static bool IsDomainJoined(ICimReader cim) =>
        CimQueryResult.Run(cim, "SELECT PartOfDomain FROM Win32_ComputerSystem", CimScopes.Default).First?.GetBool("PartOfDomain") == true;

    /// <summary>Vrai si une inscription porte le ProviderID d'un vrai service de gestion (Intune « MS DM Server », autre MDM).</summary>
    internal static bool HasOrganizationEnrollment(IRegistryReader registry)
    {
        IReadOnlyList<string> enrollments;
        try
        {
            enrollments = registry.GetSubKeyNames(RegistryHive.LocalMachine, EnrollmentsKey);
        }
        catch (MausAccessDeniedException)
        {
            return false;
        }

        foreach (var id in enrollments)
        {
            try
            {
                var provider = registry.GetString(RegistryHive.LocalMachine, $@"{EnrollmentsKey}\{id}", "ProviderID")?.Trim();
                if (!string.IsNullOrEmpty(provider) && !BuiltInProviders.Contains(provider))
                {
                    return true;
                }
            }
            catch (MausAccessDeniedException)
            {
                // Inscription illisible : ignorée.
            }
        }

        return false;
    }
}
