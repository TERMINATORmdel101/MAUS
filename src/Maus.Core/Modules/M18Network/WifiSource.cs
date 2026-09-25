using System.Runtime.InteropServices;
using System.Text;

namespace Maus.Core.Modules.M18Network;

/// <summary>Connexion Wi-Fi en cours : nom du réseau, signal (0 à 100), débits négociés, norme et canal.</summary>
public sealed record WifiLink(string Ssid, int SignalPercent, int ReceiveMbps, int TransmitMbps, int PhyType, int? Channel)
{
    /// <summary>Nom commercial de la norme (Wi-Fi 4 à 7) d'après le type de couche physique.</summary>
    public string? Standard => PhyType switch
    {
        4 => "802.11a",
        5 => "802.11b",
        6 => "802.11g",
        7 => "Wi-Fi 4 (802.11n)",
        8 => "Wi-Fi 5 (802.11ac)",
        10 => "Wi-Fi 6 (802.11ax)",
        11 => "Wi-Fi 7 (802.11be)",
        _ => null,
    };

    /// <summary>Bande de fréquence : 5 GHz si le canal ou la norme l'imposent, 2,4 GHz pour les canaux 1 à 14 hors Wi-Fi 6/7 (ambigu avec le 6 GHz).</summary>
    public string? Band => Channel switch
    {
        > 14 => "5 GHz",
        >= 1 when PhyType is 4 or 8 => "5 GHz",
        >= 1 when PhyType is not (10 or 11) => "2,4 GHz",
        _ => null,
    };
}

public interface IWifiSource
{
    /// <summary>Connexion Wi-Fi active, ou <c>null</c> (pas de Wi-Fi, non connecté, service arrêté).</summary>
    WifiLink? Current();
}

/// <summary>Lecture par l'API Wlan de Windows (wlanapi.dll), sans rien envoyer sur le réseau.</summary>
public sealed unsafe partial class WindowsWifiSource : IWifiSource
{
    private const int CurrentConnection = 7;
    private const int ChannelNumber = 8;
    private const int Connected = 1;

    public WifiLink? Current()
    {
        try
        {
            if (WlanOpenHandle(2, 0, out _, out var handle) != 0)
            {
                return null;
            }

            try
            {
                if (WlanEnumInterfaces(handle, 0, out var list) != 0)
                {
                    return null;
                }

                try
                {
                    // WLAN_INTERFACE_INFO_LIST : nombre (4), index (4), puis WLAN_INTERFACE_INFO de 532 octets (GUID, description, état).
                    var count = *(uint*)list;
                    for (var i = 0; i < count; i++)
                    {
                        var info = (byte*)list + 8 + (i * 532);
                        if (*(int*)(info + 528) != Connected)
                        {
                            continue;
                        }

                        var guid = *(Guid*)info;
                        if (WlanQueryInterface(handle, &guid, CurrentConnection, 0, out _, out var data, out _) != 0)
                        {
                            continue;
                        }

                        try
                        {
                            // WLAN_CONNECTION_ATTRIBUTES : SSID à 520 (longueur puis 32 octets), type de couche physique à 568,
                            // qualité du signal à 576, débits en kbit/s à 580 (réception) et 584 (émission).
                            var bytes = (byte*)data;
                            var length = Math.Min(32, *(int*)(bytes + 520));
                            var ssid = Encoding.UTF8.GetString(bytes + 524, Math.Max(0, length));
                            var signal = *(int*)(bytes + 576);
                            var channel = WlanQueryInterface(handle, &guid, ChannelNumber, 0, out _, out var channelData, out _) == 0 ? ReadChannel(channelData) : null;
                            return signal is >= 0 and <= 100
                                ? new WifiLink(ssid, signal, *(int*)(bytes + 580) / 1000, *(int*)(bytes + 584) / 1000, *(int*)(bytes + 568), channel)
                                : null;
                        }
                        finally
                        {
                            WlanFreeMemory(data);
                        }
                    }

                    return null;
                }
                finally
                {
                    WlanFreeMemory(list);
                }
            }
            finally
            {
                _ = WlanCloseHandle(handle, 0);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static int? ReadChannel(nint data)
    {
        var channel = *(int*)data;
        WlanFreeMemory(data);
        return channel is > 0 and < 256 ? channel : null;
    }

    [LibraryImport("wlanapi.dll")]
    private static partial int WlanOpenHandle(int clientVersion, nint reserved, out int negotiatedVersion, out nint handle);

    [LibraryImport("wlanapi.dll")]
    private static partial int WlanCloseHandle(nint handle, nint reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial int WlanEnumInterfaces(nint handle, nint reserved, out nint interfaceList);

    [LibraryImport("wlanapi.dll")]
    private static partial int WlanQueryInterface(nint handle, Guid* interfaceGuid, int opcode, nint reserved, out int dataSize, out nint data, out int valueType);

    [LibraryImport("wlanapi.dll")]
    private static partial void WlanFreeMemory(nint memory);
}
