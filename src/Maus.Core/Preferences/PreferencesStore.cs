using System.Text.Json;
using System.Text.Json.Serialization;
using Maus.Core.Fixes;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Preferences;

/// <summary>Ce que MAUS a trouvé à la dernière lecture du fichier des choix.</summary>
public enum PreferencesFileState
{
    /// <summary>Pas de fichier (premier lancement) : valeurs par défaut, c'est normal.</summary>
    Absent,

    /// <summary>Fichier lu normalement.</summary>
    Read,

    /// <summary>Fichier non fiable (propriétaire étranger) : ignoré exprès, et remplacé au prochain enregistrement.</summary>
    Untrusted,

    /// <summary>
    /// Fichier présent mais momentanément illisible : MAUS ne l'écrit plus jusqu'à sa fermeture, les choix restent en mémoire.
    /// </summary>
    Unavailable,

    /// <summary>
    /// Fichier abîmé ou écrit par une version plus récente de MAUS : ce qui est lisible est gardé, et une copie du fichier
    /// est faite à côté avant de le remplacer.
    /// </summary>
    Damaged,
}

public interface IPreferencesStore
{
    /// <summary>Préférences enregistrées, ou valeurs par défaut si le fichier est absent, illisible ou non fiable.</summary>
    UserPreferences Load();

    /// <exception cref="JournalUnsafeException">Le dossier ne peut pas être protégé.</exception>
    /// <exception cref="PreferencesNotSavedException">Fichier existant illisible : choix gardé en mémoire seulement.</exception>
    void Save(UserPreferences preferences);

    /// <summary>Explication pour l'utilisateur quand le fichier n'a pas pu être lu normalement ; <c>null</c> sinon.</summary>
    string? Notice => null;
}

/// <summary>
/// Choix non écrit sur le disque, parce que le fichier existant était illisible : l'écrire l'aurait effacé. Le choix vaut
/// jusqu'à la fermeture de MAUS (<see cref="Kept"/>).
/// </summary>
public sealed class PreferencesNotSavedException(string message, UserPreferences kept, Exception? inner = null) : IOException(message, inner)
{
    /// <summary>Préférences gardées en mémoire pour cette séance.</summary>
    public UserPreferences Kept { get; } = kept;
}

/// <summary>
/// Préférences sous <c>%ProgramData%\MAUS\settings</c>, protégées comme le journal : un programme lancé sans droits
/// administrateur ne doit pas pouvoir marquer « voulu » un antivirus coupé pour le cacher à MAUS.
/// </summary>
/// <remarks>
/// Un fichier momentanément illisible n'est jamais pris pour un fichier vide : après quelques tentatives, MAUS continue avec
/// les choix en mémoire et ne réécrit plus le fichier jusqu'à sa fermeture. Un fichier abîmé, ou écrit par une version plus
/// récente (valeur inconnue), est lu autant que possible, puis copié à côté avant d'être remplacé.
/// </remarks>
/// <param name="wait">Attente entre deux tentatives de lecture (remplaçable pour les tests).</param>
public sealed class FilePreferencesStore(string directory, IDirectoryProtector protector, Action<TimeSpan>? wait = null) : IPreferencesStore
{
    private const string FileName = "preferences.json";

    /// <summary>Lecture de secours : une valeur d'énumération inconnue de cette version prend sa valeur par défaut.</summary>
    internal static readonly JsonSerializerOptions TolerantJson = CreateTolerantOptions();

    private static readonly Lazy<FilePreferencesStore> Shared = new(() => new FilePreferencesStore(DefaultDirectory, new WindowsDirectoryProtector()));

    private readonly Lock _gate = new();

    /// <summary>Dernières préférences lues ou enregistrées par cette instance.</summary>
    private UserPreferences? _known;

    /// <summary>Le fichier est resté illisible : plus aucune écriture jusqu'à la fermeture de MAUS.</summary>
    private bool _memoryOnly;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MAUS", "settings");

    /// <summary>
    /// Le magasin de toute l'application : l'état « fichier illisible, choix gardés en mémoire » vaut ainsi pour toute la
    /// séance, quelle que soit la fenêtre qui lit ou enregistre un choix.
    /// </summary>
    public static FilePreferencesStore CreateDefault() => Shared.Value;

    public string FilePath => Path.Combine(directory, FileName);

    /// <summary>Résultat de la dernière lecture (ou de la dernière écriture réussie).</summary>
    public PreferencesFileState State { get; private set; }

    public string? Notice { get; private set; }

    public UserPreferences Load()
    {
        lock (_gate)
        {
            if (_memoryOnly)
            {
                return _known ?? UserPreferences.Default;
            }

            var read = AtomicFile.ReadText(FilePath, protector.IsTrusted, wait);
            switch (read.State)
            {
                case StoredFileState.Unavailable:
                    EnterMemoryOnly(read.Error);
                    return _known ?? UserPreferences.Default;

                case StoredFileState.Read:
                    var (preferences, complete) = Parse(read.Text!);
                    State = complete ? PreferencesFileState.Read : PreferencesFileState.Damaged;
                    Notice = complete ? null : T("Le fichier de vos choix ({0}) est abîmé ou vient d'une version plus récente de MAUS : les choix illisibles reprennent leur valeur par défaut. MAUS en gardera une copie à côté avant d'y enregistrer un nouveau choix.", FilePath);
                    _known = preferences;
                    return preferences;

                default:
                    State = read.State == StoredFileState.Untrusted ? PreferencesFileState.Untrusted : PreferencesFileState.Absent;
                    Notice = null;
                    _known = UserPreferences.Default;
                    return UserPreferences.Default;
            }
        }
    }

