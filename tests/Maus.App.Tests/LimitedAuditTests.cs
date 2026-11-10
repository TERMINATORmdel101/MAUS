using System.Reflection;
using Maus.App.ViewModels;
using Maus.Core;
using Maus.Core.Engine.Broker;
using Maus.Core.Fixes;
using Maus.Core.Hardware;
using Maus.Core.Preferences;
using Maus.Core.Reporting;
using Maus.Core.Workshop;

namespace Maus.App.Tests;

/// <summary>Audit sans droits, dernier audit retrouvé à l'ouverture, liste « Que se passe-t-il ? » (0.8.6).</summary>
public sealed class LimitedAuditTests
{
    private static readonly DateTimeOffset Day = DateTimeOffset.Now.AddDays(-3);

    private static readonly string[] NeedAudit = ["Mon PC démarre lentement", "Mon PC se réveille la nuit", "Mon PC est-il bien protégé ?", "Je veux demander de l'aide"];

    private static readonly string[] NeedNoAudit = ["Mon PC est lent", "Mon PC a planté ou s'est éteint tout seul", "Je n'ai plus de place sur le disque", "Internet rame", "Le son craque ou les jeux saccadent"];

    private static TestBench Limited(params PlannedChange[] plan)
    {
        var bench = new TestBench();
        bench.Results.Add(new("M06", "Effets visuels", [TestBench.Finding("M06.a"), TestBench.Finding("M06.gris", FindingStatus.Unknown)], TimeSpan.Zero));
        bench.Plan.AddRange(plan);
        bench.Snapshot = bench.Snapshot with { IsElevated = false };
        return bench;
    }

    /// <summary>Ce qui passe par le moteur, hors lecture de la tâche de la semaine : elle ne demande jamais d'autorisation.</summary>
    private static List<string> Asked(TestBench bench) => [.. bench.Fake.Calls.Where(c => c != nameof(IMausEngine.GetScheduleAsync))];

    private static SnapshotFinding Kept(string id, FindingStatus status, bool wanted = false) => new(id, "M06", "Titre de " + id, status, null, wanted);

    // ----- Audit sans droits -----

    [Fact]
    public void A_limited_audit_calls_nothing_but_the_audit_and_keeps_nothing() => UiThread.Run(async () =>
    {
        using var bench = Limited(TestBench.Change("M06.a"));

        Assert.True(await bench.ViewModel.RunAuditAsync());

        // Plan, journal, relance d'un module : chacun demanderait une autorisation.
        Assert.Equal(["RunAuditAsync"], Asked(bench));
        Assert.True(bench.ViewModel.IsLimitedAudit);
        Assert.Empty(bench.ViewModel.Changes);
        Assert.Empty(bench.HistoryFiles);
        Assert.Empty(bench.ViewModel.HealthTrend);
    });

    [Fact]
    public void A_healthy_pc_with_only_optimizations_is_told_so_and_a_warning_removes_it() => UiThread.Run(async () =>
    {
        using var optional = new TestBench();
        optional.Results.Add(new("M06", "Effets visuels", [TestBench.Finding("M06.a", FindingStatus.Improvable)], TimeSpan.Zero));
        await optional.ViewModel.RunAuditAsync();
        Assert.True(optional.ViewModel.OnlyOptionalLeft);
        Assert.False(optional.ViewModel.NothingToHandle);

        using var watched = new TestBench();
        watched.Results.Add(new("M06", "Effets visuels", [TestBench.Finding("M06.a", FindingStatus.Improvable), TestBench.Finding("M06.b", FindingStatus.Warning)], TimeSpan.Zero));
        await watched.ViewModel.RunAuditAsync();
        Assert.False(watched.ViewModel.OnlyOptionalLeft);

        using var calm = new TestBench();
        calm.Results.Add(new("M06", "Effets visuels", [TestBench.Finding("M06.a", FindingStatus.Ok)], TimeSpan.Zero));
        await calm.ViewModel.RunAuditAsync();
        Assert.True(calm.ViewModel.NothingToHandle);
        Assert.False(calm.ViewModel.OnlyOptionalLeft);
    });

