using Maus.Core.Modules.M14Display;

namespace Maus.Core.Tests.Modules.M14Display;

public class RefreshRateTrialTests
{
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
