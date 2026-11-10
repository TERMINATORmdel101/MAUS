using Maus.Core.Follow;
using Maus.Core.Reporting;
using Maus.Core.Workshop;

namespace Maus.Core.Tests.Follow;

/// <summary>« Qu'est-ce qui a changé ? » : ce qui est dit, ce qui ne l'est jamais, et ce qui reste « non comparable ».</summary>
public sealed class WhatChangedTests : IDisposable
{
    private const string Version = "0.8.6";

    private static readonly DateTimeOffset Day = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "maus-whatchanged-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static SnapshotFinding F(string id, FindingStatus status = FindingStatus.Ok, string module = "M12", bool? enabled = null) => new(id, module, "Titre de " + id, status, Enabled: enabled);

    private static SnapshotFinding Summary => F("M12.summary", FindingStatus.Info);

    private static AuditSnapshot Audit(int day, params SnapshotFinding[] findings) => new(Day.AddDays(day), "26300.1", 80, findings, MausVersion: Version, Tasks: [], Drivers: []);

    private static WhatChangedReport Compare(AuditSnapshot older, AuditSnapshot newer) => WhatChanged.Compare([older, newer], older, newer);

    [Fact]
    public void A_new_startup_program_is_shown_with_the_two_dates_that_frame_it()
    {
        var older = Audit(0, Summary);
        var newer = Audit(5, Summary, F("M12.hkcu-run-steam", enabled: true));

        var report = Compare(older, newer);

        var change = Assert.Single(report.Changes);
        Assert.Equal(ChangeFamily.Startup, change.Family);
        Assert.Equal(ChangeType.Appeared, change.Type);
        Assert.Equal(older.At, change.LastBefore);
        Assert.Equal(newer.At, change.FirstAfter);
        Assert.Equal("M12.hkcu-run-steam", change.FindingId);
        Assert.Empty(report.NotComparable);
    }

    [Fact]
    public void Other_startup_findings_are_not_programs()
    {
        var older = Audit(0, Summary);
        var newer = Audit(1, Summary, F("M12.boot-time"), F("M12.third-party-services", FindingStatus.Info), F("M12.logon-tasks", FindingStatus.Info));

        Assert.Empty(Compare(older, newer).Changes);
    }

    [Fact]
    public void A_startup_program_switched_off_is_a_change_only_when_both_audits_kept_its_state()
    {
        var on = Audit(0, Summary, F("M12.hkcu-run-a", enabled: true), F("M12.hkcu-run-b"));
        var off = Audit(1, Summary, F("M12.hkcu-run-a", enabled: false), F("M12.hkcu-run-b", enabled: false));

        var change = Assert.Single(Compare(on, off).Changes);

        Assert.Equal(ChangeType.Changed, change.Type);
        Assert.Equal(WhatChanged.StartupEnabled, change.Before);
        Assert.Equal(WhatChanged.StartupDisabled, change.After);
    }

    [Fact]
    public void The_date_narrows_on_the_audits_in_between()
    {
        var older = Audit(0, Summary);
        var absent = Audit(2, Summary);
        var seen = Audit(4, Summary, F("M12.hkcu-run-a"));
        var newer = Audit(6, Summary, F("M12.hkcu-run-a"));

        var change = Assert.Single(WhatChanged.Compare([older, absent, seen, newer], older, newer).Changes);

        Assert.Equal(absent.At, change.LastBefore);
        Assert.Equal(seen.At, change.FirstAfter);
    }

    [Fact]
    public void A_weekly_audit_in_between_never_narrows_the_date_by_its_silence()
    {
        var older = Audit(0, Summary);
        var weekly = Audit(3, Summary) with { Weekly = true, Score = AuditSnapshot.NoScore };
        var newer = Audit(6, Summary, F("M12.hkcu-run-a"));

        var change = Assert.Single(WhatChanged.Compare([older, weekly, newer], older, newer).Changes);

        Assert.Equal(older.At, change.LastBefore);
    }

    [Fact]
    public void A_control_added_by_a_new_maus_is_never_a_change_of_the_pc()
    {
        var older = Audit(0, Summary, F("M05.old", module: "M05")) with { MausVersion = "0.8.5" };
        var newer = Audit(1, Summary, F("M05.old", module: "M05"), F("M05.added", FindingStatus.Problem, "M05"));

        var report = Compare(older, newer);

        Assert.Empty(report.Changes);
        var note = Assert.Single(report.NotComparable);
        Assert.Equal(NotComparableReason.MausVersionChanged, note.Reason);
        Assert.Equal(ChangeType.Appeared, note.Side);
        Assert.False(report.SameMausVersion);
    }

