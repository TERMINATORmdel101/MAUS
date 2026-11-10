using System.Text.Json;

namespace Maus.Core.Platform;

/// <summary>
/// Historique dans un fichier JSON du profil (scores, audits gardés, passes du benchmark), à l'abri de <see cref="AtomicFile"/> :
/// il n'est réécrit qu'à partir de ce qui a vraiment été lu. Un fichier momentanément illisible n'est pas touché, un fichier
/// abîmé est copié à côté avant d'être remplacé.
/// </summary>
/// <param name="path">Chemin du fichier.</param>
/// <param name="options">Options de lecture et d'écriture du JSON.</param>
/// <param name="keep">Nombre d'entrées gardées (les plus récentes).</param>
/// <param name="orderBy">Date d'une entrée : le fichier reste trié du plus ancien au plus récent.</param>
/// <param name="unreadable">Message de l'erreur levée quand le fichier est momentanément illisible.</param>
/// <param name="wait">Attente entre deux tentatives de lecture (remplaçable pour les tests).</param>
/// <param name="isValid">JSON valide mais vidé de son contenu : faux, et le fichier est traité comme abîmé.</param>
/// <param name="kept">Choix des entrées gardées, à la place de <c>keep</c>, parmi toutes les entrées triées.</param>
/// <param name="clean">Ce qui est montré d'une entrée relue ; <c>null</c> : elle n'est pas montrée. Le fichier garde l'entrée telle quelle, une horloge remise à l'heure peut la rendre valable.</param>
public sealed class JsonHistoryFile<T>(
    string path,
    JsonSerializerOptions options,
    int keep,
    Func<T, DateTimeOffset> orderBy,
    Func<Exception?, string> unreadable,
    Action<TimeSpan>? wait = null,
    Func<List<T>, bool>? isValid = null,
    Func<IReadOnlyList<T>, IEnumerable<T>>? kept = null,
    Func<T, T?>? clean = null)
    where T : class
{
    private static readonly TimeSpan GateWait = TimeSpan.FromSeconds(5);

    /// <summary>Entrées gardées, de la plus ancienne à la plus récente ; vide si le fichier est absent ou illisible.</summary>
    public IReadOnlyList<T> Load() => TryLoad(out var entries) ? entries : [];

    /// <summary>
    /// Rend faux si le fichier n'a pas vraiment été lu (illisible ou abîmé) : l'absence d'une entrée n'y prouve rien. Un fichier
    /// absent rend vrai et une liste vide.
    /// </summary>
    public bool TryLoad(out IReadOnlyList<T> entries)
    {
        var read = TryReadAll(out var all);
        entries = Shown(all);
        return read;
    }

    /// <summary>Comme <see cref="TryLoad"/>, mais toutes les entrées du fichier, dans son ordre : celles qui ne sont pas montrées existent quand même.</summary>
    public bool TryReadAll(out IReadOnlyList<T> entries)
    {
        var read = AtomicFile.ReadText(path, wait: wait);
        entries = [];
        if (read.State == StoredFileState.Absent)
        {
            return true;
        }

        if (read.State == StoredFileState.Read && Parse(read.Text!) is { } parsed)
        {
            entries = parsed;
            return true;
        }

        return false;
    }

    /// <summary>Un fichier retouché peut être dans le désordre ou sans fin : ce qui est montré reste trié et borné comme ce que MAUS écrit.</summary>
    private List<T> Shown(IEnumerable<T> all)
    {
        var sorted = (clean is null ? all : all.Select(clean).OfType<T>()).OrderBy(orderBy).ToList();
        return [.. kept?.Invoke(sorted) ?? sorted.TakeLast(keep)];
    }

    /// <summary>
    /// Ajoute une entrée ; rend les entrées gardées. Lire, ajouter et réécrire se fait d'un seul tenant, y compris face à un autre
    /// processus de MAUS (audit de la semaine) : deux écritures au même instant ne s'effacent pas.
    /// </summary>
    /// <exception cref="IOException">Fichier momentanément illisible (laissé intact), occupé trop longtemps ou écriture impossible.</exception>
    /// <exception cref="UnauthorizedAccessException">Écriture refusée.</exception>
    public IReadOnlyList<T> Add(T entry)
    {
        using var gate = OpenGate();
        if (gate is null)
        {
            return AddNow(entry);
        }

        if (!Enter(gate))
        {
            throw new IOException(unreadable(null));
        }

        try
        {
            return AddNow(entry);
        }
        finally
        {
            gate.ReleaseMutex();
        }
    }

    /// <summary>Verrou partagé par les processus de cette session ; <c>null</c> s'il ne s'ouvre pas (créé par un processus administrateur) : on écrit comme avant.</summary>
    private Mutex? OpenGate()
    {
        try
        {
            return new Mutex(false, @"Local\MAUS.Historique." + Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            return null;
        }
    }

    private static bool Enter(Mutex gate)
    {
        try
        {
            return gate.WaitOne(GateWait);
        }
        catch (AbandonedMutexException)
        {
            // Le processus qui le tenait est mort : chaque écriture du fichier reste atomique, le verrou est à nous.
            return true;
        }
    }

    private List<T> AddNow(T entry)
    {
        var read = AtomicFile.ReadText(path, wait: wait);
        if (read.State == StoredFileState.Unavailable)
        {
            throw new IOException(unreadable(read.Error), read.Error);
        }

        IReadOnlyList<T> existing = [];
        if (read.State == StoredFileState.Read)
        {
            if (Parse(read.Text!) is { } parsed)
            {
                existing = parsed;
            }
            else
            {
                // Abîmé : une copie reste à côté, l'historique repart de cette entrée.
                AtomicFile.KeepCopy(path, DateTime.Now);
            }
        }

        var all = existing.Append(entry).OrderBy(orderBy).ToList();
        var updated = (kept?.Invoke(all) ?? all.TakeLast(keep)).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(updated, options));

        // L'entrée qui vient d'être écrite reste la même : l'appelant peut la retrouver dans la liste.
        return clean is null ? updated : [.. updated.Select(e => ReferenceEquals(e, entry) ? e : clean(e)).OfType<T>()];
    }

    /// <summary>JSON invalide, ou contenu refusé par <c>isValid</c> : <c>null</c>.</summary>
    private List<T>? Parse(string text)
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<T>>(text, options);
            if (list is null || isValid?.Invoke(list) == false)
            {
                return null;
            }

            list.RemoveAll(e => e is null);
            return list;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
