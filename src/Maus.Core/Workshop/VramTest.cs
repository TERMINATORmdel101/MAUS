using System.Diagnostics;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Carte graphique proposée au test de la mémoire vidéo (index DXGI, nom, mémoire dédiée).</summary>
public sealed record GpuAdapterInfo(int Index, string Name, long DedicatedBytes);

/// <summary>Cartes graphiques testables et ouverture de leur mémoire (Direct3D 11 sous Windows, faux dans les tests).</summary>
public interface IGpuMemoryProvider
{
    IReadOnlyList<GpuAdapterInfo> Adapters();

    /// <summary>Ouvre la carte ; à appeler depuis le fil qui fera tout le test (contexte Direct3D non partagé).</summary>
    IGpuMemory Open(GpuAdapterInfo adapter);
}

/// <summary>Mémoire d'une carte graphique : des blocs alloués dans sa mémoire dédiée, remplis puis relus par copie.</summary>
public interface IGpuMemory : IDisposable
{
    /// <summary>Alloue un bloc de mémoire vidéo ; <c>null</c> si la carte n'a plus de place.</summary>
    IGpuBlock? TryAllocate(int bytes);
}

/// <summary>Bloc de mémoire vidéo : on y copie des données, puis on les relit.</summary>
public interface IGpuBlock : IDisposable
{
    int Bytes { get; }

    void Upload(ReadOnlySpan<uint> data);

    void Download(Span<uint> data);
}

public sealed record VramTestOptions(long Bytes, int Passes = 1);

public sealed record VramTestProgress(double Percent, string Step, long Errors);

/// <summary>Résultat du test de la mémoire vidéo.</summary>
public sealed record VramTestResult(
    string Adapter,
    long TestedBytes,
    long Errors,
    IReadOnlyList<long> FirstErrorOffsets,
    double? ReadbackGigabytesPerSecond,
    TimeSpan Duration,
    bool Aborted,
    string? AbortReason,
    bool CardWasFull)
{
    public bool Stable => Errors == 0;
}

/// <summary>
/// Test de la mémoire vidéo, lancé uniquement par l'utilisateur : des blocs de la mémoire dédiée de la carte reçoivent des
/// motifs (adresse, bits baladeurs, pseudo-aléatoire, inverse), puis sont relus et comparés. Comme pour la RAM, la mémoire
/// déjà utilisée par l'affichage n'est pas couverte : une erreur est un signal fort, l'absence d'erreur n'est pas une preuve absolue.
/// </summary>
public static class VramTest
{
    public const int BlockBytes = 64 * 1024 * 1024;
    private const int MaxRecordedErrors = 16;

    /// <summary>Quantité proposée : 60 % de la mémoire dédiée, au moins 256 Mo, en laissant 512 Mo à l'affichage.</summary>
    public static long SuggestedBytes(long dedicatedBytes) =>
        Math.Max(256L << 20, Math.Min(dedicatedBytes * 6 / 10, dedicatedBytes - (512L << 20)));

