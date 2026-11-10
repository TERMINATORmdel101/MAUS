using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Platform;

/// <summary>Un événement lu dans un journal Windows, avec ses données nommées (<c>EventData</c>).</summary>
public sealed record EventRecordInfo(
    int Id,
    string Provider,
    DateTime TimeCreated,
    IReadOnlyDictionary<string, string> Data,
    string? Message = null);

/// <summary>Ce qu'il faut garder d'un événement lu à la suite : rien, son message, ou aussi ses données nommées.</summary>
public enum EventDetail
{
    Skip,
    Message,
    MessageAndData,
}

/// <summary>Choisit, sur le niveau, le fournisseur et l'identifiant (peu coûteux à lire), ce qu'il faut garder d'un événement.</summary>
public delegate EventDetail EventSelector(int level, string provider, int id);

/// <summary>
/// Position dans un journal pour n'en lire que les nouveautés : parcourir tout un journal toutes les 5 secondes coûte cher,
/// lire seulement ce qui a été écrit depuis la dernière fois ne coûte presque rien.
/// </summary>
public sealed class EventLogCursor(string logName, DateTime since)
{
    public string LogName { get; } = logName;

    /// <summary>Les événements plus anciens ne comptent pas à la première lecture et à la reprise après une lecture impossible.</summary>
    public DateTime Since { get; internal set; } = since;

    internal EventBookmark? Bookmark { get; set; }

    internal long? LastRecordId { get; set; }
}

/// <summary>Lecture des journaux d'événements Windows.</summary>
public interface IEventLogReader
{
    /// <summary>
    /// Événements d'un journal (par exemple « System »), filtrés par fournisseur et identifiants, depuis <paramref name="since"/>.
    /// Renvoie au plus <paramref name="maxEvents"/> événements, du plus récent au plus ancien.
    /// </summary>
    /// <exception cref="MausAccessDeniedException">Journal protégé (souvent : droits administrateur requis).</exception>
    /// <exception cref="DataSourceUnavailableException">Journal absent sur ce PC.</exception>
    IReadOnlyList<EventRecordInfo> Query(
        string logName,
        string? provider,
        IReadOnlyCollection<int> eventIds,
        DateTime since,
        int maxEvents = 200,
        bool includeMessage = false);

    /// <summary>
    /// Événements d'un journal par niveau de gravité (1 = critique, 2 = erreur, 3 = avertissement), depuis <paramref name="since"/>,
    /// du plus récent au plus ancien. Sans implémentation (sources de test), aucun événement.
    /// </summary>
    /// <exception cref="MausAccessDeniedException">Journal protégé.</exception>
    /// <exception cref="DataSourceUnavailableException">Journal absent sur ce PC.</exception>
    IReadOnlyList<EventRecordInfo> QueryLevels(
        string logName,
        IReadOnlyCollection<int> levels,
        DateTime since,
        int maxEvents = 200,
        bool includeMessage = false) => [];

    /// <summary>
    /// Événements écrits dans le journal du curseur depuis la lecture précédente, du plus ancien au plus récent ; le curseur avance.
    /// Sans implémentation (sources de test), aucun événement.
    /// </summary>
    /// <exception cref="MausAccessDeniedException">Journal protégé.</exception>
    /// <exception cref="DataSourceUnavailableException">Journal absent ou illisible ; le curseur repart du dernier événement vu.</exception>
    IReadOnlyList<EventRecordInfo> ReadNew(EventLogCursor cursor, EventSelector selector) => [];

    /// <summary>Date du plus ancien événement encore présent dans un journal, ou <c>null</c> si elle n'est pas connue (journal vide, sources de test).</summary>
    /// <exception cref="MausAccessDeniedException">Journal protégé.</exception>
    /// <exception cref="DataSourceUnavailableException">Journal absent ou illisible.</exception>
    DateTime? OldestEvent(string logName) => null;
}

public sealed class WindowsEventLogReader : IEventLogReader
{
    private static readonly XNamespace EventNamespace = "http://schemas.microsoft.com/win/2004/08/events/event";