    [Fact]
    public void Reassuring_banners_are_limited_to_what_was_read() => UiThread.Run(async () =>
    {
        Assert.StartsWith("Dans ce qui a pu être lu : ", Ui.NothingToHandleInRead, StringComparison.Ordinal);
        Assert.StartsWith("Dans ce qui a pu être lu : ", Ui.OnlyOptionalLeftInRead, StringComparison.Ordinal);

        // Audit limité (sans droits) : un contrôle non lu.
        using var limited = new TestBench();
        limited.Results.Add(new("M06", "Effets visuels", [TestBench.Finding("M06.a", FindingStatus.Improvable), TestBench.Finding("M06.gris", FindingStatus.Unknown)], TimeSpan.Zero));
        limited.Snapshot = limited.Snapshot with { IsElevated = false };
        await limited.ViewModel.RunAuditAsync();
        Assert.True(limited.ViewModel.OnlyOptionalLeft);
        Assert.True(limited.ViewModel.SomethingUnread);
        Assert.True(limited.ViewModel.ShowOnlyOptionalLeftInRead);
        Assert.False(limited.ViewModel.ShowOnlyOptionalLeft);

        // Un module en erreur n'a aucun constat : il ne doit pas laisser croire que tout a été vu.
        using var failed = new TestBench();
        failed.Results.Add(new("M06", "Effets visuels", [TestBench.Finding("M06.a", FindingStatus.Ok)], TimeSpan.Zero));
        failed.Results.Add(new("M07", "Confidentialité", [], TimeSpan.Zero, "Erreur inattendue"));
        await failed.ViewModel.RunAuditAsync();
        Assert.True(failed.ViewModel.NothingToHandle);
        Assert.True(failed.ViewModel.ShowNothingToHandleInRead);
        Assert.False(failed.ViewModel.ShowNothingToHandle);

        // Audit complet, tout lu : les bandeaux habituels, en vert.
        using var complete = new TestBench();
        complete.Results.Add(new("M06", "Effets visuels", [TestBench.Finding("M06.a", FindingStatus.Ok)], TimeSpan.Zero));
        await complete.ViewModel.RunAuditAsync();
        Assert.False(complete.ViewModel.SomethingUnread);
        Assert.True(complete.ViewModel.ShowNothingToHandle);
        Assert.False(complete.ViewModel.ShowNothingToHandleInRead);

        using var optional = new TestBench();
        optional.Results.Add(new("M06", "Effets visuels", [TestBench.Finding("M06.a", FindingStatus.Improvable)], TimeSpan.Zero));
        await optional.ViewModel.RunAuditAsync();
        Assert.True(optional.ViewModel.ShowOnlyOptionalLeft);
        Assert.False(optional.ViewModel.ShowOnlyOptionalLeftInRead);
    });

    [Fact]
    public void A_limited_audit_says_its_score_is_partial_in_plain_words() => UiThread.Run(async () =>
    {
        using var bench = Limited();

        await bench.ViewModel.RunAuditAsync();

        Assert.True(bench.ViewModel.HasScore);
        Assert.True(bench.ViewModel.IsScorePartial);
        Assert.Equal("partiel", bench.ViewModel.ScoreWord);
        Assert.Contains("Score partiel", bench.ViewModel.ScorePartialNote, StringComparison.Ordinal);
        Assert.Contains("1 contrôle non lu", bench.ViewModel.ScorePartialNote, StringComparison.Ordinal);
        Assert.Contains("« voulus »", bench.ViewModel.ScorePartialNote, StringComparison.Ordinal);
        Assert.False(bench.ViewModel.IsAllGreen);
        Assert.Contains("sans droits", bench.ViewModel.StatusText, StringComparison.Ordinal);
    });

