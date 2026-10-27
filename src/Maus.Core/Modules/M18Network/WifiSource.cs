using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Maus.Core.Modules.M18Network;

/// <summary>Connexion Wi-Fi en cours : nom du réseau, signal (0 à 100), débits négociés, norme et canal.</summary>
public sealed record WifiLink(string Ssid, int SignalPercent, int ReceiveMbps, int TransmitMbps, int PhyType, int? Channel)
{
    /// <summary>Fréquence centrale du point d'accès (MHz), lue dans la liste des réseaux visibles ; elle donne la bande sans ambiguïté.</summary>
    public int? FrequencyMhz { get; init; }

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

    /// <summary>
    /// Bande de fréquence : d'après la fréquence quand elle est connue ; sinon 5 GHz si le canal ou la norme l'imposent,
    /// 2,4 GHz pour les canaux 1 à 14 hors Wi-Fi 6/7 (ambigu avec le 6 GHz).
    /// </summary>
    public string? Band => WifiChannels.Band(FrequencyMhz) ?? Channel switch
    {
        > 14 => "5 GHz",
        >= 1 when PhyType is 4 or 8 => "5 GHz",
        >= 1 when PhyType is not (10 or 11) => "2,4 GHz",
        _ => null,
    };
}

/// <summary>Un point d'accès visible : adresse matérielle, signal et fréquence. Le nom du réseau n'est pas gardé (vie privée des voisins).</summary>
public sealed record WifiAccessPoint(string Bssid, int SignalPercent, int FrequencyMhz)
{
    public int? Channel => WifiChannels.Channel(FrequencyMhz);

    public string? Band => WifiChannels.Band(FrequencyMhz);
}

/// <summary>Résultat d'une lecture : la connexion et les points d'accès visibles, ou le refus de Windows faute d'accès à la position.</summary>
public sealed record WifiReading(WifiLink? Link, IReadOnlyList<WifiAccessPoint> Nearby, bool LocationDenied)
{
    public static WifiReading None { get; } = new(null, [], false);
}

public interface IWifiSource
{
    /// <summary>Connexion Wi-Fi active (ou <c>null</c> : pas de Wi-Fi, non connecté, service arrêté) et réseaux visibles.</summary>
    WifiReading Read();
}

/// <summary>
/// Canal et bande d'après la fréquence centrale (IEEE 802.11-2020, annexe E, reprise dans « List of WLAN channels ») :
/// 2,4 GHz canal = (f − 2407) / 5 et 14 = 2484 MHz ; 5 GHz canal = (f − 5000) / 5 ; 6 GHz canal = (f − 5950) / 5.
/// </summary>
public static class WifiChannels
{
    public static int? Channel(int? mhz) => mhz switch
    {
        2484 => 14,
        >= 2412 and <= 2472 when (mhz - 2407) % 5 == 0 => (mhz - 2407) / 5,
        >= 5150 and <= 5895 when mhz % 5 == 0 => (mhz - 5000) / 5,
        >= 5955 and <= 7115 when (mhz - 5950) % 5 == 0 => (mhz - 5950) / 5,
        _ => null,
    };

    public static string? Band(int? mhz) => mhz switch
    {
        >= 2400 and < 2500 => "2,4 GHz",
        >= 5150 and <= 5895 => "5 GHz",
        >= 5925 and <= 7125 => "6 GHz",
        _ => null,
    };
}

/// <summary>
/// Lecture par l'API Wlan de Windows (wlanapi.dll), sans rien envoyer sur le réseau ni lancer de recherche (liste en cache de Windows).
/// Depuis Windows 11 24H2, la connexion en cours et la liste des réseaux exigent l'accès à la position précise : sans lui,
/// Windows répond ERROR_ACCESS_DENIED (Microsoft Learn, « Changes to API behavior for Wi-Fi access and location »).
/// </summary>
public sealed unsafe partial class WindowsWifiSource : IWifiSource
{
    private const int CurrentConnection = 7;
    private const int ChannelNumber = 8;
    private const int Connected = 1;
    private const int AccessDenied = 5;
    private const int AnyBssType = 3;

