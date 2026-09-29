using Maus.Core.Hardware;
using Maus.Core.Modules.M14Display;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Workshop;

/// <summary>Mode de démarrage du micrologiciel (<c>FIRMWARE_TYPE</c> de winnt.h : 1 = BIOS, 2 = UEFI).</summary>
public enum FirmwareKind
{
    Unknown,
    LegacyBios,
    Uefi,
}

/// <summary>Puce TPM telle que la décrit la classe WMI <c>Win32_Tpm</c> (lisible seulement en administrateur).</summary>
public sealed record TpmIdentity(string? SpecVersion, string? Manufacturer, bool? Enabled, bool? Activated);

/// <summary>Windows et le PC : modèle, version, installation, dernier démarrage, mode de démarrage et protections.</summary>
public sealed record SystemIdentity(
    string? Manufacturer,
    string? Model,
    FormFactor FormFactor,
    WindowsInfo Windows,
    string? Architecture,
    DateTime? InstalledOn,
    DateTime? BootedAt,
    FirmwareKind Firmware,
    bool? SecureBoot,
    TpmIdentity? Tpm,
    bool TpmNeedsAdministrator);

/// <summary>Emplacements mémoire de la carte mère (SMBIOS, <c>Win32_PhysicalMemoryArray</c>).</summary>
public sealed record MemorySlots(int? Total, long? MaxCapacityBytes);

/// <summary>Un écran actif : nom, carte qui le pilote, connecteur, définition, fréquence et HDR.</summary>
public sealed record DisplayIdentity(
    string Name,
    string? Adapter,
    string Connector,
    int Width,
    int Height,
    double? RefreshHz,
    int? NativeWidth,
    int? NativeHeight,
    bool? HdrSupported,
    bool? HdrActive,
    int? BitsPerColor);

/// <summary>Carte réseau physique (sans adresse MAC ni adresse IP).</summary>
public sealed record NetworkAdapterIdentity(string Name, string Description, NetworkKind Kind, bool Connected, long? LinkSpeedBitsPerSecond);

public enum NetworkKind
{
    Other,
    Ethernet,
    WiFi,
}

public sealed record AudioDeviceIdentity(string Name, string? Manufacturer);

/// <summary>Ce que « Mon PC » affiche en plus des composants : Windows, écrans, réseau, son et emplacements mémoire.</summary>
public sealed record MachineDetails(
    SystemIdentity System,
    MemorySlots? Slots,
    IReadOnlyList<DisplayIdentity> Displays,
    IReadOnlyList<NetworkAdapterIdentity> Network,
    IReadOnlyList<AudioDeviceIdentity> Audio,
    string? Microcode = null);

/// <summary>Source du mode de démarrage (remplaçable dans les tests).</summary>
public interface IFirmwareTypeSource
{
    /// <summary>Valeur <c>FIRMWARE_TYPE</c>, ou <c>null</c> si l'appel échoue.</summary>
    int? Read();
}

/// <summary>
/// Lecture seule, sans pilote : WMI, registre et API d'affichage. Rien n'identifie la personne (ni nom du PC, ni
/// adresse MAC, ni adresse IP, ni numéro de série).
/// </summary>
public static class MachineDetailsReader
{
    internal const string OperatingSystemQuery = "SELECT OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem";

    // Use = 3 : mémoire du système (et non mémoire vidéo ou cache), d'après la documentation de Win32_PhysicalMemoryArray.
    internal const string MemoryArrayQuery = "SELECT MemoryDevices, MaxCapacity, MaxCapacityEx, Use FROM Win32_PhysicalMemoryArray";

    // Toutes les propriétés : les plus récentes (ManufacturerIdTxt) manquent sur certaines versions de Windows.
    internal const string TpmQuery = "SELECT * FROM Win32_Tpm";
    internal const string NetworkQuery = "SELECT Name, InterfaceDescription, MediaConnectState, ReceiveLinkSpeed, NdisPhysicalMedium, HardwareInterface FROM MSFT_NetAdapter";
    internal const string SoundQuery = "SELECT Name, Manufacturer FROM Win32_SoundDevice";
    internal const string SecureBootStateKey = @"SYSTEM\CurrentControlSet\Control\SecureBoot\State";

    // NDIS_PHYSICAL_MEDIUM (ntddndis.h) : 14 = 802.3 (Ethernet), 9 = Native 802.11 (Wi-Fi), comme le Module 18.
    private const long Ethernet = 14;
    private const long Wireless = 9;

    public static MachineDetails Read(AuditContext context) => Read(context, new Win32DisplayConfigReader(), new Win32FirmwareTypeSource());

    internal static MachineDetails Read(AuditContext context, IDisplayConfigReader displays, IFirmwareTypeSource firmware) => new(
        ReadSystem(context, firmware),
        ReadSlots(context.Cim),
        ReadDisplays(displays),
        ReadNetwork(context.Cim),
        ReadAudio(context.Cim),
        ReadMicrocode(context.Registry));

    /// <summary>
    /// Révision du microcode chargé, lue comme le Module 8 (valeur <c>Update Revision</c> du premier processeur, décodée par
    /// <see cref="Modules.M08Bios.MicrocodeRevision"/>) ; <c>null</c> si elle est absente ou illisible.
    /// </summary>
    internal static string? ReadMicrocode(IRegistryReader registry)
    {
        try
        {
            return Modules.M08Bios.MicrocodeRevision.Parse(registry.GetBinary(RegistryHive.LocalMachine, Modules.M08Bios.BiosModule.CpuKey, "Update Revision")) is { } revision
                ? Modules.M08Bios.MicrocodeRevision.Format(revision)
                : null;
        }
        catch (MausAccessDeniedException)
        {
            return null;
        }
    }