    [Fact]
    public void A_limited_audit_does_not_pretend_there_is_nothing_to_fix() => UiThread.Run(async () =>
    {
        using var bench = Limited(TestBench.Change("M06.a"));

        await bench.ViewModel.RunAuditAsync();
        bench.ViewModel.SelectedTab = MainViewModel.TabFindings;

        Assert.Equal(0, bench.ViewModel.JourneyStep);
        Assert.Contains("sans droits", bench.ViewModel.ChangesSummary, StringComparison.Ordinal);
        Assert.Contains("sans droits", bench.ViewModel.NextStepText, StringComparison.Ordinal);
        Assert.DoesNotContain("Rien à corriger", bench.ViewModel.NextStepTitle, StringComparison.Ordinal);
        Assert.False(bench.ViewModel.HasProposedChanges);
        Assert.False(bench.ViewModel.CanApply);
        Assert.True(bench.ViewModel.IsBlocked);
        Assert.Contains("Toutes les fonctionnalités", bench.ViewModel.BlockingReason, StringComparison.Ordinal);
        Assert.Empty(bench.ViewModel.BestFixes);
        Assert.DoesNotContain(bench.ViewModel.PriorityFindings, f => f.CanFix);
    });

    [Fact]
    public void A_managed_pc_keeps_its_own_reason_in_a_limited_audit() => UiThread.Run(async () =>
    {
        using var bench = Limited();
        bench.Snapshot = bench.Snapshot with { Hardware = new HardwareProfile { FormFactor = FormFactor.Desktop, IsManaged = true }, BlockingReason = "PC géré" };

        await bench.ViewModel.RunAuditAsync();

        Assert.Equal("PC géré", bench.ViewModel.BlockingReason);
    });

    [Fact]
    public void An_unknown_management_keeps_its_own_reason_in_a_limited_audit() => UiThread.Run(async () =>
    {
        using var bench = Limited();
        bench.Snapshot = bench.Snapshot with { Hardware = new HardwareProfile { FormFactor = FormFactor.Desktop, ManagementUnknown = true }, BlockingReason = "gestion inconnue" };

        await bench.ViewModel.RunAuditAsync();

        Assert.Equal("gestion inconnue", bench.ViewModel.BlockingReason);
    });

    [Fact]
    public void A_limited_audit_asks_no_laptop_question_and_leaves_the_wanted_marks_alone() => UiThread.Run(async () =>
    {
        using var bench = Limited();
        bench.Results[0] = new("M06", "Effets visuels", [TestBench.Finding("M06.a", FindingStatus.Info) with { AcknowledgedFrom = FindingStatus.Improvable }], TimeSpan.Zero);
        var mark = new Acknowledgement("M06.a", "valeur lue en anglais", DateTimeOffset.Now, "xx");
        var preferences = UserPreferences.Default with { Acknowledged = [mark] };
        bench.Store.Save(preferences);
        bench.Snapshot = bench.Snapshot with { Hardware = new HardwareProfile { FormFactor = FormFactor.Laptop }, Preferences = preferences };

        await bench.ViewModel.RunAuditAsync();

        Assert.Equal(0, bench.LaptopQuestions);
        Assert.Equal(mark, Assert.Single(bench.Store.Load().Acknowledged));
    });

    [Fact]
    public void The_same_audit_with_rights_rebinds_the_marks_and_asks_the_laptop_question() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Results.Add(new("M06", "Effets visuels", [TestBench.Finding("M06.a", FindingStatus.Info) with { AcknowledgedFrom = FindingStatus.Improvable }], TimeSpan.Zero));
        var mark = new Acknowledgement("M06.a", "valeur lue en anglais", DateTimeOffset.Now, "xx");
        var preferences = UserPreferences.Default with { Acknowledged = [mark] };
        bench.Store.Save(preferences);
        bench.Snapshot = bench.Snapshot with { Hardware = new HardwareProfile { FormFactor = FormFactor.Laptop }, Preferences = preferences };

