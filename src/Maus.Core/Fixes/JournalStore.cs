using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Fixes;

/// <summary>Le journal ne peut pas être protégé : MAUS refuse alors toute correction.</summary>
public sealed class JournalUnsafeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Un fichier de séance existe mais reste illisible malgré plusieurs tentatives (occupé par un autre programme, accès
/// refusé) : MAUS ne le modifie pas.
/// </summary>
public sealed class JournalUnreadableException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>Séances lisibles, et nombre de fichiers de séance laissés de côté parce qu'ils n'ont pas pu être lus.</summary>
public sealed record JournalListing(IReadOnlyList<JournalSession> Sessions, int Unreadable)
{
    /// <summary>Explication à afficher quand des séances n'ont pas pu être lues ; <c>null</c> sinon.</summary>
    public string? UnreadableNotice => Unreadable == 0
        ? null
        : T("{0} séance(s) du journal n'ont pas pu être lues (fichier occupé par un autre programme, abîmé ou écrit par une version plus récente de MAUS) : MAUS les laisse intactes. Réessayez dans un instant.", Unreadable);
}

/// <summary>Stockage des séances de corrections.</summary>
public interface IJournalStore
{
    /// <summary>Enregistre (ou réenregistre) la séance. Appelé avant chaque écriture, pour survivre à un plantage.</summary>
    /// <exception cref="JournalUnsafeException">Le dossier du journal n'est pas sûr, ou le fichier ne peut pas être remplacé sans risque.</exception>
    void Save(JournalSession session);

    /// <returns>La séance, ou <c>null</c> si elle est absente, abîmée, écrite par une version plus récente de MAUS ou non fiable.</returns>
    /// <exception cref="JournalUnreadableException">Le fichier existe mais reste illisible.</exception>
    JournalSession? Load(string id);

    /// <summary>Séances lisibles, de la plus récente à la plus ancienne.</summary>
    IReadOnlyList<JournalSession> List();

    /// <summary>Comme <see cref="List"/>, avec le nombre de séances qui n'ont pas pu être lues.</summary>
    JournalListing Browse() => new(List(), 0);
}

/// <summary>Protège le dossier du journal pour qu'un utilisateur standard ne puisse pas y injecter une valeur.</summary>
public interface IDirectoryProtector
{
    /// <summary>Crée le dossier si besoin et le réserve à SYSTEM et aux Administrateurs.</summary>
    /// <exception cref="JournalUnsafeException">Dossier impossible à sécuriser (lien symbolique, propriétaire étranger…).</exception>
    void EnsureProtected(string directory);

    /// <summary>Le fichier appartient-il à SYSTEM ou aux Administrateurs ?</summary>
    bool IsTrusted(string file);

    /// <summary>
    /// Donne le fichier aux Administrateurs, quel que soit le réglage « propriétaire par défaut des objets créés
    /// par les administrateurs » : sans cela, <see cref="IsTrusted"/> pourrait écarter une séance légitime.
    /// </summary>
    void ProtectFile(string file);
}

/// <summary>
/// Journal JSON, un fichier par séance, sous <c>%ProgramData%\MAUS\journal</c>. Il n'est jamais écrasé ni perdu à cause
/// d'une lecture ratée : une séance illisible n'est ni rejouée ni réécrite, et chaque écriture est « tout ou rien »
/// (<see cref="AtomicFile"/>).
/// </summary>
/// <param name="wait">Attente entre deux tentatives de lecture (remplaçable pour les tests).</param>
public sealed class FileJournalStore(string directory, IDirectoryProtector protector, Action<TimeSpan>? wait = null) : IJournalStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Séances lues ou écrites par cette instance : leur fichier peut être remplacé sans nouvelle vérification.</summary>
    private readonly ConcurrentDictionary<string, bool> _known = new(StringComparer.Ordinal);

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MAUS", "journal");

    public string Directory { get; } = directory;

    public static FileJournalStore CreateDefault() => new(DefaultDirectory, new WindowsDirectoryProtector());

    public void Save(JournalSession session)
    {
        protector.EnsureProtected(Directory);
        var path = PathOf(session.Id);
        if (!_known.ContainsKey(session.Id))
        {
            GuardExisting(session.Id, path);
        }

        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(session, JsonOptions), protector.ProtectFile);
        _known[session.Id] = true;
    }

    public JournalSession? Load(string id)
    {
        if (!IsValidId(id))
        {
            return null;
        }

        var read = AtomicFile.ReadText(PathOf(id), protector.IsTrusted, wait);
        if (read.State == StoredFileState.Unavailable)
        {
            throw new JournalUnreadableException(
                T("La séance {0} du journal est momentanément illisible ({1}). MAUS ne la modifie pas : réessayez dans un instant.", id, read.Error?.Message), read.Error);
        }

        var session = read.State == StoredFileState.Read ? Parse(read.Text!) : null;
        if (session is not null)
        {
            _known[id] = true;
        }

        return session;
    }

    public IReadOnlyList<JournalSession> List() => Browse().Sessions;

    /// <summary>Un fichier douteux (propriétaire étranger) est ignoré ; un fichier illisible est compté, jamais rejoué ni réécrit.</summary>
    public JournalListing Browse()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return new JournalListing([], 0);
        }

        var sessions = new List<JournalSession>();
        var unreadable = 0;
        foreach (var file in System.IO.Directory.GetFiles(Directory, "*.json"))
        {
            var read = AtomicFile.ReadText(file, protector.IsTrusted, wait);
            if (read.State == StoredFileState.Read && Parse(read.Text!) is { } session)
            {
                sessions.Add(session);
            }
            else if (read.State is StoredFileState.Read or StoredFileState.Unavailable)
            {
                unreadable++;
            }
        }

        return new JournalListing(sessions.OrderByDescending(s => s.CreatedAt).ToList(), unreadable);
    }

    /// <summary>
    /// Un fichier que cette instance n'a ni lu ni écrit n'est jamais remplacé à l'aveugle : illisible, il est laissé tel quel
    /// (enregistrement refusé) ; abîmé ou d'une version plus récente, une copie est gardée à côté avant de le remplacer.
    /// </summary>
    private void GuardExisting(string id, string path)
    {
        var read = AtomicFile.ReadText(path, protector.IsTrusted, wait);
        if (read.State == StoredFileState.Unavailable)
        {
            throw new JournalUnsafeException(
                T("Le fichier de la séance {0} existe mais reste illisible ({1}) : MAUS ne l'écrase pas.", id, read.Error?.Message), read.Error);
        }

        if (read.State == StoredFileState.Read && Parse(read.Text!) is null)
        {
            try
            {
                AtomicFile.KeepCopy(path, DateTime.Now);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new JournalUnsafeException(T("Impossible de garder une copie du fichier de la séance {0} avant de le remplacer : {1}", id, ex.Message), ex);
            }
        }
    }

    /// <summary>JSON invalide ou valeur inconnue de cette version (écrite par une version plus récente) : <c>null</c>.</summary>
    private static JournalSession? Parse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<JournalSession>(text, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string PathOf(string id) => Path.Combine(Directory, id + ".json");

    /// <summary>L'identifiant sert de nom de fichier : pas de séparateur ni de « .. ».</summary>
    private static bool IsValidId(string id) =>
        id.Length is > 0 and < 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}
