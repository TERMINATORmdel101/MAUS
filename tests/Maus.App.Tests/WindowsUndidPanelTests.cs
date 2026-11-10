using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Maus.App.ViewModels;
using Maus.Core;
using Maus.Core.Engine.Broker;
using Maus.Core.Fixes;
using Maus.Core.Reporting;

namespace Maus.App.Tests;

/// <summary>Suivi, « Windows a-t-il défait mes réglages ? » : rien ne se lance tout seul, et rien de non lu n'est dit sain.</summary>
public sealed partial class WindowsUndidPanelTests
{
    private const string Build = "26200.1000";

    private static readonly DateTimeOffset Yesterday = DateTimeOffset.Now.AddDays(-1);

    private static TestBench Bench(params AuditSnapshot[] kept)
    {
        var bench = new TestBench(kept);
        bench.ViewModel.ReadWindowsBuild = () => Build;
        return bench;
    }

    private static JournalSession Session(DateTimeOffset at, params string[] ids) => new()
    {
        Id = "seance",
        CreatedAt = at,
        Entries =
        [
            .. ids.Select(id => new JournalEntry
            {
                ChangeId = id,
                ModuleId = "M06",
                ChangeTitle = "Corriger " + id,
                Key = SettingKey.Registry("HKLM", @"Software\Essai", id),
                State = EntryState.Applied,
            }),
        ],
    };

    private static AuditSnapshot Kept(string build, params (string Id, FindingStatus Status)[] findings) =>
        new(Yesterday.AddHours(1), build, 80, [.. findings.Select(f => new SnapshotFinding(f.Id, "M06", "Titre", f.Status))]);

    [Fact]
    public void Without_rights_nothing_is_read_and_it_is_not_presented_as_healthy() => UiThread.Run(async () =>
    {
        using var bench = Bench(Kept(Build, ("M06.a", FindingStatus.Ok)));
        bench.Sessions.Add(Session(Yesterday, "M06.a"));
        bench.ViewModel.CanReadJournalSilently = () => false;

        await bench.ViewModel.ShowWindowsUndidAsync();

        // Lire le journal par le moteur demanderait une autorisation que personne n'a voulue.
        Assert.DoesNotContain(nameof(IMausEngine.BrowseJournalAsync), bench.Fake.Calls);
        Assert.DoesNotContain(nameof(IMausEngine.RunAuditAsync), bench.Fake.Calls);
        Assert.Empty(bench.Questions);
        Assert.True(bench.ViewModel.WindowsUndidNeedsRights);
        Assert.False(bench.ViewModel.WindowsUndidCanAudit);
        Assert.Empty(bench.ViewModel.WindowsUndidRows);
        Assert.Contains("Non lu sans droits", bench.ViewModel.WindowsUndidSummary, StringComparison.Ordinal);
        Assert.Contains("Toutes les fonctionnalités", bench.ViewModel.WindowsUndidNextStep, StringComparison.Ordinal);
    });

    [Fact]
    public void An_empty_journal_says_what_will_fill_it_and_runs_nothing() => UiThread.Run(async () =>
    {
        using var bench = Bench();

        await bench.ViewModel.ShowWindowsUndidAsync();

        Assert.Contains(nameof(IMausEngine.BrowseJournalAsync), bench.Fake.Calls);
        Assert.DoesNotContain(nameof(IMausEngine.RunAuditAsync), bench.Fake.Calls);
        Assert.DoesNotContain(nameof(IMausEngine.PlanAsync), bench.Fake.Calls);
        Assert.Empty(bench.ViewModel.WindowsUndidRows);
        Assert.Contains("aucune correction", bench.ViewModel.WindowsUndidSummary, StringComparison.Ordinal);
        Assert.True(bench.ViewModel.WindowsUndidCanAudit);
        Assert.Contains("Aucun audit", bench.ViewModel.WindowsUndidBuildNote, StringComparison.Ordinal);
    });

    [Fact]
    public void A_journal_that_cannot_be_read_is_said_and_never_called_empty() => UiThread.Run(async () =>
    {
        using var bench = Bench();
        bench.Fake.Answers[nameof(IMausEngine.BrowseJournalAsync)] = _ => new IOException("disque");

        await bench.ViewModel.ShowWindowsUndidAsync();

        Assert.Empty(bench.ViewModel.WindowsUndidRows);
        Assert.DoesNotContain("aucune correction", bench.ViewModel.WindowsUndidSummary, StringComparison.Ordinal);
        Assert.Contains("n'a pas pu être lu", bench.ViewModel.WindowsUndidNotice, StringComparison.Ordinal);
    });

