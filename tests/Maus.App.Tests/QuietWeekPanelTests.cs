using Maus.App.Views.Follow;
using Maus.Core;
using Maus.Core.Engine.Broker;
using Maus.Core.Preferences;
using Maus.Core.Reporting;

namespace Maus.App.Tests;

public class QuietWeekPanelTests
{
    private static AuditSnapshot Weekly(DateTimeOffset at, params SnapshotFinding[] findings) => new(at, "26200", AuditSnapshot.NoScore, findings, Weekly: true);

    private static SnapshotFinding Finding(string id, FindingStatus status = FindingStatus.Ok) => new(id, "M01", "Titre " + id, status);

    [Fact]
    public void Disabled_offers_the_setting_only_where_it_exists() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();

        await bench.ViewModel.ShowQuietWeekAsync();

        Assert.Contains("n'est pas activé", bench.ViewModel.QuietWeekHeadline, StringComparison.Ordinal);
        Assert.False(bench.ViewModel.CanOpenQuietWeekSetting);
        Assert.True(bench.ViewModel.HasQuietWeekSettingNote);
    });

    [Fact]
    public void Showing_the_card_writes_nothing_and_seeing_it_keeps_the_message_for_the_session() => UiThread.Run(async () =>
    {
        var now = DateTimeOffset.Now;
        using var bench = new TestBench(Weekly(now.AddDays(-14), Finding("a")), Weekly(now.AddDays(-7), Finding("a")));
        bench.Fake.Answers["GetScheduleAsync"] = _ => new ScheduleState(DayOfWeek.Monday, "maus");
        bench.Store.Update(p => p with { LastFollowVisit = now.AddDays(-10) });
        var visit = bench.Store.Load().LastFollowVisit;

        await bench.ViewModel.ShowQuietWeekAsync();

        Assert.Equal(visit, bench.Store.Load().LastFollowVisit);
        Assert.Contains("1 audit depuis votre dernière visite", bench.ViewModel.QuietWeekHeadline, StringComparison.Ordinal);

        await bench.ViewModel.MarkQuietWeekSeenAsync();
        Assert.True(bench.Store.Load().LastFollowVisit > visit);

        // Retour sur la page dans la même session : même repère, même bilan.
        await bench.ViewModel.ShowQuietWeekAsync();
        Assert.Contains("1 audit depuis votre dernière visite", bench.ViewModel.QuietWeekHeadline, StringComparison.Ordinal);
    });

    [Fact]
    public void A_card_without_audits_shown_is_not_a_visit() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();

        await bench.ViewModel.MarkQuietWeekSeenAsync();
        await bench.ViewModel.ShowQuietWeekAsync();
        await bench.ViewModel.MarkQuietWeekSeenAsync();

        Assert.Null(bench.Store.Load().LastFollowVisit);
    });

    [Fact]
    public void An_unreadable_task_says_nothing_false() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Fake.Answers["GetScheduleAsync"] = _ => new EngineException("moteur absent");

        await bench.ViewModel.ShowQuietWeekAsync();

        Assert.Contains("n'a pas pu être lu", bench.ViewModel.QuietWeekHeadline, StringComparison.Ordinal);
    });

    [Theory]
    [InlineData(0, 300, 800, true)]
    [InlineData(700, 300, 800, true)]
    [InlineData(790, 300, 800, false)]
    [InlineData(-400, 300, 800, false)]
    [InlineData(0, 0, 800, false)]
    public void The_card_counts_as_read_only_when_its_top_is_on_screen(double top, double height, double viewport, bool readable) =>
        Assert.Equal(readable, QuietWeekPanel.IsReadable(top, height, viewport));
}
