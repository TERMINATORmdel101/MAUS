using Maus.Core.Platform;

namespace Maus.Core.Tests.Platform;

public class EventLogReaderTests
{
    [Fact]
    public void XPath_filters_provider_ids_and_time_window()
    {
        var xpath = WindowsEventLogReader.BuildXPath("Microsoft-Windows-WHEA-Logger", [17, 19], DateTime.UtcNow.AddDays(-30));

        Assert.StartsWith("*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID=17 or EventID=19) and TimeCreated[timediff(@SystemTime) <= ", xpath);
        Assert.EndsWith("]]]", xpath);
    }

    [Fact]
    public void XPath_without_provider_or_ids_keeps_only_the_time_window()
    {
        var xpath = WindowsEventLogReader.BuildXPath(null, [], DateTime.UtcNow.AddHours(-1));

        Assert.StartsWith("*[System[TimeCreated[timediff(@SystemTime) <= ", xpath);
    }

    private static long WindowOf(string xpath) =>
        long.Parse(System.Text.RegularExpressions.Regex.Match(xpath, @"timediff\(@SystemTime\) <= (\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void The_time_window_is_really_the_one_asked_for()
    {
        const long ThirtyDays = 2_592_000_000;
        var since = DateTime.UtcNow.AddDays(-30);

        Assert.InRange(WindowOf(WindowsEventLogReader.BuildXPath(null, [], since)), ThirtyDays - 60_000, ThirtyDays + 60_000);
        Assert.InRange(WindowOf(WindowsEventLogReader.BuildLevelXPath([1, 2], since)), ThirtyDays - 60_000, ThirtyDays + 60_000);
    }

    [Fact]
    public void Level_xpath_filters_levels_and_time_window()
    {
        var xpath = WindowsEventLogReader.BuildLevelXPath([1, 2], DateTime.UtcNow.AddDays(-1));

        Assert.StartsWith("*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= ", xpath, StringComparison.Ordinal);
        Assert.StartsWith("*[System[TimeCreated[", WindowsEventLogReader.BuildLevelXPath([], DateTime.UtcNow.AddDays(-1)), StringComparison.Ordinal);
    }

    [Fact]
    public void Real_log_read_after_a_bookmark_does_not_return_the_same_event_twice()
    {
        var reader = new WindowsEventLogReader();
        var seen = new List<(string Provider, int Id, DateTime Time)>();
        var cursor = new EventLogCursor("System", DateTime.Now.AddHours(-6));
        foreach (var e in reader.ReadNew(cursor, (_, _, _) => EventDetail.Message))
        {
            seen.Add((e.Provider, e.Id, e.TimeCreated));
        }

        var last = seen.LastOrDefault();
        var more = reader.ReadNew(cursor, (_, _, _) => EventDetail.Message);

        // Le journal système peut recevoir des événements entre les deux lectures, mais jamais le dernier déjà lu.
        Assert.DoesNotContain(more, e => (e.Provider, e.Id, e.TimeCreated) == last);
    }

    [Fact]
    public void Real_system_log_is_readable_without_elevation()
    {
        var since = DateTime.Now.AddDays(-30);

        var events = new WindowsEventLogReader().Query("System", null, [], since, maxEvents: 5);

        // Le journal Système d'un Windows en marche n'est jamais vide sur 30 jours : une liste vide serait un filtre cassé.
        Assert.InRange(events.Count, 1, 5);
        Assert.All(events, e => Assert.True(e.TimeCreated >= since, e.TimeCreated.ToString("o", System.Globalization.CultureInfo.InvariantCulture)));
    }
}
