using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Maus.Core.Platform;

/// <summary>Ce que MAUS a trouvé en lisant l'un de ses propres fichiers (choix, journal, historiques).</summary>
public enum StoredFileState
{
    /// <summary>Pas de fichier : premier lancement, ou fichier supprimé. Les valeurs par défaut sont normales.</summary>
    Absent,

    /// <summary>Contenu lu (reste à vérifier qu'il est valide).</summary>
    Read,

    /// <summary>Fichier non fiable (propriétaire étranger, lien) : ignoré exprès, et remplacé au prochain enregistrement.</summary>
    Untrusted,

    /// <summary>Fichier présent mais illisible malgré plusieurs tentatives (occupé par un autre programme, accès refusé).</summary>
    Unavailable,
}

/// <summary>Résultat d'une lecture : l'état, le texte lu, et l'erreur qui a empêché la lecture le cas échéant.</summary>
public sealed record StoredFileRead(StoredFileState State, string? Text = null, Exception? Error = null);

/// <summary>
/// Lectures et écritures sûres des fichiers de MAUS.
/// <list type="bullet">
/// <item>Une lecture ratée (fichier ouvert un instant par un antivirus ou une sauvegarde) est retentée quelques fois, puis
/// signalée comme <see cref="StoredFileState.Unavailable"/> : jamais confondue avec un fichier absent.</item>
/// <item>Une écriture est « tout ou rien » : le nouveau contenu part dans un fichier temporaire du même dossier, écrit
/// directement sur le disque et vidé, puis remplace l'ancien d'un seul coup. Une coupure ou un plantage laisse l'ancien
/// fichier ou le nouveau, jamais un fichier vide ou à moitié écrit.</item>
/// <item>Un fichier abîmé est copié à côté (<c>nom.illisible-AAAAMMJJ-HHMMSS</c>) avant d'être remplacé.</item>
/// </list>
/// </summary>
public static partial class AtomicFile
{
    /// <summary>MoveFileEx : remplacer le fichier de destination s'il existe (documentation Microsoft, MoveFileExW).</summary>
    private const uint MoveFileReplaceExisting = 0x1;

    /// <summary>
    /// MoveFileEx : « la fonction ne rend la main qu'une fois le fichier réellement déplacé sur le disque » (documentation
    /// Microsoft, MoveFileExW). Sans ce drapeau, le remplacement peut rester un moment en mémoire.
    /// </summary>
    private const uint MoveFileWriteThrough = 0x8;

    private const int ErrorAccessDenied = 5;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Attentes entre les tentatives de lecture (un peu plus d'une demi-seconde au total) : un fichier ouvert un instant par
    /// un autre programme est ainsi lu quand même, sans retarder MAUS quand tout va bien.
    /// </summary>
    internal static IReadOnlyList<TimeSpan> RetryDelays { get; } =
        [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(400)];

    /// <summary>Lit un fichier texte, en retentant quelques fois s'il est momentanément inaccessible.</summary>
    /// <param name="isTrusted">Contrôle du propriétaire (fichiers protégés) ; <c>null</c> = aucun contrôle.</param>
    /// <param name="wait">Attente entre deux tentatives (remplaçable pour les tests).</param>
    public static StoredFileRead ReadText(string path, Func<string, bool>? isTrusted = null, Action<TimeSpan>? wait = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return new StoredFileRead(StoredFileState.Absent);
                }

                if (isTrusted is not null && !isTrusted(path))
                {
                    return new StoredFileRead(StoredFileState.Untrusted);
                }

                return new StoredFileRead(StoredFileState.Read, File.ReadAllText(path, Utf8));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Supprimé entre-temps.
                return new StoredFileRead(StoredFileState.Absent);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= RetryDelays.Count)
                {
                    return new StoredFileRead(StoredFileState.Unavailable, Error: ex);
                }

                (wait ?? Thread.Sleep)(RetryDelays[attempt]);
            }
        }
    }

    /// <summary>
    /// Écrit le fichier « tout ou rien » : fichier temporaire du même dossier, écrit directement sur le disque (sans cache) et
    /// vidé, puis mis à la place de l'ancien par MoveFileEx avec MOVEFILE_WRITE_THROUGH.
    /// </summary>
    /// <param name="beforeReplace">Appelé avec le chemin du fichier temporaire juste avant le remplacement (droits, par exemple).</param>
    /// <exception cref="IOException">Écriture ou remplacement impossible : l'ancien fichier est intact.</exception>
    /// <exception cref="UnauthorizedAccessException">Accès refusé : l'ancien fichier est intact.</exception>
    public static void WriteAllText(string path, string contents, Action<string>? beforeReplace = null)
    {
        path = Path.GetFullPath(path);
        var temporary = TemporaryPathFor(path);
        var replaced = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(Utf8.GetBytes(contents));
                stream.Flush(flushToDisk: true);
            }

            beforeReplace?.Invoke(temporary);
            Replace(temporary, path);
            replaced = true;
        }
        finally
        {
            if (!replaced)
            {
                TryDelete(temporary);
            }
        }
    }

    /// <summary>
    /// Garde une copie d'un fichier abîmé (ou écrit par une version plus récente de MAUS) à côté de lui, avant qu'il soit
    /// remplacé : <c>preferences.json.illisible-AAAAMMJJ-HHMMSS</c>. La copie est écrite directement sur le disque.
    /// </summary>
    /// <returns>Chemin de la copie.</returns>
    /// <exception cref="IOException">Copie impossible : l'appelant ne doit alors pas remplacer le fichier.</exception>
    /// <exception cref="UnauthorizedAccessException">Copie impossible.</exception>
    public static string KeepCopy(string path, DateTime now)
    {
        var bytes = File.ReadAllBytes(path);
        var stem = path + ".illisible-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        for (var index = 1; ; index++)
        {
            var copy = index == 1 ? stem : stem + "-" + index.ToString(CultureInfo.InvariantCulture);
            if (File.Exists(copy) && index < 100)
            {
                continue;
            }

            using var stream = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return copy;
        }
    }

    /// <summary>Fichier temporaire du même dossier (le remplacement reste un simple renommage), unique pour chaque écriture.</summary>
    internal static string TemporaryPathFor(string path) => path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";

    private static void Replace(string source, string destination)
    {
        if (!OperatingSystem.IsWindows())
        {
            // Sessions de développement sous Linux seulement : MAUS ne tourne que sous Windows.
            File.Move(source, destination, overwrite: true);
            return;
        }

        if (!MoveFileEx(source, destination, MoveFileReplaceExisting | MoveFileWriteThrough))
        {
            var error = Marshal.GetLastPInvokeError();
            var message = new Win32Exception(error).Message + " (" + destination + ")";
            throw error == ErrorAccessDenied
                ? new UnauthorizedAccessException(message)
                : new IOException(message, unchecked((int)0x80070000) | (error & 0xFFFF));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Un fichier temporaire oublié ne gêne rien : il n'est jamais lu.
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string existingFileName, string newFileName, uint flags);
}
