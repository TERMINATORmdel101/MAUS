using System.Text.RegularExpressions;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Familles de périphériques, dans l'ordre d'affichage de la liste des pilotes.</summary>
public enum DriverGroup
{
    Graphics,
    Monitor,
    Audio,
    Network,
    Bluetooth,
    Storage,
    Usb,
    Input,
    Camera,
    Chipset,
    Other,
}

/// <summary>Pilote installé pour un périphérique, tel que Windows le décrit (<c>Win32_PnPSignedDriver</c>).</summary>
/// <param name="InstanceId">Identifiant d'instance du périphérique (<c>PCI\VEN_…\…</c>), utilisé par <c>pnputil</c>.</param>
/// <param name="InfName">Paquet du pilote : <c>oem12.inf</c> pour un pilote tiers, <c>display.inf</c> pour un pilote de Windows.</param>
public sealed partial record DriverEntry(
    string Device,
    string? Class,
    DriverGroup Group,
    string InstanceId,
    string? Version,
    DateTime? Date,
    string? Provider,
    string? Manufacturer,
    string? InfName,
    bool? IsSigned,
    string? Signer)
{
    /// <summary>Pilote ajouté au magasin de pilotes (<c>oem#.inf</c>) : sauvegardable et supprimable par <c>pnputil</c>.</summary>
    public bool IsThirdPartyPackage => InfName is not null && OemInf().IsMatch(InfName);

    [GeneratedRegex(@"^oem\d{1,5}\.inf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex OemInf();
}

/// <summary>
/// Liste complète des pilotes installés, en lecture seule, par la classe WMI <c>Win32_PnPSignedDriver</c> : périphérique,
/// famille, version, date, éditeur, paquet (.inf) et signature. Aucun pilote n'est chargé, rien n'est modifié.
/// </summary>
public static class DriverInventoryReader
{
    internal const string DriverQuery =
        "SELECT DeviceName, FriendlyName, DeviceClass, DeviceID, DriverVersion, DriverProviderName, Manufacturer, InfName, IsSigned, Signer FROM Win32_PnPSignedDriver";

    /// <param name="dates">Dates du Gestionnaire de périphériques (<see cref="CfgMgrDriverDateSource"/>) ; sans elle, pas de date :
    /// celle de <c>Win32_PnPSignedDriver</c> inverse parfois le jour et le mois.</param>
    public static IReadOnlyList<DriverEntry> Read(ICimReader cim, IDriverDateSource? dates = null)
    {
        IReadOnlyList<CimRow> rows;
        try
        {
            rows = cim.Query(DriverQuery);
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or DataSourceUnavailableException)
        {
            return [];
        }

        return rows
            .Select(row =>
            {
                var name = Clean(row.GetString("FriendlyName")) ?? Clean(row.GetString("DeviceName"));
                var id = Clean(row.GetString("DeviceID"));
                if (name is null || id is null)
                {
                    return null;
                }

                var cls = Clean(row.GetString("DeviceClass"));
                return new DriverEntry(
                    name,
                    cls,
                    GroupOf(cls),
                    id,
                    Clean(row.GetString("DriverVersion")),
                    dates?.DriverDate(id),
                    Clean(row.GetString("DriverProviderName")),
                    Clean(row.GetString("Manufacturer")),
                    Clean(row.GetString("InfName")),
                    row.GetBool("IsSigned"),
                    Clean(row.GetString("Signer")));
            })
            .OfType<DriverEntry>()
            .DistinctBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d.Group)
            .ThenBy(d => d.Device, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Famille d'après la classe de périphérique de Windows (noms des classes d'installation, en majuscules chez WMI).</summary>
    public static DriverGroup GroupOf(string? deviceClass) => deviceClass?.ToUpperInvariant() switch
    {
        "DISPLAY" => DriverGroup.Graphics,
        "MONITOR" => DriverGroup.Monitor,
        "MEDIA" or "AUDIOENDPOINT" => DriverGroup.Audio,
        "NET" or "NETSERVICE" or "NETTRANS" or "NETCLIENT" => DriverGroup.Network,
        "BLUETOOTH" => DriverGroup.Bluetooth,
        "HDC" or "SCSIADAPTER" or "DISKDRIVE" or "VOLUME" or "STORAGEVOLUME" or "CDROM" or "SDHOST" => DriverGroup.Storage,
        "USB" or "USBDEVICE" => DriverGroup.Usb,
        "HIDCLASS" or "KEYBOARD" or "MOUSE" => DriverGroup.Input,
        "CAMERA" or "IMAGE" => DriverGroup.Camera,
        "SYSTEM" or "PROCESSOR" or "FIRMWARE" or "SECURITYDEVICES" => DriverGroup.Chipset,
        _ => DriverGroup.Other,
    };

    public static string GroupName(DriverGroup group) => group switch
    {
        DriverGroup.Graphics => T("Cartes graphiques"),
        DriverGroup.Monitor => T("Écrans"),
        DriverGroup.Audio => T("Son"),
        DriverGroup.Network => T("Réseau"),
        DriverGroup.Bluetooth => "Bluetooth",
        DriverGroup.Storage => T("Stockage"),
        DriverGroup.Usb => "USB",
        DriverGroup.Input => T("Clavier, souris et manettes"),
        DriverGroup.Camera => T("Caméras"),
        DriverGroup.Chipset => T("Chipset, processeur et système"),
        _ => T("Autres"),
    };

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