    public void Save(UserPreferences preferences)
    {
        lock (_gate)
        {
            if (_memoryOnly)
            {
                _known = preferences;
                throw new PreferencesNotSavedException(Notice!, preferences);
            }

            protector.EnsureProtected(directory);

            // Ce qui va être remplacé est relu juste avant : un fichier illisible n'est jamais écrasé, un fichier abîmé
            // jamais sans copie.
            var current = AtomicFile.ReadText(FilePath, protector.IsTrusted, wait);
            if (current.State == StoredFileState.Unavailable)
            {
                EnterMemoryOnly(current.Error);
                _known = preferences;
                throw new PreferencesNotSavedException(Notice!, preferences, current.Error);
            }

            if (current.State == StoredFileState.Read && !Parse(current.Text!).Complete)
            {
                var copy = AtomicFile.KeepCopy(FilePath, DateTime.Now);
                Notice = T("Le fichier de vos choix était abîmé ou venait d'une version plus récente de MAUS : les choix illisibles ont repris leur valeur par défaut. L'ancien fichier est gardé ici : {0}", copy);
            }

            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(preferences, FileJournalStore.JsonOptions), protector.ProtectFile);
            _known = preferences;
            State = PreferencesFileState.Read;
        }
    }

    private void EnterMemoryOnly(Exception? error)
    {
        _memoryOnly = true;
        State = PreferencesFileState.Unavailable;
        Notice = T("Le fichier de vos choix ({0}) est momentanément illisible ({1}). Pour ne rien effacer, MAUS ne l'écrira pas avant sa prochaine ouverture : les choix faits d'ici là ne valent que jusqu'à sa fermeture. Si ce message revient à chaque lancement, supprimez ce fichier (vos choix seront remis à zéro).", FilePath, error?.Message);
    }

    /// <returns>Les préférences lues, et <c>false</c> si une partie n'a pas pu l'être (fichier abîmé ou d'une version plus récente).</returns>
    private static (UserPreferences Preferences, bool Complete) Parse(string text)
    {
        if (TryDeserialize(text, FileJournalStore.JsonOptions) is { } strict)
        {
            return (strict, true);
        }

        return (TryDeserialize(text, TolerantJson) ?? UserPreferences.Default, false);
    }

    private static UserPreferences? TryDeserialize(string text, JsonSerializerOptions options)
    {
        try
        {
            return JsonSerializer.Deserialize<UserPreferences>(text, options) ?? UserPreferences.Default;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Mêmes réglages que l'écriture, mais les énumérations des préférences acceptent une valeur inconnue : revenir à une
    /// version plus ancienne de MAUS ne fait pas perdre tous les choix pour un seul réglage qu'elle ne connaît pas.
    /// </summary>
    private static JsonSerializerOptions CreateTolerantOptions()
    {
        var options = new JsonSerializerOptions(FileJournalStore.JsonOptions);
        options.Converters.Clear();
        options.Converters.Add(new TolerantEnumConverter<LaptopPowerChoice>());
        options.Converters.Add(new TolerantEnumConverter<Workshop.SearchEngine>());
        options.Converters.Add(new TolerantEnumConverter<ThemeChoice>());
        options.Converters.Add(new TolerantEnumConverter<AccentChoice>());
        options.Converters.Add(new TolerantEnumConverter<AnimationChoice>());
        // Une énumération ajoutée plus tard sans être listée ici reste lue strictement : fichier traité comme abîmé, copie gardée.
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

/// <summary>
/// Lecture tolérante d'une énumération : une valeur inconnue (écrite par une version plus récente de MAUS) donne la valeur
/// par défaut au lieu de rendre tout le fichier illisible.
/// </summary>
internal sealed class TolerantEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && Enum.TryParse<TEnum>(reader.GetString(), ignoreCase: true, out var named) && Enum.IsDefined(named))
        {
            return named;
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var number) && Enum.ToObject(typeof(TEnum), number) is TEnum value && Enum.IsDefined(value))
        {
            return value;
        }

        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
        }

        return default;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

public static class PreferencesStoreExtensions
{
    private static readonly Lock Gate = new();

    /// <summary>
    /// Lit le fichier, applique <paramref name="change"/> et enregistre, d'un seul tenant : chaque réglage part de la dernière
    /// version enregistrée, si bien qu'un choix fait dans une fenêtre n'en écrase jamais un autre fait ailleurs.
    /// </summary>
    /// <returns>Les préférences enregistrées.</returns>
    /// <exception cref="JournalUnsafeException">Le dossier ne peut pas être protégé.</exception>
    /// <exception cref="PreferencesNotSavedException">Fichier illisible : le choix vaut pour cette séance seulement.</exception>
    public static UserPreferences Update(this IPreferencesStore store, Func<UserPreferences, UserPreferences> change)
    {
        lock (Gate)
        {
            var updated = change(store.Load());
            store.Save(updated);
            return updated;
        }
    }
}

/// <summary>Applique les marques « voulu » aux résultats d'un module.</summary>
public static class Acknowledgements
{
    public static ModuleResult Apply(ModuleResult result, UserPreferences preferences)
    {
        if (preferences.Acknowledged.Count == 0)
        {
            return result;
        }

        var findings = result.Findings.Select(finding =>
            finding.Status is FindingStatus.Improvable or FindingStatus.Warning or FindingStatus.Problem
            && preferences.AcknowledgementFor(finding) is { } mark
                ? finding with
                {
                    Status = FindingStatus.Info,
                    AcknowledgedFrom = finding.Status,
                    Fixable = false,
                    Advice = T("Marqué « voulu » par vous le {0:dd/MM/yyyy}. MAUS le signalera de nouveau si la situation change.", mark.At.ToLocalTime()),
                }
                : finding).ToList();
        return result with { Findings = findings };
    }
}