    public static Task<VramTestResult> RunAsync(
        IGpuMemoryProvider provider,
        GpuAdapterInfo adapter,
        VramTestOptions options,
        IProgress<VramTestProgress>? progress = null,
        Func<string?>? danger = null,
        CancellationToken cancellationToken = default) =>
        // Jeton déjà annulé : Run renvoie un résultat « interrompu » au lieu d'une tâche annulée qui ferait planter l'interface.
        Task.Factory.StartNew(() => Run(provider, adapter, options, progress, danger, cancellationToken), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static VramTestResult Run(IGpuMemoryProvider provider, GpuAdapterInfo adapter, VramTestOptions options, IProgress<VramTestProgress>? progress, Func<string?>? danger, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var blocks = new List<IGpuBlock>();
        long errors = 0;
        var offsets = new List<long>();
        string? abortReason = null;
        var cardFull = false;
        double? readback = null;
        IGpuMemory? memory = null;
        try
        {
            memory = provider.Open(adapter);
            for (var remaining = options.Bytes; remaining > 0; remaining -= BlockBytes)
            {
                var size = (int)Math.Min(BlockBytes, remaining) & ~3;
                if (size == 0)
                {
                    break;
                }

                if (memory.TryAllocate(size) is not { } block)
                {
                    cardFull = true;
                    break;
                }

                blocks.Add(block);
            }

            var host = GC.AllocateUninitializedArray<uint>(BlockBytes / 4);
            var patterns = new (string Name, Func<long, uint, uint> Value)[]
            {
                (T("motif d'adresse"), (index, seed) => (uint)index ^ seed),
                (T("bits baladeurs"), (index, _) => 1u << (int)(index % 32)),
                (T("motif pseudo-aléatoire"), (index, seed) => Mix((uint)index + seed)),
                (T("motif inversé"), (index, seed) => ~((uint)index ^ seed)),
            };
            var steps = Math.Max(1, options.Passes * patterns.Length * blocks.Count * 2);
            var step = 0;
            long readBytes = 0;
            var readTime = TimeSpan.Zero;
            for (var pass = 0; pass < options.Passes; pass++)
            {
                var seed = 0x5A5A_A5A5u + (uint)pass;
                for (var p = 0; p < patterns.Length; p++)
                {
                    var (name, value) = patterns[p];

                    // Tout écrire d'abord : les données restent en mémoire vidéo pendant que les autres blocs sont remplis.
                    long baseIndex = 0;
                    foreach (var block in blocks)
                    {
                        Check(danger, cancellationToken, ref abortReason);
                        var span = host.AsSpan(0, block.Bytes / 4);
                        for (var i = 0; i < span.Length; i++)
                        {
                            span[i] = value(baseIndex + i, seed);
                        }

                        block.Upload(span);
                        baseIndex += span.Length;
                        progress?.Report(new VramTestProgress(100.0 * ++step / steps, T("{0} : écriture", name), errors));
                    }

                    baseIndex = 0;
                    foreach (var block in blocks)
                    {
                        Check(danger, cancellationToken, ref abortReason);
                        var span = host.AsSpan(0, block.Bytes / 4);
                        var watch = Stopwatch.StartNew();
                        block.Download(span);
                        readTime += watch.Elapsed;
                        readBytes += block.Bytes;
                        for (var i = 0; i < span.Length; i++)
                        {
                            if (span[i] != value(baseIndex + i, seed))
                            {
                                errors++;
                                if (offsets.Count < MaxRecordedErrors)
                                {
                                    offsets.Add((baseIndex + i) * 4);
                                }
                            }
                        }

                        baseIndex += span.Length;
                        progress?.Report(new VramTestProgress(100.0 * ++step / steps, T("{0} : relecture", name), errors));
                    }
                }
            }

            readback = readTime > TimeSpan.Zero && readBytes > 0 ? readBytes / readTime.TotalSeconds / 1e9 : null;
        }
        catch (OperationCanceledException)
        {
            abortReason ??= T("arrêté par l'utilisateur");
        }
        catch (GpuMemoryException ex)
        {
            abortReason = ex.Message;
        }
        finally
        {
            foreach (var block in blocks)
            {
                block.Dispose();
            }

            memory?.Dispose();
        }

        clock.Stop();
        return new VramTestResult(adapter.Name, blocks.Sum(b => (long)b.Bytes), errors, offsets, abortReason is null ? readback : null, clock.Elapsed, abortReason is not null, abortReason, cardFull);
    }

    private static void Check(Func<string?>? danger, CancellationToken cancellationToken, ref string? abortReason)
    {
        if (danger?.Invoke() is { } alarm)
        {
            abortReason = T("arrêt de sécurité : {0}", alarm);
            throw new OperationCanceledException(abortReason);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Mélange 32 bits (type « lowbias32 ») : motif reproductible sans stockage.</summary>
    private static uint Mix(uint x)
    {
        x ^= x >> 16;
        x *= 0x7FEB352Du;
        x ^= x >> 15;
        x *= 0x846CA68Bu;
        x ^= x >> 16;
        return x;
    }
}

/// <summary>La carte graphique a refusé une opération (pilote réinitialisé, mémoire perdue…) : le test s'arrête.</summary>
public sealed class GpuMemoryException : Exception
{
    public GpuMemoryException()
    {
    }

    public GpuMemoryException(string message)
        : base(message)
    {
    }

    public GpuMemoryException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