    internal static SystemIdentity ReadSystem(AuditContext context, IFirmwareTypeSource firmware)
    {
        var rows = Query(context.Cim, OperatingSystemQuery);
        var os = rows.Count > 0 ? rows[0] : null;
        var (tpm, tpmNeedsAdmin) = ReadTpm(context.Cim);
        return new SystemIdentity(
            Clean(context.Hardware.Manufacturer),
            Clean(context.Hardware.Model),
            context.Hardware.FormFactor,
            context.Windows,
            Clean(os?.GetString("OSArchitecture")),
            os?.GetDateTime("InstallDate"),
            os?.GetDateTime("LastBootUpTime"),
            Safe(firmware.Read) switch
            {
                1 => FirmwareKind.LegacyBios,
                2 => FirmwareKind.Uefi,
                _ => FirmwareKind.Unknown,
            },
            ReadSecureBoot(context.Registry),
            tpm,
            tpmNeedsAdmin);
    }

    /// <summary>État de Secure Boot publié par Windows (même valeur que le Module 8) ; <c>null</c> si absent.</summary>
    internal static bool? ReadSecureBoot(IRegistryReader registry)
    {
        try
        {
            return registry.GetDword(RegistryHive.LocalMachine, SecureBootStateKey, "UEFISecureBootEnabled") switch
            {
                1 => true,
                0 => false,
                _ => null,
            };
        }
        catch (MausAccessDeniedException)
        {
            return null;
        }
    }

    internal static (TpmIdentity? Tpm, bool NeedsAdministrator) ReadTpm(ICimReader cim)
    {
        try
        {
            var rows = cim.Query(TpmQuery, CimScopes.Tpm);
            var row = rows.Count > 0 ? rows[0] : null;
            if (row is null)
            {
                return (null, false);
            }

            // SpecVersion vaut par exemple « 2.0, 0, 1.59 » : la version de la norme est le premier élément.
            var spec = Clean(row.GetString("SpecVersion")?.Split(',')[0]);
            return (new TpmIdentity(spec, Clean(row.GetString("ManufacturerIdTxt")), row.GetBool("IsEnabled_InitialValue"), row.GetBool("IsActivated_InitialValue")), false);
        }
        catch (MausAccessDeniedException)
        {
            return (null, true);
        }
        catch (DataSourceUnavailableException)
        {
            return (null, false);
        }
    }

    internal static MemorySlots? ReadSlots(ICimReader cim)
    {
        var arrays = Query(cim, MemoryArrayQuery).Where(r => r.GetInt64("Use") is null or 3).ToList();
        if (arrays.Count == 0)
        {
            return null;
        }

        var total = arrays.Sum(r => r.GetInt64("MemoryDevices") ?? 0);

        // Capacité en kilo-octets ; MaxCapacityEx prend le relais au-delà de 2 To (valeur 0x80000000 dans MaxCapacity).
        var maxKb = arrays.Sum(r => r.GetInt64("MaxCapacityEx") is > 0 and var ex ? ex : r.GetInt64("MaxCapacity") is > 0 and < 0x80000000 and var max ? max : 0);
        return new MemorySlots(total > 0 ? (int)total : null, maxKb > 0 ? maxKb * 1024 : null);
    }

    internal static IReadOnlyList<DisplayIdentity> ReadDisplays(IDisplayConfigReader reader)
    {
        IReadOnlyList<DisplayPath>? paths;
        try
        {
            paths = reader.ReadActivePaths();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            paths = null;
        }

        return (paths ?? []).Select(p => new DisplayIdentity(
            Clean(p.MonitorName) ?? (DisplayParsers.IsInternal(p.Output) ? Localization.Texts.T("Écran intégré") : Localization.Texts.T("Écran")),
            Clean(p.AdapterName),
            DisplayParsers.ConnectorLabel(p.Output),
            p.Width,
            p.Height,
            p.RefreshHz,
            p.NativeWidth,
            p.NativeHeight,
            p.Color?.HdrSupported,
            p.Color?.HdrActive,
            p.Color?.BitsPerColorChannel is > 0 and var bits ? bits : null)).ToList();
    }

    internal static IReadOnlyList<NetworkAdapterIdentity> ReadNetwork(ICimReader cim) =>
        Query(cim, NetworkQuery, CimScopes.StandardCimv2)
            .Where(a => a.GetBool("HardwareInterface") == true)
            .Select(a => new NetworkAdapterIdentity(
                Clean(a.GetString("Name")) ?? "?",
                Clean(a.GetString("InterfaceDescription")) ?? "?",
                a.GetInt64("NdisPhysicalMedium") switch
                {
                    Ethernet => NetworkKind.Ethernet,
                    Wireless => NetworkKind.WiFi,
                    _ => NetworkKind.Other,
                },
                a.GetInt64("MediaConnectState") == 1,
                a.GetInt64("ReceiveLinkSpeed") is > 0 and var speed ? speed : null))
            .OrderByDescending(a => a.Connected)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    internal static IReadOnlyList<AudioDeviceIdentity> ReadAudio(ICimReader cim) =>
        Query(cim, SoundQuery)
            .Select(r => new AudioDeviceIdentity(Clean(r.GetString("Name")) ?? "?", Clean(r.GetString("Manufacturer"))))
            .Where(d => d.Name != "?")
            .DistinctBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed)
            || trimmed.Equals("To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Default string", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("System Product Name", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }

    private static IReadOnlyList<CimRow> Query(ICimReader cim, string wql, string scope = CimScopes.Default)
    {
        try
        {
            return cim.Query(wql, scope);
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or DataSourceUnavailableException)
        {
            return [];
        }
    }

    private static int? Safe(Func<int?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}
