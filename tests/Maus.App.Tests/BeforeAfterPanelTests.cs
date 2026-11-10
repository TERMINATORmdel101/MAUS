using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Maus.App.ViewModels;
using Maus.Core;
using Maus.Core.Engine.Broker;
using Maus.Core.Fixes;
using Maus.Core.Follow;

namespace Maus.App.Tests;

/// <summary>
/// Carte « Avant et après » de la page « Suivi ». Comme pour le carnet, le panneau n'est pas rendu (ses styles viennent des ressources
/// de l'application, absentes des tests) : ses liaisons sont vérifiées contre les types.
/// </summary>
public sealed partial class BeforeAfterPanelTests
{
    private static readonly DateTimeOffset Session = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static JournalSession StartupSession() => new()
    {
        Id = "20260901-120000-abcdef",
        CreatedAt = Session,
        Entries =
        [
            new JournalEntry
            {
                ChangeId = "M12.a",
                ModuleId = "M12",
                ChangeTitle = "Couper A",
                Key = SettingKey.Registry("HKCU", @"Software\X", "A"),
                State = EntryState.Applied,
            },
        ],
    };

    /// <summary>Une ligne de carnet avec cinq démarrages avant la séance et cinq après.</summary>
    private static void WriteBoots(string path, double[] before, double[] after)
    {
        var boots = before.Select((s, i) => (Session.AddDays(i - before.Length).UtcDateTime.ToLocalTime(), (double?)s))
            .Concat(after.Select((s, i) => (Session.AddDays(i + 1).UtcDateTime.ToLocalTime(), (double?)s)));
        var result = new ModuleResult("M12", "M12",
        [
            new Finding { Id = "M12.boot-time", Title = "x", Status = FindingStatus.Ok, Explanation = "x", Measures = HealthLog.BootMeasures(boots) },
        ], TimeSpan.Zero);
        new HealthLogStore(path).AddAudit([result], DateTimeOffset.Now, weekly: false, elevated: true);
    }

    [Fact]
    public void Shows_the_frank_verdict_when_the_ranges_overlap() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Sessions.Add(StartupSession());
        WriteBoots(bench.HealthLogPath, [30, 32, 34, 36, 38], [29, 30, 31, 32, 33]);

        await bench.ViewModel.RefreshBeforeAfterAsync();

        var row = Assert.Single(bench.ViewModel.BeforeAfterRows);
        Assert.Equal(BeforeAfterVerdict.NoVisibleEffect, row.Verdict);
        Assert.False(bench.ViewModel.HasBeforeAfterMessage);
    });

    [Fact]
    public void No_session_explains_what_will_fill_the_card() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();

        await bench.ViewModel.RefreshBeforeAfterAsync();

        Assert.Empty(bench.ViewModel.BeforeAfterRows);
        Assert.Contains("rien à comparer", bench.ViewModel.BeforeAfterMessage, StringComparison.Ordinal);
    });

    [Fact]
    public void A_damaged_log_shows_no_figure() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Sessions.Add(StartupSession());
        Directory.CreateDirectory(Path.GetDirectoryName(bench.HealthLogPath)!);
        await File.WriteAllTextAsync(bench.HealthLogPath, "{ abîmé");

        await bench.ViewModel.RefreshBeforeAfterAsync();

        Assert.Empty(bench.ViewModel.BeforeAfterRows);
        Assert.Contains("n'a pas pu être relu", bench.ViewModel.BeforeAfterMessage, StringComparison.Ordinal);
    });

    [Fact]
    public void A_journal_that_fails_to_read_shows_no_figure() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Fake.Answers["BrowseJournalAsync"] = _ => new IOException("occupé");

        await bench.ViewModel.RefreshBeforeAfterAsync();

        Assert.Empty(bench.ViewModel.BeforeAfterRows);
        Assert.Contains("n'a pas pu être lu", bench.ViewModel.BeforeAfterMessage, StringComparison.Ordinal);
    });

    [Fact]
    public void Without_rights_it_says_so_and_never_asks_for_the_authorization() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        var starts = 0;
        using var onDemand = new OnDemandMausEngine(bench.Engine, _ =>
        {
            starts++;
            throw new EngineDeclinedException("autorisation demandée");
        });
        EngineHost.Current = onDemand;
        bench.Sessions.Add(StartupSession());

        await bench.ViewModel.RefreshBeforeAfterAsync();

        Assert.Equal(0, starts);
        Assert.DoesNotContain("BrowseJournalAsync", bench.Fake.Calls);
        Assert.Empty(bench.ViewModel.BeforeAfterRows);
        Assert.Contains("sans droits", bench.ViewModel.BeforeAfterMessage, StringComparison.Ordinal);
    });

    [Fact]
    public void Every_binding_of_the_panel_exists()
    {
        var names = typeof(MainViewModel).GetProperties().Select(p => p.Name)
            .Concat(typeof(BeforeAfterRow).GetProperties().Select(p => p.Name))
            .ToHashSet(StringComparer.Ordinal);

        var bindings = Binding().Matches(File.ReadAllText(PanelFile())).Select(m => m.Groups[1].Value).ToList();

        Assert.NotEmpty(bindings);
        Assert.All(bindings, name => Assert.Contains(name, names));
    }

    private static string PanelFile([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "Maus.App", "Views", "Follow", "BeforeAfterPanel.xaml"));

    [GeneratedRegex(@"\{Binding\s+(\w+)")]
    private static partial Regex Binding();
}
