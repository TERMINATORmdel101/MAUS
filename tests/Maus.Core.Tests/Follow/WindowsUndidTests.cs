using Maus.Core.Fixes;
using Maus.Core.Follow;
using Maus.Core.Reporting;

namespace Maus.Core.Tests.Follow;

public sealed class WindowsUndidTests
{
    private static readonly DateTimeOffset Applied = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Audited = Applied.AddDays(2);

    private static JournalEntry Entry(string id, EntryState state = EntryState.Applied, string? note = null, string hive = "HKLM") => new()
    {
        ChangeId = id,
        ModuleId = "M06",
        ChangeTitle = "Corriger " + id,
        Key = SettingKey.Registry(hive, @"Software\Essai", id),
        State = state,
        EffectNote = note,
    };

    private static JournalSession Session(DateTimeOffset at, string? sid, params JournalEntry[] entries) => new()
    {
        Id = at.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        CreatedAt = at,
        UserSid = sid,
        Entries = [.. entries],
    };

    private static JournalSession Session(params JournalEntry[] entries) => Session(Applied, null, entries);

    private static AuditSnapshot Audit(params (string Id, FindingStatus Status)[] findings) =>
        new(Audited, "26200.1000", 80, [.. findings.Select(f => new SnapshotFinding(f.Id, "M06", "Titre", f.Status))]);

    private static FixHold One(FixHoldReport report) => Assert.Single(report.Fixes);

    [Fact]
    public void No_session_gives_an_empty_report_that_does_not_crash()
    {
        var report = WindowsUndid.Compare([], null, null, null, null);

        Assert.Empty(report.Fixes);
        Assert.Null(report.CheckedAt);
        Assert.False(report.BuildChanged);
    }

    [Fact]
    public void Only_what_is_still_applied_is_listed()
    {
        var report = WindowsUndid.Compare([Session(Entry("a"), Entry("b", EntryState.Reverted), Entry("c", EntryState.Failed), Entry("d", EntryState.RevertSkipped))], Audit(("a", FindingStatus.Ok)), null, null, null);

        Assert.Equal("a", One(report).ChangeId);
    }

    [Fact]
    public void Without_an_audit_nothing_is_called_in_place()
    {
        var fix = One(WindowsUndid.Compare([Session(Entry("a"))], null, null, null, "26200.1000"));

        Assert.Equal(FixHoldState.NotChecked, fix.State);
        Assert.Equal(NotCheckedReason.NoAudit, fix.Reason);
    }

    [Theory]
    [InlineData(FindingStatus.Ok, FixHoldState.InPlace)]
    [InlineData(FindingStatus.Info, FixHoldState.InPlace)]
    [InlineData(FindingStatus.Improvable, FixHoldState.NotInPlace)]
    [InlineData(FindingStatus.Warning, FixHoldState.NotInPlace)]
    [InlineData(FindingStatus.Problem, FixHoldState.NotInPlace)]
    [InlineData(FindingStatus.Unknown, FixHoldState.NotChecked)]
    public void The_kept_audit_answers_when_there_is_no_plan(FindingStatus status, FixHoldState expected)
    {
        var fix = One(WindowsUndid.Compare([Session(Entry("a"))], Audit(("a", status)), null, null, null));

        Assert.Equal(expected, fix.State);
    }

    [Fact]
    public void A_fix_proposed_again_is_no_longer_in_place_even_without_its_finding()
    {
        var fix = One(WindowsUndid.Compare([Session(Entry("a"))], Audit(("autre", FindingStatus.Ok)), new HashSet<string> { "a" }, null, null));

        Assert.Equal(FixHoldState.NotInPlace, fix.State);
    }

    [Fact]
    public void A_fix_with_no_finding_of_its_name_needs_a_plan_to_be_called_in_place()
    {
        var audit = Audit(("autre", FindingStatus.Ok));
        var sessions = new[] { Session(Entry("a")) };

        var withoutPlan = One(WindowsUndid.Compare(sessions, audit, null, null, null));
        var withPlan = One(WindowsUndid.Compare(sessions, audit, new HashSet<string>(), null, null));

        Assert.Equal(NotCheckedReason.NeedsNewAudit, withoutPlan.Reason);
        Assert.Equal(FixHoldState.InPlace, withPlan.State);
    }

    [Fact]
    public void A_module_left_out_of_the_audit_is_not_called_in_place()
    {
        var otherModule = new AuditSnapshot(Audited, "26200.1000", 80, [new SnapshotFinding("x", "M99", "Titre", FindingStatus.Ok)]);

        var fix = One(WindowsUndid.Compare([Session(Entry("a"))], otherModule, new HashSet<string>(), null, null));

        Assert.Equal(NotCheckedReason.ModuleNotRead, fix.Reason);
    }

