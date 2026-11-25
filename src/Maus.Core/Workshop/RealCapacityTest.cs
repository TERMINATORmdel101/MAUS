using System.Runtime.InteropServices;

namespace Maus.Core.Workshop;

public sealed record CapacityProgress(bool Writing, long Bytes, long Total);

/// <summary>Résultat du test de vraie capacité.</summary>
/// <param name="TestedBytes">Octets écrits (moins que prévu si le support a refusé d'écrire).</param>
/// <param name="GoodBytes">Octets relus à l'identique, depuis le début.</param>
/// <param name="Aborted">Arrêté par l'utilisateur.</param>
public sealed record CapacityResult(long TestedBytes, long GoodBytes, bool Aborted)
{
    /// <summary>Le support a répondu par une erreur avant d'avoir écrit tout ce qui était prévu.</summary>
    public bool WriteFailed { get; init; }

    /// <summary>Le support a répondu par une erreur pendant la relecture.</summary>
    public bool ReadFailed { get; init; }

    /// <summary>Fichiers « MAUS-capacite » restés sur le support : MAUS n'a pas pu les retirer.</summary>
    public int LeftFiles { get; init; }
}

/// <summary>
/// Vraie capacité d'un support : écrit des blocs tous différents sur la place libre, les relit sans passer par le cache de Windows, puis
/// retire ses fichiers. Une clé contrefaite annonce plus qu'elle ne contient : ce qu'elle a « écrit » en trop se relit faux. MAUS n'ouvre
/// ni ne modifie les fichiers déjà présents ; mais une clé contrefaite peut ranger ce qu'elle reçoit au-delà de sa vraie capacité par-dessus
/// ce qu'elle contient déjà : remplir sa place libre peut alors abîmer ces fichiers. La confirmation le dit et demande une clé vide ou
/// sauvegardée. Une erreur du support pendant l'écriture ou la relecture donne un verdict, avec ce qui a été mesuré jusque-là.
/// </summary>
public static class RealCapacityTest
{
    private const int BlockBytes = 1 << 20;
    private const int BlocksPerFile = 256;
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    /// <summary>Nom des fichiers du test, à la racine du support.</summary>
    internal const string FilePattern = "MAUS-capacite-*.tmp";

    /// <summary>Place laissée libre à la fin : Windows en a besoin pour écrire le répertoire.</summary>
    private const long Margin = 16L << 20;

    /// <summary>Dossiers que Windows crée lui-même à la racine d'un support : ce ne sont pas des fichiers de l'utilisateur.</summary>
    private static readonly string[] SystemEntries = ["System Volume Information", "$RECYCLE.BIN"];

    /// <summary>Place à tester : la place libre, plus celle des fichiers laissés par un test interrompu, qui seront retirés au début du test.</summary>
    public static long Testable(string root) =>
        Math.Max(0, new DriveInfo(root).AvailableFreeSpace + Leftovers(root).Sum(SizeOf) - Margin) / BlockBytes * BlockBytes;

