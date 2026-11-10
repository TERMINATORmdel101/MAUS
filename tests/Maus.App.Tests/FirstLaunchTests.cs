using System.Runtime.CompilerServices;
using Maus.App.Controls;
using Maus.Core;
using Maus.Core.Engine.Broker;
using Maus.Core.Hardware;

namespace Maus.App.Tests;

/// <summary>Ce que voit quelqu'un qui ouvre MAUS pour la première fois, sans droits : essai réel du 2/11/2026.</summary>
public sealed class FirstLaunchTests
{
    private static string MainWindowXaml([CallerFilePath] string here = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "Maus.App", "MainWindow.xaml")));

    [Fact]
    public void A_tab_keeps_its_shortcut_on_its_own_entry_not_on_the_whole_page() =>
        Assert.DoesNotContain("NavTab}\" ToolTip=", MainWindowXaml(), StringComparison.Ordinal);

    [Fact]
    public void The_settings_describe_the_window_as_it_is()
    {
        Assert.Contains("Ctrl+7", Ui.ShortcutsHint, StringComparison.Ordinal);
        Assert.DoesNotContain("Plus d'outils", Ui.HomeLayoutNote, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4, 660, 4)]
    [InlineData(4, 484, 2)]
    [InlineData(4, 362, 2)]
    [InlineData(4, 236, 1)]
    [InlineData(4, double.PositiveInfinity, 4)]
    [InlineData(3, 500, 3)]
    [InlineData(3, 400, 1)]
    [InlineData(0, 400, 1)]
    public void Tiles_wrap_by_whole_rows_when_they_run_out_of_room(int items, double width, int columns) =>
        Assert.Equal(columns, FittingGrid.ColumnsFor(items, width, 160));

    [Fact]
    public void The_score_says_the_first_audit_is_running() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        var finish = new TaskCompletionSource<AuditRun>();
        bench.Fake.Answers["RunAuditAsync"] = _ => finish.Task;
        var seen = new List<string?>();
        bench.ViewModel.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        var audit = bench.ViewModel.RunAuditAsync();

        Assert.Equal("audit en cours", bench.ViewModel.ScoreWord);
        Assert.Contains("à la fin de l'audit", bench.ViewModel.ScoreDetail, StringComparison.Ordinal);
        Assert.Contains(nameof(bench.ViewModel.ScoreWord), seen);
        Assert.Contains(nameof(bench.ViewModel.ScoreDetail), seen);

        finish.SetResult(new AuditRun(bench.Snapshot, []));
        await audit;
        Assert.NotEqual("audit en cours", bench.ViewModel.ScoreWord);
    });

    [Fact]
    public void A_limited_audit_says_what_to_do_once_and_stays_blocked() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Snapshot = bench.Snapshot with { IsElevated = false };

        await bench.ViewModel.RunAuditAsync();

        Assert.False(bench.ViewModel.ShowBlockingBanner);
        Assert.Contains("Toutes les fonctionnalités", bench.ViewModel.ChangesSummary, StringComparison.Ordinal);
        Assert.True(bench.ViewModel.IsBlocked);
        Assert.False(bench.ViewModel.CanApply);
    });

    [Fact]
    public void A_managed_pc_keeps_its_red_banner_and_no_false_promise() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Snapshot = bench.Snapshot with { IsElevated = false, Hardware = new HardwareProfile { FormFactor = FormFactor.Desktop, IsManaged = true }, BlockingReason = "PC géré" };

        await bench.ViewModel.RunAuditAsync();

        Assert.True(bench.ViewModel.ShowBlockingBanner);
        Assert.DoesNotContain("Toutes les fonctionnalités", bench.ViewModel.ChangesSummary, StringComparison.Ordinal);
    });

    [Fact]
    public void A_real_block_keeps_its_red_banner() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Snapshot = bench.Snapshot with { BlockingReason = "Bloqué" };

        await bench.ViewModel.RunAuditAsync();

        Assert.True(bench.ViewModel.ShowBlockingBanner);
    });

    [Fact]
    public void Two_totals_are_explained_when_one_subject_makes_one_card() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Results.Add(new("M01", "Audit", [TestBench.Finding("M01.secure-boot", FindingStatus.Problem), TestBench.Finding("M01.a")], TimeSpan.Zero));
        bench.Results.Add(new("M08", "Sécurité", [TestBench.Finding("M08.secure-boot", FindingStatus.Problem)], TimeSpan.Zero));

        await bench.ViewModel.RunAuditAsync();

        Assert.True(bench.ViewModel.HasPriorityMergedNote);
        Assert.Contains("3 constats pour 2 points", bench.ViewModel.PriorityMergedNote, StringComparison.Ordinal);
        Assert.Contains("(2)", bench.ViewModel.PriorityTitle, StringComparison.Ordinal);
    });

    [Fact]
    public void Nothing_to_explain_when_every_finding_has_its_card() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Results.Add(new("M01", "Audit", [TestBench.Finding("M01.a"), TestBench.Finding("M01.b")], TimeSpan.Zero));

        await bench.ViewModel.RunAuditAsync();

        Assert.False(bench.ViewModel.HasPriorityMergedNote);
    });
}
