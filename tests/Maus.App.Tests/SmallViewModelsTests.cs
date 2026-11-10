using Maus.App.ViewModels;
using Maus.App.ViewModels.Workshop;
using Maus.App.Views;
using Maus.Core;
using Maus.Core.Platform;

namespace Maus.App.Tests;

public sealed class SmallViewModelsTests
{
    [Fact]
    public void An_unfinished_core_test_only_blames_the_setting_after_a_restart()
    {
        var started = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        var trace = new Maus.Core.Workshop.CoreTestCheckpoint(started, 3, 0, 8);

        Assert.Null(WorkshopViewModel.DescribeCrash(null, started));
        Assert.Contains("sans que le PC redémarre", WorkshopViewModel.DescribeCrash(trace, started.AddHours(-1)), StringComparison.Ordinal);
        Assert.Contains("le PC a gelé ou redémarré", WorkshopViewModel.DescribeCrash(trace, started.AddHours(1)), StringComparison.Ordinal);
    }

    private sealed class NoActions : IFindingActions
    {
        public Task AcknowledgeAsync(string moduleId, Finding finding, bool acknowledge) => Task.CompletedTask;

        public bool CanFix(Finding finding) => false;

        public void Fix(Finding finding)
        {
        }

        public void SetModuleAudited(string moduleId, bool audited)
        {
        }
    }

    private static FindingViewModel View(Finding finding) => new(finding, "M11", new NoActions());

