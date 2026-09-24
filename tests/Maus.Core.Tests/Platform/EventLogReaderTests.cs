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

    [Fact]
    public void Real_system_log_is_readable_without_elevation()
    {
        var events = new WindowsEventLogReader().Query("System", null, [], DateTime.Now.AddDays(-30), maxEvents: 5);

        Assert.True(events.Count <= 5);
    }
}
