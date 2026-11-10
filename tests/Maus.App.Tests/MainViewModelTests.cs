using Maus.App.ViewModels;
using Maus.Core;
using Maus.Core.Engine.Broker;
using Maus.Core.Fixes;

namespace Maus.App.Tests;

/// <summary>Ce que l'écran promet : rien n'est modifié sans accord, un refus ou une panne n'est jamais présenté comme un succès.</summary>
public sealed class MainViewModelTests
{
    private static TestBench Audited(params PlannedChange[] plan)
    {
        var bench = new TestBench();
        bench.Results.Add(new("M06", "Effets visuels", [.. plan.Select(c => TestBench.Finding(c.Id))], TimeSpan.Zero));
        bench.Plan.AddRange(plan);
        return bench;
    }

    private static void ApplyAnswersOk(TestBench bench, string? sessionId = null)
    {
        bench.Fake.Answers["ApplyAsync"] = args =>
        {
            var ids = (IReadOnlyList<string>)args[0]!;
            var session = sessionId is null ? null : new JournalSession { Id = sessionId, CreatedAt = DateTimeOffset.Now };
            return new ApplyResult(session, [.. ids.Select(id => new ChangeOutcome(id, id, ChangeStatus.Applied, "fait"))], null);
        };
    }

    [Fact]
    public void An_audit_shows_the_findings_and_the_proposed_changes() => UiThread.Run(async () =>
    {
        using var bench = Audited(TestBench.Change("M06.a"));

        Assert.True(await bench.ViewModel.RunAuditAsync());

        Assert.True(bench.ViewModel.HasAudit);
        Assert.False(bench.ViewModel.IsRunning);
        Assert.Contains("Audit terminé", bench.ViewModel.StatusText, StringComparison.Ordinal);
        Assert.Equal("Titre de M06.a", Assert.Single(bench.ViewModel.PriorityFindings).Title);
        Assert.Equal("M06.a", Assert.Single(bench.ViewModel.Changes).Change.Id);
        Assert.Contains(bench.ViewModel.Modules, m => m.Id == "M06" && m.AuditState == AuditState.Done);
    });

    [Fact]
    public void The_recommended_fixes_come_first_and_nothing_chosen_stays_hidden() => UiThread.Run(async () =>
    {
        using var bench = Audited(TestBench.Change("M06.a"), TestBench.Change("M06.b", recommended: false));

        Assert.True(await bench.ViewModel.RunAuditAsync());

        Assert.Equal("M06.a", Assert.Single(bench.ViewModel.ImportantChanges).Change.Id);
        var other = Assert.Single(bench.ViewModel.MoreChanges);
        Assert.False(bench.ViewModel.ShowMoreChanges);

        other.IsSelected = true;

        Assert.True(bench.ViewModel.ShowMoreChanges);
    });

    [Fact]
    public void A_declined_authorization_is_said_as_such_and_is_not_an_error() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Fake.Answers["RunAuditAsync"] = _ => new EngineDeclinedException("Autorisation refusée");

        Assert.False(await bench.ViewModel.RunAuditAsync());