    [Fact]
    public void Compare_never_counts_a_new_control_as_an_aggravation()
    {
        var older = new AuditSnapshot(Day, "1", 80, [F("a", module: "M05")], MausVersion: "0.8.5");
        var newer = new AuditSnapshot(Day.AddDays(1), "1", 80, [F("a", module: "M05"), F("n", FindingStatus.Problem, "M05")], MausVersion: "0.8.6");

        Assert.Equal(AuditChangeKind.NotComparable, Assert.Single(AuditComparison.Compare(older, newer)).Kind);
        Assert.Equal(AuditChangeKind.New, Assert.Single(AuditComparison.Compare(older with { MausVersion = "0.8.6" }, newer)).Kind);

        // Résumé écrit avant le champ : sa version est inconnue, il ne prouve rien.
        Assert.Equal(AuditChangeKind.NotComparable, Assert.Single(AuditComparison.Compare(older with { MausVersion = null }, newer)).Kind);
    }

    [Fact]
    public void A_verdict_changed_across_two_versions_of_maus_says_so()
    {
        var older = Audit(0, F("M05.a", FindingStatus.Ok, "M05")) with { MausVersion = "0.8.5" };
        var newer = Audit(1, F("M05.a", FindingStatus.Problem, "M05"));

        var change = Assert.Single(Compare(older, newer).Changes);

        Assert.Equal(ChangeType.Changed, change.Type);
        Assert.True(change.MausVersionChanged);
        Assert.Equal(FindingStatus.Problem, change.StatusAfter);
        Assert.Equal("M05.a", change.FindingId);
    }

    [Fact]
    public void A_weekly_audit_reads_less_so_nothing_disappears_because_of_it()
    {
        var full = Audit(0, Summary, F("M12.hklm-run-a"), F("M05.problem", FindingStatus.Problem, "M05"), F("M05.ok", module: "M05"));
        var weekly = Audit(7, Summary, F("M05.ok", module: "M05")) with { Weekly = true, Score = AuditSnapshot.NoScore, Tasks = null, Drivers = null };

        var report = Compare(full, weekly);

        Assert.Empty(report.Changes);
        Assert.Contains(report.NotComparable, n => n.Family == ChangeFamily.Startup && n.Reason == NotComparableReason.ReadsLess && n.Side == ChangeType.Gone && n.WithoutRights);
        Assert.Contains(report.NotComparable, n => n.Family == ChangeFamily.Finding && n.Reason == NotComparableReason.ReadsLess && n.Side == ChangeType.Gone);
        Assert.Contains(report.NotComparable, n => n.Family == ChangeFamily.Task && n.Reason == NotComparableReason.NotKept && n.WithoutRights);
    }

    [Fact]
    public void Something_a_weekly_audit_did_see_and_a_fuller_one_did_not_has_really_appeared()
    {
        var full = Audit(0, Summary);
        var weekly = Audit(7, Summary, F("M12.hkcu-run-a")) with { Weekly = true, Score = AuditSnapshot.NoScore };

        Assert.Equal(ChangeType.Appeared, Assert.Single(Compare(full, weekly).Changes).Type);
    }

    [Fact]
    public void Absent_from_a_weekly_audit_then_seen_by_a_fuller_one_is_not_new()
    {
        var weekly = Audit(0, Summary) with { Weekly = true, Score = AuditSnapshot.NoScore };
        var full = Audit(7, Summary, F("M12.hkcu-run-a"));

        var report = Compare(weekly, full);

        Assert.Empty(report.Changes);
        Assert.Equal(ChangeType.Appeared, Assert.Single(report.NotComparable).Side);
    }

    [Fact]
    public void Two_weekly_audits_without_rights_read_the_same_and_compare()
    {
        var first = Audit(0, Summary, F("M12.hkcu-run-a")) with { Weekly = true, Score = AuditSnapshot.NoScore };
        var second = Audit(7, Summary) with { Weekly = true, Score = AuditSnapshot.NoScore };

        Assert.Equal(ChangeType.Gone, Assert.Single(Compare(first, second).Changes).Type);
    }

    [Fact]
    public void A_startup_source_not_read_hides_nothing_and_invents_nothing()
    {
        var older = Audit(0, Summary, F("M12.hklm-run-a"));
        var newer = Audit(1, Summary, new SnapshotFinding("M12.source-hklm-run", "M12", "Lecture du registre", FindingStatus.Unknown));

        var report = Compare(older, newer);

        Assert.Empty(report.Changes);
        var note = Assert.Single(report.NotComparable);
        Assert.Equal(NotComparableReason.SourceNotRead, note.Reason);
        Assert.Equal("Lecture du registre", note.Name);
    }

