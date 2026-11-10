using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Markup;
using Maus.App.Controls;
using Maus.App.ViewModels;
using Maus.Core.Follow;

namespace Maus.App.Tests;

/// <summary>Ce que MAUS présente aux lecteurs d'écran : chaque élément une seule fois, et sous un nom fait pour être lu.</summary>
public sealed partial class ScreenReaderTests
{
    /// <summary>Même forme que le modèle de la fenêtre : un bouton, la liste des pages dans une zone qui défile, la page ouverte.</summary>
    private const string Template =
        """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TabControl">
            <DockPanel>
                <Button DockPanel.Dock="Top" Content="Bouton du cadre" />
                <ScrollViewer DockPanel.Dock="Left">
                    <StackPanel IsItemsHost="True" />
                </ScrollViewer>
                <ContentPresenter x:Name="PART_SelectedContentHost" ContentSource="SelectedContent" />
            </DockPanel>
        </ControlTemplate>
        """;

    private static IEnumerable<AutomationPeer> All(AutomationPeer peer) => [peer, .. (peer.GetChildren() ?? []).SelectMany(All)];

    [Fact]
    public void The_pages_and_the_open_page_appear_once() => UiThread.Run(() =>
    {
        var tabs = new PageTabControl { Template = (ControlTemplate)XamlReader.Parse(Template) };
        tabs.Items.Add(new TabItem { Header = "Accueil", Content = new Button { Content = "Bouton de la page" } });
        tabs.Items.Add(new TabItem { Header = "Constats", Content = new Button { Content = "Autre page" } });
        tabs.SelectedIndex = 0;
        tabs.ApplyTemplate();
        tabs.Measure(new Size(800, 600));
        tabs.Arrange(new Rect(0, 0, 800, 600));
        tabs.UpdateLayout();

        var peers = All(UIElementAutomationPeer.CreatePeerForElement(tabs)).ToList();
        var named = peers.Where(p => p.GetAutomationControlType() is AutomationControlType.TabItem or AutomationControlType.Custom or AutomationControlType.Button)
            .Select(p => p.GetName()).ToList();

        Assert.Equal(1, named.Count(n => n == "Accueil"));
        Assert.Equal(1, named.Count(n => n == "Constats"));
        Assert.Equal(1, named.Count(n => n == "Bouton de la page"));
        Assert.Equal(1, named.Count(n => n == "Bouton du cadre"));
        return Task.CompletedTask;
    });

    /// <summary>Lignes de liste sans type dans leur gabarit : ni le XAML ni son « DataType » ne les trouve.</summary>
    private static readonly Type[] UntypedRows =
    [
        typeof(HealthLogRow), typeof(BeforeAfterRow), typeof(QuietWeekPoint), typeof(CrashTimelineSection), typeof(NewPartRow),
        typeof(Maus.App.ViewModels.Workshop.DriverRowViewModel), typeof(Maus.App.ViewModels.Workshop.ReadingRowViewModel),
        typeof(Maus.App.ViewModels.Workshop.BenchmarkRankRow), typeof(Maus.App.ViewModels.Workshop.BenchmarkRunRow),
        typeof(Maus.App.ViewModels.Workshop.BenchmarkDetailRow), typeof(Maus.App.ViewModels.Workshop.BenchmarkCompareRow),
    ];

    /// <summary>Le nom d'une ligne de liste est le texte de son modèle : sans cela, le lecteur d'écran dit « Maus.App.ViewModels… » ou « InfoLine { … } ».</summary>
    [Fact]
    public void Every_list_row_has_a_name_made_to_be_read()
    {
        var known = typeof(MainViewModel).Assembly.GetTypes().ToLookup(t => t.Name, StringComparer.Ordinal);
        var typed = Directory.EnumerateFiles(Folder("src", "Maus.App"), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => TemplateType().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .SelectMany(name => known[name])
            .ToList();

        Assert.NotEmpty(typed);
        Assert.All(typed.Concat(UntypedRows), type =>
        {
            var method = type.GetMethod(nameof(ToString), Type.EmptyTypes)!;
            Assert.True(method.DeclaringType == type && !method.IsDefined(typeof(CompilerGeneratedAttribute)), type.Name);
        });
    }

    /// <summary>
    /// Pourquoi « Constats » passait sur « Par module » à chaque changement de langue : tant que les onglets sont fermés, deux
    /// boutons d'un même nom de groupe n'ont pas de fenêtre, donc passent pour le même groupe, d'une fenêtre à l'autre.
    /// </summary>
    [Fact]
    public void A_shared_group_name_reaches_across_windows_a_parent_panel_does_not() => UiThread.Run(() =>
    {
        var (first, second) = (new RadioButton { GroupName = "Vue", IsChecked = true }, new RadioButton { GroupName = "Vue" });
        second.IsChecked = true;
        Assert.False(first.IsChecked);

        var (left, right) = (new StackPanel(), new StackPanel());
        var (kept, other) = (new RadioButton { IsChecked = true }, new RadioButton { IsChecked = true });
        left.Children.Add(kept);
        right.Children.Add(other);
        other.IsChecked = false;
        other.IsChecked = true;
        Assert.True(kept.IsChecked);
        return Task.CompletedTask;
    });

    [Fact]
    public void The_main_window_names_no_radio_group() =>
        Assert.DoesNotContain("GroupName=", File.ReadAllText(Path.Combine(Folder("src", "Maus.App"), "MainWindow.xaml")), StringComparison.Ordinal);

    private static string Folder(string first, string second, [CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", first, second));

    [GeneratedRegex(@"DataType=""\{x:Type \w+:(\w+)\}""")]
    private static partial Regex TemplateType();
}
