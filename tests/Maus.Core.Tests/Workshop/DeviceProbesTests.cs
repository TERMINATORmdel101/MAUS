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
    public async Task Real_capacity_removes_what_an_interrupted_test_left_and_never_a_user_file()
    {
        var folder = Directory.CreateTempSubdirectory("maus-capacity").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "MAUS-capacite-0-ancien.tmp"), new byte[4096]);
            File.WriteAllText(Path.Combine(folder, "photo.jpg"), "photo");
            Directory.CreateDirectory(Path.Combine(folder, "System Volume Information"));
            Assert.Single(RealCapacityTest.Leftovers(folder));
            Assert.Equal(1, RealCapacityTest.ExistingEntries(folder));

            var result = await RealCapacityTest.RunAsync(folder, 2L << 20, null, CancellationToken.None);

            Assert.Equal(2L << 20, result.GoodBytes);
            Assert.Equal(0, result.LeftFiles);
            Assert.Empty(RealCapacityTest.Leftovers(folder));
            Assert.True(File.Exists(Path.Combine(folder, "photo.jpg")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Real_capacity_reads_back_what_was_written_before_the_drive_refused_to_write()
    {
        var drive = new FakeDrive(writable: 2L << 20);

        var result = await Run(drive, 4L << 20);

        Assert.True(result.WriteFailed);
        Assert.False(result.ReadFailed);
        Assert.False(result.Aborted);
        Assert.Equal(2L << 20, result.TestedBytes);
        Assert.Equal(2L << 20, result.GoodBytes);
    }

    [Fact]
    public async Task Real_capacity_keeps_what_was_read_before_a_read_error()
    {
        var drive = new FakeDrive(readable: 1L << 20);

        var result = await Run(drive, 3L << 20);

        Assert.True(result.ReadFailed);
        Assert.False(result.WriteFailed);
        Assert.Equal(3L << 20, result.TestedBytes);
        Assert.Equal(1L << 20, result.GoodBytes);
    }

    [Fact]
    public async Task Real_capacity_finds_where_a_fake_drive_stops_keeping_data()
    {
        var drive = new FakeDrive(real: 2L << 20);

        var result = await Run(drive, 4L << 20);

        Assert.False(result.WriteFailed || result.ReadFailed || result.Aborted);
        Assert.Equal(4L << 20, result.TestedBytes);
        Assert.Equal(2L << 20, result.GoodBytes);
    }

    [Fact]
    public async Task Real_capacity_reports_a_drive_that_writes_nothing_as_an_error() =>
        await Assert.ThrowsAsync<IOException>(() => Run(new FakeDrive(writable: 0), 2L << 20));

    private static async Task<CapacityResult> Run(FakeDrive drive, long bytes)
    {
        var folder = Directory.CreateTempSubdirectory("maus-capacity").FullName;
        try
        {
            return await RealCapacityTest.RunAsync(folder, bytes, null, drive.Open, CancellationToken.None);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Faux support : refuse d'écrire au-delà de <c>writable</c> octets, répond par une erreur à la relecture au-delà de <c>readable</c>,
    /// ou, comme une clé contrefaite, ne garde rien au-delà de <c>real</c> octets.
    /// </summary>
    private sealed class FakeDrive(long writable = long.MaxValue, long readable = long.MaxValue, long real = long.MaxValue)
    {
        private readonly long _writable = writable;
        private readonly long _readable = readable;
        private readonly long _real = real;
        private long _written;
        private long _read;

        public Func<string, Stream> Open => _ => new Part(this);

        private sealed class Part(FakeDrive drive) : MemoryStream
        {
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (drive._written + count > drive._writable)
                {
                    throw new IOException("plus de place");
                }

                var kept = drive._written + count <= drive._real ? buffer : new byte[buffer.Length];
                drive._written += count;
                base.Write(kept, offset, count);
            }

            public override int Read(Span<byte> buffer)
            {
                if (drive._read >= drive._readable)
                {
                    throw new IOException("erreur de lecture");
                }

                var count = base.Read(buffer);
                drive._read += count;
                return count;
            }
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
