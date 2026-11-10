using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Maus.App.ViewModels;
using Maus.Core.Engine.Broker;
using Maus.Core.Fixes;
using Maus.Core.Follow;

namespace Maus.App.Tests;

/// <summary>Carte « Mon PC a planté » : rien avant le clic, jamais d'autorisation demandée, copie masquée, échec dit.</summary>
public sealed class FollowCrashTimelineTests
{
    private static CrashTimeline Timeline(int lines = 1, CrashTimelineState state = CrashTimelineState.Found) => new(
        Enumerable.Range(0, lines).Select(i => new CrashTimelineLine(DateTime.Now.AddHours(-i), true, CrashTimelineKind.BlueScreen, "Code d'arrêt " + i)).ToList(),
        [new("Écrans bleus", state, lines)],
        DateTime.Now);

    [Fact]
    public void Nothing_is_read_before_the_click() => UiThread.Run(() =>
    {
        using var bench = new TestBench();
        var model = bench.ViewModel;
        var reads = 0;
        model.CrashTimelineSource = _ =>
        {
            reads++;
            return Timeline();
        };

        model.SelectedTab = MainViewModel.TabFollow;

        Assert.Equal(0, reads);
        Assert.False(model.HasCrashTimeline);
        Assert.Empty(model.CrashTimelineRows);
        Assert.DoesNotContain("BrowseJournalAsync", bench.Fake.Calls);
        return Task.CompletedTask;
    });

    [Fact]
    public void The_click_shows_the_lines_and_reads_the_journal_through_the_running_engine() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Sessions.Add(new JournalSession { Id = "s", CreatedAt = DateTimeOffset.Now });
        var model = bench.ViewModel;
        CrashTimelineJournal? asked = null;
        model.CrashTimelineSource = journal =>
        {
            asked = journal;
            return Timeline(2);
        };

        await model.ReadCrashTimelineAsync();