    private static readonly Dictionary<string, string> NoData = [];

    public IReadOnlyList<EventRecordInfo> Query(
        string logName,
        string? provider,
        IReadOnlyCollection<int> eventIds,
        DateTime since,
        int maxEvents = 200,
        bool includeMessage = false) =>
        Read(logName, BuildXPath(provider, eventIds, since), maxEvents, includeMessage, includeData: true);

    public IReadOnlyList<EventRecordInfo> QueryLevels(
        string logName,
        IReadOnlyCollection<int> levels,
        DateTime since,
        int maxEvents = 200,
        bool includeMessage = false) =>
        Read(logName, BuildLevelXPath(levels, since), maxEvents, includeMessage, includeData: false);

    public DateTime? OldestEvent(string logName) => Guarded(logName, () =>
    {
        // Sens normal du journal : le premier événement lu est le plus ancien.
        using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(new EventLogQuery(logName, PathType.LogName, "*"));
        using var record = reader.ReadEvent();
        return record?.TimeCreated is { } when ? [new EventRecordInfo(record.Id, record.ProviderName ?? string.Empty, when, NoData)] : new List<EventRecordInfo>();
    }).FirstOrDefault()?.TimeCreated;

    public IReadOnlyList<EventRecordInfo> ReadNew(EventLogCursor cursor, EventSelector selector)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(selector);

