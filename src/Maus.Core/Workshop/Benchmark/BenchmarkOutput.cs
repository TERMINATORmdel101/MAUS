using System.Text.Json;
using Maus.Core.Diagnostics;
using Maus.Core.Fixes;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop.Benchmark;

/// <summary>Fin d'une passe du benchmark.</summary>
/// <param name="ExitCode">Code de sortie : 0, 3 (carte perdue), 4 (refus de Direct3D ou de Windows), 5 (arrêt de sécurité)…</param>
/// <param name="Report">Passe rendue par le moteur, que la fenêtre garde ; <c>null</c> sans passe, ou si le benchmark l'a gardée lui-même (sans droits).</param>
/// <param name="Image">Image du résultat (PNG) rendue par le moteur ; <c>null</c> sinon.</param>
public sealed record BenchmarkOutcome(int ExitCode, BenchmarkReport? Report = null, byte[]? Image = null);

/// <summary>
/// Le benchmark lancé par le moteur garde ses droits administrateur (température du processeur par PawnIO, pour l'arrêt de
/// sécurité) et n'écrit rien dans le profil de l'utilisateur (0.8.6). Le moteur crée un dossier neuf réservé à SYSTEM et aux
/// Administrateurs, <c>%ProgramData%\MAUS\benchmark\&lt;guid&gt;</c> ; le benchmark y écrit seulement <see cref="ReportFileName"/>
/// et <see cref="ImageFileName"/> (option <c>--output</c>) ; le moteur les relit, bornés et vérifiés, efface le dossier et les rend
/// à la fenêtre, qui les garde dans l'historique sans droits (<see cref="Keep"/>).
/// </summary>
public static class BenchmarkOutput
{
    public const string ReportFileName = "report.json";

    public const string ImageFileName = "share.png";

    /// <summary>Une passe tient en quelques kilo-octets : au-delà de 1 Mo, elle n'a pas été écrite par le benchmark.</summary>
    internal const long MaxReportBytes = 1L << 20;

    /// <summary>L'image du résultat fait moins d'un mégaoctet : au-delà de 16 Mo, elle n'est pas lue.</summary>
    internal const long MaxImageBytes = 16L << 20;

    /// <summary>Un dossier de sortie oublié (moteur arrêté en pleine passe) est effacé à la passe suivante, passé ce délai.</summary>
    private static readonly TimeSpan LeftoverAge = TimeSpan.FromDays(1);

    /// <summary>Signature d'un fichier PNG (spécification PNG du W3C, section 5.2).</summary>
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Dossier des sorties : <c>%ProgramData%\MAUS\benchmark</c>, réservé à SYSTEM et aux Administrateurs.</summary>
    public static string Root => Path.Combine(SystemPaths.MausData, "benchmark");

    /// <summary>Chemin accepté pour <c>--output</c> : un dossier de sortie que le moteur a pu créer, et rien d'autre.</summary>
    public static bool IsValidFolder(string? path) => IsValidFolder(path, Root);

    /// <summary>
    /// Chemin complet déjà normalisé, directement sous <paramref name="root"/>, dont le nom est un GUID de 32 chiffres
    /// hexadécimaux : ni chemin relatif, ni « .. », ni préfixe <c>\\?\</c>, ni flux de données, ni sous-dossier.
    /// </summary>
    internal static bool IsValidFolder(string? path, string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (string.IsNullOrEmpty(path) || path.Length > 240 || path.Any(char.IsControl) || path.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetFullPath(path), path, StringComparison.Ordinal))
            {
                return false;
            }

