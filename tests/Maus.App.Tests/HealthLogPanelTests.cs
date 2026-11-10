using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Maus.App.ViewModels;
using Maus.Core;
using Maus.Core.Follow;
using Maus.Core.Workshop;

namespace Maus.App.Tests;

/// <summary>
/// Carte « Carnet de santé » de la page « Suivi ». Le panneau lui-même n'est pas rendu : ses styles viennent des ressources de
/// l'application, absentes des tests (jamais de <c>new App()</c>). Ses liaisons sont vérifiées contre les types à la place.
/// </summary>
public sealed partial class HealthLogPanelTests
{
    private static ModuleResult Disk(double wear) => new("M11", "M11",
    [
        new Finding
        {
            Id = "M11.disk-0-nvme", Title = "x", Status = FindingStatus.Ok, Explanation = "x", MeasuredItem = "SSD A",
            Measures = new Dictionary<string, double> { [MeasureHistory.Wear] = wear, [MeasureHistory.Errors] = 0 },
        },
    ], TimeSpan.Zero);

    [Fact]
    public void Each_audit_writes_a_line_and_the_card_shows_it() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Results.Add(Disk(3));

        await bench.ViewModel.RunAuditAsync();
        bench.Results[0] = Disk(4);
        await bench.ViewModel.RunAuditAsync();

        Assert.Equal(2, new HealthLogStore(bench.HealthLogPath).Load().Count);
        Assert.False(bench.ViewModel.HasHealthLogMessage);
        Assert.Contains(bench.ViewModel.HealthLogRows, r => r.Label.Contains("SSD A", StringComparison.Ordinal) && r.HasCurve);
        Assert.NotEmpty(bench.ViewModel.HealthLogLastLine);
    });

    [Fact]
    public void A_language_change_adds_no_line_and_the_card_stays_filled() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Results.Add(Disk(3));

        await bench.ViewModel.RunAuditAsync();
        bench.Results[0] = Disk(4);
        await bench.ViewModel.RunAuditAsync(forLanguage: true);

        Assert.Single(new HealthLogStore(bench.HealthLogPath).Load());
        Assert.Contains(bench.ViewModel.HealthLogRows, r => r.Label.Contains("SSD A", StringComparison.Ordinal));
    });

    [Fact]
    public void An_empty_log_explains_how_to_fill_it() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();

        await bench.ViewModel.RefreshHealthLogAsync();

        Assert.Contains("audit", bench.ViewModel.HealthLogMessage, StringComparison.Ordinal);
        Assert.Empty(bench.ViewModel.HealthLogRows);
    });

    [Fact]
    public void A_damaged_log_is_said_and_shows_no_figure() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        Directory.CreateDirectory(Path.GetDirectoryName(bench.HealthLogPath)!);
        await File.WriteAllTextAsync(bench.HealthLogPath, "{ abîmé");

        await bench.ViewModel.RefreshHealthLogAsync();

        Assert.Contains("n'a pas pu être relu", bench.ViewModel.HealthLogMessage, StringComparison.Ordinal);
        Assert.Empty(bench.ViewModel.HealthLogRows);
    });

    [Fact]
    public void An_audit_without_any_measure_adds_no_line() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();

        await bench.ViewModel.RunAuditAsync();

        Assert.False(File.Exists(bench.HealthLogPath));
    });

    [Fact]
    public void Every_binding_of_the_panel_exists()
    {
        var names = typeof(MainViewModel).GetProperties().Select(p => p.Name)
            .Concat(typeof(HealthLogRow).GetProperties().Select(p => p.Name))
            .ToHashSet(StringComparer.Ordinal);

        var bindings = Binding().Matches(File.ReadAllText(PanelFile())).Select(m => m.Groups[1].Value).ToList();

        Assert.NotEmpty(bindings);
        Assert.All(bindings, name => Assert.Contains(name, names));
    }

    private static string PanelFile([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "Maus.App", "Views", "Follow", "HealthLogPanel.xaml"));

    [GeneratedRegex(@"\{Binding\s+(\w+)")]
    private static partial Regex Binding();
}
