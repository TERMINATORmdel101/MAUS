using Maus.Core.Fixes;
using Maus.Core.Follow;
using Maus.Core.Reporting;
using Maus.Core.Workshop;
using Maus.Core.Workshop.Benchmark;

namespace Maus.Core.Tests.Follow;

/// <summary>Les calculs de la page Suivi (médiane, plage, comparaison) sur des listes vides et d'un seul élément : aucun chiffre inventé, aucun plantage.</summary>
public sealed class FollowShortListsTests
{
    private static readonly DateTimeOffset Day = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static JournalSession Session(DateTimeOffset at, params JournalEntry[] entries) => new() { Id = "s", CreatedAt = at, Entries = [.. entries] };

    private static JournalEntry Boot(EntryState state = EntryState.Applied) => new()
    {
        ChangeId = "M12.a",
        ModuleId = "M12",
        ChangeTitle = "Titre",
        Key = SettingKey.Registry("HKCU", @"Software\X", "a"),
        State = state,
    };

    private static IEnumerable<DatedBootTime> BootsAround(DateTimeOffset at, int before, int after) =>
        Enumerable.Range(1, before).Select(i => new DatedBootTime(at.AddDays(-i), 30))
            .Concat(Enumerable.Range(1, after).Select(i => new DatedBootTime(at.AddDays(i), 30)));

    private static HealthLogEntry Line(int day) => new(Day.AddDays(day)) { Elevated = true };

    private static AuditSnapshot Audit(DateTimeOffset at, params SnapshotFinding[] findings) => new(at, "1", 80, findings, MausVersion: "1");

    [Fact]
    public void The_median_of_nothing_is_not_a_number_rather_than_a_crash()
    {
        Assert.True(double.IsNaN(HealthLogSummary.Median([])));
    }

    [Theory]
    [InlineData(new[] { 7d }, 7)]
    [InlineData(new[] { 7d, 9d }, 8)]
    [InlineData(new[] { 9d, 7d, 8d }, 8)]
    public void The_median_of_a_few_values(double[] values, double expected)
    {
        Assert.Equal(expected, HealthLogSummary.Median([.. values]));
    }

    [Fact]
    public void An_empty_log_gives_no_figure()
    {
        var summary = HealthLogSummary.Build([]);

        Assert.Empty(summary.Rows);
        Assert.Equal(string.Empty, summary.LastLine);
        Assert.Single(summary.Notes);
        Assert.Empty(HealthLog.BootTimes([]));
    }

    [Fact]
    public void A_line_with_empty_lists_gives_no_figure_and_says_what_was_not_read()
    {
        var summary = HealthLogSummary.Build([new HealthLogEntry(Day) { Disks = [], Batteries = [], Boots = [] }]);

        Assert.Empty(summary.Rows);
        Assert.Equal(2, summary.Notes.Count);
    }