        return Guarded(cursor.LogName, () =>
        {
            try
            {
                return cursor.Bookmark is null ? ReadBackToSince(cursor, selector) : ReadAfterBookmark(cursor, selector);
            }
            catch (EventLogException)
            {
                // Signet périmé (journal vidé, par exemple) : la prochaine lecture repart du dernier événement vu.
                cursor.Bookmark = null;
                throw;
            }
        });
    }

    /// <summary>Première lecture : du plus récent en remontant, jusqu'au premier événement plus ancien que le début de la surveillance.</summary>
    private static List<EventRecordInfo> ReadBackToSince(EventLogCursor cursor, EventSelector selector)
    {
        var events = new List<EventRecordInfo>();
        var (since, lastSeen) = (cursor.Since, cursor.LastRecordId);
        var first = true;
        using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(new EventLogQuery(cursor.LogName, PathType.LogName, "*") { ReverseDirection = true });
        while (reader.ReadEvent() is { } record)
        {
            using (record)
            {
                if (first)
                {
                    Advance(cursor, record);
                    first = false;
                }

                if (record.TimeCreated < since || record.RecordId == lastSeen)
                {
                    break;
                }

                Keep(record, selector, events);
            }
        }

        events.Reverse();
        return events;
    }

    /// <summary>Lectures suivantes : seulement ce qui suit le signet.</summary>
    private static List<EventRecordInfo> ReadAfterBookmark(EventLogCursor cursor, EventSelector selector)
    {
        var events = new List<EventRecordInfo>();
        using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(new EventLogQuery(cursor.LogName, PathType.LogName, "*"), cursor.Bookmark);
        while (reader.ReadEvent() is { } record)
        {
            using (record)
            {
                if (record.RecordId != cursor.LastRecordId)
                {
                    Keep(record, selector, events);
                    Advance(cursor, record);
                }
            }
        }

        return events;
    }

    private static void Advance(EventLogCursor cursor, EventRecord record)
    {
        cursor.Bookmark = record.Bookmark;
        cursor.LastRecordId = record.RecordId;
        if (record.TimeCreated > cursor.Since)
        {
            cursor.Since = record.TimeCreated.Value;
        }
    }

    private static void Keep(EventRecord record, EventSelector selector, List<EventRecordInfo> events)
    {
        var provider = record.ProviderName ?? string.Empty;
        var detail = selector(record.Level ?? 0, provider, record.Id);
        if (detail != EventDetail.Skip)
        {
            events.Add(new EventRecordInfo(
                record.Id,
                provider,
                record.TimeCreated ?? DateTime.MinValue,
                detail == EventDetail.MessageAndData ? ReadEventData(record) : NoData,
                TryFormat(record)));
        }
    }

    /// <summary>Filtre XPath par niveau de gravité et fenêtre de temps.</summary>
    public static string BuildLevelXPath(IReadOnlyCollection<int> levels, DateTime since)
    {
        ArgumentNullException.ThrowIfNull(levels);

        var conditions = new List<string>();
        if (levels.Count > 0)
        {
            conditions.Add(AnyOf("Level", levels));
        }

        return Filter(conditions, since);
    }

    /// <summary>« (Nom=1 or Nom=2) ».</summary>
    private static string AnyOf(string name, IEnumerable<int> values) =>
        "(" + string.Join(" or ", values.Select(value => $"{name}={value.ToString(CultureInfo.InvariantCulture)}")) + ")";

    /// <summary>Filtre complet : les conditions données et la fenêtre de temps (<c>timediff</c> en millisecondes).</summary>
    private static string Filter(List<string> conditions, DateTime since)
    {
        var milliseconds = (long)Math.Max(0, (DateTime.UtcNow - since.ToUniversalTime()).TotalMilliseconds);
        conditions.Add($"TimeCreated[timediff(@SystemTime) <= {milliseconds.ToString(CultureInfo.InvariantCulture)}]");
        return $"*[System[{string.Join(" and ", conditions)}]]";
    }

    private static List<EventRecordInfo> Read(string logName, string xpath, int maxEvents, bool includeMessage, bool includeData)
    {
        var query = new EventLogQuery(logName, PathType.LogName, xpath) { ReverseDirection = true };
        return Guarded(logName, () =>
        {
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);
            var events = new List<EventRecordInfo>();
            while (events.Count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    events.Add(new EventRecordInfo(
                        record.Id,
                        record.ProviderName ?? string.Empty,
                        record.TimeCreated ?? DateTime.MinValue,
                        includeData ? ReadEventData(record) : NoData,
                        includeMessage ? TryFormat(record) : null));
                }
            }

            return events;
        });
    }

    /// <summary>Traduit les erreurs de lecture d'un journal en exceptions de MAUS.</summary>
    private static List<EventRecordInfo> Guarded(string logName, Func<List<EventRecordInfo>> read)
    {
        try
        {
            return read();
        }
        catch (EventLogNotFoundException ex)
        {
            throw new DataSourceUnavailableException(T("Journal absent : {0}", logName), ex);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or EventLogException { InnerException: UnauthorizedAccessException })
        {
            throw new MausAccessDeniedException(T("Lecture du journal refusée : {0}", logName), ex);
        }
        catch (EventLogException ex)
        {
            throw new DataSourceUnavailableException(T("Journal illisible : {0}", logName), ex);
        }
    }

    /// <summary>Filtre XPath : fournisseur, identifiants et fenêtre de temps (<c>timediff</c> en millisecondes).</summary>
    public static string BuildXPath(string? provider, IReadOnlyCollection<int> eventIds, DateTime since)
    {
        ArgumentNullException.ThrowIfNull(eventIds);

        var conditions = new List<string>();
        if (!string.IsNullOrEmpty(provider))
        {
            conditions.Add($"Provider[@Name='{provider}']");
        }

        if (eventIds.Count > 0)
        {
            conditions.Add(AnyOf("EventID", eventIds));
        }

        return Filter(conditions, since);
    }

    private static Dictionary<string, string> ReadEventData(EventRecord record)
    {
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var xml = XDocument.Parse(record.ToXml());
            var index = 0;
            foreach (var element in xml.Descendants(EventNamespace + "Data"))
            {
                var name = element.Attribute("Name")?.Value ?? $"Data{index}";
                data[name] = element.Value;
                index++;
            }
        }
        catch (Exception ex) when (ex is EventLogException or System.Xml.XmlException)
        {
            // Données illisibles : l'événement reste compté, sans détail.
        }

        return data;
    }

    private static string? TryFormat(EventRecord record)
    {
        try
        {
            return record.FormatDescription();
        }
        catch (EventLogException)
        {
            return null;
        }
    }
}
