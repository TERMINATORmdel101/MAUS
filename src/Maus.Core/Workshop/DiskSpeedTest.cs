using System.Diagnostics;
using System.Runtime.InteropServices;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

public sealed record DiskSpeedProgress(double Percent, string Step);

/// <summary>Résultat du test de vitesse : lecture et écriture séquentielles (Mo/s), lectures aléatoires de 4 Ko (par seconde, latence).</summary>
public sealed record DiskSpeedResult(string Folder, long TestedBytes, double? WriteMegabytesPerSecond, double? ReadMegabytesPerSecond, double? RandomReadsPerSecond, double? RandomLatencyMilliseconds, bool Aborted);

/// <summary>
/// Test de vitesse du disque, lancé uniquement par l'utilisateur : MAUS écrit un fichier temporaire incompressible, le relit
/// (séquentiel, 4 files en parallèle), puis lit des blocs de 4 Ko au hasard. Sous Windows, le cache est contourné pour mesurer
/// le disque et non la mémoire. Le fichier est supprimé à la fermeture, même en cas d'arrêt.
/// </summary>
public static unsafe class DiskSpeedTest
{
    public const int BlockBytes = 1 << 20;
    private const int RandomBytes = 4096;
    private const int Workers = 4;
    private const FileOptions NoBuffering = (FileOptions)0x20000000;
    private static readonly TimeSpan RandomDuration = TimeSpan.FromSeconds(5);

    public static Task<DiskSpeedResult> RunAsync(string folder, long bytes, IProgress<DiskSpeedProgress>? progress = null, CancellationToken cancellationToken = default) =>
        RunAsync(folder, bytes, RandomDuration, progress, cancellationToken);

    internal static Task<DiskSpeedResult> RunAsync(string folder, long bytes, TimeSpan randomDuration, IProgress<DiskSpeedProgress>? progress, CancellationToken cancellationToken) =>
        Task.Factory.StartNew(() => Run(folder, bytes, randomDuration, progress, cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static DiskSpeedResult Run(string folder, long bytes, TimeSpan randomDuration, IProgress<DiskSpeedProgress>? progress, CancellationToken cancellationToken)
    {
        var blocks = Math.Max(Workers, bytes / BlockBytes);
        var size = blocks * BlockBytes;
        var path = Path.Combine(folder, $"MAUS-test-{Guid.NewGuid():N}.tmp");
        var options = FileOptions.DeleteOnClose | (OperatingSystem.IsWindows() ? FileOptions.WriteThrough | NoBuffering : FileOptions.None);
        var buffers = Enumerable.Range(0, Workers).Select(_ => (nint)NativeMemory.AlignedAlloc(BlockBytes, 4096)).ToArray();
        double? write = null, read = null, iops = null, latency = null;
        try
        {
            foreach (var buffer in buffers)
            {
                Fill(new Span<byte>((void*)buffer, BlockBytes));
            }

            using var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, options, size);
            long done = 0;
            void Report(string step, double share) => progress?.Report(new DiskSpeedProgress(share * 100, step));

            var watch = Stopwatch.StartNew();
            Parallel.For(0, Workers, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = cancellationToken }, worker =>
            {
                var data = new ReadOnlySpan<byte>((void*)buffers[worker], BlockBytes);
                for (var block = worker; block < blocks; block += Workers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RandomAccess.Write(handle, data, block * BlockBytes);
                    var total = Interlocked.Increment(ref done);
                    if (total % 64 == 0)
                    {
                        Report(T("écriture"), 0.4 * total / blocks);
                    }
                }
            });
            RandomAccess.FlushToDisk(handle);
            write = size / watch.Elapsed.TotalSeconds / 1e6;

            done = 0;
            watch.Restart();
            Parallel.For(0, Workers, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = cancellationToken }, worker =>
            {
                var data = new Span<byte>((void*)buffers[worker], BlockBytes);
                for (var block = worker; block < blocks; block += Workers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RandomAccess.Read(handle, data, block * BlockBytes);
                    var total = Interlocked.Increment(ref done);
                    if (total % 64 == 0)
                    {
                        Report(T("lecture"), 0.4 + (0.4 * total / blocks));
                    }
                }
            });
            read = size / watch.Elapsed.TotalSeconds / 1e6;

            var random = new Random(42);
            var small = new Span<byte>((void*)buffers[0], RandomBytes);
            var pages = size / RandomBytes;
            long reads = 0;
            watch.Restart();
            while (watch.Elapsed < randomDuration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RandomAccess.Read(handle, small, random.NextInt64(pages) * RandomBytes);
                if (++reads % 256 == 0)
                {
                    Report(T("accès aléatoires"), 0.8 + (0.2 * watch.Elapsed / randomDuration));
                }
            }

            iops = reads / watch.Elapsed.TotalSeconds;
            latency = watch.Elapsed.TotalMilliseconds / Math.Max(1, reads);
            return new DiskSpeedResult(folder, size, write, read, iops, latency, false);
        }
        catch (OperationCanceledException)
        {
            return new DiskSpeedResult(folder, size, write, read, null, null, true);
        }
        finally
        {
            foreach (var buffer in buffers)
            {
                NativeMemory.AlignedFree((void*)buffer);
            }
        }
    }

    /// <summary>Données pseudo-aléatoires : un SSD qui compresse ne peut pas tricher sur le débit.</summary>
    private static void Fill(Span<byte> data)
    {
        var state = 0x9E3779B97F4A7C15UL;
        var words = MemoryMarshal.Cast<byte, ulong>(data);
        for (var i = 0; i < words.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            words[i] = state;
        }
    }
}