    private static Maus.Core.Workshop.ProcessSample Process(int pid, double cpu, DateTime start, string? path = null) =>
        new(pid, "p" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture), path, cpu, 0, 1048576, 0, null, 1, start);

    [Fact]
    public void A_process_read_again_keeps_its_row_and_only_its_values_change()
    {
        var state = new Maus.App.ViewModels.Workshop.ProcessListState();
        var (catalog, folders) = (Maus.Core.Workshop.ProcessCatalog.Default, Maus.Core.Workshop.SystemFolders.Current);
        var start = new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);
        state.Show([Process(1, 10, start), Process(2, 5, start), Process(3, 1, start)], catalog, folders);
        var (one, two) = (state.Rows[0], state.Rows[1]);
        var changed = new List<string?>();
        one.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var cleared = false;
        state.Rows.CollectionChanged += (_, e) => cleared |= e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset;

        // Le 2 passe devant, le 3 disparaît, un 4 arrive, et le PID 1 reste le même processus.
        state.Show([Process(2, 50, start), Process(1, 20, start), Process(4, 1, start)], catalog, folders);

        Assert.False(cleared);
        Assert.Equal([2, 1, 4], state.Rows.Select(r => r.Pid));
        Assert.Same(two, state.Rows[0]);
        Assert.Same(one, state.Rows[1]);
        Assert.Contains(nameof(one.Cpu), changed);
        Assert.DoesNotContain(nameof(one.Memory), changed);
    }

    [Theory]
    [InlineData(0, 100, 120, true)]
    [InlineData(0, 100, -120, false)]
    [InlineData(100, 100, -120, true)]
    [InlineData(100, 100, 120, false)]
    [InlineData(50, 100, 120, false)]
    public void The_wheel_passes_to_the_page_only_at_the_edge_of_a_list(double offset, double scrollable, int delta, bool expected) =>
        Assert.Equal(expected, Maus.App.Controls.WheelChain.IsAtEdge(offset, scrollable, delta));

    [Fact]
    public void An_idle_stop_is_explained_in_the_limited_banner() =>
        Assert.Contains("15 minutes", Maus.App.ViewModels.ElevationState.WithClosedNote("Mode limité."), StringComparison.Ordinal);

    [Fact]
    public void A_copy_outside_the_store_warns_about_the_unknown_publisher()
    {
        Assert.True(Maus.App.Views.ElevationOfferWindow.IsUnsignedCopy(@"C:\Program Files\MAUS\MAUS.exe"));
        Assert.False(Maus.App.Views.ElevationOfferWindow.IsUnsignedCopy(@"C:\Program Files\WindowsApps\MAUS_1\MAUS.exe"));
    }

    [Fact]
    public void A_reused_pid_is_another_process_and_gets_a_new_row()
    {
        var state = new Maus.App.ViewModels.Workshop.ProcessListState();
        var (catalog, folders) = (Maus.Core.Workshop.ProcessCatalog.Default, Maus.Core.Workshop.SystemFolders.Current);
        var start = new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);
        state.Show([Process(1, 10, start)], catalog, folders);
        var first = state.Rows[0];

        state.Show([Process(1, 10, start.AddMinutes(5))], catalog, folders);

        Assert.Single(state.Rows);
        Assert.NotSame(first, state.Rows[0]);
    }

    [Fact]
    public void A_finding_shows_the_value_read_and_the_expected_one()
    {
        var view = View(TestBench.Finding("M11.x"));

        Assert.Equal("Constaté : valeur lue   ·   Attendu : valeur attendue", view.Values);
        Assert.Contains("M11.x", view.Identity, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_a_deviation_is_introduced_by_why_it_matters()
    {
        Assert.True(View(TestBench.Finding("M11.x", FindingStatus.Warning)).ShowWhy);
        Assert.False(View(TestBench.Finding("M11.x", FindingStatus.Ok)).ShowWhy);
    }

    [Fact]
    public void A_threshold_shows_its_source_or_says_it_was_chosen()
    {
        var sourced = View(TestBench.Finding("M11.free-space"));
        Assert.True(sourced.HasThresholdNote);
        Assert.Contains("Microsoft Learn", sourced.ThresholdNote, StringComparison.Ordinal);

        Assert.Contains("choisie par MAUS", View(TestBench.Finding("M18.wifi")).ThresholdNote, StringComparison.Ordinal);
        Assert.False(View(TestBench.Finding("M11.x")).HasThresholdNote);
    }

    [Fact]
    public void A_web_address_is_shortened_in_the_text_and_kept_for_the_button()
    {
        var view = View(TestBench.Finding("M11.x") with { Advice = "Voir https://learn.microsoft.com/fr-fr/windows/page-tres-longue" });

        Assert.True(view.HasLink);
        Assert.Equal("https://learn.microsoft.com/fr-fr/windows/page-tres-longue", view.Link!.ToString());
        Assert.DoesNotContain("page-tres-longue", view.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public void An_address_in_an_explanation_never_gives_a_link() =>
        Assert.False(View(TestBench.Finding("M17.x") with { Explanation = "Appareil https://exemple.test/page en erreur." }).HasLink);

    [Fact]
    public void A_text_read_on_the_pc_stays_exact_and_gives_no_link()
    {
        const string Text = "Commande : app.exe --update https://exemple.test/charge.";
        var view = View(TestBench.Finding("M12.x") with { Explanation = Text, ExplanationHasPcData = true });

        Assert.Equal(Text, view.Explanation);
        Assert.False(view.HasLink);

        // Le conseil vient de MAUS : son lien reste proposé.
        Assert.True(View(TestBench.Finding("M12.x") with { Explanation = Text, ExplanationHasPcData = true, Advice = "Voir https://learn.microsoft.com/page" }).HasLink);
    }

    [Fact]
    public void A_recommended_change_starts_checked_and_an_advanced_one_never_does()
    {
        Assert.True(new ChangeViewModel(TestBench.Change("a")).IsSelected);
        Assert.False(new ChangeViewModel(TestBench.Change("b", recommended: false)).IsSelected);
        Assert.False(new ChangeViewModel(TestBench.Change("c", advanced: true)).IsSelected);
    }

    [Fact]
    public void Each_workshop_entry_comes_back_to_its_last_section() => UiThread.Run(() =>
    {
        using var bench = new TestBench();
        var workshop = bench.ViewModel.Workshop;

        workshop.Section = WorkshopViewModel.SectionDrivers;
        Assert.Equal(WorkshopViewModel.GroupPc, workshop.Group);
        Assert.True(workshop.ShowPcSections);
        Assert.False(workshop.ShowLiveSections);

        workshop.Group = WorkshopViewModel.GroupLive;
        Assert.Equal(WorkshopViewModel.SectionLive, workshop.Section);
        Assert.True(workshop.ShowLiveSections);
        workshop.Section = WorkshopViewModel.SectionProcesses;

        workshop.Group = WorkshopViewModel.GroupTests;
        Assert.Equal(WorkshopViewModel.SectionTests, workshop.Section);
        Assert.False(workshop.ShowPcSections);
        Assert.False(workshop.ShowLiveSections);

        workshop.Group = WorkshopViewModel.GroupPc;
        Assert.Equal(WorkshopViewModel.SectionDrivers, workshop.Section);

        workshop.Group = WorkshopViewModel.GroupLive;
        Assert.Equal(WorkshopViewModel.SectionProcesses, workshop.Section);
        return Task.CompletedTask;
    });

    [Fact]
    public void A_size_set_with_the_mouse_wheel_stays_visible_in_the_settings() => UiThread.Run(() =>
    {
        using var bench = new TestBench();
        Maus.App.Appearance.AppearanceManager.SetZoom(1.05);
        try
        {
            var settings = new SettingsViewModel(bench.Store, () => { });

            Assert.Equal(1.05, settings.Zoom.Value);
            Assert.Contains("105 %", settings.Zoom.Label, StringComparison.Ordinal);
            Assert.Equal(5, settings.ZoomOptions.Count);
        }
        finally
        {
            Maus.App.Appearance.AppearanceManager.SetZoom(1);
        }

        return Task.CompletedTask;
    });

    [Fact]
    public void The_hashes_kept_at_compile_time_are_those_of_the_libraries()
    {
        var expected = InstallationRow.ExpectedLibraries();
        var probe = new WindowsInstallationProbe();

        Assert.Equal(["Maus.Bench", "Maus.Core"], expected.Keys.Order(StringComparer.Ordinal));
        foreach (var library in expected)
        {
            Assert.Equal(probe.HashOf(Path.Combine(AppContext.BaseDirectory, library.Key + ".dll")), library.Value, ignoreCase: true);
        }
    }

    [Fact]
    public void The_installation_check_ends_with_its_limit()
    {
        var rows = InstallationRow.Build(Path.Combine(AppContext.BaseDirectory, "MAUS.exe"), new WindowsInstallationProbe());

        Assert.Contains(rows, r => r.Heading.StartsWith("Conforme : Bibliothèques", StringComparison.Ordinal));
        Assert.Contains("pas une preuve absolue", rows[^1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_button_is_disabled_while_its_action_runs() => UiThread.Run(async () =>
    {
        var finish = new TaskCompletionSource();
        var command = new AsyncCommand(() => finish.Task);
        var changes = 0;
        command.CanExecuteChanged += (_, _) => changes++;

        command.Execute(null);

        Assert.False(command.CanExecute(null));
        finish.SetResult();
        await Task.Yield();
        Assert.True(command.CanExecute(null));
        Assert.Equal(2, changes);
    });

    [Theory]
    [InlineData("Un seul paragraphe.", 1)]
    [InlineData("Premier.\n\nDeuxième.", 2)]
    [InlineData("Premier.\r\n\r\nDeuxième.\n\n\nTroisième.\n", 3)]
    [InlineData("Une ligne\nla suivante.", 1)]
    public void A_message_is_cut_at_each_blank_line(string message, int expected) =>
        Assert.Equal(expected, Maus.App.Views.QuestionWindow.Paragraphs(message).Count);
}
