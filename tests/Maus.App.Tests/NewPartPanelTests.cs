using Maus.App.ViewModels;
using Maus.Core;
using Maus.Core.Follow;
using Maus.Core.Reporting;
using Maus.Core.Workshop;

namespace Maus.App.Tests;

public sealed class NewPartPanelTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("maus-part-vm-").FullName;
    private readonly TestBench _bench = new();
    private IReadOnlyList<HardwarePart> _reading = [];

    public void Dispose()
    {
        _bench.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private string CardPath => Path.Combine(_folder, "card.json");

    private static HardwarePart Screen(string name) => new(PartKind.Screen, name, name);

    private MainViewModel NewViewModel() => new(new AuditHistories(
        new BenchmarkHistory(Path.Combine(_folder, "health.json")),
        new BenchmarkHistory(Path.Combine(_folder, "measures.json")),
        new AuditSnapshotStore(Path.Combine(_folder, "snapshots.json"))))
    {
        Engine = _bench.Engine,
        NewPartStore = new PartsCardStore(CardPath),
        ReadNewParts = _ => _reading,
    };

    [Fact]
    public void Without_a_card_the_panel_explains_how_to_fill_it() => UiThread.Run(async () =>
    {
        var vm = NewViewModel();

        await vm.LoadNewPartAsync();

        Assert.Contains("Pas encore de fiche", vm.NewPartStatus, StringComparison.Ordinal);
        Assert.Empty(vm.NewPartChanges);
        Assert.False(vm.NewPartNeedsAudit);
        Assert.False(vm.HasNewPartUnread);
        Assert.False(File.Exists(CardPath));
    });

    [Fact]
    public void Opening_the_panel_never_reads_the_hardware() => UiThread.Run(async () =>
    {
        var reads = 0;
        var vm = new MainViewModel(new AuditHistories(
            new BenchmarkHistory(Path.Combine(_folder, "health.json")),
            new BenchmarkHistory(Path.Combine(_folder, "measures.json")),
            new AuditSnapshotStore(Path.Combine(_folder, "snapshots.json"))))
        {
            Engine = _bench.Engine,
            NewPartStore = new PartsCardStore(CardPath),
            ReadNewParts = _ =>
            {
                reads++;
                return [];
            },
        };

        await vm.LoadNewPartAsync();

        Assert.Equal(0, reads);
    });

    [Fact]
    public void An_empty_first_reading_creates_nothing_and_says_it_is_not_a_problem() => UiThread.Run(async () =>
    {
        var vm = NewViewModel();

        await vm.RefreshNewPartAsync(null);

        Assert.False(File.Exists(CardPath));
        Assert.True(vm.HasNewPartNotice);
        Assert.Contains("n'est pas un problème", vm.NewPartNotice, StringComparison.Ordinal);
    });

    [Fact]
    public void First_use_announces_nothing_then_a_new_screen_asks_for_an_audit() => UiThread.Run(async () =>
    {
        var vm = NewViewModel();
        _reading = [Screen("A")];

        await vm.RefreshNewPartAsync(null);

        Assert.Empty(vm.NewPartChanges);
        Assert.Contains("Rien de changé", vm.NewPartStatus, StringComparison.Ordinal);
        Assert.Contains("A", vm.NewPartList, StringComparison.Ordinal);
        Assert.Contains("non lu", vm.NewPartUnread, StringComparison.OrdinalIgnoreCase);

        _reading = [Screen("A"), Screen("B")];
        await vm.RefreshNewPartAsync(null);

        var row = Assert.Single(vm.NewPartChanges);
        Assert.Contains("Nouvel écran : B", row.Title, StringComparison.Ordinal);
        Assert.True(vm.NewPartNeedsAudit);
        Assert.False(row.ShowFindings);
    });

    [Fact]
    public void An_audit_after_the_change_gives_its_findings_and_leads_to_them() => UiThread.Run(async () =>
    {
        var vm = NewViewModel();
        _reading = [Screen("A")];
        await vm.RefreshNewPartAsync(null);

        _reading = [Screen("A"), Screen("B")];
        _bench.Results.Add(new ModuleResult("M14", "Écran", [new Finding
        {
            Id = "M14.refresh.display-2",
            Title = "Fréquence d'actualisation : B",
            Category = "Écran",
            Explanation = "E",
            Status = FindingStatus.Improvable,
            Current = "60 Hz",
            Expected = "165 Hz",
        }], TimeSpan.Zero));
        await vm.RunAuditAsync();

        var row = Assert.Single(vm.NewPartChanges);
        Assert.False(vm.NewPartNeedsAudit);
        Assert.Contains("60 Hz", Assert.Single(row.Checks), StringComparison.Ordinal);

        row.OpenFindings!.Execute(null);
        Assert.Equal(MainViewModel.TabFindings, vm.SelectedTab);
    });

    [Fact]
    public void A_graphics_card_leads_to_the_benchmark() => UiThread.Run(async () =>
    {
        var vm = NewViewModel();
        _reading = [new HardwarePart(PartKind.Graphics, "A", "A")];
        await vm.RefreshNewPartAsync(null);
        _reading = [new HardwarePart(PartKind.Graphics, "A", "A"), new HardwarePart(PartKind.Graphics, "B", "B")];
        await vm.RefreshNewPartAsync(null);

        var row = Assert.Single(vm.NewPartChanges);
        Assert.Contains("benchmark", row.Guide, StringComparison.OrdinalIgnoreCase);
        row.OpenBenchmark!.Execute(null);
        Assert.Equal(MainViewModel.TabBenchmark, vm.SelectedTab);
    });

    [Fact]
    public void A_damaged_file_never_shows_anything_false() => UiThread.Run(async () =>
    {
        File.WriteAllText(CardPath, "{ abîmé");
        var vm = NewViewModel();

        await vm.LoadNewPartAsync();

        Assert.Empty(vm.NewPartChanges);
        Assert.Contains("n'a pas pu être relue", vm.NewPartStatus, StringComparison.Ordinal);
    });

    [Fact]
    public void A_file_that_cannot_be_read_again_keeps_the_card_in_memory() => UiThread.Run(async () =>
    {
        _reading = [Screen("Écran A")];
        var vm = NewViewModel();
        await vm.RefreshNewPartAsync(null);
        var shown = vm.NewPartStatus;
        Assert.DoesNotContain("Pas encore de fiche", shown, StringComparison.Ordinal);
        File.WriteAllText(CardPath, "{ abîmé");

        await vm.LoadNewPartAsync();

        Assert.Equal(shown, vm.NewPartStatus);
    });

    [Fact]
    public void A_failing_reading_never_breaks_and_is_said() => UiThread.Run(async () =>
    {
        var vm = new MainViewModel(new AuditHistories(
            new BenchmarkHistory(Path.Combine(_folder, "health.json")),
            new BenchmarkHistory(Path.Combine(_folder, "measures.json")),
            new AuditSnapshotStore(Path.Combine(_folder, "snapshots.json"))))
        {
            Engine = _bench.Engine,
            NewPartStore = new PartsCardStore(CardPath),
            ReadNewParts = _ => throw new InvalidOperationException("non"),
        };

        await vm.RefreshNewPartAsync(null);

        Assert.True(vm.HasNewPartNotice);
    });

    [Fact]
    public void Without_a_store_nothing_is_read_or_written() => UiThread.Run(async () =>
    {
        await _bench.ViewModel.RefreshNewPartAsync(null);
        await _bench.ViewModel.LoadNewPartAsync();

        Assert.Contains("Pas encore de fiche", _bench.ViewModel.NewPartStatus, StringComparison.Ordinal);
    });
}
