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
}

public sealed class WindowsEventLogReader : IEventLogReader
{
    private static readonly XNamespace EventNamespace = "http://schemas.microsoft.com/win/2004/08/events/event";

    public IReadOnlyList<EventRecordInfo> Query(
        string logName,
        string? provider,
        IReadOnlyCollection<int> eventIds,
        DateTime since,
        int maxEvents = 200,
        bool includeMessage = false)
    {
        var query = new EventLogQuery(logName, PathType.LogName, BuildXPath(provider, eventIds, since)) { ReverseDirection = true };
        try
        {
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);
            var events = new List<EventRecordInfo>();
            for (var record = reader.ReadEvent(); record is not null && events.Count < maxEvents; record = reader.ReadEvent())
            {
                using (record)
                {
                    events.Add(new EventRecordInfo(
                        record.Id,
                        record.ProviderName ?? string.Empty,
                        record.TimeCreated ?? DateTime.MinValue,
                        ReadEventData(record),
                        includeMessage ? TryFormat(record) : null));
                }
            }

            return events;
        }
        catch (EventLogNotFoundException ex)
        {
            throw new DataSourceUnavailableException(T("Journal absent : {0}", logName), ex);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or EventLogException { InnerException: UnauthorizedAccessException })
        {
            throw new MausAccessDeniedException(T("Lecture du journal refusée : {0}", logName), ex);
        }
    }

    /// <summary>Filtre XPath : fournisseur, identifiants et fenêtre de temps (<c>timediff</c> en millisecondes).</summary>
    public static string BuildXPath(string? provider, IReadOnlyCollection<int> eventIds, DateTime since)
    {
        var conditions = new List<string>();
        if (!string.IsNullOrEmpty(provider))
        {
            conditions.Add($"Provider[@Name='{provider}']");
        }

        if (eventIds.Count > 0)
        {
            conditions.Add("(" + string.Join(" or ", eventIds.Select(id => $"EventID={id}")) + ")");
        }

        var milliseconds = (long)Math.Max(0, (DateTime.UtcNow - since.ToUniversalTime()).TotalMilliseconds);
        conditions.Add($"TimeCreated[timediff(@SystemTime) <= {milliseconds.ToString(CultureInfo.InvariantCulture)}]");
        return $"*[System[{string.Join(" and ", conditions)}]]";
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
