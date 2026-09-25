using System.Net;
using Maus.Core.Workshop;

namespace Maus.Core.Tests.Workshop;

public class ConnectionTestTests
{
    private static readonly IPAddress Box = IPAddress.Parse("192.168.1.1");

    /// <summary>Réponses scriptées par cible ; au-delà du script, la dernière valeur se répète.</summary>
    private sealed class FakePinger(Dictionary<string, double?[]> script) : IPinger
    {
        private readonly Dictionary<string, int> _next = [];

        public Task<double?> PingAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = address.ToString();
            var values = script.TryGetValue(key, out var v) ? v : [null];
            var index = _next.GetValueOrDefault(key);
            _next[key] = index + 1;
            return Task.FromResult(values[Math.Min(index, values.Length - 1)]);
        }
    }

    private static Task<ConnectionResult> Run(Dictionary<string, double?[]> script, IPAddress? gateway = null, int rounds = 50) =>
        ConnectionTest.RunAsync(new FakePinger(script), gateway ?? Box, rounds, TimeSpan.Zero, null, CancellationToken.None);

    [Fact]
    public void Stats_give_median_jitter_and_losses()
    {
        var stats = PingStats.From("x", Box, [10, 12, null, 14, 30]);

        Assert.Equal(4, stats.Sent - stats.Lost);
        Assert.Equal(20, stats.LossPercent);
        Assert.Equal(13, stats.Median);
        Assert.Equal(30, stats.Worst);
        Assert.Equal((2 + 2 + 16) / 3.0, stats.Jitter!.Value, 6);
    }

    [Fact]
    public async Task Stable_connection_is_good()
    {
        var result = await Run(new() { ["192.168.1.1"] = [1, 2], ["1.1.1.1"] = [12, 13], ["8.8.8.8"] = [15, 14] });

        Assert.Equal(ConnectionVerdict.Good, result.Verdict);
        Assert.Equal("Cloudflare (1.1.1.1)", result.BestInternet!.Label);
        Assert.False(result.Aborted);
        Assert.Equal(50, result.Gateway!.Sent);
    }

    [Fact]
    public async Task Unstable_box_points_to_the_local_network()
    {
        var box = Enumerable.Range(0, 50).Select(i => (double?)(i % 2 == 0 ? 2 : 40)).ToArray();
        var result = await Run(new() { ["192.168.1.1"] = box, ["1.1.1.1"] = box, ["8.8.8.8"] = box });

        Assert.Equal(ConnectionVerdict.LocalNetwork, result.Verdict);
    }

    [Fact]
    public async Task Losses_beyond_a_stable_box_point_to_the_line()
    {
        var lossy = Enumerable.Range(0, 50).Select(i => i % 10 == 0 ? null : (double?)20).ToArray();
        var result = await Run(new() { ["192.168.1.1"] = [1], ["1.1.1.1"] = lossy, ["8.8.8.8"] = lossy });

        Assert.Equal(ConnectionVerdict.Internet, result.Verdict);
        Assert.Equal(10, result.BestInternet!.LossPercent);
    }

    [Fact]
    public async Task One_lossy_server_is_not_enough_to_blame_the_line()
    {
        var lossy = Enumerable.Range(0, 50).Select(i => i % 2 == 0 ? null : (double?)20).ToArray();
        var result = await Run(new() { ["192.168.1.1"] = [1], ["1.1.1.1"] = lossy, ["8.8.8.8"] = [18] });

        Assert.Equal(ConnectionVerdict.Good, result.Verdict);
    }

    [Fact]
    public async Task Box_that_ignores_ping_is_not_blamed()
    {
        var result = await Run(new() { ["192.168.1.1"] = [null], ["1.1.1.1"] = [12], ["8.8.8.8"] = [14] });

        Assert.Equal(ConnectionVerdict.Good, result.Verdict);
        Assert.Contains("aucune réponse", ConnectionTest.Describe(result.Gateway!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_answer_at_all_is_offline()
    {
        var result = await Run(new() { ["192.168.1.1"] = [null] });

        Assert.Equal(ConnectionVerdict.Offline, result.Verdict);
        Assert.NotEmpty(ConnectionTest.Advice(result));
    }

    [Fact]
    public async Task Without_gateway_only_internet_is_measured()
    {
        var result = await ConnectionTest.RunAsync(new FakePinger(new() { ["1.1.1.1"] = [12], ["8.8.8.8"] = [14] }), null, 5, TimeSpan.Zero, null, CancellationToken.None);

        Assert.Null(result.Gateway);
        Assert.Equal(2, result.Internet.Count);
        Assert.Equal(ConnectionVerdict.Good, result.Verdict);
    }

    [Fact]
    public async Task Cancel_stops_and_keeps_what_was_measured()
    {
        using var cancellation = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p >= 20)
            {
                cancellation.Cancel();
            }
        });

        var result = await ConnectionTest.RunAsync(new FakePinger(new() { ["1.1.1.1"] = [12], ["8.8.8.8"] = [14] }), Box, 50, TimeSpan.Zero, progress, cancellation.Token);

        Assert.True(result.Aborted);
        Assert.InRange(result.Internet[0].Sent, 1, 49);
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
