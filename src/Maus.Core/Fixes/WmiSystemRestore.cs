using System.Globalization;
using System.Management;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Fixes;

/// <summary>Restauration du système par WMI (<c>root\default</c>, classe <c>SystemRestore</c>).</summary>
public sealed class WmiSystemRestore(IRegistryReader registry) : ISystemRestore
{
    private const string Scope = @"\\.\root\default";
    private const uint ModifySettings = 12;
    private const uint BeginSystemChange = 100;

    /// <summary>Client « Protection du système » de VSS : liste des volumes protégés (à vérifier selon les builds).</summary>
    private const string SppClients = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SPP\Clients";
    private const string SystemRestoreClient = "{09F7EDC5-294E-4180-AF6A-FB0E6A0E9513}";

    public ProtectionState GetProtectionState(string drive)
    {
        try
        {
            if (registry.GetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore", "DisableSR") == 1)
            {
                return ProtectionState.DisabledByPolicy;
            }

            if (!registry.KeyExists(RegistryHive.LocalMachine, SppClients))
            {
                return ProtectionState.Unknown;
            }

            var volumes = registry.GetValue(RegistryHive.LocalMachine, SppClients, SystemRestoreClient) switch
            {
                string[] values => values,
                string value => [value],
                _ => [],
            };
            return volumes.Any(v => !string.IsNullOrWhiteSpace(v)) ? ProtectionState.Enabled : ProtectionState.Disabled;
        }
        catch (MausAccessDeniedException)
        {
            return ProtectionState.Unknown;
        }
    }

    public void EnableProtection(string drive) => Invoke("Enable", drive);

    public IReadOnlyList<RestorePointInfo> ListRestorePoints()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(Scope), new ObjectQuery("SELECT SequenceNumber, Description, CreationTime FROM SystemRestore"));
            using var results = searcher.Get();
            return results.Cast<ManagementBaseObject>()
                .Select(o =>
                {
                    using (o)
                    {
                        return new RestorePointInfo(
                            Convert.ToInt64(o["SequenceNumber"], CultureInfo.InvariantCulture),
                            o["Description"] as string ?? string.Empty,
                            ParseDmtf(o["CreationTime"] as string));
                    }
                })
                .ToList();
        }
        catch (ManagementException ex)
        {
            throw new DataSourceUnavailableException($"Liste des points de restauration illisible : {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new MausAccessDeniedException("Liste des points de restauration refusée.", ex);
        }
    }

    public void CreateRestorePoint(string description) => Invoke("CreateRestorePoint", description, ModifySettings, BeginSystemChange);

    private static void Invoke(string method, params object[] arguments)
    {
        try
        {
            using var restore = new ManagementClass(new ManagementScope(Scope), new ManagementPath("SystemRestore"), null);
            var result = restore.InvokeMethod(method, arguments);
            var code = Convert.ToUInt32(result ?? 0u, CultureInfo.InvariantCulture);
            if (code != 0)
            {
                throw new InvalidOperationException($"SystemRestore.{method} a renvoyé le code 0x{code:X8}.");
            }
        }
        catch (ManagementException ex)
        {
            throw new InvalidOperationException($"SystemRestore.{method} a échoué : {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new MausAccessDeniedException($"SystemRestore.{method} refusé : droits administrateur requis.", ex);
        }
    }

    private static DateTimeOffset? ParseDmtf(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(value));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
