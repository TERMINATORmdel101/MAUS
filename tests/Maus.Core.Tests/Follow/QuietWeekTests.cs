using Maus.Core.Engine.Broker;
using Maus.Core.Follow;
using Maus.Core.Reporting;

namespace Maus.Core.Tests.Follow;

public class QuietWeekTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly ScheduleState On = new(DayOfWeek.Monday, "maus");

    private static SnapshotFinding Finding(string id, FindingStatus status = FindingStatus.Ok, bool wanted = false) =>
        new(id, "M01", "Titre " + id, status, Acknowledged: wanted);

    /// <summary>Audit de la semaine, <paramref name="daysAgo"/> jours avant maintenant.</summary>
    private static AuditSnapshot Audit(int daysAgo, bool elevated, params SnapshotFinding[] findings) =>
        new(Now.AddDays(-daysAgo), "26200", elevated ? 80 : AuditSnapshot.NoScore, findings, Weekly: true, MausVersion: "0.8.6");

    private static AuditSnapshot Weekly(int daysAgo, params SnapshotFinding[] findings) => Audit(daysAgo, false, findings);

    [Fact]
    public void Disabled_says_so_even_with_old_summaries()
    {
        var summary = QuietWeek.Build([Weekly(30, Finding("a"))], null, new ScheduleState(null, null), Now);

        Assert.Equal(QuietWeekState.Off, summary.State);
        Assert.False(summary.ShowsAudits);
    }

    [Fact]
    public void Enabled_with_nothing_kept_says_what_will_fill_the_card()
    {
        var summary = QuietWeek.Build([], Now.AddDays(-3), On, Now);

        Assert.Equal(QuietWeekState.Waiting, summary.State);
        Assert.False(summary.ShowsAudits);
    }

    [Fact]
    public void An_unread_task_claims_nothing_without_a_summary_but_keeps_what_is_kept()
    {
        Assert.Equal(QuietWeekState.Unread, QuietWeek.Build([], null, null, Now).State);
        Assert.Equal(QuietWeekState.Quiet, QuietWeek.Build([Weekly(14, Finding("a")), Weekly(7, Finding("a"))], null, null, Now).State);
    }

    [Fact]
    public void The_task_of_another_account_is_named_as_such()
    {
        var summary = QuietWeek.Build([Weekly(7, Finding("a"))], null, new ScheduleState(DayOfWeek.Friday, "maus", OfAnotherAccount: true), Now);

        Assert.Equal(QuietWeekState.OtherAccount, summary.State);
    }

    [Fact]
    public void Nothing_new_is_counted_since_the_visit_and_limited_to_what_was_read()
    {
        var kept = new[] { Weekly(21, Finding("a")), Weekly(14, Finding("a")), Weekly(7, Finding("a")) };

        var summary = QuietWeek.Build(kept, Now.AddDays(-20), On, Now);

        Assert.Equal(QuietWeekState.Quiet, summary.State);
        Assert.Equal(2, summary.Audits);
        Assert.Contains("2 audits depuis votre dernière visite, rien de nouveau sur ce qu'ils ont pu lire", summary.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void A_complete_audit_does_not_add_the_limit()
    {
        var kept = new[] { Audit(14, true, Finding("a")), Audit(7, true, Finding("a")) };

        Assert.DoesNotContain("pu lire", QuietWeek.Build(kept, Now.AddDays(-10), On, Now).Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_finding_is_dated_and_a_worse_one_named()
    {
        var kept = new[]
        {
            Weekly(21, Finding("a"), Finding("b")),
            Weekly(14, Finding("a", FindingStatus.Problem), Finding("b")),
            Weekly(7, Finding("a", FindingStatus.Problem), Finding("b"), Finding("c", FindingStatus.Warning)),
        };

        var summary = QuietWeek.Build(kept, Now.AddDays(-20), On, Now);

        Assert.Equal(QuietWeekState.News, summary.State);
        Assert.Contains("2 constats nouveaux ou aggravés", summary.Headline, StringComparison.Ordinal);
        Assert.Equal(["Titre a", "Titre c"], summary.Points.Select(p => p.Title));
        Assert.True(summary.Points[0].Worse);
        Assert.False(summary.Points[1].Worse);
        Assert.Equal(Now.AddDays(-7), summary.Points[1].At);
    }

    [Fact]
    public void What_came_and_went_between_two_visits_is_still_told_once()
    {
        var kept = new[]
        {
            Weekly(21, Finding("a")),
            Weekly(14, Finding("a"), Finding("c", FindingStatus.Warning)),
            Weekly(7, Finding("a")),
        };

        Assert.Single(QuietWeek.Build(kept, Now.AddDays(-20), On, Now).Points);
    }

    [Fact]
    public void A_finding_marked_wanted_and_an_unread_one_are_not_news()
    {
        var kept = new[]
        {
            Weekly(14, Finding("a"), Finding("b", FindingStatus.Unknown)),
            Weekly(7, Finding("a"), Finding("b", FindingStatus.Problem), Finding("c", FindingStatus.Warning, wanted: true)),
        };

        Assert.Equal(QuietWeekState.Quiet, QuietWeek.Build(kept, Now.AddDays(-10), On, Now).State);
    }

    [Fact]
    public void Audits_with_and_without_rights_are_not_compared()
    {
        var kept = new[]
        {
            Audit(14, true, Finding("a"), Finding("admin", FindingStatus.Problem)),
            Audit(7, false, Finding("a")),
            Audit(3, true, Finding("a"), Finding("admin", FindingStatus.Problem)),
        };

        // Le sans-droits du milieu lit moins : il ne fait ni disparaître puis réapparaître « admin », ni une fausse nouveauté.
        Assert.Equal(QuietWeekState.Quiet, QuietWeek.Build(kept, Now.AddDays(-10), On, Now).State);
    }

    [Fact]
    public void A_lone_audit_cannot_say_nothing_new()
    {
        var summary = QuietWeek.Build([Weekly(7, Finding("a", FindingStatus.Problem))], Now.AddDays(-20), On, Now);

        Assert.Equal(QuietWeekState.NotComparable, summary.State);
        Assert.DoesNotContain("rien de nouveau", summary.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void No_audit_since_the_visit_is_said_with_the_dates()
    {
        var summary = QuietWeek.Build([Weekly(30, Finding("a"))], Now.AddDays(-3), On, Now);

        Assert.Equal(QuietWeekState.NoneSince, summary.State);
        Assert.True(summary.ShowsAudits);
    }

    [Fact]
    public void A_long_absence_says_at_least_and_where_the_comparison_starts()
    {
        var kept = Enumerable.Range(1, AuditSnapshotStore.KeepWeekly).Select(w => Weekly(w * 7, Finding("a"))).ToArray();

        var summary = QuietWeek.Build(kept, Now.AddDays(-90), On, Now);

        Assert.Contains($"Au moins {AuditSnapshotStore.KeepWeekly} audits", summary.Headline, StringComparison.Ordinal);
        Assert.Contains(summary.Notes, n => n.Contains("le plus ancien gardé", StringComparison.Ordinal));
    }

    [Fact]
    public void A_first_visit_counts_what_is_kept_without_inventing_a_visit()
    {
        var summary = QuietWeek.Build([Weekly(14, Finding("a")), Weekly(7, Finding("a"))], null, On, Now);

        Assert.Contains("2 audits de la semaine gardés", summary.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void Dates_in_the_future_and_manual_audits_are_ignored()
    {
        var manual = new AuditSnapshot(Now.AddDays(-5), "26200", 50, [Finding("z", FindingStatus.Problem)]);

        var summary = QuietWeek.Build([Weekly(14, Finding("a")), manual, Weekly(7, Finding("a")), Weekly(-30, Finding("x", FindingStatus.Problem))], Now.AddDays(40), On, Now);

        Assert.Equal(QuietWeekState.Quiet, summary.State);
        Assert.Equal(2, summary.Audits);
    }

    [Fact]
    public void A_damaged_file_shows_nothing_false()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("maus-quiet-").FullName, "snapshots.json");
        File.WriteAllText(path, "{ abîmé");

        var summary = QuietWeek.Build(new AuditSnapshotStore(path).Load(), Now.AddDays(-3), On, Now);

        Assert.Equal(QuietWeekState.Waiting, summary.State);
    }

    [Fact]
    public void A_long_list_is_cut_and_the_rest_is_counted()
    {
        var many = Enumerable.Range(0, QuietWeekSummary.ShownPoints + 3).Select(i => Finding("n" + i, FindingStatus.Warning)).ToArray();

        var summary = QuietWeek.Build([Weekly(14, Finding("a")), Weekly(7, [Finding("a"), .. many])], Now.AddDays(-10), On, Now);

        Assert.Equal(QuietWeekSummary.ShownPoints, summary.Points.Count);
        Assert.Contains(summary.Notes, n => n.Contains("Et 3", StringComparison.Ordinal));
        Assert.Contains($"{many.Length} constats", summary.Headline, StringComparison.Ordinal);
    }
}