    [Fact]
    public void An_engine_that_fails_during_the_reading_does_not_crash_the_card() => UiThread.Run(async () =>
    {
        using var bench = Bench();
        bench.Fake.Answers[nameof(IMausEngine.BrowseJournalAsync)] = _ => new InvalidOperationException("moteur");

        await bench.ViewModel.ShowWindowsUndidAsync();

        Assert.Empty(bench.ViewModel.WindowsUndidRows);
        Assert.Contains("ne peut pas répondre", bench.ViewModel.WindowsUndidSummary, StringComparison.Ordinal);
        Assert.Contains("n'a pas pu être lu", bench.ViewModel.WindowsUndidNotice, StringComparison.Ordinal);
    });

    [Fact]
    public void A_journal_refused_after_an_audit_is_never_taken_for_an_empty_one() => UiThread.Run(async () =>
    {
        using var bench = Bench();
        bench.Fake.Answers[nameof(IMausEngine.BrowseJournalAsync)] = _ => new EngineDeclinedException("refusé");
        await bench.ViewModel.RunAuditAsync();

        // Sans moteur, la carte dit qu'elle n'a pas lu, jamais « aucune correction ».
        bench.ViewModel.CanReadJournalSilently = () => false;
        await bench.ViewModel.ShowWindowsUndidAsync();

        Assert.Contains("Non lu sans droits", bench.ViewModel.WindowsUndidSummary, StringComparison.Ordinal);
    });

    [Fact]
    public void After_a_windows_update_the_kept_audit_is_dated_and_no_audit_starts() => UiThread.Run(async () =>
    {
        using var bench = Bench(Kept("26200.900", ("M06.a", FindingStatus.Ok)));
        bench.Sessions.Add(Session(Yesterday, "M06.a"));

        await bench.ViewModel.ShowWindowsUndidAsync();

        Assert.DoesNotContain(nameof(IMausEngine.RunAuditAsync), bench.Fake.Calls);
        Assert.True(bench.ViewModel.HasWindowsUndidBuildChange);
        Assert.False(bench.ViewModel.HasWindowsUndidPlainBuildNote);
        Assert.Contains("26200.900", bench.ViewModel.WindowsUndidBuildNote, StringComparison.Ordinal);
        Assert.Contains(Build, bench.ViewModel.WindowsUndidBuildNote, StringComparison.Ordinal);
        Assert.Equal("En place", Assert.Single(bench.ViewModel.WindowsUndidRows).State);
    });

    [Fact]
    public void The_same_build_is_said_plainly() => UiThread.Run(async () =>
    {
        using var bench = Bench(Kept(Build, ("M06.a", FindingStatus.Ok)));

        await bench.ViewModel.ShowWindowsUndidAsync();

        Assert.False(bench.ViewModel.HasWindowsUndidBuildChange);
        Assert.True(bench.ViewModel.HasWindowsUndidPlainBuildNote);
    });

    [Fact]
    public void An_unreadable_windows_version_claims_no_change() => UiThread.Run(async () =>
    {
        using var bench = Bench(Kept(Build, ("M06.a", FindingStatus.Ok)));
        bench.ViewModel.ReadWindowsBuild = () => null;

        await bench.ViewModel.ShowWindowsUndidAsync();

        Assert.False(bench.ViewModel.HasWindowsUndidBuildChange);
        Assert.Contains("n'a pas pu être comparée", bench.ViewModel.WindowsUndidBuildNote, StringComparison.Ordinal);
    });