        Assert.Equal("Autorisation refusée", bench.ViewModel.StatusText);
        Assert.False(bench.ViewModel.HasAudit);
        Assert.False(bench.ViewModel.IsRunning);
        Assert.False(Directory.Exists(bench.LogFolder));
    });

    [Fact]
    public void A_failed_audit_is_explained_and_keeps_the_previous_results() => UiThread.Run(async () =>
    {
        using var bench = Audited(TestBench.Change("M06.a"));
        await bench.ViewModel.RunAuditAsync();
        bench.Fake.Answers["RunAuditAsync"] = _ => new IOException("disque plein");

        Assert.False(await bench.ViewModel.RunAuditAsync());

        Assert.Contains("L'audit a échoué", bench.ViewModel.StatusText, StringComparison.Ordinal);
        Assert.True(bench.ViewModel.HasAudit);
        Assert.Single(bench.ViewModel.PriorityFindings);
        Assert.False(bench.ViewModel.IsRunning);
    });

    [Fact]
    public void Nothing_is_applied_when_the_question_is_refused() => UiThread.Run(async () =>
    {
        using var bench = Audited(TestBench.Change("M06.a"));
        await bench.ViewModel.RunAuditAsync();
        bench.Answer = (_, _) => false;

        await bench.ViewModel.ApplyAsync();

        Assert.Single(bench.Questions);
        Assert.DoesNotContain("ApplyAsync", bench.Fake.Calls);
        Assert.False(bench.ViewModel.HasFixReport);
        Assert.False(bench.ViewModel.IsApplying);
    });

    [Fact]
    public void Nothing_is_asked_nor_applied_without_a_checked_change() => UiThread.Run(async () =>
    {
        using var bench = Audited(TestBench.Change("M06.a", recommended: false));
        await bench.ViewModel.RunAuditAsync();

        Assert.False(bench.ViewModel.CanApply);
        await bench.ViewModel.ApplyAsync();

        Assert.Empty(bench.Questions);
        Assert.DoesNotContain("ApplyAsync", bench.Fake.Calls);
    });

    [Fact]
    public void Only_the_checked_changes_are_sent_with_a_restore_point() => UiThread.Run(async () =>
    {
        using var bench = Audited(
            TestBench.Change("M06.a"),
            TestBench.Change("M06.b", recommended: false),
            TestBench.Change("M06.c", advanced: true));
        IReadOnlyList<string>? sent = null;
        ApplyOptions? options = null;
        ApplyAnswersOk(bench);
        var apply = bench.Fake.Answers["ApplyAsync"];
        bench.Fake.Answers["ApplyAsync"] = args =>
        {
            sent = (IReadOnlyList<string>)args[0]!;
            options = (ApplyOptions)args[1]!;
            return apply(args);
        };
        await bench.ViewModel.RunAuditAsync();

        Assert.Equal(1, bench.ViewModel.SelectedCount);
        await bench.ViewModel.ApplyAsync();

        Assert.Equal(["M06.a"], sent);
        Assert.True(options!.CreateRestorePoint);
        Assert.False(options.ProceedWithoutRestorePoint);
        Assert.Contains("Corriger M06.a", bench.Questions[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Corriger M06.b", bench.Questions[0].Message, StringComparison.Ordinal);
        Assert.Contains("point de restauration", bench.Questions[0].Message, StringComparison.Ordinal);
    });

    [Fact]
    public void Warnings_are_repeated_in_the_question() => UiThread.Run(async () =>
    {
        using var bench = Audited(TestBench.Change("M06.a", warning: "Ceci coupe le Wi-Fi"));
        await bench.ViewModel.RunAuditAsync();
        bench.Answer = (_, _) => false;

        await bench.ViewModel.ApplyAsync();

        Assert.Contains("Ceci coupe le Wi-Fi", bench.Questions[0].Message, StringComparison.Ordinal);
    });

    [Fact]
    public void A_blocking_reason_stops_every_fix() => UiThread.Run(async () =>
    {
        using var bench = Audited(TestBench.Change("M06.a"));
        bench.Snapshot = bench.Snapshot with { BlockingReason = "PC géré par une organisation" };
        await bench.ViewModel.RunAuditAsync();

        Assert.True(bench.ViewModel.IsBlocked);
        Assert.False(bench.ViewModel.CanApply);
        await bench.ViewModel.ApplyAsync();

        Assert.Empty(bench.Questions);
        Assert.DoesNotContain("ApplyAsync", bench.Fake.Calls);
    });

    private const string OldStoreFolder = @"C:\Program Files\WindowsApps\Editeur.MAUS_0.8.5.0_x64__6c0b8wp3bzq1y\MAUS.exe";
    private const string NewStoreFolder = @"C:\Program Files\WindowsApps\Editeur.MAUS_0.8.6.0_x64__6c0b8wp3bzq1y\MAUS.exe";

    [Fact]
    public void A_weekly_task_left_on_the_old_store_folder_is_told_at_opening_and_moved_after_yes() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.ViewModel.CurrentExecutable = NewStoreFolder;
        var moved = new List<DayOfWeek?>();
        bench.Fake.Answers["GetScheduleAsync"] = _ => new ScheduleState(DayOfWeek.Monday, moved.Count > 0 ? NewStoreFolder : OldStoreFolder);
        bench.Fake.Answers["SetScheduleAsync"] = args =>
        {
            moved.Add((DayOfWeek?)args[0]);
            return null;
        };

        await bench.ViewModel.CheckScheduleAtOpeningAsync();
        Assert.True(bench.ViewModel.IsScheduleOutdated);
        Assert.Empty(moved);

        bench.ViewModel.MoveScheduleCommand.Execute(null);
        await bench.ViewModel.CheckScheduleAtOpeningAsync();

        Assert.Single(bench.Questions);
        Assert.Equal([DayOfWeek.Monday], moved);
        Assert.False(bench.ViewModel.IsScheduleOutdated);
    });

    [Fact]
    public void A_task_with_the_rights_of_an_earlier_version_is_only_rebuilt_by_its_button() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.ViewModel.CurrentExecutable = NewStoreFolder;
        var moved = new List<DayOfWeek?>();
        bench.Fake.Answers["GetScheduleAsync"] = _ => new ScheduleState(DayOfWeek.Friday, NewStoreFolder, KeepsOldRights: moved.Count == 0);
        bench.Fake.Answers["SetScheduleAsync"] = args =>
        {
            moved.Add((DayOfWeek?)args[0]);
            return null;
        };

        await bench.ViewModel.CheckScheduleAtOpeningAsync();
        Assert.True(bench.ViewModel.IsScheduleOutdated);
        Assert.Contains("droits administrateur", bench.ViewModel.ScheduleOutdatedText, StringComparison.Ordinal);
        Assert.Empty(moved);
        Assert.Empty(bench.Questions);

        bench.ViewModel.MoveScheduleCommand.Execute(null);
        await bench.ViewModel.CheckScheduleAtOpeningAsync();

        Assert.Contains("droits administrateur", Assert.Single(bench.Questions).Message, StringComparison.Ordinal);
        Assert.Equal([DayOfWeek.Friday], moved);
        Assert.False(bench.ViewModel.IsScheduleOutdated);
    });

    [Fact]
    public void An_old_folder_task_of_another_account_is_not_offered_for_a_move() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.ViewModel.CurrentExecutable = NewStoreFolder;
        bench.Fake.Answers["GetScheduleAsync"] = _ => new ScheduleState(DayOfWeek.Monday, OldStoreFolder, OfAnotherAccount: true);

        await bench.ViewModel.CheckScheduleAtOpeningAsync();

        Assert.False(bench.ViewModel.IsScheduleOutdated);
    });

    [Fact]
    public void A_classic_installation_does_not_read_the_task_at_opening() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.ViewModel.CurrentExecutable = @"C:\Program Files\MAUS\MAUS.exe";

        await bench.ViewModel.CheckScheduleAtOpeningAsync();

        Assert.DoesNotContain("GetScheduleAsync", bench.Fake.Calls);
        Assert.False(bench.ViewModel.IsScheduleOutdated);
    });

    [Fact]
    public void Another_administrator_account_blocks_the_fixes_too() => UiThread.Run(async () =>
    {
        using var bench = Audited(TestBench.Change("M06.a"));
        bench.Snapshot = bench.Snapshot with { ElevatedAsAnotherUser = true };

        await bench.ViewModel.RunAuditAsync();

        Assert.Contains("autre compte", bench.ViewModel.BlockingReason, StringComparison.Ordinal);
    });

    [Fact]
    public void Protection_turned_off_can_be_enabled_before_going_on() => UiThread.Run(async () =>
    {
        using var bench = Audited(TestBench.Change("M06.a"));
        var calls = new List<ApplyOptions>();
        bench.Fake.Answers["ApplyAsync"] = args =>
        {
            calls.Add((ApplyOptions)args[1]!);
            return calls[^1].EnableProtectionIfNeeded
                ? new ApplyResult(null, [new ChangeOutcome("M06.a", "M06.a", ChangeStatus.Applied, "fait")], null)
                : ApplyResult.Block("Coupée", new RestorePointOutcome(RestorePointStatus.ProtectionDisabled, null, "Protection coupée"));
        };
        await bench.ViewModel.RunAuditAsync();

        await bench.ViewModel.ApplyAsync();

        Assert.Equal(2, calls.Count);
        Assert.True(calls[1].EnableProtectionIfNeeded);
        Assert.False(calls[1].ProceedWithoutRestorePoint);
        Assert.Contains("Activer la protection du système et continuer ?", bench.Questions[1].Message, StringComparison.Ordinal);
    });

    [Fact]
    public void Protection_turned_off_by_a_policy_is_never_offered() => UiThread.Run(async () =>
    {
        using var bench = Audited(TestBench.Change("M06.a"));
        var calls = new List<ApplyOptions>();
        bench.Fake.Answers["ApplyAsync"] = args =>
        {
            calls.Add((ApplyOptions)args[1]!);
            return calls[^1].ProceedWithoutRestorePoint
                ? new ApplyResult(null, [new ChangeOutcome("M06.a", "M06.a", ChangeStatus.Applied, "fait")], null)
                : ApplyResult.Block("Stratégie", new RestorePointOutcome(RestorePointStatus.ProtectionDisabledByPolicy, null, "Coupée par une stratégie"));
        };
        await bench.ViewModel.RunAuditAsync();

        await bench.ViewModel.ApplyAsync();

        Assert.False(calls[^1].EnableProtectionIfNeeded);
        Assert.DoesNotContain(bench.Questions, q => q.Message.Contains("Activer la protection", StringComparison.Ordinal));
    });

    [Fact]
    public void Without_a_restore_point_going_on_needs_a_second_yes() => UiThread.Run(async () =>
    {
        using var bench = Audited(TestBench.Change("M06.a"));
        var calls = new List<ApplyOptions>();
        bench.Fake.Answers["ApplyAsync"] = args =>
        {
            calls.Add((ApplyOptions)args[1]!);
            return calls[^1].ProceedWithoutRestorePoint
                ? new ApplyResult(null, [new ChangeOutcome("M06.a", "M06.a", ChangeStatus.Applied, "fait")], null)
                : ApplyResult.Block("Pas de point", new RestorePointOutcome(RestorePointStatus.Failed, null, "Point impossible"));
        };
        await bench.ViewModel.RunAuditAsync();
        var answers = new Queue<bool>([true, false]);
        bench.Answer = (_, _) => answers.Dequeue();

        await bench.ViewModel.ApplyAsync();

        Assert.Single(calls);
        Assert.Contains("Point impossible", bench.Questions[1].Message, StringComparison.Ordinal);
        Assert.True(bench.ViewModel.HasFixReport);

        bench.Answer = (_, _) => true;
        await bench.ViewModel.ApplyAsync();

        Assert.Equal(3, calls.Count);
        Assert.True(calls[2].ProceedWithoutRestorePoint);
    });

    [Fact]
    public void Going_on_without_a_point_never_asks_for_a_new_point() => UiThread.Run(async () =>
    {
        using var bench = Audited(TestBench.Change("M06.a"));
        var calls = new List<ApplyOptions>();
        bench.Fake.Answers["ApplyAsync"] = args =>
        {
            calls.Add((ApplyOptions)args[1]!);
            return calls[^1].CreateRestorePoint
                ? ApplyResult.Block("Pas de point", new RestorePointOutcome(RestorePointStatus.Failed, null, "Point impossible"))
                : new ApplyResult(null, [new ChangeOutcome("M06.a", "M06.a", ChangeStatus.Applied, "fait")], null);
        };
        await bench.ViewModel.RunAuditAsync();

        await bench.ViewModel.ApplyAsync();

        Assert.Equal(2, calls.Count);
        Assert.True(calls[0].CreateRestorePoint);
        Assert.False(calls[0].ProceedWithoutRestorePoint);
        Assert.False(calls[1].CreateRestorePoint);
        Assert.True(calls[1].ProceedWithoutRestorePoint);
    });

    [Fact]
    public void Going_on_without_a_point_after_enabling_protection_keeps_that_choice() => UiThread.Run(async () =>
    {
        using var bench = Audited(TestBench.Change("M06.a"));
        var calls = new List<ApplyOptions>();
        bench.Fake.Answers["ApplyAsync"] = args =>
        {
            calls.Add((ApplyOptions)args[1]!);
            return calls.Count switch
            {
                1 => ApplyResult.Block("Coupée", new RestorePointOutcome(RestorePointStatus.ProtectionDisabled, null, "Protection coupée")),
                2 => ApplyResult.Block("Pas de point", new RestorePointOutcome(RestorePointStatus.Failed, null, "Point impossible")),
                _ => new ApplyResult(null, [new ChangeOutcome("M06.a", "M06.a", ChangeStatus.Applied, "fait")], null),
            };
        };
        await bench.ViewModel.RunAuditAsync();

        await bench.ViewModel.ApplyAsync();

        Assert.Equal(3, calls.Count);
        Assert.True(calls[1].CreateRestorePoint);
        Assert.False(calls[2].CreateRestorePoint);
        Assert.True(calls[2].ProceedWithoutRestorePoint);
        Assert.True(calls[2].EnableProtectionIfNeeded);
    });

    [Fact]
    public void A_failed_verification_never_says_a_change_had_no_effect() => UiThread.Run(async () =>
    {
        using var bench = Audited(TestBench.Change("M06.a"));
        ApplyAnswersOk(bench, "S1");
        IReadOnlyList<VerifiedOutcome>? recorded = null;
        bench.Fake.Answers["RecordVerificationAsync"] = args =>
        {
            recorded = (IReadOnlyList<VerifiedOutcome>)args[1]!;
            return null;
        };
        await bench.ViewModel.RunAuditAsync();
        bench.Fake.Answers["ApplyAsync"] = Chain(bench.Fake.Answers["ApplyAsync"], () => bench.Fake.Answers["RunAuditAsync"] = _ => new IOException("panne"));

        await bench.ViewModel.ApplyAsync();

        Assert.DoesNotContain(recorded!, v => v.Check == EffectCheck.NoEffect);
        Assert.NotEmpty(recorded!);
        Assert.Contains("L'audit a échoué", bench.ViewModel.StatusText, StringComparison.Ordinal);
        Assert.False(bench.ViewModel.IsApplying);

        static Func<object?[], object?> Chain(Func<object?[], object?> first, Action then) => args =>
        {
            var result = first(args);
            then();
            return result;
        };
    });

    [Fact]
    public void An_interrupted_fix_points_to_the_history_page() => UiThread.Run(async () =>
    {
        using var bench = Audited(TestBench.Change("M06.a"));
        bench.Fake.Answers["ApplyAsync"] = _ => new EngineUnavailableException("canal fermé");
        await bench.ViewModel.RunAuditAsync();

        await bench.ViewModel.ApplyAsync();

        Assert.Contains("Historique", bench.ViewModel.FixReport, StringComparison.Ordinal);
        Assert.False(bench.ViewModel.IsApplying);
    });

    [Fact]
    public void A_profile_only_checks_boxes() => UiThread.Run(async () =>
    {
        using var bench = Audited(TestBench.Change("M06.a"));
        await bench.ViewModel.RunAuditAsync();

        bench.ViewModel.Profiles.Single(p => p.Name == FixProfile.None.Name).Command.Execute(null);

        Assert.Equal(0, bench.ViewModel.SelectedCount);
        Assert.Empty(bench.Questions);
        Assert.DoesNotContain("ApplyAsync", bench.Fake.Calls);
    });

    [Fact]
    public void Filters_keep_only_what_they_promise() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Results.Add(new("M06", "Effets visuels",
        [
            TestBench.Finding("M06.red", FindingStatus.Problem, fixable: false),
            TestBench.Finding("M06.orange", FindingStatus.Warning, fixable: false),
            TestBench.Finding("M06.fix"),
            TestBench.Finding("M06.ok", FindingStatus.Ok, fixable: false),
        ], TimeSpan.Zero));
        bench.Plan.Add(TestBench.Change("M06.fix"));
        var vm = bench.ViewModel;
        await vm.RunAuditAsync();

        Assert.Equal(["Titre de M06.red", "Titre de M06.orange", "Titre de M06.fix"], vm.PriorityFindings.Select(f => f.Title));
        Assert.Contains(vm.OtherFindings, f => f.Title == "Titre de M06.ok");

        vm.FindingFilterChoice = vm.FindingFilterOptions.Single(o => o.Value == FindingFilter.RedOnly);
        Assert.Equal(["Titre de M06.red"], vm.PriorityFindings.Select(f => f.Title));

        vm.FindingFilterChoice = vm.FindingFilterOptions.Single(o => o.Value == FindingFilter.Fixable);
        Assert.Equal(["Titre de M06.fix"], vm.PriorityFindings.Select(f => f.Title));
    });

    [Fact]
    public void Marking_a_finding_asks_first_and_warns_about_a_red_problem() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        var red = TestBench.Finding("M06.red", FindingStatus.Problem);
        bench.Results.Add(new("M06", "Effets visuels", [red], TimeSpan.Zero));
        await bench.ViewModel.RunAuditAsync();
        IFindingActions actions = bench.ViewModel;
        bench.Answer = (_, _) => false;

        await actions.AcknowledgeAsync("M06", red, true);

        Assert.Empty(bench.Store.Load().Acknowledged);
        Assert.Contains("problème de sécurité ou de fiabilité", bench.Questions[0].Message, StringComparison.Ordinal);

        bench.Answer = (_, _) => true;
        await actions.AcknowledgeAsync("M06", red, true);

        Assert.Equal("M06.red", Assert.Single(bench.Store.Load().Acknowledged).FindingId);
        Assert.Contains("RunModuleAsync", bench.Fake.Calls);

        var questions = bench.Questions.Count;
        await actions.AcknowledgeAsync("M06", red, false);

        Assert.Empty(bench.Store.Load().Acknowledged);
        Assert.Equal(questions, bench.Questions.Count);
    });

    private static JournalSession Session(string id, DateTimeOffset created) => new()
    {
        Id = id,
        CreatedAt = created,
        Entries =
        [
            new JournalEntry
            {
                ChangeId = "M06.a",
                ModuleId = "M06",
                ChangeTitle = "Corriger M06.a",
                Key = SettingKey.Registry("HKCU", @"Software\Essai", "Valeur"),
                State = EntryState.Applied,
            },
        ],
    };

    [Fact]
    public void A_session_of_another_account_says_so_and_only_keeps_what_this_account_can_undo()
    {
        const string Mine = "S-1-5-21-1-1001";
        const string Other = "S-1-5-21-1-1002";
        var profile = new JournalSession
        {
            Id = "s",
            CreatedAt = DateTimeOffset.Now,
            UserSid = Other,
            Entries = Session("s", DateTimeOffset.Now).Entries,
        };

        var vm = new SessionViewModel(profile, (_, _) => Task.CompletedTask, Mine);

        Assert.True(vm.IsFromAnotherAccount);
        Assert.Contains("autre compte", vm.Header, StringComparison.Ordinal);
        Assert.False(vm.CanRevert);
        Assert.False(vm.Changes.Single().CanRevert);
        Assert.Contains("autre compte", vm.Changes.Single().Note, StringComparison.Ordinal);

        var own = new SessionViewModel(profile, (_, _) => Task.CompletedTask, Other);
        Assert.False(own.IsFromAnotherAccount);
        Assert.DoesNotContain("autre compte", own.Header, StringComparison.Ordinal);
        Assert.True(own.CanRevert);

        // Séance d'avant la 0.8.6, sans compte noté : comme avant.
        var old = new SessionViewModel(Session("s", DateTimeOffset.Now), (_, _) => Task.CompletedTask, Mine);
        Assert.False(old.IsFromAnotherAccount);
        Assert.True(old.CanRevert);
    }

    [Fact]
    public void Undoing_today_leaves_the_sessions_of_another_account() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        var mine = Session("mine", DateTime.Today.AddHours(12));
        var other = new JournalSession
        {
            Id = "other",
            CreatedAt = DateTime.Today.AddHours(12),
            UserSid = "S-1-5-21-1-9999",
            Entries = Session("other", DateTime.Today.AddHours(12)).Entries,
        };
        bench.Sessions.AddRange([mine, other]);
        var reverted = AnswerReverts(bench);
        await bench.ViewModel.RunAuditAsync();
        bench.Answer = (_, _) => true;

        await bench.ViewModel.RevertTodayAsync();

        Assert.Equal(["mine"], reverted.Select(r => r.Id));
    });

    [Fact]
    public void A_weekly_task_of_another_account_is_shown_and_never_changed_without_asking() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Fake.Answers["GetScheduleAsync"] = _ => new Maus.Core.Engine.Broker.ScheduleState(DayOfWeek.Monday, "maus.exe", OfAnotherAccount: true);
        await bench.ViewModel.RunAuditAsync();
        for (var i = 0; i < 200 && !bench.ViewModel.HasScheduleNotice; i++)
        {
            await Task.Delay(10);
        }

        Assert.Contains("autre compte", bench.ViewModel.ScheduleNotice, StringComparison.Ordinal);
        bench.Answer = (_, _) => false;

        bench.ViewModel.ScheduleChoice = bench.ViewModel.ScheduleOptions[0];
        for (var i = 0; i < 200 && bench.Questions.Count == 0; i++)
        {
            await Task.Delay(10);
        }

        Assert.Contains("autre compte", bench.Questions[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SetScheduleAsync", bench.Fake.Calls);
    });

    private static JournalSession WithSensitiveValue(string id)
    {
        var session = Session(id, DateTime.Today.AddHours(9));
        session.Entries.Add(new JournalEntry
        {
            ChangeId = "M01.winlogon",
            ModuleId = "M01",
            ChangeTitle = "Corriger Winlogon",
            Key = SettingKey.Registry("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "Shell"),
            Before = SettingValue.Text("valeur-d-origine.exe"),
            State = EntryState.Applied,
        });
        return session;
    }

    [Fact]
    public void Undoing_today_leaves_sensitive_values_out_and_says_so() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Sessions.Add(WithSensitiveValue("mixte"));
        var calls = new List<string?>();
        bench.Fake.Answers["RevertAsync"] = args =>
        {
            calls.Add((string?)args[1]);
            return new RevertResult(null, []);
        };
        await bench.ViewModel.RunAuditAsync();
        bench.Answer = (_, _) => true;

        await bench.ViewModel.RevertTodayAsync();

        Assert.Equal(["M06.a"], calls);
        var note = Maus.Core.Localization.Texts.T("{0} valeur sensible laissée de côté : elle s'annule à part dans cet onglet, où elle est montrée avant d'être remise.", 1);
        Assert.Contains(note, bench.Questions[0].Message, StringComparison.Ordinal);
        Assert.Contains(note, bench.ViewModel.FixReport, StringComparison.Ordinal);
    });

    [Fact]
    public void A_session_with_only_sensitive_values_is_not_in_the_group_undo() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        var session = WithSensitiveValue("sensible");
        session.Entries.RemoveAll(e => e.ChangeId == "M06.a");
        bench.Sessions.Add(session);
        await bench.ViewModel.RunAuditAsync();

        Assert.False(bench.ViewModel.HasRevertibleToday);
        Assert.True(bench.ViewModel.Sessions.Single().CanRevert);
    });

    [Fact]
    public void Undoing_a_session_with_a_sensitive_value_shows_the_value() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Sessions.Add(WithSensitiveValue("mixte"));
        await bench.ViewModel.RunAuditAsync();
        bench.Answer = (_, _) => false;

        bench.ViewModel.Sessions.Single().RevertCommand.Execute(null);
        for (var i = 0; i < 200 && bench.Questions.Count == 0; i++)
        {
            await Task.Delay(10);
        }

        Assert.Contains("valeur-d-origine.exe", bench.Questions[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("RevertAsync", bench.Fake.Calls);
    });

    private static List<(string Id, bool Force)> AnswerReverts(TestBench bench, string? failingId = null)
    {
        var reverted = new List<(string Id, bool Force)>();
        bench.Fake.Answers["RevertAsync"] = args =>
        {
            var id = (string)args[0]!;
            reverted.Add((id, (bool)args[2]!));
            return new RevertResult(null, [], id == failingId ? "Séance illisible" : null);
        };
        return reverted;
    }

    [Fact]
    public void Undoing_today_asks_first_then_goes_from_the_newest_to_the_oldest_without_force() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        var now = new DateTimeOffset(DateTime.Today.AddHours(12));
        bench.Sessions.AddRange([Session("ancienne", now.AddDays(-2)), Session("matin", now.AddSeconds(-2)), Session("midi", now)]);
        var reverted = AnswerReverts(bench);
        await bench.ViewModel.RunAuditAsync();
        Assert.True(bench.ViewModel.HasRevertibleToday);
        bench.Answer = (_, _) => false;

        await bench.ViewModel.RevertTodayAsync();

        Assert.Empty(reverted);

        bench.Answer = (_, _) => true;
        await bench.ViewModel.RevertTodayAsync();

        Assert.Equal([("midi", false), ("matin", false)], reverted);
    });

    [Fact]
    public void Undoing_everything_also_takes_the_older_sessions_and_says_when() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        var now = DateTimeOffset.Now;
        bench.Sessions.AddRange([Session("ancienne", now.AddDays(-2)), Session("midi", now)]);
        var reverted = AnswerReverts(bench);
        await bench.ViewModel.RunAuditAsync();
        Assert.True(bench.ViewModel.HasRevertibleEarlier);
        Assert.Contains("2", bench.ViewModel.RevertEverythingLabel, StringComparison.Ordinal);
        string? asked = null;
        bench.Answer = (_, message) =>
        {
            asked = message;
            return true;
        };

        await bench.ViewModel.RevertEverythingAsync();

        Assert.Equal([("midi", false), ("ancienne", false)], reverted);
        Assert.Contains(now.AddDays(-2).ToLocalTime().ToString("d", Maus.Core.Localization.Texts.Culture), asked, StringComparison.Ordinal);
    });

    [Fact]
    public void A_fix_proposed_again_says_it_had_already_been_applied() => UiThread.Run(async () =>
    {
        using var bench = Audited(TestBench.Change("M06.a"), TestBench.Change("M06.b"));
        bench.Sessions.Add(Session("matin", DateTimeOffset.Now.AddDays(-1)));

        await bench.ViewModel.RunAuditAsync();

        var (back, other) = (bench.ViewModel.Changes.Single(c => c.Change.Id == "M06.a"), bench.ViewModel.Changes.Single(c => c.Change.Id == "M06.b"));
        Assert.True(back.HasComeBack);
        Assert.False(other.HasComeBack);
        Assert.True(bench.ViewModel.HasComeBackChanges);
        Assert.Equal("remise depuis", bench.ViewModel.Sessions.Single().Changes.Single().State);
        back.IsSelected = false;
        bench.ViewModel.RecheckComeBackCommand.Execute(null);
        Assert.True(back.IsSelected);
    });

    [Fact]
    public void A_search_finds_compliant_findings_without_accents_and_says_when_nothing_matches() => UiThread.Run(async () =>
    {
        using var bench = Audited();
        bench.Results.Clear();
        bench.Results.Add(new(
            "M01",
            "Essai",
            [
                TestBench.Finding("M01.defender", FindingStatus.Ok) with { Title = "Sécurité : antivirus actif" },
                TestBench.Finding("M01.uac", FindingStatus.Warning) with { Title = "Contrôle de compte" },
            ],
            TimeSpan.Zero));
        await bench.ViewModel.RunAuditAsync();

        bench.ViewModel.FindingSearch = "securite";
        bench.ViewModel.ApplyFindingSearch();

        Assert.Equal("M01.defender", bench.ViewModel.OtherFindings.Single().Id);
        Assert.Empty(bench.ViewModel.PriorityFindings);
        Assert.True(bench.ViewModel.ShowOthers);
        Assert.False(bench.ViewModel.FindingSearchFindsNothing);

        bench.ViewModel.FindingSearch = "introuvable";
        bench.ViewModel.ApplyFindingSearch();

        Assert.True(bench.ViewModel.FindingSearchFindsNothing);

        bench.ViewModel.FindingSearch = string.Empty;
        bench.ViewModel.ApplyFindingSearch();

        Assert.Equal("M01.uac", bench.ViewModel.PriorityFindings.Single().Id);
    });

    [Fact]
    public void A_restart_reminder_ends_once_the_pc_has_started_after_the_fixes()
    {
        var fixes = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

        Assert.True(MainViewModel.IsRestartStillDue(fixes, fixes.AddHours(-3)));
        Assert.False(MainViewModel.IsRestartStillDue(fixes, fixes.AddHours(1)));
        Assert.False(MainViewModel.IsRestartStillDue(null, fixes));
        Assert.False(MainViewModel.IsRestartStillDue(new DateTimeOffset(2090, 1, 1, 0, 0, 0, TimeSpan.Zero), fixes));
        Assert.False(MainViewModel.IsRestartStillDue(DateTimeOffset.MaxValue, fixes));
        Assert.False(MainViewModel.IsRestartStillDue(DateTimeOffset.MinValue, fixes));
    }

    [Fact]
    public void Undoing_today_stops_at_the_first_failure() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        var now = new DateTimeOffset(DateTime.Today.AddHours(12));
        bench.Sessions.AddRange([Session("matin", now.AddSeconds(-2)), Session("midi", now)]);
        var reverted = AnswerReverts(bench, failingId: "midi");
        await bench.ViewModel.RunAuditAsync();

        await bench.ViewModel.RevertTodayAsync();

        Assert.Equal([("midi", false)], reverted);
        Assert.Contains("Séance illisible", bench.ViewModel.FixReport, StringComparison.Ordinal);
    });

    [Theory]
    [InlineData(80, 86, false, "80 → 86 (+6)")]
    [InlineData(86, 80, false, "86 → 80 (-6)")]
    [InlineData(80, 80, false, "inchangé")]
    [InlineData(80, 86, true, "Score partiel")]
    public void The_score_change_is_said_honestly(int before, int after, bool partial, string expected) =>
        Assert.Contains(expected, MainViewModel.ScoreChange(before, after, partial), StringComparison.Ordinal);

    [Fact]
    public void Without_two_scores_there_is_no_score_change() =>
        Assert.Null(MainViewModel.ScoreChange(-1, 80, partial: false));
}