            var name = Path.GetFileName(path);
            return string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase)
                && name.Length == 32 && Guid.TryParseExact(name, "N", out _);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Le dossier de sortie existe toujours et n'est pas un lien : sinon le benchmark n'y écrit rien, et ne le recrée pas.</summary>
    internal static bool IsExistingFolder(string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            return info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Moteur : crée un dossier de sortie neuf, réservé à SYSTEM et aux Administrateurs, avec les contrôles de
    /// <see cref="WindowsDirectoryProtector"/> (dossiers au-dessus, lien refusé, dossier préexistant mis de côté).
    /// </summary>
    /// <exception cref="JournalUnsafeException">Le dossier ne peut pas être réservé : le benchmark ne part pas.</exception>
    public static string CreateFolder()
    {
        var protector = new WindowsDirectoryProtector();
        var root = Root;
        protector.EnsureProtected(root);
        DeleteLeftovers(root);
        var folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        protector.EnsureProtected(folder);
        return folder;
    }

    /// <summary>Benchmark : écrit la passe dans le dossier de sortie, et nulle part ailleurs ; un fichier déjà là est refusé.</summary>
    /// <exception cref="IOException">Écriture impossible.</exception>
    /// <exception cref="UnauthorizedAccessException">Écriture refusée.</exception>
    public static void WriteReport(string folder, BenchmarkReport report)
    {
        using var stream = new FileStream(Path.Combine(folder, ReportFileName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, report, BenchmarkHistoryStore.Json);
    }

    /// <summary>
    /// Moteur, après la fin du benchmark : la passe (1 Mo au plus, relue dans le type fixe <see cref="BenchmarkReport"/> et
    /// contrôlée comme une passe de l'historique) et son image (16 Mo au plus, signature PNG), seulement s'il s'agit de fichiers
    /// ordinaires. Ce qui ne passe pas ces contrôles ne repart pas vers la fenêtre.
    /// </summary>
    public static BenchmarkOutcome Read(string folder, int exitCode)
    {
        var report = ParseReport(ReadOrdinaryFile(Path.Combine(folder, ReportFileName), MaxReportBytes));
        var image = report is not null && ReadOrdinaryFile(Path.Combine(folder, ImageFileName), MaxImageBytes) is { } bytes && IsPng(bytes) ? bytes : null;
        return new BenchmarkOutcome(exitCode, report, image);
    }

    /// <summary>La passe, si elle est valable et contient au moins un test ; son chemin d'image, propre au dossier de sortie, est retiré.</summary>
    internal static BenchmarkReport? ParseReport(byte[]? bytes)
    {
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BenchmarkReport>(bytes, BenchmarkHistoryStore.Json) is { } report
                && BenchmarkHistoryStore.Clean(report) is { Tests.Count: > 0 } clean
                ? clean with { Image = null }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool IsPng(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return bytes.Length > PngSignature.Length && bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature);
    }

    /// <summary>Fichier ordinaire et borné : ni lien (point d'analyse), ni dossier, ni second nom sur le disque, ni trop gros.</summary>
    internal static bool IsOrdinary(FileAttributes attributes, uint links, long length, long maxBytes) =>
        (attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0 && links == 1 && length >= 0 && length <= maxBytes;

    /// <summary>
    /// Le contenu d'un fichier ordinaire, ou <c>null</c> : absent, lien, plusieurs noms, trop gros ou illisible. Le lien se voit
    /// sur le chemin, avant l'ouverture (qui le suivrait) ; le nombre de noms et la taille se relisent sur la poignée.
    /// </summary>
    internal static byte[]? ReadOrdinaryFile(string path, long maxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            if (info.LinkTarget is not null || !IsOrdinary(info.Attributes, 1, info.Length, maxBytes))
            {
                Breadcrumbs.Add("benchmark : fichier de sortie refusé (lien ou trop gros)");
                return null;
            }

            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var (attributes, links) = AtomicFile.Describe(handle);
            var length = RandomAccess.GetLength(handle);
            if (!IsOrdinary(attributes, links, length, maxBytes))
            {
                Breadcrumbs.Add("benchmark : fichier de sortie refusé (plusieurs noms ou trop gros)");
                return null;
            }

            var bytes = new byte[length];
            var read = 0;
            while (read < bytes.Length)
            {
                var count = RandomAccess.Read(handle, bytes.AsSpan(read), read);
                if (count == 0)
                {
                    return null;
                }

                read += count;
            }

            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Breadcrumbs.Add("benchmark : fichier de sortie illisible (" + ex.GetType().Name + ")");
            return null;
        }
    }

    /// <summary>Moteur : efface le dossier de sortie (dossier réservé ; un lien qui s'y trouverait est effacé, jamais suivi).</summary>
    public static void Delete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Breadcrumbs.Add("benchmark : dossier de sortie pas encore effacé (" + ex.GetType().Name + ")");
        }
    }

    /// <summary>Dossiers de sortie laissés par un moteur arrêté en pleine passe : seulement les noms de GUID, vieux d'un jour.</summary>
    private static void DeleteLeftovers(string root)
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(folder);
                if (name.Length == 32 && Guid.TryParseExact(name, "N", out _) && DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) > LeftoverAge)
                {
                    Delete(folder);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Breadcrumbs.Add("benchmark : anciens dossiers de sortie non effacés (" + ex.GetType().Name + ")");
        }
    }

    /// <summary>
    /// Fenêtre : garde la passe rendue par le moteur, avec son image (<see cref="BenchmarkHistoryStore.ImagePathFor"/>). Une
    /// fenêtre lancée en administrateur ne l'écrit pas : aucun processus élevé n'écrit dans le profil de l'utilisateur.
    /// </summary>
    /// <returns><c>null</c> si la passe est gardée ou s'il n'y a rien à garder ; sinon, ce qu'il faut dire à l'utilisateur.</returns>
    public static string? Keep(BenchmarkOutcome outcome, BenchmarkHistoryStore store, bool elevated)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(store);
        if (outcome.Report is not { } report)
        {
            return null;
        }

        if (elevated)
        {
            return T("MAUS a été lancé en administrateur : cette passe n'est pas gardée dans l'historique, car MAUS n'écrit rien dans vos fichiers avec ces droits. Rouvrez MAUS normalement pour garder vos passes.");
        }

        string? image = null;
        if (outcome.Image is { Length: <= (int)MaxImageBytes } bytes && IsPng(bytes))
        {
            var path = store.ImagePathFor(report.Date);
            try
            {
                Directory.CreateDirectory(store.ImagesFolder);
                File.WriteAllBytes(path, bytes);
                image = path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Sans image, la passe garde ses scores.
                Breadcrumbs.Add("benchmark : image du résultat non écrite (" + ex.GetType().Name + ")");
            }
        }

        try
        {
            store.Add(report with { Image = image });
            store.PruneImages();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Breadcrumbs.Add("benchmark : passe non enregistrée (" + ex.GetType().Name + ")");
            return T("L'historique de MAUS est illisible : cette passe n'y a pas été enregistrée.");
        }
    }
}