        await bench.ViewModel.RunAuditAsync();

        Assert.Equal(1, bench.LaptopQuestions);
        Assert.NotEqual(mark, Assert.Single(bench.Store.Load().Acknowledged));
    });

    [Fact]
    public void With_the_full_engine_running_nothing_changes() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Results.Add(new("M06", "Effets visuels", [TestBench.Finding("M06.a")], TimeSpan.Zero));
        bench.Plan.Add(TestBench.Change("M06.a"));

        await bench.ViewModel.RunAuditAsync();

        Assert.False(bench.ViewModel.IsLimitedAudit);
        Assert.False(bench.ViewModel.IsScorePartial);
        Assert.Contains("PlanAsync", bench.Fake.Calls);
        Assert.Contains("BrowseJournalAsync", bench.Fake.Calls);
        Assert.Single(bench.ViewModel.Changes);
        Assert.Single(bench.KeptSnapshots);
        Assert.Single(bench.ViewModel.HealthTrend);
        Assert.False(bench.ViewModel.IsBlocked);
    });

    [Fact]
    public void The_what_changed_card_says_an_audit_without_rights_is_not_kept() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Snapshot = bench.Snapshot with { IsElevated = false };

        await bench.ViewModel.RunAuditAsync();

        Assert.Contains("n'est pas gardé", bench.ViewModel.WhatChangedEmpty, StringComparison.Ordinal);
        Assert.Contains("Toutes les fonctionnalités", bench.ViewModel.WhatChangedEmpty, StringComparison.Ordinal);
    });

    [Fact]
    public void Rights_closed_right_after_the_audit_never_call_the_engine_again() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Results.Add(new("M06", "Effets visuels", [TestBench.Finding("M06.a")], TimeSpan.Zero));
        bench.Plan.Add(TestBench.Change("M06.a"));
        bench.Snapshot = bench.Snapshot with { Hardware = new HardwareProfile { FormFactor = FormFactor.Laptop } };
        bench.ViewModel.CanReadJournalSilently = () => false;

        Assert.True(await bench.ViewModel.RunAuditAsync());

        Assert.True(bench.ViewModel.IsLimitedAudit);
        Assert.Contains("droits refermés", bench.ViewModel.StatusText, StringComparison.Ordinal);
        Assert.Equal([nameof(IMausEngine.RunAuditAsync)], Asked(bench));
        Assert.Empty(bench.ViewModel.Changes);
        Assert.Empty(bench.KeptSnapshots);
        Assert.Equal(0, bench.LaptopQuestions);
    });

    [Fact]
    public void A_limited_audit_after_a_full_one_drops_its_fixes_and_its_comparison() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Results.Add(new("M06", "Effets visuels", [TestBench.Finding("M06.a")], TimeSpan.Zero));
        bench.Plan.Add(TestBench.Change("M06.a"));
        await bench.ViewModel.RunAuditAsync();
        await bench.ViewModel.RunAuditAsync();
        Assert.True(bench.ViewModel.HasComparison);
        bench.Fake.Calls.Clear();
        bench.Snapshot = bench.Snapshot with { IsElevated = false };

        await bench.ViewModel.RunAuditAsync();

        Assert.Equal(["RunAuditAsync"], Asked(bench));
        Assert.Empty(bench.ViewModel.Changes);
        Assert.False(bench.ViewModel.HasComparison);
        Assert.Equal(2, bench.KeptSnapshots.Count);

        // Le moteur revient : un nouvel audit retrouve tout.
        bench.Snapshot = bench.Snapshot with { IsElevated = true };
        await bench.ViewModel.RunAuditAsync();

        Assert.False(bench.ViewModel.IsLimitedAudit);
        Assert.Single(bench.ViewModel.Changes);
        Assert.False(bench.ViewModel.IsBlocked);
    });

    [Fact]
    public void A_choice_after_a_limited_audit_relaunches_no_module() => UiThread.Run(async () =>
    {
        using var bench = Limited();
        await bench.ViewModel.RunAuditAsync();
        bench.Fake.Calls.Clear();

        bench.ViewModel.GameBarChoice = bench.ViewModel.GameBarOptions[3];
        await Task.Delay(100);

        Assert.DoesNotContain("RunModuleAsync", bench.Fake.Calls);
        Assert.DoesNotContain("PlanAsync", bench.Fake.Calls);
        Assert.Equal(3, bench.Store.Load().GameBarProfile);
    });

    [Fact]
    public void Fixes_checked_by_an_audit_without_rights_stay_unverified_and_ask_nothing_more() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Results.Add(new("M06", "Effets visuels", [TestBench.Finding("M06.a")], TimeSpan.Zero));
        bench.Plan.Add(TestBench.Change("M06.a"));
        await bench.ViewModel.RunAuditAsync();
        bench.ViewModel.Changes[0].IsSelected = true;
        bench.Fake.Answers["ApplyAsync"] = args =>
        {
            // Le moteur s'arrête juste après les corrections.
            bench.Snapshot = bench.Snapshot with { IsElevated = false };
            return new ApplyResult(
                new JournalSession { Id = "s1", CreatedAt = DateTimeOffset.Now },
                [.. ((IReadOnlyList<string>)args[0]!).Select(id => new ChangeOutcome(id, id, ChangeStatus.Applied, "fait"))],
                null);
        };
        bench.Fake.Calls.Clear();

        await bench.ViewModel.ApplyAsync();

        Assert.Equal(["ApplyAsync", "RunAuditAsync"], Asked(bench));
        Assert.Contains("n'a pas pu", bench.ViewModel.FixReport, StringComparison.Ordinal);
    });

    [Fact]
    public void The_real_on_demand_engine_never_starts_for_a_limited_audit() => UiThread.Run(async () =>
    {
        // TestBench pose ses propres fichiers ; ici, seul le moteur change.
        using var bench = Limited(TestBench.Change("M06.a"));
        var starts = 0;
        using var onDemand = new OnDemandMausEngine(bench.Engine, _ =>
        {
            starts++;
            throw new EngineDeclinedException("autorisation demandée");
        });
        EngineHost.Current = onDemand;
        var folder = Directory.CreateTempSubdirectory("maus-vm-limite-").FullName;
        try
        {
            var viewModel = new MainViewModel(new AuditHistories(
                new BenchmarkHistory(Path.Combine(folder, "health.json")),
                new BenchmarkHistory(Path.Combine(folder, "measures.json")),
                new AuditSnapshotStore(Path.Combine(folder, "snapshots.json"))))
            {
                Engine = onDemand,
                PreferencesStore = bench.Store,
                Confirm = (_, _) => false,
                AskLaptopChoice = () => null,
            };

            Assert.True(await viewModel.RunAuditAsync());

            Assert.Equal(0, starts);
            Assert.True(viewModel.IsLimitedAudit);
            Assert.Equal(["RunAuditAsync"], Asked(bench));
            viewModel.Shutdown();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    // ----- Dernier audit retrouvé à l'ouverture -----

    [Fact]
    public void Someone_who_never_audited_still_sees_the_first_step() => UiThread.Run(() =>
    {
        using var bench = new TestBench();

        Assert.False(bench.ViewModel.ShowLastAudit);
        Assert.False(bench.ViewModel.IsPastScore);
        Assert.Equal(1, bench.ViewModel.JourneyStep);
        Assert.Contains("Étape 1", bench.ViewModel.NextStepTitle, StringComparison.Ordinal);
        Assert.Equal("lancez l'audit", bench.ViewModel.ScoreWord);
        Assert.Equal("—", bench.ViewModel.ScoreText);
        Assert.Equal("Lancer l'audit", bench.ViewModel.HomeAuditLabel);
        return Task.CompletedTask;
    });

    [Fact]
    public void The_last_audit_comes_back_dated_and_never_as_the_current_state() => UiThread.Run(() =>
    {
        using var bench = new TestBench(new AuditSnapshot(Day, "26200.1000", 72,
        [
            Kept("M06.bleu", FindingStatus.Improvable),
            Kept("M06.orange", FindingStatus.Warning),
            Kept("M06.rouge", FindingStatus.Problem),
            Kept("M06.gris", FindingStatus.Unknown),
            Kept("M06.voulu", FindingStatus.Problem, wanted: true),
        ]));
        var model = bench.ViewModel;

        // L'état actuel reste inconnu : le score affiché est celui d'avant, en gris, avec sa date.
        Assert.Empty(bench.Fake.Calls);
        Assert.False(model.HasAudit);
        Assert.False(model.HasScore);
        Assert.Equal(-1, model.Score);
        Assert.True(model.IsPastScore);
        Assert.True(model.ShowLastAudit);
        Assert.Equal(72, model.DisplayedScore);
        Assert.Equal("72", model.ScoreText);
        Assert.Equal("dernier audit", model.ScoreWord);
        Assert.Contains("Ce n'est pas l'état actuel", model.ScoreDetail, StringComparison.Ordinal);
        var date = Day.LocalDateTime.ToString("g", Maus.Core.Localization.Texts.Culture);
        Assert.Contains(date, model.ScoreDetail, StringComparison.Ordinal);
        Assert.Contains(date, model.LastAuditTitle, StringComparison.Ordinal);
        Assert.Contains("Dernier audit", model.LastAuditTitle, StringComparison.Ordinal);

        // Rouges d'abord, puis orange ; un constat « voulu » n'est plus signalé.
        Assert.Equal(["Titre de M06.rouge", "Titre de M06.orange"], model.LastAuditRows.Select(r => r.Title));
        Assert.Equal("Score : 72 sur 100.", model.LastAuditScore);
        Assert.Equal("1\u00A0problème · 1\u00A0à\u00A0surveiller · 1\u00A0optimisation · 1\u00A0indéterminé", model.LastAuditCounts);

        // Un habitué : plus d'étape 1 ni de premier audit annoncé.
        Assert.Equal(0, model.JourneyStep);
        Assert.False(model.ShowJourney);
        Assert.DoesNotContain("Étape", model.NextStepTitle, StringComparison.Ordinal);
        Assert.Contains("état actuel", model.NextStepTitle, StringComparison.Ordinal);
        Assert.Equal("Relancer l'audit", model.HomeAuditLabel);
        Assert.True(model.IsFirstAudit);
        return Task.CompletedTask;
    });

    [Fact]
    public void A_long_list_of_titles_is_cut_and_says_how_many_remain() => UiThread.Run(() =>
    {
        using var bench = new TestBench(new AuditSnapshot(Day, "1", 40, [.. Enumerable.Range(0, 9).Select(i => Kept("M06.r" + i, FindingStatus.Problem))]));

        Assert.Equal(6, bench.ViewModel.LastAuditRows.Count);
        Assert.True(bench.ViewModel.HasLastAuditMore);
        Assert.Contains('3', bench.ViewModel.LastAuditMore);
        return Task.CompletedTask;
    });

    [Fact]
    public void A_new_audit_replaces_the_card() => UiThread.Run(async () =>
    {
        using var bench = new TestBench(new AuditSnapshot(Day, "1", 40, [Kept("M06.a", FindingStatus.Problem)]));
        bench.Results.Add(new("M06", "Effets visuels", [TestBench.Finding("M06.a")], TimeSpan.Zero));

        await bench.ViewModel.RunAuditAsync();

        Assert.False(bench.ViewModel.ShowLastAudit);
        Assert.False(bench.ViewModel.IsPastScore);
        Assert.True(bench.ViewModel.HasScore);
        Assert.Equal(bench.ViewModel.Score, bench.ViewModel.DisplayedScore);
    });

    [Fact]
    public void A_failed_audit_keeps_the_card() => UiThread.Run(async () =>
    {
        using var bench = new TestBench(new AuditSnapshot(Day, "1", 40, [Kept("M06.a", FindingStatus.Problem)]));
        bench.Fake.Answers["RunAuditAsync"] = _ => new IOException("disque plein");

        Assert.False(await bench.ViewModel.RunAuditAsync());

        Assert.True(bench.ViewModel.ShowLastAudit);
        Assert.Equal(40, bench.ViewModel.DisplayedScore);
    });

    [Fact]
    public void The_weekly_summary_shows_without_a_score_and_is_never_compared() => UiThread.Run(async () =>
    {
        using var bench = new TestBench(
            new AuditSnapshot(Day, "1", 81, [Kept("M06.a", FindingStatus.Ok)]),
            new AuditSnapshot(Day.AddDays(1), "1", AuditSnapshot.NoScore, [Kept("M06.a", FindingStatus.Problem)], Weekly: true));
        bench.Results.Add(new("M06", "Effets visuels", [TestBench.Finding("M06.a", FindingStatus.Ok)], TimeSpan.Zero));
        var model = bench.ViewModel;

        // La carte dit ce que la notification a annoncé ; le chiffre de l'anneau vient du dernier audit qui en a un.
        Assert.Contains("audit de la semaine", model.LastAuditTitle, StringComparison.Ordinal);
        Assert.Contains("Pas de score", model.LastAuditScore, StringComparison.Ordinal);
        Assert.Equal("Titre de M06.a", Assert.Single(model.LastAuditRows).Title);
        Assert.Equal(81, model.DisplayedScore);
        Assert.Contains(Day.LocalDateTime.ToString("g", Maus.Core.Localization.Texts.Culture), model.ScoreDetail, StringComparison.Ordinal);

        await model.RunAuditAsync();

        var option = Assert.Single(model.ComparisonOptions);
        Assert.False(option.Value.Weekly);
    });

    [Fact]
    public void A_weekly_summary_alone_gives_no_score_at_all() => UiThread.Run(() =>
    {
        using var bench = new TestBench(new AuditSnapshot(Day, "1", AuditSnapshot.NoScore, [Kept("M06.a", FindingStatus.Warning)], Weekly: true));

        Assert.True(bench.ViewModel.ShowLastAudit);
        Assert.False(bench.ViewModel.IsPastScore);
        Assert.Equal("—", bench.ViewModel.ScoreText);
        Assert.Equal("dernier audit", bench.ViewModel.ScoreWord);
        Assert.Contains("Pas encore de score", bench.ViewModel.ScoreDetail, StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    [Fact]
    public void A_windows_update_is_measured_from_the_last_audit_started_by_hand() => UiThread.Run(() =>
    {
        // L'audit de la semaine, plus récent, a vu le nouveau build : la mise à jour reste à montrer.
        var current = Maus.Core.Platform.WindowsInfo.Read(new Maus.Core.Platform.WindowsRegistryReader()).FullBuild;
        using var bench = new TestBench(
            new AuditSnapshot(Day, "ancien", 81, []),
            new AuditSnapshot(Day.AddDays(1), current, AuditSnapshot.NoScore, [], Weekly: true));

        Assert.True(bench.ViewModel.HasBuildNotice);
        return Task.CompletedTask;
    });

    // ----- Que se passe-t-il ? -----

    private static SymptomRow Row(MainViewModel model, string title) => model.Symptoms.Single(s => s.Title == title);

    [Fact]
    public void Without_an_audit_a_symptom_says_so_and_offers_the_audit() => UiThread.Run(() =>
    {
        using var bench = new TestBench();
        var model = bench.ViewModel;

        foreach (var title in NeedAudit)
        {
            var row = Row(model, title);
            Assert.Contains("audit", row.Detail, StringComparison.Ordinal);
            Assert.Equal("Lancer l'audit", row.ActionLabel);
            Assert.Same(model.RunAuditCommand, row.Action);
        }

        // Ceux-là n'ont pas besoin de l'audit.
        foreach (var title in NeedNoAudit)
        {
            Assert.NotSame(model.RunAuditCommand, Row(model, title).Action);
        }

        Assert.All(model.Symptoms, s => Assert.False(string.IsNullOrWhiteSpace(s.AccessibleName)));
        return Task.CompletedTask;
    });

    [Fact]
    public void After_an_audit_a_symptom_opens_the_findings_of_its_module() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Results.Add(new("M12", "Démarrage", [TestBench.Finding("M12.boot-time")], TimeSpan.Zero));
        var model = bench.ViewModel;
        await model.RunAuditAsync();

        foreach (var (title, module) in new[] { ("Mon PC démarre lentement", "M12"), ("Mon PC se réveille la nuit", "M05") })
        {
            model.SelectedTab = 0;
            model.ShowByModule = false;
            var row = Row(model, title);
            Assert.Equal("Voir", row.ActionLabel);

            row.Action.Execute(null);

            Assert.Equal(MainViewModel.TabFindings, model.SelectedTab);
            Assert.True(model.ShowByModule);
            Assert.Equal(module, model.SelectedModule?.Id);
        }
    });

    [Fact]
    public void The_protection_question_opens_the_security_family() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Results.Add(new("M06", "Effets visuels", [TestBench.Finding("M06.a")], TimeSpan.Zero));
        var model = bench.ViewModel;
        await model.RunAuditAsync();
        model.FindingFilterChoice = model.FindingFilterOptions[1];
        model.ShowByModule = true;

        Row(model, "Mon PC est-il bien protégé ?").Action.Execute(null);

        Assert.Equal(MainViewModel.TabFindings, model.SelectedTab);
        Assert.Equal(ModuleFamily.Security, model.FamilyFilterChoice.Value);
        Assert.Equal(FindingFilter.All, model.FindingFilterChoice.Value);
        Assert.False(model.ShowByModule);
    });

    [Fact]
    public void Help_keeps_its_own_command_once_audited() => UiThread.Run(async () =>
    {
        using var bench = Limited();

        await bench.ViewModel.RunAuditAsync();

        Assert.Same(bench.ViewModel.HelpSummaryCommand, Row(bench.ViewModel, "Je veux demander de l'aide").Action);
    });

    [Fact]
    public void The_tools_of_the_old_list_stay_reachable()
    {
        var folder = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(folder, "Maus.slnx")))
        {
            folder = Path.GetDirectoryName(folder)!;
        }

        var xaml = File.ReadAllText(Path.Combine(folder, "src", "Maus.App", "MainWindow.xaml"));

        Assert.DoesNotContain("Ui.MoreTools}", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource NavTab}\" AutomationProperties.AcceleratorKey=\"Ctrl+6\" AutomationProperties.Name=\"{x:Static app:Ui.NavBenchmark}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<TabItem Header=\"{x:Static app:Ui.LiveTitle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{x:Static app:Ui.SaveReport}\" Margin=\"0,0,8,6\" Command=\"{Binding SaveReportCommand}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_audit_buttons_are_never_greyed_by_the_mode()
    {
        var folder = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(folder, "Maus.slnx")))
        {
            folder = Path.GetDirectoryName(folder)!;
        }

        var lines = File.ReadAllLines(Path.Combine(folder, "src", "Maus.App", "MainWindow.xaml"));

        var audit = lines.Where(l => l.Contains("Command=\"{Binding RunAuditCommand}\"", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, audit.Count);
        Assert.DoesNotContain(audit, l => l.Contains("IsFull", StringComparison.Ordinal));

        // Les corrections, elles, restent grisées en mode limité.
        var apply = Array.FindIndex(lines, l => l.Contains("Command=\"{Binding ApplyCommand}\"", StringComparison.Ordinal));
        Assert.Contains("Path=IsFull", lines[apply - 2], StringComparison.Ordinal);
    }
}
