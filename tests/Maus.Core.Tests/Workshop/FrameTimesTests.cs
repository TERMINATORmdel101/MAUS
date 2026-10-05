using Maus.Core.Workshop;

namespace Maus.Core.Tests.Workshop;

public class FrameTimesTests
{
    /// <summary>En-tête réel de PresentMon 2.6.0 en mode --v1_metrics (relevé le 05/10/2026).</summary>
    private const string Header = "Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped,TimeInSeconds,msInPresentAPI,msBetweenPresents,AllowsTearing,PresentMode";

    private static string Line(string app, double ms, int dropped = 0) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{app},1234,0x0,DXGI,0,0,{dropped},1.0,0.1,{ms:0.0000},0,Hardware: Independent Flip");

    [Fact]
    public void Game_is_the_program_with_the_most_frames_and_dwm_is_ignored()
    {
        var log = new FrameTimeLog();
        log.AddCsvLine(Header);
        for (var i = 0; i < 500; i++)
        {
            log.AddCsvLine(Line("dwm.exe", 5));
        }

        for (var i = 0; i < 200; i++)
        {
            // 198 images à 10 ms (100 images/s), 2 à 40 ms : le 1 % le plus lent (2 images) a duré 40 ms.
            log.AddCsvLine(Line("jeu.exe", i < 198 ? 10 : 40));
        }

        var summary = log.Summarize()!;

        Assert.Equal("jeu.exe", summary.Application);
        Assert.Equal(200, summary.Frames);
        Assert.Equal(1000 * 200 / (198 * 10.0 + 2 * 40), summary.AverageFps, 3);
        Assert.Equal(40, summary.SlowestFrameMs);
        Assert.Equal(25, summary.SlowestFps, 3);
    }

    [Fact]
    public void Slowest_one_percent_is_the_shortest_of_the_slowest_frames()
    {
        var log = new FrameTimeLog();
        log.AddCsvLine(Header);
        for (var i = 0; i < 1000; i++)
        {
            log.AddCsvLine(Line("jeu.exe", i < 980 ? 10 : 25));
        }

        Assert.Equal(25, log.Summarize()!.SlowestFrameMs);
    }

    [Fact]
    public void Dropped_frames_bad_lines_and_short_captures_are_ignored()
    {
        var log = new FrameTimeLog();
        log.AddCsvLine(Header);
        log.AddCsvLine("ligne,abîmée");
        log.AddCsvLine(Line("jeu.exe", 0));
        for (var i = 0; i < 150; i++)
        {
            log.AddCsvLine(Line("jeu.exe", 10, dropped: 1));
        }

        Assert.Null(log.Summarize());
    }
}
