using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Maus.Core;
using Maus.Core.Reporting;

namespace Maus.App.Tests;

/// <summary>« Qu'est-ce qui a changé ? » dans la page Suivi : tout vient des résumés gardés, rien n'est lancé à l'ouverture.</summary>
public sealed class WhatChangedPanelTests
{
    private const string Version = "0.8.6";

    private static readonly DateTimeOffset Day = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static SnapshotFinding Kept(string id, FindingStatus status = FindingStatus.Ok, string module = "M12", bool? enabled = null) => new(id, module, "Titre de " + id, status, Enabled: enabled);

    private static AuditSnapshot Audit(int day, params SnapshotFinding[] findings) => new(Day.AddDays(day), "26200.1000", 70, findings, MausVersion: Version, Tasks: [], Drivers: []);

    private static string Date(int day) => Day.AddDays(day).LocalDateTime.ToString("g", Maus.Core.Localization.Texts.Culture);

    [Fact]
    public void Without_any_audit_the_card_says_what_to_do_and_nothing_runs() => UiThread.Run(() =>
    {
        using var bench = new TestBench();
        var model = bench.ViewModel;

        Assert.True(model.ShowWhatChangedEmpty);
        Assert.False(model.HasWhatChanged);
        Assert.Contains("Lancez un audit", model.WhatChangedEmpty, StringComparison.Ordinal);
        Assert.Empty(model.WhatChangedRows);
        Assert.Empty(bench.Fake.Calls);
        return Task.CompletedTask;
    });