    public WifiReading Read()
    {
        try
        {
            if (WlanOpenHandle(2, 0, out _, out var handle) != 0)
            {
                return WifiReading.None;
            }

            try
            {
                return ReadInterfaces(handle);
            }
            finally
            {
                _ = WlanCloseHandle(handle, 0);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return WifiReading.None;
        }
    }

    private static WifiReading ReadInterfaces(nint handle)
    {
        if (WlanEnumInterfaces(handle, 0, out var list) != 0)
        {
            return WifiReading.None;
        }

        try
        {
            // WLAN_INTERFACE_INFO_LIST : nombre (4), index (4), puis WLAN_INTERFACE_INFO de 532 octets (GUID, description, état).
            var count = Math.Min(*(uint*)list, 64u);
            var denied = false;
            for (var i = 0; i < count; i++)
            {
                var info = (byte*)list + 8 + (i * 532);
                if (*(int*)(info + 528) != Connected)
                {
                    continue;
                }

                var guid = *(Guid*)info;
                var nearby = ReadNearby(handle, &guid, ref denied);
                var result = WlanQueryInterface(handle, &guid, CurrentConnection, 0, out _, out var data, out _);
                if (result == AccessDenied)
                {
                    denied = true;
                    continue;
                }

                if (result != 0)
                {
                    continue;
                }

                try
                {
                    // WLAN_CONNECTION_ATTRIBUTES : SSID à 520 (longueur puis 32 octets), adresse du point d'accès à 560,
                    // type de couche physique à 568, qualité du signal à 576, débits en kbit/s à 580 (réception) et 584 (émission).
                    var bytes = (byte*)data;
                    var length = Math.Min(32, *(int*)(bytes + 520));
                    var ssid = Encoding.UTF8.GetString(bytes + 524, Math.Max(0, length));
                    var bssid = WifiBssList.FormatBssid(new ReadOnlySpan<byte>(bytes + 560, 6));
                    var signal = *(int*)(bytes + 576);
                    var channel = WlanQueryInterface(handle, &guid, ChannelNumber, 0, out _, out var channelData, out _) == 0 ? ReadChannel(channelData) : null;
                    if (signal is < 0 or > 100)
                    {
                        return new WifiReading(null, nearby, denied);
                    }

                    var link = new WifiLink(ssid, signal, *(int*)(bytes + 580) / 1000, *(int*)(bytes + 584) / 1000, *(int*)(bytes + 568), channel)
                    {
                        FrequencyMhz = nearby.FirstOrDefault(p => p.Bssid == bssid)?.FrequencyMhz,
                    };
                    return new WifiReading(link, nearby.Where(p => p.Bssid != bssid).ToList(), denied);
                }
                finally
                {
                    WlanFreeMemory(data);
                }
            }

            return new WifiReading(null, [], denied);
        }
        finally
        {
            WlanFreeMemory(list);
        }
    }

    /// <summary>Points d'accès en cache pour cette carte (WlanGetNetworkBssList, sans nom de réseau ni type imposé).</summary>
    private static List<WifiAccessPoint> ReadNearby(nint handle, Guid* guid, ref bool denied)
    {
        var result = WlanGetNetworkBssList(handle, guid, 0, AnyBssType, 0, 0, out var list);
        if (result == AccessDenied)
        {
            denied = true;
            return [];
        }

        if (result != 0)
        {
            return [];
        }

        try
        {
            var total = *(uint*)list;
            return WifiBssList.Parse(new ReadOnlySpan<byte>((void*)list, (int)Math.Min(total, 1u << 24)));
        }
        finally
        {
            WlanFreeMemory(list);
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
    private static partial int WlanGetNetworkBssList(nint handle, Guid* interfaceGuid, nint ssid, int bssType, int securityEnabled, nint reserved, out nint bssList);

    [LibraryImport("wlanapi.dll")]
    private static partial void WlanFreeMemory(nint memory);
}

/// <summary>
/// Lecture de WLAN_BSS_LIST (wlanapi.h) : taille totale (4), nombre (4), puis des WLAN_BSS_ENTRY de 360 octets en 64 bits :
/// adresse du point d'accès à 40 (6 octets), qualité du lien à 60 (0 à 100), fréquence centrale à 92 (kHz).
/// </summary>
public static class WifiBssList
{
    internal const int EntrySize = 360;
    private const int MaxEntries = 1024;

    public static List<WifiAccessPoint> Parse(ReadOnlySpan<byte> list)
    {
        var found = new List<WifiAccessPoint>();
        if (list.Length < 8)
        {
            return found;
        }

        var count = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(list[4..]), MaxEntries);
        for (var i = 0; i < count; i++)
        {
            var start = 8 + (i * EntrySize);
            if (start + EntrySize > list.Length)
            {
                break;
            }

            var entry = list.Slice(start, EntrySize);
            var quality = BinaryPrimitives.ReadInt32LittleEndian(entry[60..]);
            var mhz = (int)(BinaryPrimitives.ReadUInt32LittleEndian(entry[92..]) / 1000);
            if (quality is >= 0 and <= 100 && mhz > 0)
            {
                found.Add(new WifiAccessPoint(FormatBssid(entry.Slice(40, 6)), quality, mhz));
            }
        }

        return found;
    }

    public static string FormatBssid(ReadOnlySpan<byte> address) => Convert.ToHexString(address);
}
