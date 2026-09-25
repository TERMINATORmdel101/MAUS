using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Envoi d'un « ping » : temps de réponse en millisecondes, ou <c>null</c> si la réponse n'est pas arrivée.</summary>
public interface IPinger
{
    Task<double?> PingAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Ping ICMP de .NET (aucune donnée personnelle : 32 octets de remplissage).</summary>
public sealed class SystemPinger : IPinger
{
    public async Task<double?> PingAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(address, timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (PingException)
        {
            return null;
        }
    }
}

/// <summary>Mesures vers une cible : latence médiane, gigue (écart moyen entre deux réponses successives), pertes.</summary>
public sealed record PingStats(string Label, IPAddress Address, int Sent, int Lost, double? Median, double? Jitter, double? Worst)
{
    public double LossPercent => Sent == 0 ? 0 : 100.0 * Lost / Sent;

    public bool Answered => Sent > Lost;

    public static PingStats From(string label, IPAddress address, IReadOnlyList<double?> replies)
    {
        var times = replies.Where(r => r is not null).Select(r => r!.Value).ToList();
        if (times.Count == 0)
        {
            return new(label, address, replies.Count, replies.Count, null, null, null);
        }

        var sorted = times.Order().ToList();
        var median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
        double? jitter = times.Count < 2 ? null : times.Zip(times.Skip(1), (a, b) => Math.Abs(b - a)).Average();
        return new(label, address, replies.Count, replies.Count - times.Count, median, jitter, sorted[^1]);
    }
}

public enum ConnectionVerdict
{
    /// <summary>Connexion stable.</summary>
    Good,

    /// <summary>Souci entre le PC et la box (Wi-Fi, câble, box).</summary>
    LocalNetwork,

    /// <summary>Souci au-delà de la box (ligne, fournisseur d'accès).</summary>
    Internet,

    /// <summary>Aucune réponse d'Internet (hors ligne, ou ping bloqué par un pare-feu).</summary>
    Offline,
}

public sealed record ConnectionResult(PingStats? Gateway, IReadOnlyList<PingStats> Internet, ConnectionVerdict Verdict, bool Aborted)
{
    /// <summary>Meilleure des cibles Internet : le moins de pertes, puis la latence la plus basse.</summary>
    public PingStats? BestInternet => Internet
        .OrderBy(s => s.LossPercent)
        .ThenBy(s => s.Median ?? double.MaxValue)
        .FirstOrDefault();
}

/// <summary>
/// Test de connexion : des pings vers la box (passerelle) et vers deux serveurs publics, en parallèle, pour dire si un
/// souci vient du réseau local (Wi-Fi, câble, box) ou d'au-delà (ligne, fournisseur d'accès). Ne change aucun réglage.
/// </summary>
public static class ConnectionTest
{
    /// <summary>Pertes à partir desquelles la liaison est jugée instable (2 %, soit une réponse perdue sur 50).</summary>
    public const double LossThreshold = 2.0;

    /// <summary>Box : au-delà de 10 ms de latence ou de gigue, le réseau local freine (en Ethernet, moins de 1 ms).</summary>
    public const double LocalLatencyThreshold = 10.0;

    /// <summary>Internet : au-delà de 15 ms de gigue, les jeux en ligne et les appels vidéo saccadent.</summary>
    public const double InternetJitterThreshold = 15.0;

    /// <summary>Internet : au-delà de 80 ms de latence médiane vers un serveur public proche, la ligne est lente (hors satellite).</summary>
    public const double InternetLatencyThreshold = 80.0;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    /// <summary>Serveurs publics très répandus, qui répondent au ping : Cloudflare et Google.</summary>
    public static IReadOnlyList<(string Label, IPAddress Address)> InternetTargets { get; } =
    [
        ("Cloudflare (1.1.1.1)", IPAddress.Parse("1.1.1.1")),
        ("Google (8.8.8.8)", IPAddress.Parse("8.8.8.8")),
    ];

