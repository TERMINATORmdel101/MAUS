using System.Globalization;
using Maus.Core.Workshop;

namespace Maus.Core.Tests.Workshop;

public class FrameTimesTests
{
    /// <summary>En-tête réel de PresentMon 2.6.0 en mode --v1_metrics (relevé le 05/10/2026).</summary>
    private const string Header = "Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped,TimeInSeconds,msInPresentAPI,msBetweenPresents,AllowsTearing,PresentMode,msUntilRenderComplete,msUntilDisplayed,msBetweenDisplayChange,msFlipDelay,msUntilRenderStart,msGPUActive,msSinceInput";

    private static string Line(string app, double time, double ms, double gpu, int sync = 0, int dropped = 0) =>
        string.Create(CultureInfo.InvariantCulture, $"{app},1234,0x0,DXGI,{sync},0,{dropped},{time:0.000},0.1,{ms:0.0000},0,Hardware: Independent Flip,1,2,{ms:0.0000},0,0,{gpu:0.0000},0");

    private static FrameTimeLog Log(int count, Func<int, double> ms, double gpuShare = 0.5, int sync = 0, string app = "jeu.exe")
    {
        var log = new FrameTimeLog();
        log.AddCsvLine(Header);
        var time = 0.0;
        for (var i = 0; i < count; i++)
        {
            time += ms(i) / 1000;
            log.AddCsvLine(Line(app, time, ms(i), ms(i) * gpuShare, sync));
        }

        return log;
    }

    [Fact]
    public void Game_is_the_program_with_the_most_frames_and_dwm_is_ignored()
    {
        var log = Log(200, i => i < 198 ? 10 : 40);
        for (var i = 0; i < 500; i++)
        {
            log.AddCsvLine(Line("dwm.exe", i, 5, 1));
        }

        var summary = log.Summarize()!;

        // 198 images à 10 ms, 2 à 40 ms : le 1 % le plus lent (2 images) a duré 40 ms, soit 25 images par seconde.
        Assert.Equal("jeu.exe", summary.Application);
        Assert.Equal(200, summary.Frames);
        Assert.Equal(1000 * 200 / (198 * 10.0 + 2 * 40), summary.AverageFps, 3);
        Assert.Equal(40, summary.Low1Ms);
        Assert.Equal(25, summary.Low1Fps, 3);
        Assert.Null(summary.Low01Ms);
    }

    [Fact]
    public void Slowest_one_and_one_tenth_percent_from_one_thousand_frames()
    {
        // 2 000 images : 1 % = 20 images, 0,1 % = 2 images.
        var summary = Log(2000, i => i < 1975 ? 10 : i < 1998 ? 20 : 50).Summarize()!;

        Assert.Equal(20, summary.Low1Ms);
        Assert.Equal(50, summary.Low01Ms);
        Assert.Equal(20, summary.Low01Fps!.Value, 3);
    }

    [Fact]
    public void Gpu_busy_share_and_vsync_are_measured()
    {
        var summary = Log(300, _ => 16.7, gpuShare: 0.6, sync: 1).Summarize()!;

        Assert.Equal(0.6, summary.GpuBusyShare!.Value, 3);
        Assert.Equal(1, summary.VSyncShare, 3);
    }

    [Fact]
    public void Last_seconds_only_for_the_live_counter()
    {
        // 10 s à 100 images/s (10 ms) puis 5 s à 50 images/s (20 ms).
        var log = Log(1250, i => i < 1000 ? 10 : 20);

        Assert.Equal(50, log.Summarize(TimeSpan.FromSeconds(4))!.AverageFps, 1);
        Assert.True(log.Summarize()!.AverageFps > 80);
    }

    [Fact]
    public void A_game_that_stopped_sending_frames_is_no_longer_shown_live()
    {
        var now = 0L;
        var log = new FrameTimeLog(TimeSpan.FromSeconds(20), () => now);
        log.AddCsvLine(Header);
        for (var i = 0; i < 300; i++)
        {
            log.AddCsvLine(Line("jeu.exe", i * 0.01, 10, 5));
        }

        now = 2_000;
        Assert.NotNull(log.Summarize(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3)));