    [Fact]
    public void With_one_audit_the_card_gives_its_date_and_asks_for_another() => UiThread.Run(() =>
    {
        using var bench = new TestBench(Audit(0, Kept("M12.summary")));

        Assert.True(bench.ViewModel.ShowWhatChangedEmpty);
        Assert.Contains(Date(0), bench.ViewModel.WhatChangedEmpty, StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    [Fact]
    public void A_damaged_file_shows_the_empty_state_and_never_crashes() => UiThread.Run(() =>
    {
        // Un résumé retouché : champs vides, version absente.
        using var bench = new TestBench(
            new AuditSnapshot(Day, null!, 70, []),
            new AuditSnapshot(Day.AddDays(1), "1", AuditSnapshot.NoScore, [new("x", "M01", null!, FindingStatus.Problem)], Weekly: true));

        Assert.True(bench.ViewModel.HasWhatChanged);
        Assert.Empty(bench.ViewModel.WhatChangedRows);
        Assert.Empty(bench.Fake.Calls);
        return Task.CompletedTask;
    });

    [Fact]
    public void Two_audits_show_what_changed_with_its_dates_and_no_link_before_an_audit() => UiThread.Run(() =>
    {
        using var bench = new TestBench(Audit(0, Kept("M12.summary")), Audit(3, Kept("M12.summary"), Kept("M12.hkcu-run-steam", enabled: true)));
        var model = bench.ViewModel;

        Assert.False(model.ShowWhatChangedEmpty);
        var row = Assert.Single(model.WhatChangedRows);
        Assert.Equal("Titre de M12.hkcu-run-steam", row.Title);
        Assert.Equal("Nouveau programme au démarrage", row.Detail);
        Assert.Contains(Date(0), row.When, StringComparison.Ordinal);
        Assert.Contains(Date(3), row.When, StringComparison.Ordinal);
        Assert.False(row.HasOpen);
        Assert.True(model.HasWhatChangedLinkHint);
        Assert.False(model.HasWhatChangedVersionNote);
        Assert.Empty(bench.Fake.Calls);
        return Task.CompletedTask;
    });

    [Fact]
    public void The_choice_of_the_older_audit_changes_the_answer() => UiThread.Run(() =>
    {
        using var bench = new TestBench(
            Audit(0, Kept("M12.summary")),
            Audit(1, Kept("M12.summary"), Kept("M12.hkcu-run-a")),
            Audit(2, Kept("M12.summary"), Kept("M12.hkcu-run-a"), Kept("M12.hkcu-run-b")));
        var model = bench.ViewModel;

        Assert.Equal(2, model.WhatChangedOptions.Count);
        Assert.Equal("Titre de M12.hkcu-run-b", Assert.Single(model.WhatChangedRows).Title);

        model.WhatChangedChoice = model.WhatChangedOptions[1];

        Assert.Equal(2, model.WhatChangedRows.Count);
        Assert.Contains(Date(1), model.WhatChangedSummary + model.WhatChangedRows[1].When, StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    [Fact]
    public void Nothing_changed_is_said_without_promising_too_much() => UiThread.Run(() =>
    {
        using var bench = new TestBench(Audit(0, Kept("M12.summary")), Audit(1, Kept("M12.summary")));

        Assert.Empty(bench.ViewModel.WhatChangedRows);
        Assert.Contains("Aucun changement relevé", bench.ViewModel.WhatChangedSummary, StringComparison.Ordinal);
        Assert.Contains("pas que rien n'a bougé du tout", bench.ViewModel.WhatChangedSummary, StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    [Fact]
    public void A_new_maus_control_is_not_comparable_and_says_why() => UiThread.Run(() =>
    {
        using var bench = new TestBench(
            Audit(0, Kept("M12.summary"), Kept("M05.a", module: "M05")) with { MausVersion = "0.8.5" },
            Audit(1, Kept("M12.summary"), Kept("M05.a", module: "M05"), Kept("M05.added", FindingStatus.Problem, "M05")));
        var model = bench.ViewModel;

        Assert.Empty(model.WhatChangedRows);
        Assert.True(model.HasWhatChangedVersionNote);
        Assert.Contains("0.8.5", model.WhatChangedVersionNote, StringComparison.Ordinal);
        var note = Assert.Single(model.WhatChangedNotes);
        Assert.Equal("Titre de M05.added", note.Title);
        Assert.Contains("contrôle ajouté", note.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("version de MAUS", note.Detail, StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    [Fact]
    public void The_version_warning_is_said_once_at_the_top_of_the_card() => UiThread.Run(() =>
    {
        using var bench = new TestBench(
            Audit(0, Kept("M12.summary"), Kept("M05.a", FindingStatus.Ok, "M05")) with { MausVersion = "0.8.5" },
            Audit(1, Kept("M12.summary"), Kept("M05.a", FindingStatus.Problem, "M05")));
        var model = bench.ViewModel;

        Assert.True(model.HasWhatChangedVersionNote);
        var row = Assert.Single(model.WhatChangedRows);
        Assert.DoesNotContain("version", row.Detail, StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    [Fact]
    public void A_weekly_audit_without_rights_says_not_read_without_rights() => UiThread.Run(() =>
    {
        using var bench = new TestBench(
            Audit(0, Kept("M12.summary"), Kept("M12.hklm-run-a")),
            Audit(7, Kept("M12.summary"), new SnapshotFinding("M12.source-hklm-run", "M12", "Lecture du registre", FindingStatus.Unknown)) with { Weekly = true, Score = AuditSnapshot.NoScore, Tasks = null });
        var model = bench.ViewModel;

        Assert.Empty(model.WhatChangedRows);
        Assert.Contains(model.WhatChangedNotes, n => n.Title == "Lecture du registre" && n.Detail.Contains("Non lu sans droits", StringComparison.Ordinal));
        Assert.Contains(model.WhatChangedNotes, n => n.Title.Contains("Tâches", StringComparison.Ordinal) && n.Detail.Contains("Non lu sans droits", StringComparison.Ordinal));
        return Task.CompletedTask;
    });

    [Fact]
    public void A_weekly_audit_is_named_as_such_in_the_choice() => UiThread.Run(() =>
    {
        using var bench = new TestBench(
            Audit(0, Kept("M12.summary")) with { Weekly = true, Score = AuditSnapshot.NoScore },
            Audit(1, Kept("M12.summary")) with { Weekly = true },
            Audit(2, Kept("M12.summary")) with { IsPartial = true },
            Audit(3, Kept("M12.summary")));

        var labels = bench.ViewModel.WhatChangedOptions.Select(o => o.Label).ToList();

        Assert.Contains("partiel", labels[0], StringComparison.Ordinal);
        Assert.Contains("semaine", labels[1], StringComparison.Ordinal);
        Assert.DoesNotContain("sans droits", labels[1], StringComparison.Ordinal);
        Assert.Contains("sans droits", labels[2], StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    [Fact]
    public void After_an_audit_the_link_opens_the_finding_and_a_gone_finding_has_none() => UiThread.Run(async () =>
    {
        using var bench = new TestBench(Audit(0, Kept("M06.gone", FindingStatus.Warning, "M06"), Kept("M06.keep", module: "M06")));
        bench.Results.Add(new("M06", "Effets visuels", [TestBench.Finding("M06.keep", FindingStatus.Ok), TestBench.Finding("M06.new", FindingStatus.Problem)], TimeSpan.Zero));
        var model = bench.ViewModel;

        await model.RunAuditAsync();

        Assert.False(model.HasWhatChangedLinkHint);
        var added = model.WhatChangedRows.Single(r => r.Title == "Titre de M06.new");
        Assert.True(added.HasOpen);
        Assert.False(model.WhatChangedRows.Single(r => r.Title == "Titre de M06.gone").HasOpen);
        Assert.DoesNotContain(model.WhatChangedNotes, n => n.Title.Contains("M06", StringComparison.Ordinal));

        added.Open!.Execute(null);

        Assert.Equal(Maus.App.ViewModels.MainViewModel.TabFindings, model.SelectedTab);
        Assert.Equal("Titre de M06.new", model.FindingSearch);
        Assert.Contains(model.PriorityFindings.Concat(model.OtherFindings), f => f.Finding.Id == "M06.new");
    });

    [Fact]
    public void A_limited_audit_keeps_the_kept_audits_and_refreshes_the_links() => UiThread.Run(async () =>
    {
        using var bench = new TestBench(Audit(0, Kept("M06.a", module: "M06")), Audit(1, Kept("M06.a", FindingStatus.Problem, "M06")));
        bench.Results.Add(new("M06", "Effets visuels", [TestBench.Finding("M06.a", FindingStatus.Problem)], TimeSpan.Zero));
        bench.Snapshot = bench.Snapshot with { IsElevated = false };

        await bench.ViewModel.RunAuditAsync();

        // Un audit sans droits n'est pas gardé : la comparaison reste celle des résumés, le constat lu devient joignable.
        Assert.Equal(2, bench.KeptSnapshots.Count);
        Assert.True(Assert.Single(bench.ViewModel.WhatChangedRows).HasOpen);
    });

    [Fact]
    public void The_text_to_copy_carries_each_change_its_date_and_what_is_not_comparable() => UiThread.Run(() =>
    {
        using var bench = new TestBench(
            Audit(0, Kept("M12.summary"), Kept("M05.a", module: "M05")) with { Tasks = null },
            Audit(1, Kept("M12.summary"), Kept("M05.a", module: "M05"), Kept("M12.hkcu-run-a")));

        var text = bench.ViewModel.WhatChangedText();

        Assert.Contains("Qu'est-ce qui a changé ?", text, StringComparison.Ordinal);
        Assert.Contains("Titre de M12.hkcu-run-a", text, StringComparison.Ordinal);
        Assert.Contains(Date(1), text, StringComparison.Ordinal);
        Assert.Contains("Tâches planifiées", text, StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    [Fact]
    public void The_panel_renders_its_rows_off_screen() => UiThread.Run(() =>
    {
        using var bench = new TestBench(
            Audit(0, Kept("M12.summary")) with { Tasks = null },
            Audit(1, Kept("M12.summary"), Kept("M12.hkcu-run-a", enabled: true)));

        var window = new Window
        {
            Content = LoadPanel(),
            DataContext = bench.ViewModel,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Width = 800,
            Height = 700,
        };
        window.Show();
        window.UpdateLayout();
        var texts = Texts(window).ToList();
        window.Close();

        Assert.Contains("Qu'est-ce qui a changé ?", texts);
        Assert.Contains("Titre de M12.hkcu-run-a", texts);
        Assert.Contains("Nouveau programme au démarrage", texts);
        Assert.Contains(bench.ViewModel.WhatChangedSummary, texts);
        return Task.CompletedTask;
    });

    private static IEnumerable<string> Texts(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock block)
            {
                yield return block.Text;
            }

            foreach (var text in Texts(child))
            {
                yield return text;
            }
        }
    }

    /// <summary>
    /// Le XAML du panneau, lu hors compilation : le thème de l'application n'est là qu'avec le vrai démarrage, jamais lancé ici. Une
    /// ressource statique se résout à la lecture : des styles vides, sous les mêmes noms, enveloppent le panneau dans le même XAML.
    /// </summary>
    private static FrameworkElement LoadPanel()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Maus.slnx")))
        {
            folder = folder.Parent;
        }

        Assert.NotNull(folder);
        var panel = Regex.Replace(
                File.ReadAllText(Path.Combine(folder.FullName, "src", "Maus.App", "Views", "Follow", "WhatChangedPanel.xaml"), Encoding.UTF8),
                @"clr-namespace:([\w.]+)""",
                $@"clr-namespace:$1;assembly={typeof(Maus.App.ViewModels.MainViewModel).Assembly.GetName().Name}""")
            .Replace("x:Class=\"Maus.App.Views.Follow.WhatChangedPanel\"", string.Empty, StringComparison.Ordinal);
        return (FrameworkElement)XamlReader.Parse(
            """
            <ContentControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:sys="clr-namespace:System;assembly=System.Runtime">
                <ContentControl.Resources>
                    <BooleanToVisibilityConverter x:Key="BoolToVisibility" />
                    <sys:Double x:Key="FontSizeSmall">12</sys:Double>
                    <Style x:Key="SoftCard" TargetType="Border" />
                    <Style x:Key="SoftButton" TargetType="Button" />
                    <Style x:Key="CardTitle" TargetType="TextBlock" />
                    <Style x:Key="Muted" TargetType="TextBlock" />
                </ContentControl.Resources>
            """
            + panel[panel.IndexOf("<UserControl", StringComparison.Ordinal)..]
            + "</ContentControl>");
    }
}