    /// <summary>Passerelle IPv4 de la carte réseau active (la box), ou <c>null</c>.</summary>
    public static IPAddress? DefaultGateway()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    public static async Task<ConnectionResult> RunAsync(
        IPinger pinger,
        IPAddress? gateway,
        int rounds,
        TimeSpan interval,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var targets = new List<(string Label, IPAddress Address)>();
        if (gateway is not null)
        {
            targets.Add((T("Box (passerelle {0})", gateway), gateway));
        }

        targets.AddRange(InternetTargets);
        var replies = targets.Select(_ => new List<double?>()).ToList();
        var aborted = false;
        for (var round = 0; round < rounds; round++)
        {
            try
            {
                var answers = await Task.WhenAll(targets.Select(t => pinger.PingAsync(t.Address, Timeout, cancellationToken))).ConfigureAwait(false);
                for (var i = 0; i < answers.Length; i++)
                {
                    replies[i].Add(answers[i]);
                }

                progress?.Report(100.0 * (round + 1) / rounds);
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                aborted = true;
                break;
            }
        }

        var stats = targets.Select((t, i) => PingStats.From(t.Label, t.Address, replies[i])).ToList();
        var gatewayStats = gateway is null ? null : stats[0];
        var internet = gateway is null ? stats : stats.Skip(1).ToList();
        return new ConnectionResult(gatewayStats, internet, Judge(gatewayStats, internet), aborted);
    }

    /// <summary>Une ligne de mesures, par exemple « Google (8.8.8.8) : 14 ms (pire 31 ms), gigue 2,1 ms, pertes 0 % ».</summary>
    public static string Describe(PingStats stats) => stats.Answered
        ? T("{0} : {1:0} ms (pire {2:0} ms), gigue {3:0.#} ms, pertes {4:0.#} %", stats.Label, stats.Median, stats.Worst, stats.Jitter ?? 0, stats.LossPercent)
        : T("{0} : aucune réponse", stats.Label);

    /// <summary>Ce qu'il faut en retenir et quoi faire, sans promettre de miracle.</summary>
    public static string Advice(ConnectionResult result) => result.Verdict switch
    {
        ConnectionVerdict.Good => T("Connexion stable. Pour jouer ou appeler en vidéo, c'est la régularité qui compte : peu de gigue et aucune perte."),
        ConnectionVerdict.LocalNetwork => T("Le souci est entre le PC et la box : en Wi-Fi, rapprochez-vous de la box ou passez en 5 GHz ; "
            + "sinon essayez un câble Ethernet, un autre port, ou redémarrez la box. Certaines box répondent lentement au ping "
            + "quand elles sont occupées : refaites le test pour confirmer."),
        ConnectionVerdict.Internet => T("Le réseau local va bien : le souci est au-delà de la box (ligne ou fournisseur d'accès). "
            + "Un téléchargement en cours (mise à jour, jeu, vidéo) peut aussi provoquer ces écarts. Refaites le test à un autre moment ; "
            + "si cela persiste, signalez-le à votre fournisseur avec ces chiffres."),
        _ => T("Aucune réponse d'Internet : le PC semble hors ligne, ou un pare-feu bloque le ping. "
            + "Vérifiez la connexion dans Paramètres > Réseau et Internet."),
    };

    public static string References => T("Repères (latence) : fibre souvent 5 à 20 ms, ADSL 20 à 50 ms, 4G/5G 30 à 60 ms, Starlink 25 à 60 ms. "
        + "Ce test mesure la stabilité, pas le débit.");

    internal static ConnectionVerdict Judge(PingStats? gateway, IReadOnlyList<PingStats> internet)
    {
        var best = internet.OrderBy(s => s.LossPercent).ThenBy(s => s.Median ?? double.MaxValue).FirstOrDefault();
        if (best is null || !best.Answered)
        {
            return gateway is { Answered: true } && IsUnstable(gateway, LocalLatencyThreshold, LocalLatencyThreshold)
                ? ConnectionVerdict.LocalNetwork
                : ConnectionVerdict.Offline;
        }

        if (gateway is { Answered: true } && IsUnstable(gateway, LocalLatencyThreshold, LocalLatencyThreshold))
        {
            return ConnectionVerdict.LocalNetwork;
        }

        return IsUnstable(best, InternetLatencyThreshold, InternetJitterThreshold) ? ConnectionVerdict.Internet : ConnectionVerdict.Good;
    }

    private static bool IsUnstable(PingStats stats, double latency, double jitter) =>
        stats.LossPercent >= LossThreshold || stats.Median > latency || stats.Jitter > jitter;
}
