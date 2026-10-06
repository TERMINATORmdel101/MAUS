using Maus.Core.Workshop;

namespace Maus.Core.Tests.Workshop;

public class VramTestTests
{
    /// <summary>Carte simulée : mémoire en RAM, capacité limitée, bit éventuellement collé à 1 dans un bloc.</summary>
    private sealed class FakeGpu(long capacity, int? faultyBlock = null) : IGpuMemoryProvider, IGpuMemory
    {
        private long _used;
        private int _blocks;

        public bool Disposed { get; private set; }

        public IReadOnlyList<GpuAdapterInfo> Adapters() => [new GpuAdapterInfo(0, "Carte de test", capacity)];

        public IGpuMemory Open(GpuAdapterInfo adapter) => this;

        public IGpuBlock? TryAllocate(int bytes)
        {
            if (_used + bytes > capacity)
            {
                return null;
            }

            _used += bytes;
            return new Block(bytes, _blocks++ == faultyBlock);
        }

        public void Dispose() => Disposed = true;

        private sealed class Block(int bytes, bool faulty) : IGpuBlock
        {
            private readonly uint[] _data = new uint[bytes / 4];

            public int Bytes { get; } = bytes;

            public void Upload(ReadOnlySpan<uint> data) => data.CopyTo(_data);

            public void Download(Span<uint> data)
            {
                _data.CopyTo(data);
                if (faulty)
                {
                    data[123] |= 0x10;
                }
            }

            public void Dispose()
            {
            }
        }
    }

    private static readonly GpuAdapterInfo Adapter = new(0, "Carte de test", 8L << 20);

    [Fact]
    public async Task Healthy_memory_passes_every_pattern()
    {
        var gpu = new FakeGpu(8L << 20);

        var result = await VramTest.RunAsync(gpu, Adapter, new VramTestOptions(4L << 20));

        Assert.True(result.Stable);
        Assert.False(result.Aborted);
        Assert.Equal(4L << 20, result.TestedBytes);
        Assert.NotNull(result.ReadbackGigabytesPerSecond);
        Assert.True(gpu.Disposed);
    }

    [Fact]
    public async Task Stopping_before_the_start_gives_an_interrupted_result_not_an_exception()
    {
        // Bouton « Arrêter le test » cliqué pendant la préparation : le jeton est déjà annulé au lancement.
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        var result = await VramTest.RunAsync(new FakeGpu(8L << 20), Adapter, new VramTestOptions(4L << 20), cancellationToken: stop.Token);

        Assert.True(result.Aborted);
    }

    [Fact]
    public async Task A_stuck_bit_is_reported_with_its_offset()
    {
        var gpu = new FakeGpu(8L << 20, faultyBlock: 0);

        var result = await VramTest.RunAsync(gpu, Adapter, new VramTestOptions(1L << 20));

        Assert.False(result.Stable);
        Assert.Contains(123L * 4, result.FirstErrorOffsets);
    }

    [Fact]
    public async Task A_full_card_limits_the_test_instead_of_failing()
    {
        var gpu = new FakeGpu(VramTest.BlockBytes);

        var result = await VramTest.RunAsync(gpu, Adapter, new VramTestOptions(3L * VramTest.BlockBytes));

        Assert.True(result.CardWasFull);
        Assert.Equal(VramTest.BlockBytes, result.TestedBytes);
        Assert.True(result.Stable);
    }

    [Fact]
    public async Task A_danger_alarm_stops_the_test()
    {
        var result = await VramTest.RunAsync(new FakeGpu(8L << 20), Adapter, new VramTestOptions(2L << 20), danger: () => "carte graphique à 95 °C");

        Assert.True(result.Aborted);
        Assert.Contains("95 °C", result.AbortReason, StringComparison.Ordinal);
        Assert.Null(result.ReadbackGigabytesPerSecond);
    }

    [Fact]
    public async Task Cancelling_stops_the_test()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await VramTest.RunAsync(new FakeGpu(8L << 20), Adapter, new VramTestOptions(2L << 20), cancellationToken: CancellationToken.None, danger: () =>
        {
            cancellation.Token.ThrowIfCancellationRequested();
            return null;
        });

        Assert.True(result.Aborted);
    }

    [Theory]
    [InlineData(8L << 30, 4_294_967_296L * 6 / 5)]
    [InlineData(1L << 30, 536_870_912L)]
    [InlineData(512L << 20, 268_435_456L)]
    public void Suggested_size_keeps_room_for_the_display(long dedicated, long expected)
    {
        Assert.Equal(expected, VramTest.SuggestedBytes(dedicated));
    }

    [Theory]
    [InlineData(11L << 30, 10L << 30, (10L << 30) - (256L << 20))]
    [InlineData(24L << 30, 30L << 30, (24L << 30) - (256L << 20))]
    [InlineData(16L << 30, null, (16L << 30) - (16L << 30) / 10)]
    [InlineData(4L << 30, null, (4L << 30) - (512L << 20))]
    [InlineData(1L << 30, 100L << 20, 256L << 20)]
    public void Maximum_size_uses_what_windows_leaves_free_on_the_card(long dedicated, long? available, long expected)
    {
        Assert.Equal(expected, VramTest.MaximumBytes(dedicated, available));
    }
}
