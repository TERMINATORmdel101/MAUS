using Maus.Core.Workshop;

namespace Maus.Core.Tests.Workshop;

public class DeviceProbesTests
{
    [Fact]
    public void A_left_tone_stays_silent_on_the_right()
    {
        var wave = TestTone.Wave(left: true, right: false);

        Assert.Equal("RIFF"u8.ToArray(), wave[..4]);
        var samples = wave.AsSpan(44);
        var left = 0;
        for (var i = 0; i + 3 < samples.Length; i += 4)
        {
            left = Math.Max(left, Math.Abs((int)BitConverter.ToInt16(samples[i..])));
            Assert.Equal(0, BitConverter.ToInt16(samples[(i + 2)..]));
        }

        Assert.True(left > 1000);
    }

    [Fact]
    public void Mouse_rate_keeps_the_best_second()
    {
        var rate = new MouseRate();
        for (var i = 0; i < 125; i++)
        {
            rate.Add(1000 + (i * 8));
        }

        rate.Add(5000);
        Assert.Equal(125, rate.Best);
    }

    [Fact]
    public void Two_clicks_too_close_count_as_a_bounce()
    {
        var bounce = new ClickBounce();
        bounce.Add(1000);
        bounce.Add(1000 + ClickBounce.LimitMilliseconds - 1);
        bounce.Add(2000);

        Assert.Equal(1, bounce.Count);
    }

    [Theory]
    [InlineData(80, 76, 10, 190)]
    [InlineData(80, 80, 10, null)]
    public void Battery_time_left_follows_the_drop(int start, int end, int minutes, int? expected)
    {
        var left = BatteryProbe.Remaining(start, end, TimeSpan.FromMinutes(minutes));

        Assert.Equal(expected, left is { } value ? (int)value.TotalMinutes : null);
    }

    [Fact]
    public async Task Real_capacity_reads_back_what_it_wrote_and_removes_its_files()
    {
        var folder = Directory.CreateTempSubdirectory("maus-capacity").FullName;
        try
        {
            var result = await RealCapacityTest.RunAsync(folder, 3L << 20, null, CancellationToken.None);

            Assert.Equal(3L << 20, result.TestedBytes);
            Assert.Equal(3L << 20, result.GoodBytes);
            Assert.False(result.Aborted);
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Cooling_result_finds_the_rise_and_the_way_back()
    {
        var cooling = CoolingResult.From([40, 42], [70, 85, 80], [(10, 60), (40, 46), (60, 44)]);

        Assert.NotNull(cooling);
        Assert.Equal(41, cooling.IdleC);
        Assert.Equal(44, cooling.RiseC);
        Assert.Equal(40, cooling.BackInSeconds);
        Assert.Null(CoolingResult.From([], [70], []));
    }
}