        // Jeu fermé ou en pause : plus aucune image depuis quatre secondes.
        now = 4_000;
        Assert.Null(log.Summarize(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3)));
        Assert.NotNull(log.Summarize());
    }

    [Fact]
    public void Live_counter_shows_a_figure_from_a_few_frames_but_no_lows()
    {
        var log = Log(30, _ => 33.3);

        Assert.Null(log.Summarize());
        var live = log.Summarize(TimeSpan.FromSeconds(10), minimumFrames: FrameTimeLog.MinimumLiveFrames)!;
        Assert.Equal(30, live.LiveFps, 0);
        Assert.False(live.HasLows);
    }

    [Fact]
    public void After_a_pause_the_live_counter_starts_again_from_the_resumed_frames()
    {
        // 5 s à 90 images/s, pause de 6 s (une image de 6 000 ms à la reprise), puis 3 s à 60 images/s.
        var log = new FrameTimeLog(TimeSpan.FromSeconds(20), () => 0);
        log.AddCsvLine(Header);
        var time = 0.0;
        void Frames(int count, double ms)
        {
            for (var i = 0; i < count; i++)
            {
                time += ms / 1000;
                log.AddCsvLine(Line("jeu.exe", time, ms, ms / 2));
            }
        }

        Frames(450, 1000 / 90.0);
        Frames(1, 6000);
        Frames(180, 1000 / 60.0);

        var live = log.Summarize(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3))!;

        Assert.Equal(180, live.Frames);
        Assert.Equal(60, live.AverageFps, 0);
        Assert.Equal(60, live.CurrentFps!.Value, 0);

        // Le relevé de toute la partie, lui, garde tout.
        Assert.Equal(631, log.Summarize()!.Frames);
    }

    [Fact]
    public void Live_figure_follows_the_last_second_without_waiting_for_the_ten_second_average()
    {
        // 9 s à 50 images/s (20 ms), puis 1 s à 200 images/s (5 ms) : la moyenne sur 10 s traîne, pas le chiffre en direct.
        var log = Log(650, i => i < 450 ? 20 : 5);

        var summary = log.Summarize(TimeSpan.FromSeconds(10))!;

        Assert.True(summary.AverageFps < 70);
        Assert.InRange(summary.CurrentFps!.Value, 190, 205);
        Assert.Equal(summary.CurrentFps, summary.LiveFps);
    }

    [Fact]
    public void Dropped_frames_bad_lines_and_short_captures_are_ignored()
    {
        var log = new FrameTimeLog();
        log.AddCsvLine(Header);
        log.AddCsvLine("ligne,abîmée");
        for (var i = 0; i < 150; i++)
        {
            log.AddCsvLine(Line("jeu.exe", i, 10, 5, dropped: 1));
        }

        Assert.Null(log.Summarize());
    }

    [Fact]
    public void Advice_points_to_the_limiting_part_and_the_screen()
    {
        var gpuBound = Log(300, _ => 10, gpuShare: 0.98).Summarize()!;
        var cpuBound = Log(300, _ => 10, gpuShare: 0.5).Summarize()!;

        Assert.Contains(FrameAdvice.Explain(gpuBound, 144), l => l.Contains("C'est elle qui limite", StringComparison.Ordinal));
        Assert.Contains(FrameAdvice.Explain(cpuBound, 144), l => l.Contains("plutôt le processeur", StringComparison.Ordinal));
        Assert.Contains(FrameAdvice.Explain(cpuBound, 60), l => l.Contains("la moyenne atteint sa fréquence", StringComparison.Ordinal));
        Assert.Contains(FrameAdvice.Explain(cpuBound, 144), l => l.Contains("reste sous sa fréquence", StringComparison.Ordinal));
    }
}