    [Fact]
    public void A_startup_source_never_seen_before_a_new_maus_is_not_comparable()
    {
        var older = Audit(0, Summary, F("M12.hkcu-run-a")) with { MausVersion = "0.8.5" };
        var newer = Audit(1, Summary, F("M12.hkcu-run-a"), F("M12.hkcu-run-b"), F("M12.store-c"));

        var report = Compare(older, newer);

        // La source « hkcu-run » était déjà lue : « b » est bien nouveau. « store » n'avait rien montré : peut-être un nouveau contrôle.
        Assert.Equal("Titre de M12.hkcu-run-b", Assert.Single(report.Changes).Name);
        Assert.Equal("Titre de M12.store-c", report.NotComparable.Single(n => n.Reason == NotComparableReason.MausVersionChanged).Name);
    }

    [Fact]
    public void An_audit_made_with_another_account_proves_nothing_about_what_belongs_to_the_user()
    {
        var mine = Audit(0, Summary, F("M12.hkcu-run-a"), F("M05.a", FindingStatus.Problem, "M05"), F("M05.b", module: "M05")) with { Drivers = [new("Carte", "1")] };
        var other = Audit(1, Summary, F("M05.b", module: "M05")) with { OtherAccount = true, Drivers = [] };

        var report = Compare(mine, other);

        // Les pilotes sont ceux de la machine : leur disparition reste vue.
        Assert.Equal(ChangeFamily.Driver, Assert.Single(report.Changes).Family);
        Assert.Equal(2, report.NotComparable.Count(n => n.Reason == NotComparableReason.OtherAccount && n.At == other.At));
        Assert.Equal(AuditChangeKind.NotComparable, Assert.Single(AuditComparison.Compare(mine, other)).Kind);
    }

    [Fact]
    public void A_verdict_between_two_accounts_or_two_reading_levels_says_it_may_come_from_that()
    {
        var mine = Audit(0, Summary, F("M12.hkcu-run-a", enabled: true), F("M05.a", FindingStatus.Ok, "M05"));
        var other = Audit(1, Summary, F("M12.hkcu-run-a", enabled: false), F("M05.a", FindingStatus.Warning, "M05")) with { OtherAccount = true };

        var report = Compare(mine, other);

        // L'entrée de démarrage du profil n'est pas dite « désactivée » : c'est une note.
        Assert.Contains(report.NotComparable, n => n.Reason == NotComparableReason.OtherAccount && n.Name == "Titre de M12.hkcu-run-a");
        var verdict = Assert.Single(report.Changes);
        Assert.Equal(ChangeFamily.Finding, verdict.Family);
        Assert.True(verdict.OtherAccount);
        Assert.False(verdict.ReadingChanged);

        var weekly = Audit(1, Summary, F("M05.a", FindingStatus.Ok, "M05")) with { Weekly = true, Score = AuditSnapshot.NoScore };
        var manual = Audit(2, Summary, F("M05.a", FindingStatus.Warning, "M05"));
        var seen = Compare(weekly, manual).Changes.Single(c => c.Family == ChangeFamily.Finding);
        Assert.True(seen.ReadingChanged);
        Assert.False(seen.OtherAccount);

        // Même niveau de lecture, même compte : rien à signaler.
        var plain = Compare(manual, Audit(3, Summary, F("M05.a", FindingStatus.Ok, "M05"))).Changes.Single(c => c.Family == ChangeFamily.Finding);
        Assert.False(plain.ReadingChanged);
        Assert.False(plain.OtherAccount);
    }

    [Fact]
    public void A_module_read_only_once_is_said_and_never_compared()
    {
        var older = Audit(0, F("M05.a", FindingStatus.Problem, "M05"), F("M06.b", FindingStatus.Ok, "M06"));
        var newer = Audit(1, F("M06.b", FindingStatus.Ok, "M06"));

        var report = Compare(older, newer);

        Assert.Empty(report.Changes);
        var note = report.NotComparable.Single(n => n.Reason == NotComparableReason.ModuleNotRead);
        Assert.Equal("M05", note.Name);
        Assert.Equal(newer.At, note.At);
    }

