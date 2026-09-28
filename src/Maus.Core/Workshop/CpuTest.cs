using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;

namespace Maus.Core.Workshop;

public sealed record CpuTestOptions(TimeSpan Duration, int Threads, CpuStressMode Mode = CpuStressMode.Automatic)
{
    /// <summary>Pour les tests : altère le résultat d'un tour (fil, numéro de tour, résultat) pour simuler une erreur de calcul.</summary>
    internal Func<int, long, ulong, ulong>? Fault { get; init; }
}

public sealed record CpuTestProgress(TimeSpan Elapsed, long Rounds, int Errors);

/// <summary>Résultat d'un test processeur : score (tours de calcul vérifiés par seconde) et erreurs de calcul.</summary>
public sealed record CpuTestResult(int Threads, TimeSpan Duration, long Rounds, int Errors, bool Aborted, string? AbortReason, CpuStressMode Mode = CpuStressMode.Automatic)
{
    /// <summary>Score : tours vérifiés par seconde, arrondi (comparable d'un passage à l'autre sur le même PC).</summary>
    public double Score => Duration.TotalSeconds > 0 ? Math.Round(Rounds / Duration.TotalSeconds, 1) : 0;

    /// <summary>Une seule erreur de calcul suffit à signaler un PC instable.</summary>
    public bool Stable => Errors == 0;
}

/// <summary>
/// Test de stabilité et de performance du processeur, lancé uniquement par l'utilisateur. Chaque tour enchaîne un
/// hachage SHA-256, un calcul de matrices et un crible de nombres premiers (mode automatique), ou une charge vectorielle
/// AVX / AVX-512 (voir <see cref="CpuStress"/>), et compare le résultat à une référence calculée au début : un résultat
/// différent trahit une instabilité (surcadençage, tension trop basse, surchauffe).
/// </summary>
public static class CpuTest
{
    private const int BufferBytes = 64 * 1024;
    private const int MatrixSize = 48;
    private const int SieveLimit = 50_000;

    public static async Task<CpuTestResult> RunAsync(
        CpuTestOptions options,
        IProgress<CpuTestProgress>? progress = null,
        Func<string?>? abortCheck = null,
        CancellationToken cancellationToken = default)
    {
        var fault = options.Fault;
        // Mode « automatique » : le tour mixte ; modes AVX et très lourd : charge vectorielle (voir CpuStress).
        Func<ulong> round = options.Mode == CpuStressMode.Automatic ? () => Round(0) : () => CpuStress.Round(options.Mode);
        var reference = round();
        long rounds = 0;
        var errors = 0;
        string? abortReason = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var clock = Stopwatch.StartNew();

        var workers = Enumerable.Range(0, Math.Max(1, options.Threads)).Select(thread => Task.Factory.StartNew(() =>
        {
            long local = 0;
            while (!stop.IsCancellationRequested && clock.Elapsed < options.Duration)
            {
                var result = round();
                if (fault is not null)
                {
                    result = fault(thread, local, result);
                }

                if (result != reference)
                {
                    Interlocked.Increment(ref errors);
                }

                local++;
                Interlocked.Increment(ref rounds);
            }
        }, stop.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        try
        {
            while (!Task.WhenAll(workers).IsCompleted)
            {
                await Task.WhenAny(Task.WhenAll(workers), Task.Delay(250, CancellationToken.None)).ConfigureAwait(false);
                progress?.Report(new CpuTestProgress(clock.Elapsed, Interlocked.Read(ref rounds), Volatile.Read(ref errors)));
                if (abortReason is null && abortCheck?.Invoke() is { } reason)
                {
                    abortReason = reason;
                    await stop.CancelAsync().ConfigureAwait(false);
                }
            }

            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested || abortReason is not null)
        {
        }
        catch (OperationCanceledException)
        {
            abortReason ??= Localization.Texts.T("Test arrêté par l'utilisateur.");
        }

        clock.Stop();
        var cancelled = cancellationToken.IsCancellationRequested;
        return new CpuTestResult(options.Threads, clock.Elapsed, rounds, errors,
            Aborted: abortReason is not null || cancelled,
            AbortReason: abortReason ?? (cancelled ? Localization.Texts.T("Test arrêté par l'utilisateur.") : null),
            Mode: options.Mode);
    }

    /// <summary>Un tour de calcul déterministe : même entrée, même résultat, sur tout processeur sain.</summary>
    internal static ulong Round(int seed)
    {
        var data = new byte[BufferBytes];
        var state = 0x9E3779B97F4A7C15UL ^ (ulong)seed;
        for (var i = 0; i < data.Length; i += 8)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            BitConverter.TryWriteBytes(data.AsSpan(i), state);
        }

        var hash = SHA256.HashData(data);
        var checksum = BitConverter.ToUInt64(hash, 0);
        checksum ^= Matrix(checksum);
        checksum ^= (ulong)Sieve();
        return checksum;
    }

    private static ulong Matrix(ulong seed)
    {
        var n = MatrixSize;
        var a = new double[n * n];
        var b = new double[n * n];
        var c = new double[n * n];
        for (var i = 0; i < a.Length; i++)
        {
            a[i] = ((seed >> (i % 48)) & 0xFF) / 255.0 + i * 1e-3;
            b[i] = (((ulong)i * 2654435761UL) & 0xFFFF) / 65535.0;
        }

        var width = Vector<double>.Count;
        for (var i = 0; i < n; i++)
        {
            for (var k = 0; k < n; k++)
            {
                var factor = new Vector<double>(a[(i * n) + k]);
                var j = 0;
                for (; j <= n - width; j += width)
                {
                    var target = new Vector<double>(c, (i * n) + j);
                    (target + (factor * new Vector<double>(b, (k * n) + j))).CopyTo(c, (i * n) + j);
                }

                for (; j < n; j++)
                {
                    c[(i * n) + j] += a[(i * n) + k] * b[(k * n) + j];
                }
            }
        }

        double sum = 0;
        foreach (var value in c)
        {
            sum += value;
        }

        return BitConverter.DoubleToUInt64Bits(sum);
    }

    private static int Sieve()
    {
        var composite = new bool[SieveLimit + 1];
        var count = 0;
        for (var i = 2; i <= SieveLimit; i++)
        {
            if (composite[i])
            {
                continue;
            }

            count++;
            for (long j = (long)i * i; j <= SieveLimit; j += i)
            {
                composite[j] = true;
            }
        }

        return count;
    }
}