        Assert.Contains("BrowseJournalAsync", bench.Fake.Calls);
        Assert.Single(asked!.Sessions);
        Assert.True(model.HasCrashTimeline);
        Assert.True(model.HasCrashTimelineLines);
        Assert.Equal(2, model.CrashTimelineRows.Count);
        Assert.True(model.CrashTimelineRows[0].IsCrash);
        Assert.Equal("Relire", model.CrashTimelineReadLabel);
        Assert.Equal(["Écrans bleus : 2 trouvés"], model.CrashTimelineSections);
    });

    [Fact]
    public void Without_rights_no_authorization_is_ever_asked_and_the_journal_is_said_not_read() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        var starts = 0;
        using var onDemand = new OnDemandMausEngine(bench.Engine, _ =>
        {
            starts++;
            throw new EngineDeclinedException("autorisation demandée");
        });
        EngineHost.Current = onDemand;
        var model = bench.ViewModel;
        CrashTimelineJournal? asked = null;
        model.CrashTimelineSource = journal =>
        {
            asked = journal;
            return Timeline();
        };

        await model.ReadCrashTimelineAsync();

        Assert.Equal(0, starts);
        Assert.DoesNotContain("BrowseJournalAsync", bench.Fake.Calls);
        Assert.True(asked!.NoRights);
    });

    [Fact]
    public void An_unreadable_journal_is_passed_on_as_not_read() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Fake.Answers["BrowseJournalAsync"] = _ => new IOException("occupé");
        var model = bench.ViewModel;
        CrashTimelineJournal? asked = null;
        model.CrashTimelineSource = journal =>
        {
            asked = journal;
            return Timeline();
        };

        await model.ReadCrashTimelineAsync();

        Assert.NotNull(asked!.Error);
        Assert.True(model.HasCrashTimeline);
    });

    [Fact]
    public void A_failed_reading_says_so_and_leaves_no_old_lines() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        var model = bench.ViewModel;
        model.CrashTimelineSource = _ => Timeline();
        await model.ReadCrashTimelineAsync();

        model.CrashTimelineSource = _ => throw new InvalidOperationException("panne");
        await model.ReadCrashTimelineAsync();

        Assert.False(model.HasCrashTimeline);
        Assert.Empty(model.CrashTimelineRows);
        Assert.StartsWith("La ligne du temps n'a pas pu être lue", model.CrashTimelineStatus, StringComparison.Ordinal);
    });

    [Fact]
    public void The_copy_gives_every_line_and_says_when_the_clipboard_is_busy() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        var model = bench.ViewModel;
        model.CrashTimelineSource = _ => Timeline(35);
        string? copied = null;
        model.CrashTimelineClipboard = text =>
        {
            copied = text;
            return true;
        };

        model.CopyCrashTimelineCommand.Execute(null);
        Assert.Null(copied);

        await model.ReadCrashTimelineAsync();
        model.CopyCrashTimelineCommand.Execute(null);
        Assert.Contains("Code d'arrêt 34", copied, StringComparison.Ordinal);
        Assert.Contains("masqués", model.CrashTimelineStatus, StringComparison.Ordinal);

        model.CrashTimelineClipboard = _ => false;
        model.CopyCrashTimelineCommand.Execute(null);
        Assert.Equal(ClipboardText.Busy, model.CrashTimelineStatus);
    });

    [Fact]
    public void A_long_list_shows_the_most_recent_lines_first_then_all_of_them() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        var model = bench.ViewModel;
        model.CrashTimelineSource = _ => Timeline(35);

        await model.ReadCrashTimelineAsync();
        Assert.Equal(30, model.CrashTimelineRows.Count);
        Assert.True(model.CanShowAllCrashTimeline);

        model.ShowAllCrashTimelineCommand.Execute(null);
        Assert.Equal(35, model.CrashTimelineRows.Count);
        Assert.False(model.CanShowAllCrashTimeline);
    });

    [Fact]
    public void The_symptom_opens_the_follow_page_without_an_audit() => UiThread.Run(() =>
    {
        using var bench = new TestBench();
        var model = bench.ViewModel;
        var row = model.Symptoms.Single(s => s.Title == "Mon PC a planté ou s'est éteint tout seul");

        row.Action.Execute(null);

        Assert.Equal(MainViewModel.TabFollow, model.SelectedTab);
        Assert.Equal("Voir", row.ActionLabel);
        return Task.CompletedTask;
    });

    private sealed class BindingErrors : TraceListener
    {
        public List<string> Lines { get; } = [];

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (message is not null)
            {
                Lines.Add(message);
            }
        }
    }

    /// <summary>Le panneau tel qu'il est écrit, dans une fenêtre hors écran. Les styles du thème sont remplacés par des vides : c'est leurs liaisons qu'on regarde.</summary>
    private static Window Render(MainViewModel model, [CallerFilePath] string here = "")
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "Maus.App", "Views", "Follow", "CrashTimelinePanel.xaml"));
        var xaml = File.ReadAllText(path)
            .Replace("x:Class=\"Maus.App.Views.Follow.CrashTimelinePanel\"", string.Empty, StringComparison.Ordinal)
            .Replace("clr-namespace:Maus.App.ViewModels", "clr-namespace:Maus.App.ViewModels;assembly=" + typeof(MainViewModel).Assembly.GetName().Name, StringComparison.Ordinal)
            .Replace(
                "<Border Style=\"{StaticResource SoftCard}\"",
                """
                <UserControl.Resources>
                    <Style x:Key="SoftCard" TargetType="Border" />
                    <Style x:Key="CardTitle" TargetType="TextBlock" />
                    <Style x:Key="Muted" TargetType="TextBlock" />
                    <Style x:Key="SoftButton" TargetType="Button" />
                    <BooleanToVisibilityConverter x:Key="BoolToVisibility" />
                </UserControl.Resources>
                <Border Style="{StaticResource SoftCard}"
                """,
                StringComparison.Ordinal);
        var panel = (UserControl)XamlReader.Parse(xaml);
        var window = new Window
        {
            Content = panel,
            DataContext = model,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Width = 700,
            Height = 600,
        };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static IEnumerable<string> Texts(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock { Text: { Length: > 0 } text })
            {
                yield return text;
            }

            foreach (var inner in Texts(child))
            {
                yield return inner;
            }
        }
    }

    [Fact]
    public void The_panel_renders_with_every_binding_found() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        var model = bench.ViewModel;
        model.CrashTimelineSource = _ => Timeline(35);
        await model.ReadCrashTimelineAsync();

        var errors = new BindingErrors();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        var window = Render(model);
        try
        {
            var texts = Texts(window).ToList();
            Assert.Contains(CrashTimelineUi.Title, texts);
            Assert.Contains("Code d'arrêt 0", texts);
            Assert.Contains("Écrans bleus : 35 trouvés", texts);
            Assert.Contains(CrashTimeline.Caution, texts);
            Assert.DoesNotContain("Code d'arrêt 34", texts);
            Assert.Empty(errors.Lines);
        }
        finally
        {
            window.Close();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
        }
    });
}
