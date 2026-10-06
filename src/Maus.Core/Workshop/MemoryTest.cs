using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Maus.Core.Workshop;

public sealed record MemoryTestOptions(long Bytes, int Passes = 1)
{
    /// <summary>Pour les tests : altère un bloc après écriture (bloc, numéro de motif) pour simuler une barrette défaillante.</summary>
    internal SpanAction? Fault { get; init; }

    internal delegate void SpanAction(Span<ulong> block, int pattern);
}

public sealed record MemoryTestProgress(double Percent, string Step, long Errors);

/// <summary>Résultat du test de la RAM : erreurs de motif, débit de copie et latence d'accès.</summary>
public sealed record MemoryTestResult(
    long TestedBytes,
    long Errors,
    IReadOnlyList<long> FirstErrorOffsets,
    double? CopyGigabytesPerSecond,
    double? LatencyNanoseconds,
    TimeSpan Duration,
    bool Aborted,
    string? AbortReason = null)
{
    public bool Stable => Errors == 0;
}

/// <summary>
/// Test de la mémoire vive en mode utilisateur, lancé uniquement par l'utilisateur : écriture puis relecture de motifs
/// (adresse, bits baladeurs, pseudo-aléatoire, inverse) sur la quantité choisie. Il ne couvre pas la mémoire déjà utilisée
/// par Windows : une erreur est un signal fort, l'absence d'erreur n'est pas une preuve absolue.
/// </summary>
public static unsafe class MemoryTest
{
    private const long ChunkBytes = 64L * 1024 * 1024;
    private const int MaxRecordedErrors = 16;

    /// <summary>Test plus rapide : la moitié de la mémoire libre, au moins 256 Mo (sans plafond : 16, 24, 32, 64 Go…).</summary>
    public static long SuggestedBytes(long availableBytes) => Math.Max(availableBytes / 2, 256L << 20);

    /// <summary>
    /// Le maximum (demande du porteur) : toute la mémoire libre, quelle que soit la quantité installée, moins une marge que
    /// MAUS laisse à Windows (1 Go, ou un seizième de la mémoire si c'est plus) pour qu'il n'écrive pas sur le disque
    /// pendant le test. Au moins 256 Mo.
    /// </summary>
    public static long MaximumBytes(long availableBytes, long totalBytes) =>
        Math.Max(256L << 20, availableBytes - Math.Max(1L << 30, totalBytes / 16));

