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