    [Fact]
    public void An_undetermined_control_is_counted_never_shown_as_gone()
    {
        var older = Audit(0, Summary, F("M05.a", FindingStatus.Problem, "M05"), F("M05.b", FindingStatus.Ok, "M05"));
        var newer = Audit(1, Summary, F("M05.a", FindingStatus.Unknown, "M05"), F("M05.b", FindingStatus.Ok, "M05"));

        var report = Compare(older, newer);

        Assert.Empty(report.Changes);
        var note = Assert.Single(report.NotComparable);
        Assert.Equal(NotComparableReason.Undetermined, note.Reason);
        Assert.Equal(1, note.Count);
    }

    [Fact]
    public void A_finding_that_appears_or_goes_with_the_same_maus_is_a_change_with_its_link()
    {
        var older = Audit(0, F("M05.keep", module: "M05"), F("M05.gone", FindingStatus.Warning, "M05"));
        var newer = Audit(1, F("M05.keep", module: "M05"), F("M05.new", FindingStatus.Problem, "M05"));

        var report = Compare(older, newer);

        Assert.Equal(2, report.Changes.Count);
        var added = report.Changes.Single(c => c.Type == ChangeType.Appeared);
        Assert.Equal(FindingStatus.Problem, added.StatusAfter);
        Assert.Equal("M05.new", added.FindingId);
        Assert.Equal(FindingStatus.Warning, report.Changes.Single(c => c.Type == ChangeType.Gone).StatusBefore);
    }

    [Fact]
    public void Windows_tasks_and_drivers_are_compared()
    {
        var older = Audit(0) with
        {
            Build = "26300.1",
            Tasks = [new("Adobe"), new("Ancienne")],
            Drivers = [new("Carte", "1.0"), new("Clé", "3"), new("Ancien pilote", "1")],
        };
        var newer = Audit(1) with
        {
            Build = "26300.2",
            Tasks = [new("adobe"), new("Nouvelle")],
            Drivers = [new("Carte", "2.0"), new("Clé", "3"), new("Nouveau pilote", "4")],
        };

        var report = Compare(older, newer);

        var windows = report.Changes.Single(c => c.Family == ChangeFamily.Windows);
        Assert.Equal("26300.1", windows.Before);
        Assert.Equal("26300.2", windows.After);
        Assert.Equal(["Ancienne", "Nouvelle"], report.Changes.Where(c => c.Family == ChangeFamily.Task).Select(c => c.Name).Order(StringComparer.Ordinal));
        var driver = report.Changes.Single(c => c.Family == ChangeFamily.Driver && c.Type == ChangeType.Changed);
        Assert.Equal("Carte", driver.Name);
        Assert.Equal("1.0", driver.Before);
        Assert.Equal("2.0", driver.After);
        Assert.Equal("Nouveau pilote", report.Changes.Single(c => c.Family == ChangeFamily.Driver && c.Type == ChangeType.Appeared).Name);
        Assert.Equal("Ancien pilote", report.Changes.Single(c => c.Family == ChangeFamily.Driver && c.Type == ChangeType.Gone).Name);
    }

    [Fact]
    public void A_summary_written_before_these_fields_is_read_and_says_what_it_cannot_tell()
    {
        var path = Path.Combine(_folder, "audit-snapshots.json");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(path, """[{"At":"2026-11-01T09:00:00+00:00","Build":"1","Score":80,"Findings":[{"Id":"M12.summary","ModuleId":"M12","Title":"s","Status":"Info"},{"Id":"M12.hkcu-run-a","ModuleId":"M12","Title":"a","Status":"Ok"}],"IsPartial":false}]""");
        var old = Assert.Single(new AuditSnapshotStore(path).Load());
        Assert.Null(old.MausVersion);
        Assert.Null(old.Tasks);
        Assert.Null(old.Drivers);
        Assert.Null(old.Findings[1].Enabled);

        var report = Compare(old, new AuditSnapshot(old.At.AddDays(1), "1", 80, [F("M12.summary", FindingStatus.Info), F("M12.hkcu-run-a", enabled: true), F("M12.hkcu-run-b", enabled: true)], MausVersion: Version, Tasks: [], Drivers: []));

        Assert.False(report.SameMausVersion);
        Assert.Equal("Titre de M12.hkcu-run-b", Assert.Single(report.Changes).Name);
        Assert.Contains(report.NotComparable, n => n.Family == ChangeFamily.Task && n.Reason == NotComparableReason.NotKept);
        Assert.Contains(report.NotComparable, n => n.Family == ChangeFamily.Driver && n.Reason == NotComparableReason.NotKept);
    }