    [Fact]
    public void One_reading_gives_no_first_value_no_range_and_no_comparison()
    {
        var summary = HealthLogSummary.Build(
        [
            Line(0) with
            {
                Disks = [new HealthLogDisk("0", "SSD", HealthLog.SourceNvme, 1, 0)],
                Batteries = [new HealthLogBattery("0", 90)],
                Boots = [new DatedBootTime(Day, 30)],
            },
            new HealthLogEntry(Day) { Cooling = new HealthLogCooling(30, 30, 60) },
        ]);

        Assert.Equal(5, summary.Rows.Count);
        Assert.All(summary.Rows, row =>
        {
            Assert.Null(row.First);
            Assert.Null(row.Range);
            Assert.False(row.HasCurve);
        });
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    public void Boots_are_only_compared_once_there_are_enough(int count, bool hasRange)
    {
        var boots = Enumerable.Range(0, count).Select(i => new DatedBootTime(Day.AddDays(i), 30 + i)).ToList();

        var row = Assert.Single(HealthLogSummary.Build([Line(count) with { Boots = boots }]).Rows);

        Assert.Equal(hasRange, row.HasRange);
        Assert.Equal(count >= 4, row.Variation.Contains("Ces derniers temps", StringComparison.Ordinal));
        Assert.DoesNotContain("NaN", row.Variation + row.Range, StringComparison.Ordinal);
    }

    [Fact]
    public void Identical_boots_stay_within_their_own_range()
    {
        var boots = Enumerable.Range(0, 4).Select(i => new DatedBootTime(Day.AddDays(i), 30)).ToList();

        var row = Assert.Single(HealthLogSummary.Build([Line(4) with { Boots = boots }]).Rows);

        Assert.Contains("reste dans l'écart", row.Variation, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_to_compare_before_and_after()
    {
        Assert.Empty(BeforeAfter.Build([], []).Rows);
        Assert.Empty(BeforeAfter.Build([], [new DatedBootTime(Day, 30)]).Rows);
        Assert.Empty(BeforeAfter.Build([Session(Day)], []).Rows);
        Assert.Equal(0, BeforeAfter.Build([Session(Day)], []).Older);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(4, 5)]
    [InlineData(5, 4)]
    public void Too_few_boots_give_no_figure(int before, int after)
    {
        var row = Assert.Single(BeforeAfter.Build([Session(Day, Boot())], BootsAround(Day, before, after)).Rows);

        Assert.Equal(BeforeAfterVerdict.NotEnough, row.Verdict);
        Assert.False(row.HasFigures);
        Assert.Null(row.After);
    }

    [Fact]
    public void Five_identical_boots_on_each_side_show_no_effect()
    {
        var row = Assert.Single(BeforeAfter.Build([Session(Day, Boot())], BootsAround(Day, 5, 5)).Rows);

        Assert.Equal(BeforeAfterVerdict.NoVisibleEffect, row.Verdict);
    }

    [Fact]
    public void A_session_put_back_with_no_date_counts_no_boot_after_it()
    {
        var row = Assert.Single(BeforeAfter.Build([Session(Day, Boot(EntryState.Reverted))], BootsAround(Day, 5, 5)).Rows);

        Assert.Equal(BeforeAfterVerdict.NotEnough, row.Verdict);
    }

    [Fact]
    public void Comparing_audits_with_nothing_kept()
    {
        var empty = Audit(Day);
        var one = Audit(Day.AddDays(1), new SnapshotFinding("M01.a", "M01", "A", FindingStatus.Problem));

        Assert.Empty(WhatChanged.Compare([], empty, empty).Changes);
        Assert.DoesNotContain(WhatChanged.Compare([], empty, Audit(Day.AddDays(1))).NotComparable, n => n.Name is not null);
        Assert.NotNull(WhatChanged.Compare([], empty, one));
        Assert.NotNull(WhatChanged.Compare([one], one, one));
        Assert.Empty(WhatChanged.KeptTasks([])!);
        Assert.Empty(WhatChanged.KeptDrivers([])!);
        Assert.Empty(AuditComparison.Compare(empty, empty));
        Assert.Empty(AuditComparison.Compare(one, one));
    }

    [Fact]
    public void Windows_undid_with_nothing_kept()
    {
        Assert.Null(WindowsUndid.LastAudit([]));

        var report = WindowsUndid.Compare([], null, null, null, null);

        Assert.Empty(report.Fixes);
        Assert.Null(report.CheckedAt);
        Assert.False(report.BuildChanged);

        var one = WindowsUndid.Compare([Session(Day, Boot())], null, null, null, null);
        Assert.Equal(FixHoldState.NotChecked, Assert.Single(one.Fixes).State);
    }

    [Fact]
    public void Quiet_week_with_no_or_one_audit()
    {
        var on = new Maus.Core.Engine.Broker.ScheduleState(DayOfWeek.Monday, null);

        Assert.Equal(QuietWeekState.Unread, QuietWeek.Build([], null, null, Day).State);
        Assert.Equal(QuietWeekState.Waiting, QuietWeek.Build([], null, on, Day).State);
        Assert.Equal(QuietWeekState.Waiting, QuietWeek.Build([], Day, on, Day).State);

        var one = QuietWeek.Build([Audit(Day) with { Weekly = true }], null, on, Day.AddDays(1));
        Assert.Equal(QuietWeekState.NotComparable, one.State);
        Assert.Empty(one.Points);
    }

    [Fact]
    public void A_card_with_nothing_read()
    {
        Assert.Null(PartsCard.Apply(null, [], Day));
        Assert.Empty(NewPartView.Build(null, [], null));
        Assert.Empty(PartCheck.From(Audit(Day)));

        var card = PartsCard.Apply(null, [new HardwarePart(PartKind.Screen, "A", "A")], Day);
        Assert.Empty(NewPartView.Build(card, [], null));
        Assert.Empty(NewPartView.Select(PartKind.Disk, string.Empty, []));
    }

    [Fact]
    public void Scores_with_no_or_one_pass()
    {
        var one = new BenchmarkReport { Date = Day, Completed = true, OverallScore = 10 };
        var none = new BenchmarkReport { Date = Day };

        Assert.Empty(BenchmarkComparison.Ranking([]));
        Assert.Empty(BenchmarkComparison.Ranking([none]));
        Assert.Single(BenchmarkComparison.Ranking([one]));
        Assert.All(BenchmarkComparison.Compare(none, none), row => Assert.Null(row.Difference));
        Assert.Equal((null, null), BenchmarkScoring.Extremes([]));
        Assert.Equal((null, null), BenchmarkScoring.Extremes([new BenchmarkTestResult("a", "gpu", 1, "fps", 1)]));
        Assert.Equal(0, BenchmarkScoring.Combine([]));
        Assert.Null(BenchmarkScoring.Low1Fps([]));
        Assert.Null(BenchmarkHistory.CompareToPrevious([], new BenchmarkEntry("ram", 1, true, Day)));
        Assert.Null(BenchmarkHistory.CompareToPrevious([new BenchmarkEntry("ram", 0, true, Day)], new BenchmarkEntry("ram", 1, true, Day.AddDays(1))));
        Assert.Equal("Pas encore de résultat.", ScoreTrends.Describe([]));
        Assert.Equal("Pas encore de résultat.", ScoreTrends.DescribeHealth([]));
        Assert.Empty(ScoreTrends.Series([], "ram"));
    }
}