    [Fact]
    public void A_fix_written_after_the_audit_has_not_been_checked()
    {
        var late = Session(Audited.AddHours(1), null, Entry("a"));

        var fix = One(WindowsUndid.Compare([late], Audit(("a", FindingStatus.Ok)), new HashSet<string> { "a" }, null, null));

        Assert.Equal(NotCheckedReason.AppliedSinceAudit, fix.Reason);
    }

    [Fact]
    public void A_setting_in_another_profile_is_not_judged_from_this_account()
    {
        var theirs = Session(Applied, "S-1-5-21-autre", Entry("a", hive: "HKCU"));

        var fix = One(WindowsUndid.Compare([theirs], Audit(("a", FindingStatus.Improvable)), new HashSet<string> { "a" }, "S-1-5-21-moi", null));

        Assert.Equal(NotCheckedReason.OtherAccount, fix.Reason);
    }

    [Fact]
    public void An_audit_made_with_another_account_does_not_judge_a_setting_of_this_profile()
    {
        var mine = Session(Applied, "S-1-5-21-moi", Entry("a", hive: "HKCU"), Entry("b"));
        var audit = Audit(("a", FindingStatus.Ok), ("b", FindingStatus.Ok)) with { OtherAccount = true };

        var report = WindowsUndid.Compare([mine], audit, null, "S-1-5-21-moi", null);

        Assert.Equal(NotCheckedReason.AuditOfOtherAccount, report.Fixes.Single(f => f.ChangeId == "a").Reason);
        Assert.Equal(FixHoldState.InPlace, report.Fixes.Single(f => f.ChangeId == "b").State);
    }

    [Fact]
    public void A_setting_common_to_the_pc_is_judged_whoever_wrote_it()
    {
        var theirs = Session(Applied, "S-1-5-21-autre", Entry("a"));

        var fix = One(WindowsUndid.Compare([theirs], Audit(("a", FindingStatus.Ok)), null, "S-1-5-21-moi", null));

        Assert.Equal(FixHoldState.InPlace, fix.State);
    }

    [Fact]
    public void A_fix_written_twice_counts_once_with_its_latest_date_and_note()
    {
        var first = Session(Applied, null, Entry("a", note: "ancienne note"));
        var second = Session(Applied.AddDays(1), null, Entry("a"));

        var fix = One(WindowsUndid.Compare([first, second], Audit(("a", FindingStatus.Ok)), null, null, null));

        Assert.Equal(Applied.AddDays(1), fix.AppliedOn);
        Assert.Null(fix.EffectNote);
    }

    [Fact]
    public void What_is_no_longer_in_place_comes_first()
    {
        var report = WindowsUndid.Compare([Session(Entry("a"), Entry("b"), Entry("c"))], Audit(("a", FindingStatus.Ok), ("b", FindingStatus.Unknown), ("c", FindingStatus.Problem)), null, null, null);

        Assert.Equal(["c", "b", "a"], report.Fixes.Select(f => f.ChangeId));
        Assert.Equal(1, report.Count(FixHoldState.InPlace));
    }

    [Theory]
    [InlineData("26200.1000", "26200.1000", false, true)]
    [InlineData("26200.1000", "26200.2000", true, true)]
    [InlineData(null, "26200.2000", false, false)]
    [InlineData("", "26200.2000", false, false)]
    [InlineData("26200.1000", null, false, false)]
    public void A_build_change_is_said_only_when_both_are_known(string? audit, string? current, bool changed, bool known)
    {
        var report = new FixHoldReport([], Audited, audit, current);

        Assert.Equal(changed, report.BuildChanged);
        Assert.Equal(known, report.KnowsBothBuilds);
    }

    [Fact]
    public void The_last_audit_is_the_last_one_started_by_hand()
    {
        var byHand = new AuditSnapshot(Audited, "a", 80, []);
        var weekly = new AuditSnapshot(Audited.AddDays(1), "b", AuditSnapshot.NoScore, [], Weekly: true);

        Assert.Same(byHand, WindowsUndid.LastAudit([byHand, weekly]));
        Assert.Null(WindowsUndid.LastAudit([weekly]));
        Assert.Null(WindowsUndid.LastAudit([]));
    }

    [Fact]
    public void A_damaged_snapshot_file_reads_as_no_audit_and_never_as_healthy()
    {
        var folder = Directory.CreateTempSubdirectory("maus-undid-").FullName;
        try
        {
            var path = Path.Combine(folder, "audit-snapshots.json");
            File.WriteAllText(path, "{ pas du json");
            var kept = new AuditSnapshotStore(path, _ => { }).Load();

            var fix = One(WindowsUndid.Compare([Session(Entry("a"))], WindowsUndid.LastAudit(kept), null, null, null));

            Assert.Equal(NotCheckedReason.NoAudit, fix.Reason);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
