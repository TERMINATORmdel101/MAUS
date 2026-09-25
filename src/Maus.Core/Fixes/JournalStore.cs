using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maus.Core.Fixes;

/// <summary>Le journal ne peut pas être protégé : MAUS refuse alors toute correction.</summary>
public sealed class JournalUnsafeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Stockage des séances de corrections.</summary>
public interface IJournalStore
{
    /// <summary>Enregistre (ou réenregistre) la séance. Appelé avant chaque écriture, pour survivre à un plantage.</summary>
    /// <exception cref="JournalUnsafeException">Le dossier du journal n'est pas sûr.</exception>
    void Save(JournalSession session);

    JournalSession? Load(string id);

    /// <summary>Séances connues, de la plus récente à la plus ancienne.</summary>
    IReadOnlyList<JournalSession> List();
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

/// <summary>Journal JSON, un fichier par séance, sous <c>%ProgramData%\MAUS\journal</c>.</summary>
public sealed class FileJournalStore(string directory, IDirectoryProtector protector) : IJournalStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MAUS", "journal");

    public string Directory { get; } = directory;

    public static FileJournalStore CreateDefault() => new(DefaultDirectory, new WindowsDirectoryProtector());

    public void Save(JournalSession session)
    {
        protector.EnsureProtected(Directory);
        var path = PathOf(session.Id);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(session, JsonOptions));
        protector.ProtectFile(temporary);
        File.Move(temporary, path, overwrite: true);
    }

    public JournalSession? Load(string id)
    {
        if (!IsValidId(id))
        {
            return null;
        }

        var path = PathOf(id);
        return File.Exists(path) ? Read(path) : null;
    }

    public IReadOnlyList<JournalSession> List()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        return System.IO.Directory.GetFiles(Directory, "*.json")
            .Select(Read)
            .OfType<JournalSession>()
            .OrderByDescending(s => s.CreatedAt)
            .ToList();
    }

    /// <summary>Un fichier douteux (propriétaire étranger, JSON invalide) est ignoré, jamais rejoué.</summary>
    private JournalSession? Read(string path)
    {
        if (!protector.IsTrusted(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<JournalSession>(File.ReadAllText(path), JsonOptions);
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
