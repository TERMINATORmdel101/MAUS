using Maus.Core.Engine;
using Maus.Core.Engine.Broker;
using Maus.Core.Preferences;
using Maus.Core.Reporting;
using Maus.Core.Workshop;

namespace Maus.Core.Tests.Reporting;

/// <summary>Lot E de la 0.8.6 : comparaison entre deux audits, carte « Avant une mise à jour », mesures gardées, modules non choisis.</summary>
public sealed class AuditSnapshotsTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2026, 6, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "maus-snapshots-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static SnapshotFinding F(string id, FindingStatus status, string module = "M01") => new(id, module, id, status);

    private static AuditSnapshot Audit(string build = "26300.1", params SnapshotFinding[] findings) => new(Day, build, 80, findings, MausVersion: "1");

    [Fact]
    public void Compare_reports_status_changes_worst_first()
    {
        var older = Audit(findings: [F("a", FindingStatus.Ok), F("b", FindingStatus.Problem), F("c", FindingStatus.Ok)]);
        var newer = Audit(findings: [F("a", FindingStatus.Warning), F("b", FindingStatus.Ok), F("c", FindingStatus.Ok)]);

        var changes = AuditComparison.Compare(older, newer);

        Assert.Equal(["a", "b"], changes.Select(c => c.Title));
        Assert.Equal(AuditChangeKind.Worse, changes[0].Kind);
        Assert.Equal(AuditChangeKind.Better, changes[1].Kind);
    }

    [Fact]
    public void Compare_never_treats_an_unread_module_or_status_as_a_change()
    {
        var older = Audit(findings: [F("a", FindingStatus.Problem, "M01"), F("b", FindingStatus.Ok, "M02"), F("d", FindingStatus.Problem, "M04"), F("keep", FindingStatus.Ok, "M04")]);
        var newer = Audit(findings: [F("b", FindingStatus.Unknown, "M02"), F("n", FindingStatus.Problem, "M03"), F("keep", FindingStatus.Ok, "M04")]);

        var changes = AuditComparison.Compare(older, newer);

        // M01 absent du nouvel audit : rien à dire. M02 indéterminé : rien. M03 absent de l'ancien : rien. M04 relu : « d » a disparu.
        var change = Assert.Single(changes);
        Assert.Equal("d", change.Title);
        Assert.Equal(AuditChangeKind.Gone, change.Kind);
    }

    [Fact]
    public void Compare_reports_a_new_finding_that_needs_action_in_a_module_read_both_times()
    {
        var older = Audit(findings: [F("a", FindingStatus.Ok)]);
        var newer = Audit(findings: [F("a", FindingStatus.Ok), F("n", FindingStatus.Warning), F("quiet", FindingStatus.Ok)]);

        var change = Assert.Single(AuditComparison.Compare(older, newer));

        Assert.Equal("n", change.Title);
        Assert.Equal(AuditChangeKind.New, change.Kind);
    }

    [Fact]
    public void Store_keeps_the_last_twelve_audits_and_survives_a_damaged_file()
    {
        var path = Path.Combine(_folder, "audit-snapshots.json");
        var store = new AuditSnapshotStore(path);
        Assert.Empty(store.Load());

        for (var i = 0; i < 15; i++)
        {
            store.Add(new AuditSnapshot(Day.AddDays(i), "1", i, [F("a", FindingStatus.Problem)]));
        }

        var kept = store.Load();
        Assert.Equal(12, kept.Count);
        Assert.Equal(14, kept[^1].Score);
        Assert.Equal(FindingStatus.Problem, kept[0].Findings[0].Status);

        File.WriteAllText(path, "{ abîmé");
        Assert.Empty(store.Load());
        Assert.Single(store.Add(Audit(findings: [])));
    }

    [Fact]
    public void Weekly_summaries_never_push_out_the_audits_to_compare()
    {
        var store = new AuditSnapshotStore(Path.Combine(_folder, "audit-snapshots.json"));
        for (var i = 0; i < 12; i++)
        {
            store.Add(new AuditSnapshot(Day.AddDays(i), "1", i, []));
        }

        for (var i = 0; i < 6; i++)
        {
            store.Add(new AuditSnapshot(Day.AddDays(20 + i), "1", AuditSnapshot.NoScore, [], Weekly: true));
        }

        var kept = store.Load();
        Assert.Equal(12, kept.Count(s => !s.Weekly));
        Assert.Equal(4, kept.Count(s => s.Weekly));
        Assert.True(kept[^1].Weekly);
        Assert.False(kept[^1].HasScore);
    }

    [Fact]
    public void A_summary_written_before_these_fields_still_reads()
    {
        var path = Path.Combine(_folder, "audit-snapshots.json");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(path, """[{"At":"2026-11-01T09:00:00+00:00","Build":"1","Score":80,"Findings":[{"Id":"a","ModuleId":"M01","Title":"a","Status":"Problem"}],"IsPartial":false}]""");

        var read = Assert.Single(new AuditSnapshotStore(path).Load());

        Assert.False(read.Weekly);
        Assert.True(read.HasScore);
        Assert.False(read.Findings[0].Acknowledged);
    }

    private static ModuleResult Module(params Finding[] findings) => new("M01", "Module", findings, TimeSpan.Zero);

    [Fact]
    public void Weekly_summary_keeps_no_partial_score()
    {
        var read = Module(Finding("a", FindingStatus.Problem));

        var withoutRights = AuditSnapshot.ForWeeklyAudit([read], "1", Day, elevated: false);
        Assert.True(withoutRights!.Weekly);
        Assert.False(withoutRights.HasScore);
        Assert.Equal("a", Assert.Single(withoutRights.Findings).Id);

        var withRights = AuditSnapshot.ForWeeklyAudit([read], "1", Day, elevated: true);
        Assert.True(withRights!.Weekly);
        Assert.True(withRights.HasScore);

        // Un module en erreur : aucun résumé.
        var failed = new ModuleResult("M02", "En panne", [], TimeSpan.Zero, "délai dépassé");
        Assert.Null(AuditSnapshot.ForWeeklyAudit([read, failed], "1", Day, elevated: true));
        Assert.Null(AuditSnapshot.ForWeeklyAudit([read, failed], "1", Day, elevated: false));
    }

    [Fact]
    public void A_finding_marked_wanted_keeps_its_status_and_says_it_is_acknowledged()
    {
        var wanted = Finding("a", FindingStatus.Info) with { AcknowledgedFrom = FindingStatus.Problem };

        var kept = Assert.Single(AuditSnapshot.From([Module(wanted)], "1", Day).Findings);

        Assert.Equal(FindingStatus.Problem, kept.Status);
        Assert.True(kept.Acknowledged);
    }

    private static Finding Finding(string id, FindingStatus status, Dictionary<string, double>? measures = null) =>
        new() { Id = id, Title = id, Status = status, Explanation = "x", Measures = measures };

    [Fact]
    public void Update_card_is_due_only_when_something_is_pending()
    {
        Assert.False(UpdateReadiness.IsDue([Finding(UpdateReadiness.PendingId, FindingStatus.Ok), Finding(UpdateReadiness.OptionalId, FindingStatus.Info, new() { [UpdateReadiness.CountMeasure] = 0 })]));
        Assert.False(UpdateReadiness.IsDue([Finding(UpdateReadiness.PendingId, FindingStatus.Unknown)]));
        Assert.True(UpdateReadiness.IsDue([Finding(UpdateReadiness.PendingId, FindingStatus.Warning)]));
        Assert.True(UpdateReadiness.IsDue([Finding(UpdateReadiness.OptionalId, FindingStatus.Info, new() { [UpdateReadiness.CountMeasure] = 2 })]));
    }

    [Fact]
    public void Measures_are_kept_once_a_day_and_only_for_known_quantities()
    {
        var results = new[]
        {
            new ModuleResult("M11", "M11", [Finding("disk", FindingStatus.Ok, new() { [MeasureHistory.Wear] = 12, [MeasureHistory.Temperature] = 41, [UpdateReadiness.CountMeasure] = 3 })], TimeSpan.Zero),
        };

        var first = MeasureHistory.FromAudit(results, [], Day);
        Assert.Equal(2, first.Count);
        Assert.Contains(first, e => e.Kind == "measure:disk:wear" && e.Score == 12);
        Assert.Empty(MeasureHistory.FromAudit(results, first, Day.AddHours(1)));
        Assert.Equal(2, MeasureHistory.FromAudit(results, first, Day.AddDays(1)).Count);

        Assert.Equal("°C", ScoreTrends.Unit("measure:disk:temperature"));
        Assert.Equal("%", ScoreTrends.Unit("measure:disk:wear"));
    }

    private sealed class Stub(string id) : IAuditModule
    {
        public string Id => id;

        public string Title => id;

        public int Order => 1;

        public TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Finding>>([new Finding { Id = id + ".x", Title = "x", Status = FindingStatus.Problem, Severity = Severity.High, Explanation = "x" }]);
    }

    [Fact]
    public async Task A_skipped_module_is_reported_as_not_checked_and_the_score_is_partial()
    {
        var results = await new AuditEngine([new Stub("M01"), new Stub("M02")]).RunAsync(TestContext.Create(), skip: ["M02"]);

        Assert.Single(results.Single(r => r.ModuleId == "M01").Findings);
        var skipped = results.Single(r => r.ModuleId == "M02");
        Assert.Empty(skipped.Findings);
        Assert.NotNull(skipped.Error);
        Assert.True(HealthScore.Explain(results).IsPartial);
    }

    private sealed class Answer(bool yes) : IEngineConfirmation
    {
        public int Asked { get; private set; }

        public bool Confirm(string title, string message)
        {
            Asked++;
            return yes;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unticking_a_module_waits_for_the_engine_window(bool yes)
    {
        var store = new MemoryPreferencesStore();
        using var engine = new LocalMausEngine(new AuditEngine([new Stub("M01")]), () => TestContext.Create(), preferences: store, journal: new InMemoryJournalStore()) { Confirmation = new Answer(yes) };

        var state = await engine.SavePreferencesAsync(UserPreferences.Default with { SkippedModules = ["M01"] }, CancellationToken.None);

        Assert.Equal(yes, state.NotSaved is null);
        Assert.Equal(yes ? ["M01"] : [], store.Load().SkippedModules);
    }

    private sealed class RefusingMachine : IMachinePreferences
    {
        public bool IsAvailable => true;

        public UserPreferences Load() => UserPreferences.Default;

        public void Save(UserPreferences preferences) => throw new PreferencesNotSavedException("refusé", UserPreferences.Default);
    }

    [Fact]
    public void A_refused_protected_choice_never_stays_in_the_user_file_but_display_choices_do()
    {
        var user = new FilePreferencesStore(_folder, new UserDirectoryProtector(), _ => { });
        var store = new SplitPreferencesStore(user, new RefusingMachine());

        Assert.Throws<PreferencesNotSavedException>(() => store.Save(UserPreferences.Default with { SkippedModules = ["M19"], Theme = ThemeChoice.Dark }));

        Assert.Empty(user.Load().SkippedModules);
        Assert.Equal(ThemeChoice.Dark, user.Load().Theme);
    }

    [Fact]
    public void Skipped_module_list_is_bounded_on_the_channel()
    {
        Assert.NotNull(EngineServer.CheckedPreferences(UserPreferences.Default with { SkippedModules = ["M19"] }));
        Assert.Throws<EngineException>(() => EngineServer.CheckedPreferences(UserPreferences.Default with { SkippedModules = [.. Enumerable.Range(0, 501).Select(i => "M" + i)] }));
        Assert.Throws<EngineException>(() => EngineServer.CheckedPreferences(UserPreferences.Default with { SkippedModules = ["M\n"] }));
    }
}
