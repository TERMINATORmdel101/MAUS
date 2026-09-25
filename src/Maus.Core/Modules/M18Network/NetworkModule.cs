using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M18Network;

/// <summary>
/// Module 18 — Réseau : vitesse réellement négociée par le câble Ethernet, qualité et norme du Wi-Fi. Lecture seule, rien
/// n'est envoyé sur Internet. Un câble abîmé ou de mauvaise catégorie bloque souvent le lien à 100 Mb/s au lieu de 1 000.
/// </summary>
public sealed class NetworkModule : IAuditModule
{
    internal const string AdaptersQuery =
        "SELECT Name, InterfaceDescription, MediaConnectState, ReceiveLinkSpeed, NdisPhysicalMedium, HardwareInterface FROM MSFT_NetAdapter";

    private const long Ethernet = 14;
    private const long Wireless = 9;
    private const long Gigabit = 1_000_000_000;

    private readonly IWifiSource _wifi;

    public NetworkModule()
        : this(new WindowsWifiSource())
    {
    }

    internal NetworkModule(IWifiSource wifi) => _wifi = wifi;

    private static string Category => T("Réseau");

    public string Id => "M18";

    public string Title => T("Réseau");

    public int Order => 180;

    public Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<CimRow> adapters;
        try
        {
            adapters = context.Cim.Query(AdaptersQuery, CimScopes.StandardCimv2);
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or DataSourceUnavailableException)
        {
            return Result(Finding.Unknown("M18.connection", T("Connexion réseau"), T("La liste des cartes réseau n'a pas pu être lue : {0}", ex.Message), Category));
        }

        var connected = adapters
            .Where(a => a.GetInt64("MediaConnectState") == 1 && a.GetBool("HardwareInterface") != false)
            .ToList();
        var findings = new List<Finding>();
        foreach (var adapter in connected.Where(a => a.GetInt64("NdisPhysicalMedium") == Ethernet))
        {
            findings.Add(DescribeEthernet(adapter));
        }

        if (connected.Any(a => a.GetInt64("NdisPhysicalMedium") == Wireless))
        {
            findings.Add(DescribeWifi(connected.First(a => a.GetInt64("NdisPhysicalMedium") == Wireless)));
        }

        if (findings.Count == 0)
        {
            findings.Add(new Finding
            {
                Id = "M18.connection",
                Title = T("Connexion réseau"),
                Category = Category,
                Status = FindingStatus.Info,
                Current = T("aucune carte réseau physique connectée"),
                Explanation = T("Aucun câble Ethernet ni Wi-Fi n'est connecté en ce moment (ou seulement des cartes virtuelles, VPN par exemple) : rien à vérifier."),
            });
        }

        return Result([.. findings]);
    }

    private static Task<IReadOnlyList<Finding>> Result(params Finding[] findings) => Task.FromResult<IReadOnlyList<Finding>>(findings);

    private static Finding DescribeEthernet(CimRow adapter)
    {
        var name = adapter.GetString("Name") ?? "Ethernet";
        var speed = adapter.GetInt64("ReceiveLinkSpeed") ?? 0;
        var slow = speed is > 0 and < Gigabit;
        var severity = speed is > 0 and <= 10_000_000 ? Severity.Medium : Severity.Low;
        return new Finding
        {
            Id = "M18.ethernet." + Slug(name),
            Title = T("Câble Ethernet : vitesse du lien ({0})", name),
            Category = Category,
            Status = speed == 0 ? FindingStatus.Unknown : slow ? FindingStatusExtensions.ForDeviation(severity) : FindingStatus.Ok,
            Severity = severity,
            Current = speed == 0 ? T("vitesse non communiquée") : Rate(speed),
            Expected = T("1 Gb/s ou plus (carte gigabit)"),
            Explanation = T("C'est la vitesse négociée entre la carte réseau et la box ou le commutateur. Presque toutes les cartes depuis 2010 montent à 1 Gb/s : un lien à 100 Mb/s trahit un câble abîmé ou de catégorie trop ancienne (4 fils au lieu de 8), une prise mal enfoncée ou un port limité. Cela ne gêne que si votre connexion Internet dépasse 100 Mb/s, ou pour les copies entre PC.")
                + (adapter.GetString("InterfaceDescription") is { } description ? " " + T("Carte : {0}.", description) : string.Empty),
            Advice = slow ? T("Essayez un autre câble (catégorie 5e ou 6, les plus courants aujourd'hui), enfoncez bien les deux prises, puis un autre port de la box. Le lien se renégocie tout seul.") : null,
        };
    }

    private Finding DescribeWifi(CimRow adapter)
    {
        var name = adapter.GetString("Name") ?? "Wi-Fi";
        var link = _wifi.Current();
        var speed = adapter.GetInt64("ReceiveLinkSpeed") ?? 0;
        if (link is null)
        {
            return new Finding
            {
                Id = "M18.wifi",
                Title = T("Qualité du Wi-Fi"),
                Category = Category,
                Status = FindingStatus.Info,
                Current = speed > 0 ? T("connecté à {0}", Rate(speed)) : T("connecté"),
                Explanation = T("Le détail du signal Wi-Fi n'a pas pu être lu (service de configuration automatique WLAN arrêté ?)."),
            };
        }

        var weak = link.SignalPercent < 50;
        var old = link.PhyType is > 0 and < 7;
        var details = new List<string> { T("signal {0} %", link.SignalPercent) };
        if (link.Standard is { } standard)
        {
            details.Add(standard);
        }

        if (link.Band is { } band)
        {
            details.Add(band);
        }

        details.Add(T("{0} Mb/s", Math.Max(link.ReceiveMbps, link.TransmitMbps)));
        var advice = new List<string>();
        if (weak)
        {
            advice.Add(T("Signal faible : rapprochez-vous de la box, évitez les murs épais et les meubles métalliques, ou ajoutez un répéteur ou un système mesh."));
        }

        if (link.Band == "2,4 GHz")
        {
            advice.Add(T("Vous êtes en 2,4 GHz : si la box le propose, la bande 5 GHz est plus rapide et moins encombrée à courte distance."));
        }

        if (old)
        {
            advice.Add(T("Norme Wi-Fi ancienne : une carte ou une clé Wi-Fi 6 récente (quelques dizaines d'euros) améliore nettement le débit."));
        }

        return new Finding
        {
            Id = "M18.wifi",
            Title = T("Qualité du Wi-Fi ({0})", name),
            Category = Category,
            Status = weak || old ? FindingStatusExtensions.ForDeviation(Severity.Low) : FindingStatus.Ok,
            Severity = Severity.Low,
            Current = string.Join(" · ", details),
            Expected = T("signal de 50 % ou plus"),
            Explanation = T("Un signal faible fait chuter le débit et monter la latence (le « ping » en jeu). Pour jouer en ligne, un câble Ethernet reste plus stable que le meilleur Wi-Fi."),
            Advice = advice.Count == 0 ? null : string.Join(" ", advice),
        };
    }

    private static string Rate(long bitsPerSecond) => bitsPerSecond >= Gigabit
        ? T("{0:0.#} Gb/s", bitsPerSecond / 1e9)
        : T("{0:0} Mb/s", bitsPerSecond / 1e6);

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars).Trim('-');
        return slug.Length == 0 ? "x" : slug;
    }
}
