using Maus.Core.Modules.M14Display;

namespace Maus.Core.Tests.Modules.M14Display;

public class RefreshRateTrialTests
{
    /// <summary>Lecture réelle, sans rien changer : la structure DEVMODEW commune doit donner des valeurs sensées (ou rien, sans écran).</summary>
    [Fact]
    public void Shared_display_mode_structure_reads_sane_values()
    {
        Assert.True(Maus.Core.Platform.DisplayRefresh.PrimaryHz() is null or (> 1 and < 1000));

        foreach (var path in new Win32DisplayConfigReader().ReadActivePaths() ?? [])
        {
            Assert.All(path.Modes, mode => Assert.True(mode.Width > 0 && mode.Height > 0 && mode.RefreshHz is > 1 and < 1000));
        }
    }

    private static DisplayPath Screen(double refresh, int width = 3440, int height = 1440) => new()
    {
        MonitorName = "AW3423DWF",
        SourceName = @"\\.\DISPLAY2",
        Output = OutputTechnology.DisplayPortExternal,
        Width = width,
        Height = height,
        RefreshHz = refresh,
        Modes = [new DisplayMode(1920, 1080, 240), new DisplayMode(3440, 1440, 60), new DisplayMode(3440, 1440, 165)],
    };

    [Fact]
    public void Target_keeps_the_resolution_and_takes_the_maximum_at_that_resolution()
    {
        var target = RefreshRateTargets.Find([Screen(144), Screen(59.94)], "M14.refresh.display-2");

        Assert.Equal(new RefreshTarget(@"\\.\DISPLAY2", "AW3423DWF", 3440, 1440, 60, 165), target);
    }

    [Theory]
    [InlineData("M14.refresh.display-1", 164.99)]
    [InlineData("M14.refresh.display-3", 60)]
    [InlineData("M14.refresh.display-0", 60)]
    [InlineData("M14.refresh.display-x", 60)]
    [InlineData("M14.encoding.display-1", 60)]
    public void No_target_when_already_at_maximum_or_unknown_screen(string findingId, double refresh) =>
        Assert.Null(RefreshRateTargets.Find([Screen(refresh), Screen(refresh)], findingId));

    [Fact]
    public void Resolution_missing_from_the_modes_gives_no_target() =>
        Assert.Null(RefreshRateTargets.Find([Screen(60, 2560, 1080)], "M14.refresh.display-1"));

    [Fact]
    public void Trial_is_kept_only_on_request()
    {
        var writer = new FakeWriter();
        var trial = new RefreshRateTrial(writer, new RefreshTarget(@"\\.\DISPLAY1", "écran", 1920, 1080, 60, 144));

        Assert.Equal(DisplayChange.Applied, trial.Start());
        Assert.Equal(DisplayChange.Applied, trial.Keep());
        Assert.Equal(["try 1920x1080 144", "keep 1920x1080 144"], writer.Calls);
        Assert.Throws<InvalidOperationException>(() => trial.Revert());
    }

    [Fact]
    public void Revert_returns_to_the_saved_mode_and_a_refused_keep_too()
    {
        var writer = new FakeWriter();
        var trial = new RefreshRateTrial(writer, new RefreshTarget(@"\\.\DISPLAY1", "écran", 1920, 1080, 60, 144));
        trial.Start();
        Assert.Equal(DisplayChange.Applied, trial.Revert());
        Assert.Equal(["try 1920x1080 144", "restore"], writer.Calls);

        var refusing = new FakeWriter { KeepResult = DisplayChange.Failed };
        var second = new RefreshRateTrial(refusing, new RefreshTarget(@"\\.\DISPLAY1", "écran", 1920, 1080, 60, 144));
        second.Start();
        Assert.Equal(DisplayChange.Failed, second.Keep());
        Assert.Equal(["try 1920x1080 144", "keep 1920x1080 144", "restore"], refusing.Calls);
    }