    /// <summary>Fichiers « MAUS-capacite » laissés par un test interrompu (clé retirée, PC éteint pendant le test).</summary>
    public static IReadOnlyList<string> Leftovers(string folder)
    {
        try
        {
            return [.. Directory.EnumerateFiles(folder, FilePattern, SearchOption.TopDirectoryOnly)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Fichiers et dossiers déjà sur le support, hors ceux que Windows crée lui-même et hors les restes d'un test interrompu : s'il y en a,
    /// la confirmation le dit. <c>-1</c> si la racine n'a pas pu être lue.
    /// </summary>
    public static int ExistingEntries(string root)
    {
        try
        {
            var leftovers = Leftovers(root).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Count(name => !SystemEntries.Contains(name, StringComparer.OrdinalIgnoreCase) && !leftovers.Contains(name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    public static Task<CapacityResult> RunAsync(string folder, long bytes, IProgress<CapacityProgress>? progress, CancellationToken cancellationToken) =>
        RunAsync(folder, bytes, progress, Open, cancellationToken);

    /// <param name="open">Crée un fichier du test (remplaçable dans les tests, pour simuler un support qui répond mal).</param>
    internal static Task<CapacityResult> RunAsync(string folder, long bytes, IProgress<CapacityProgress>? progress, Func<string, Stream> open, CancellationToken cancellationToken) =>
        Task.Run(() => Run(folder, bytes, progress, open, cancellationToken), CancellationToken.None);

    /// <summary>
    /// Écriture et relecture par le même accès, sans cache de Windows. Le fichier disparaît quand il est fermé : MAUS fermé ou arrêté
    /// pendant le test ne laisse rien derrière lui (seuls une clé retirée ou un PC éteint le peuvent, d'où le ménage du début).
    /// </summary>
    private static FileStream Open(string path) =>
        new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 0, NoBuffering | FileOptions.WriteThrough | FileOptions.DeleteOnClose);

    private static CapacityResult Run(string folder, long bytes, IProgress<CapacityProgress>? progress, Func<string, Stream> open, CancellationToken cancellationToken)
    {
        // Restes d'un test interrompu : ils prennent la place à tester (Testable la compte déjà comme libre).
        Remove(Leftovers(folder));

        // Le cache de Windows étant contourné, le tampon doit commencer sur une frontière de secteur.
        var storage = GC.AllocateUninitializedArray<byte>(BlockBytes + 4096, pinned: true);
        var offset = (int)(4096 - (Marshal.UnsafeAddrOfPinnedArrayElement(storage, 0) % 4096));
        var expected = new byte[BlockBytes];
        var blocks = bytes / BlockBytes;
        var files = new List<(string Path, Stream Stream)>();
        long written = 0;
        long good = 0;
        bool writeFailed = false, readFailed = false;
        try
        {
            try
            {
                for (long block = 0; block < blocks && !cancellationToken.IsCancellationRequested; block += BlocksPerFile)
                {
                    var path = Path.Combine(folder, $"MAUS-capacite-{files.Count}-{Guid.NewGuid():N}.tmp");
                    var stream = open(path);
                    files.Add((path, stream));
                    for (var i = block; i < Math.Min(blocks, block + BlocksPerFile) && !cancellationToken.IsCancellationRequested; i++)
                    {
                        new Random((int)i).NextBytes(expected);
                        expected.CopyTo(storage, offset);
                        stream.Write(storage, offset, BlockBytes);
                        written += BlockBytes;
                        progress?.Report(new CapacityProgress(true, written, bytes));
                    }
                }
            }
            catch (IOException) when (written > 0)
            {
                // Le support refuse d'écrire dans la place qu'il annonçait libre (ou il a été retiré) : la relecture dit ce qui tient.
                writeFailed = true;
            }

            try
            {
                long index = 0;
                foreach (var (_, stream) in files)
                {
                    stream.Seek(0, SeekOrigin.Begin);
                    while (index * BlockBytes < written && !cancellationToken.IsCancellationRequested && stream.ReadAtLeast(storage.AsSpan(offset, BlockBytes), BlockBytes, throwOnEndOfStream: false) == BlockBytes)
                    {
                        new Random((int)index).NextBytes(expected);
                        if (!storage.AsSpan(offset, BlockBytes).SequenceEqual(expected))
                        {
                            return Result();
                        }

                        good += BlockBytes;
                        index++;
                        progress?.Report(new CapacityProgress(false, good, written));
                    }
                }
            }
            catch (IOException)
            {
                // Erreur de lecture : ce qui a été relu juste avant compte, la suite non.
                readFailed = true;
            }

            return Result();
        }
        finally
        {
            Close();
        }

        // Résultat lu après la fermeture des fichiers : ce qui reste sur le support est compté pour de vrai.
        CapacityResult Result()
        {
            Close();
            return new CapacityResult(written, good, cancellationToken.IsCancellationRequested)
            {
                WriteFailed = writeFailed,
                ReadFailed = readFailed,
                LeftFiles = Remove(files.Select(f => f.Path).Where(File.Exists)),
            };
        }

        void Close()
        {
            foreach (var (_, stream) in files)
            {
                try
                {
                    stream.Dispose();
                }
                catch (IOException)
                {
                    // Support retiré : le fichier reste, il sera retiré au début du test suivant.
                }
            }
        }
    }

    /// <summary>Retire des fichiers du test ; renvoie le nombre de ceux qui restent.</summary>
    private static int Remove(IEnumerable<string> paths)
    {
        var left = 0;
        foreach (var path in paths.ToList())
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                left++;
                Diagnostics.Breadcrumbs.Add("capacité : fichier temporaire non retiré");
            }
        }

        return left;
    }

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
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
