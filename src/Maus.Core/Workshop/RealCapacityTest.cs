using System.Runtime.InteropServices;

namespace Maus.Core.Workshop;

public sealed record CapacityProgress(bool Writing, long Bytes, long Total);

public sealed record CapacityResult(long TestedBytes, long GoodBytes, bool Aborted);

/// <summary>
/// Vraie capacité d'un support : écrit des blocs tous différents sur la place libre, les relit sans passer par le cache de Windows, puis
/// retire ses fichiers. Une clé contrefaite annonce plus qu'elle ne contient : ce qu'elle a « écrit » en trop se relit faux. Les fichiers
/// déjà présents ne sont jamais touchés.
/// </summary>
public static class RealCapacityTest
{
    private const int BlockBytes = 1 << 20;
    private const int BlocksPerFile = 256;
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    /// <summary>Place laissée libre à la fin : Windows en a besoin pour écrire le répertoire.</summary>
    private const long Margin = 16L << 20;

    public static long Testable(string root) => Math.Max(0, new DriveInfo(root).AvailableFreeSpace - Margin) / BlockBytes * BlockBytes;

    public static Task<CapacityResult> RunAsync(string folder, long bytes, IProgress<CapacityProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => Run(folder, bytes, progress, cancellationToken), CancellationToken.None);

    private static CapacityResult Run(string folder, long bytes, IProgress<CapacityProgress>? progress, CancellationToken cancellationToken)
    {
        // Le cache de Windows étant contourné, le tampon doit commencer sur une frontière de secteur.
        var storage = GC.AllocateUninitializedArray<byte>(BlockBytes + 4096, pinned: true);
        var offset = (int)(4096 - (Marshal.UnsafeAddrOfPinnedArrayElement(storage, 0) % 4096));
        var expected = new byte[BlockBytes];
        var blocks = bytes / BlockBytes;
        var files = new List<string>();
        long written = 0;
        long good = 0;
        var aborted = false;
        try
        {
            for (long block = 0; block < blocks && !cancellationToken.IsCancellationRequested; block += BlocksPerFile)
            {
                var path = Path.Combine(folder, $"MAUS-capacite-{files.Count}-{Guid.NewGuid():N}.tmp");
                files.Add(path);
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 0, NoBuffering | FileOptions.WriteThrough);
                for (var i = block; i < Math.Min(blocks, block + BlocksPerFile) && !cancellationToken.IsCancellationRequested; i++)
                {
                    new Random((int)i).NextBytes(expected);
                    expected.CopyTo(storage, offset);
                    stream.Write(storage, offset, BlockBytes);
                    written += BlockBytes;
                    progress?.Report(new CapacityProgress(true, written, bytes));
                }
            }

            long index = 0;
            foreach (var path in files)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 0, NoBuffering | FileOptions.SequentialScan);
                while (index * BlockBytes < written && !cancellationToken.IsCancellationRequested && stream.ReadAtLeast(storage.AsSpan(offset, BlockBytes), BlockBytes, throwOnEndOfStream: false) == BlockBytes)
                {
                    new Random((int)index).NextBytes(expected);
                    if (!storage.AsSpan(offset, BlockBytes).SequenceEqual(expected))
                    {
                        return new CapacityResult(written, good, false);
                    }

                    good += BlockBytes;
                    index++;
                    progress?.Report(new CapacityProgress(false, good, written));
                }
            }

            aborted = cancellationToken.IsCancellationRequested;
        }
        catch (IOException) when (written > 0 && good == 0)
        {
            // Support plein ou retiré pendant l'écriture : on juge sur ce qui a pu être écrit.
            aborted = true;
        }
        finally
        {
            foreach (var path in files)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Diagnostics.Breadcrumbs.Add("capacité : fichier temporaire non retiré");
                }
            }
        }

        return new CapacityResult(written, good, aborted);
    }
}

/// <summary>Résultat du test de refroidissement : température au repos, pic sous charge et retour au calme.</summary>
public sealed record CoolingResult(double IdleC, double PeakC, double? BackInSeconds)
{
    /// <summary>« Revenu au calme » : à 5 °C ou moins de la température de repos (marge choisie par MAUS).</summary>
    public const double CalmMarginC = 5;

    public double RiseC => PeakC - IdleC;

    public static CoolingResult? From(IReadOnlyList<double> idle, IReadOnlyList<double> load, IReadOnlyList<(double Seconds, double C)> after)
    {
        if (idle.Count == 0 || load.Count == 0)
        {
            return null;
        }

        var rest = idle.Average();
        var back = after.Where(a => a.C <= rest + CalmMarginC).Select(a => (double?)a.Seconds).FirstOrDefault();
        return new CoolingResult(rest, load.Max(), back);
    }
}