    [Fact]
    public void Refused_mode_is_never_started()
    {
        var writer = new FakeWriter { TryResult = DisplayChange.Refused };
        var trial = new RefreshRateTrial(writer, new RefreshTarget(@"\\.\DISPLAY1", "écran", 1920, 1080, 60, 144));

        Assert.Equal(DisplayChange.Refused, trial.Start());
        Assert.False(trial.Started);
        Assert.Throws<InvalidOperationException>(() => trial.Keep());
        Assert.Equal(15, RefreshRateTrial.ConfirmDelay.TotalSeconds);
    }

    [Fact]
    public void Going_back_restores_the_old_rate_and_never_overwrites_a_later_change()
    {
        var kept = new Maus.Core.Preferences.KeptRefreshRate(@"\\.\DISPLAY1", "Écran", 2560, 1440, 60, 144, DateTimeOffset.UnixEpoch);
        var screen = Screen(144, 2560, 1440) with { SourceName = kept.SourceName, Modes = [new DisplayMode(2560, 1440, 60), new DisplayMode(2560, 1440, 144)] };

        var writer = new FakeWriter();
        var done = RefreshRateUndo.Run(kept, [screen], writer);
        Assert.True(done.Finished);
        Assert.Equal(["keep 2560x1440 60"], writer.Calls);

        // Fréquence changée depuis, dans Windows : laissée telle quelle.
        var changed = new FakeWriter();
        Assert.True(RefreshRateUndo.Run(kept, [screen with { RefreshHz = 120 }], changed).Finished);
        Assert.Empty(changed.Calls);

        // Écran débranché : rien ne change, l'entrée reste.
        var absent = new FakeWriter();
        Assert.False(RefreshRateUndo.Run(kept, [], absent).Finished);
        Assert.Empty(absent.Calls);
    }

    [Theory]
    [InlineData(75)]
    [InlineData(500)]
    public void Going_back_writes_only_a_rate_that_windows_lists_for_this_screen(int noted)
    {
        var kept = new Maus.Core.Preferences.KeptRefreshRate(@"\\.\DISPLAY1", "Écran", 2560, 1440, noted, 144, DateTimeOffset.UnixEpoch);
        var screen = Screen(144, 2560, 1440) with { SourceName = kept.SourceName, Modes = [new DisplayMode(2560, 1440, 60), new DisplayMode(2560, 1440, 144), new DisplayMode(1920, 1080, 75)] };
        var writer = new FakeWriter();

        Assert.False(RefreshRateUndo.Run(kept, [screen], writer).Finished);

        Assert.Empty(writer.Calls);
    }

    [Fact]
    public void Going_back_takes_the_listed_rate_close_to_the_note()
    {
        var kept = new Maus.Core.Preferences.KeptRefreshRate(@"\\.\DISPLAY1", "Écran", 1440, 2560, 60, 144, DateTimeOffset.UnixEpoch);
        var screen = Screen(144, 1440, 2560) with { SourceName = kept.SourceName, Modes = [new DisplayMode(2560, 1440, 59), new DisplayMode(2560, 1440, 144)] };
        var writer = new FakeWriter();

        Assert.True(RefreshRateUndo.Run(kept, [screen], writer).Finished);

        Assert.Equal(["keep 1440x2560 59"], writer.Calls);
    }

    private sealed class FakeWriter : IDisplayModeWriter
    {
        public DisplayChange TryResult { get; init; } = DisplayChange.Applied;

        public DisplayChange KeepResult { get; init; } = DisplayChange.Applied;

        public List<string> Calls { get; } = [];

        public DisplayChange TryMode(string sourceName, int width, int height, int hz)
        {
            Calls.Add($"try {width}x{height} {hz}");
            return TryResult;
        }

        public DisplayChange Keep(string sourceName, int width, int height, int hz)
        {
            Calls.Add($"keep {width}x{height} {hz}");
            return KeepResult;
        }

        public DisplayChange RestoreSaved(string sourceName)
        {
            Calls.Add("restore");
            return DisplayChange.Applied;
        }
    }
}