    [Fact]
    public void After_an_audit_a_fix_proposed_again_leads_to_the_fixes_page_and_writes_nothing() => UiThread.Run(async () =>
    {
        using var bench = Bench();
        bench.Results.Add(new("M06", "Effets visuels", [TestBench.Finding("M06.a"), TestBench.Finding("M06.b", FindingStatus.Ok)], TimeSpan.Zero));
        bench.Plan.Add(TestBench.Change("M06.a", recommended: false));
        bench.Sessions.Add(Session(Yesterday, "M06.a", "M06.b"));

        await bench.ViewModel.ShowWindowsUndidAsync();
        Assert.Equal(["Non contrôlée", "Non contrôlée"], bench.ViewModel.WindowsUndidRows.Select(r => r.State));

        // Le bouton lance l'audit : la carte se met à jour d'elle-même, à la fin.
        Assert.True(await bench.ViewModel.RunAuditAsync());

        var rows = bench.ViewModel.WindowsUndidRows;
        Assert.Equal(["Plus en place", "En place"], rows.Select(r => r.State));
        Assert.Contains("Windows, une mise à jour, un autre programme ou vous-même", rows[0].Detail, StringComparison.Ordinal);
        Assert.Contains("1 en place, 1 plus en place, 0 non contrôlée", bench.ViewModel.WindowsUndidSummary, StringComparison.Ordinal);
        Assert.True(bench.ViewModel.HasComeBackChanges);
        Assert.Contains("Rien n'est réappliqué", bench.ViewModel.WindowsUndidNextStep, StringComparison.Ordinal);

        var change = bench.ViewModel.Changes.Single();
        Assert.False(change.IsSelected);
        bench.ViewModel.WindowsUndidRecheckCommand.Execute(null);

        Assert.True(change.IsSelected);
        Assert.Equal(MainViewModel.TabFixes, bench.ViewModel.SelectedTab);
        Assert.DoesNotContain(nameof(IMausEngine.ApplyAsync), bench.Fake.Calls);
    });

    [Fact]
    public void Rights_closed_after_the_reading_keep_the_list_and_only_offer_the_rights_button() => UiThread.Run(async () =>
    {
        using var bench = Bench(Kept(Build, ("M06.a", FindingStatus.Ok)));
        bench.Sessions.Add(Session(Yesterday, "M06.a"));
        await bench.ViewModel.ShowWindowsUndidAsync();
        bench.Fake.Calls.Clear();

        bench.ViewModel.CanReadJournalSilently = () => false;
        await bench.ViewModel.ShowWindowsUndidAsync();

        Assert.Empty(bench.Fake.Calls);
        Assert.Single(bench.ViewModel.WindowsUndidRows);
        Assert.True(bench.ViewModel.WindowsUndidNeedsRights);
        Assert.Contains("dernière lecture du journal", bench.ViewModel.WindowsUndidNextStep, StringComparison.Ordinal);
    });

    [Fact]
    public void A_limited_audit_does_not_turn_the_kept_comparison_into_something_else() => UiThread.Run(async () =>
    {
        using var bench = Bench(Kept(Build, ("M06.a", FindingStatus.Problem)));
        bench.Results.Add(new("M06", "Effets visuels", [TestBench.Finding("M06.a", FindingStatus.Unknown)], TimeSpan.Zero));
        bench.Snapshot = bench.Snapshot with { IsElevated = false };
        bench.Sessions.Add(Session(Yesterday, "M06.a"));
        await bench.ViewModel.ShowWindowsUndidAsync();

        await bench.ViewModel.RunAuditAsync();
        await bench.ViewModel.ShowWindowsUndidAsync();

        var row = Assert.Single(bench.ViewModel.WindowsUndidRows);
        Assert.Equal("Plus en place", row.State);
        Assert.False(bench.ViewModel.HasComeBackChanges);
        Assert.Contains("Relancez l'audit", bench.ViewModel.WindowsUndidNextStep, StringComparison.Ordinal);
    });

    [Fact]
    public void Every_binding_of_the_panel_exists()
    {
        // La carte ne se rend pas sans les ressources de l'application : une liaison mal écrite passerait sans erreur.
        var paths = Binding().Matches(File.ReadAllText(PanelPath())).Select(m => m.Groups[1].Value).Distinct().ToList();

        var known = typeof(MainViewModel).GetProperties().Concat(typeof(WindowsUndidRow).GetProperties()).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(paths);
        Assert.DoesNotContain(paths, p => !known.Contains(p));
    }

    private static string PanelPath([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "Maus.App", "Views", "Follow", "WindowsUndidPanel.xaml"));

    [GeneratedRegex(@"\{Binding (\w+)\b(?!\s*=)")]
    private static partial Regex Binding();
}