    [Fact]
    public void Lines_without_a_name_in_a_retouched_file_are_ignored()
    {
        var older = Audit(0) with { Drivers = [new("Carte", "1"), null!, new(null!, "2")], Build = null! };
        var newer = Audit(1) with { Drivers = [new("Carte", "1")] };

        Assert.Empty(Compare(older, newer).Changes);
    }

    [Fact]
    public void Nothing_to_compare_and_nothing_changed_give_an_empty_answer()
    {
        var one = Audit(0, Summary);
        var report = WhatChanged.Compare([], one, Audit(1, Summary));

        Assert.Empty(report.Changes);
        Assert.Empty(report.NotComparable);
    }

    [Fact]
    public void Kept_drivers_leave_out_microsoft_and_the_device_identifier()
    {
        DriverEntry Driver(string name, string? provider, string instance) => new(name, "NET", DriverGroup.Network, instance, "1.0", null, provider, null, "oem1.inf", true, null);

        var kept = WhatChanged.KeptDrivers([Driver("Realtek", "Realtek", @"PCI\SERIE123"), Driver("Générique", "Microsoft", "x"), Driver("Deux fois", null, "a"), Driver("Deux fois", null, "b")]);

        Assert.Equal(["Deux fois", "Realtek"], kept!.Select(k => k.Name));
        Assert.DoesNotContain("SERIE123", string.Join(' ', kept!), StringComparison.Ordinal);
    }

    [Fact]
    public void Too_many_items_are_not_kept_rather_than_kept_in_part()
    {
        var many = Enumerable.Range(0, 501).Select(i => "tâche " + i).ToList();

        Assert.Null(WhatChanged.KeptTasks(many));
        Assert.Equal(500, WhatChanged.KeptTasks(many.Take(500))!.Count);
    }

    [Fact]
    public void Kept_tasks_lose_the_account_identifier()
    {
        var kept = WhatChanged.KeptTasks([@"OneDrive Reporting Task-S-1-5-21-111-222-333-1001", "Adobe"]);

        Assert.Equal(["Adobe", "OneDrive Reporting Task-…"], kept!.Select(k => k.Name));
    }

    [Fact]
    public void The_summary_of_an_audit_keeps_the_version_the_tasks_and_the_drivers()
    {
        var tasks = new Finding { Id = WhatChanged.TasksFindingId, Title = "t", Status = FindingStatus.Info, Explanation = "x", Followed = [new("Adobe")] };
        var entry = new Finding { Id = "M12.hkcu-run-a", Title = "a", Status = FindingStatus.Ok, Explanation = "x", Enabled = false };
        var drivers = new Finding { Id = WhatChanged.DriversFindingId, Title = "d", Status = FindingStatus.Ok, Explanation = "x", Followed = [new("Carte", "1")] };

        var summary = AuditSnapshot.From([new ModuleResult("M12", "m", [tasks, entry], TimeSpan.Zero), new ModuleResult("M17", "m", [drivers], TimeSpan.Zero)], "1", Day);

        Assert.Equal(AppVersion.Display, summary.MausVersion);
        Assert.Equal("Adobe", Assert.Single(summary.Tasks!).Name);
        Assert.Equal("Carte", Assert.Single(summary.Drivers!).Name);
        Assert.False(summary.Findings.Single(f => f.Id == "M12.hkcu-run-a").Enabled);
    }

    [Fact]
    public void Unread_lists_stay_unread_in_the_summary()
    {
        var unread = Finding.AdminRequired(WhatChanged.TasksFindingId, "t");

        var summary = AuditSnapshot.From([new ModuleResult("M12", "m", [unread], TimeSpan.Zero)], "1", Day);

        Assert.Null(summary.Tasks);
        Assert.Null(summary.Drivers);
    }

    [Fact]
    public void A_damaged_file_gives_no_audit_to_compare()
    {
        var path = Path.Combine(_folder, "audit-snapshots.json");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(path, "{ abîmé");

        Assert.Empty(new AuditSnapshotStore(path).Load());
    }

    [Fact]
    public void Summaries_round_trip_with_the_new_fields()
    {
        var store = new AuditSnapshotStore(Path.Combine(_folder, "audit-snapshots.json"));
        store.Add(Audit(0, F("M12.hkcu-run-a", enabled: true)) with { Tasks = [new("Adobe")], Drivers = [new("Carte", "1.0")] });

        var read = Assert.Single(store.Load());

        Assert.Equal(Version, read.MausVersion);
        Assert.Equal("Adobe", Assert.Single(read.Tasks!).Name);
        Assert.Equal("1.0", Assert.Single(read.Drivers!).Version);
        Assert.True(read.Findings[0].Enabled);
    }
}