    public static Task<MemoryTestResult> RunAsync(
        MemoryTestOptions options,
        IProgress<MemoryTestProgress>? progress = null,
        Func<string?>? abortCheck = null,
        CancellationToken cancellationToken = default) =>
        // Jeton déjà annulé : Run renvoie un résultat « interrompu » au lieu d'une tâche annulée qui ferait planter l'interface.
        Task.Factory.StartNew(() => Run(options, progress, abortCheck, cancellationToken), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static MemoryTestResult Run(MemoryTestOptions options, IProgress<MemoryTestProgress>? progress, Func<string?>? abortCheck, CancellationToken cancellationToken)
    {
        var fault = options.Fault;
        var clock = Stopwatch.StartNew();
        var chunks = new List<(nint Pointer, long Bytes)>();
        long errors = 0;
        var offsets = new List<long>();
        var aborted = false;
        string? abortReason = null;
        try
        {
            for (var remaining = options.Bytes & ~7L; remaining > 0; remaining -= ChunkBytes)
            {
                var size = Math.Min(ChunkBytes, remaining);
                try
                {
                    chunks.Add(((nint)NativeMemory.AlignedAlloc((nuint)size, 64), size));
                }
                catch (OutOfMemoryException)
                {
                    // Windows n'a plus de place (un programme a pris de la mémoire entre-temps) : on teste ce qui a été réservé.
                    break;
                }
            }

            var patterns = new (string Name, Func<long, ulong, ulong> Value)[]
            {
                (Localization.Texts.T("motif d'adresse"), (index, seed) => (ulong)index ^ seed),
                (Localization.Texts.T("bits baladeurs"), (index, _) => 1UL << (int)(index % 64)),
                (Localization.Texts.T("motif pseudo-aléatoire"), (index, seed) => Mix((ulong)index + seed)),
                (Localization.Texts.T("motif inversé"), (index, seed) => ~((ulong)index ^ seed)),
            };
            var steps = options.Passes * patterns.Length;
            var step = 0;
            for (var pass = 0; pass < options.Passes; pass++)
            {
                var seed = 0xA5A5_5A5A_0F0F_F0F0UL + (ulong)pass;
                for (var p = 0; p < patterns.Length; p++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (name, value) = patterns[p];
                    long baseIndex = 0;
                    var chunkIndex = 0;
                    foreach (var (pointer, bytes) in chunks)
                    {
                        // Avancement tous les 1 Go : avec 32 ou 64 Go, un motif dure plus d'une minute.
                        if (++chunkIndex % 16 == 0)
                        {
                            progress?.Report(new MemoryTestProgress(100.0 * (step + ((double)chunkIndex / chunks.Count)) / steps, name, errors));
                        }

                        var span = new Span<ulong>((void*)pointer, (int)(bytes / 8));
                        for (var i = 0; i < span.Length; i++)
                        {
                            span[i] = value(baseIndex + i, seed);
                        }

                        fault?.Invoke(span, p);
                        for (var i = 0; i < span.Length; i++)
                        {
                            if (span[i] != value(baseIndex + i, seed))
                            {
                                errors++;
                                if (offsets.Count < MaxRecordedErrors)
                                {
                                    offsets.Add((baseIndex + i) * 8);
                                }
                            }
                        }

                        baseIndex += span.Length;
                        cancellationToken.ThrowIfCancellationRequested();

                        // Arrêt automatique en cas de surchauffe (alarme « danger » des capteurs), vérifié à chaque bloc de 64 Mo.
                        if (abortCheck?.Invoke() is { } reason)
                        {
                            abortReason = reason;
                            throw new OperationCanceledException(reason);
                        }
                    }

                    step++;
                    progress?.Report(new MemoryTestProgress(100.0 * step / steps, name, errors));
                }
            }
        }
        catch (OperationCanceledException)
        {
            aborted = true;
        }
        finally
        {
            foreach (var (pointer, _) in chunks)
            {
                NativeMemory.AlignedFree((void*)pointer);
            }
        }

        var tested = chunks.Sum(c => c.Bytes);
        var (bandwidth, latency) = aborted ? (null, null) : Measure(Math.Min(tested, 256L << 20));
        clock.Stop();
        return new MemoryTestResult(tested, errors, offsets, bandwidth, latency, clock.Elapsed, aborted, abortReason);
    }

    /// <summary>Débit de copie (Go/s) et latence d'accès aléatoire (ns, parcours de pointeurs dans un grand tableau).</summary>
    private static (double? Bandwidth, double? Latency) Measure(long bytes)
    {
        if (bytes < 16L << 20)
        {
            return (null, null);
        }

        var source = NativeMemory.AlignedAlloc((nuint)bytes, 64);
        var target = NativeMemory.AlignedAlloc((nuint)bytes, 64);
        try
        {
            NativeMemory.Fill(source, (nuint)bytes, 0x5A);
            Buffer.MemoryCopy(source, target, bytes, bytes);
            var clock = Stopwatch.StartNew();
            const int copies = 4;
            for (var i = 0; i < copies; i++)
            {
                Buffer.MemoryCopy(source, target, bytes, bytes);
            }

            var bandwidth = copies * bytes / clock.Elapsed.TotalSeconds / 1e9;

            // Cycle aléatoire sur des lignes de 64 octets : chaque lecture dépend de la précédente.
            var slots = (int)(bytes / 64);
            var order = Enumerable.Range(0, slots).ToArray();
            new Random(42).Shuffle(order);
            var cells = (long*)source;
            for (var i = 0; i < slots; i++)
            {
                cells[order[i] * 8L] = order[(i + 1) % slots] * 8L;
            }

            const int hops = 2_000_000;
            long position = 0;
            clock.Restart();
            for (var i = 0; i < hops; i++)
            {
                position = cells[position];
            }

            var latency = clock.Elapsed.TotalNanoseconds / hops + (position < 0 ? 1 : 0);
            return (Math.Round(bandwidth, 1), Math.Round(latency, 1));
        }
        finally
        {
            NativeMemory.AlignedFree(source);
            NativeMemory.AlignedFree(target);
        }
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 33;
        value *= 0xFF51AFD7ED558CCDUL;
        value ^= value >> 33;
        value *= 0xC4CEB9FE1A85EC53UL;
        return value ^ (value >> 33);
    }
}
