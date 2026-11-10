using System.Runtime.CompilerServices;
using Maus.Core.Modules.M09Gpu;
using Maus.Core.Reporting;
using Maus.Core.Workshop;

namespace Maus.Core.Tests.Reporting;

/// <summary>Chaque seuil annonce sa source ou dit qu'il est choisi par MAUS : la table ne doit pas dériver du code.</summary>
public sealed class ThresholdNotesTests
{
    private static string ModulesSources([CallerFilePath] string here = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "src", "Maus.Core", "Modules"));
        return string.Join("\n", Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
    }

    [Fact]
    public void Every_key_is_an_identifier_used_by_a_module()
    {
        var sources = ModulesSources();
        foreach (var key in ThresholdNotes.Keys)
        {
            var parts = key.Split('*');
            Assert.True(sources.Contains('"' + parts[0], StringComparison.Ordinal), key);
            Assert.True(parts.Length == 1 || sources.Contains(parts[1] + '"', StringComparison.Ordinal), key);
        }
    }

    [Theory]
    [InlineData("M09.driver.nvidia-rtx-4070")]
    [InlineData("M11.disk-0-reliability")]
    [InlineData("M18.wifi")]
    public void A_threshold_without_source_says_so(string id) =>
        Assert.Contains("choisie par MAUS", ThresholdNotes.For(id), StringComparison.Ordinal);

    [Fact]
    public void Verified_sources_are_named()
    {
        Assert.Contains("Microsoft Learn", ThresholdNotes.For("M01.wu-pause"), StringComparison.Ordinal);
        Assert.Contains("35 jours", ThresholdNotes.For("M01.wu-pause"), StringComparison.Ordinal);
        Assert.Contains("defrag", ThresholdNotes.For("M11.free-space"), StringComparison.Ordinal);
        Assert.Contains("15 %", ThresholdNotes.For("M11.free-space"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("M01.wu-pauses")]
    [InlineData("M11.disk-0-health")]
    [InlineData("M05.power-plan")]
    public void Other_findings_have_no_note(string id) => Assert.Null(ThresholdNotes.For(id));

    [Fact]
    public void Catalog_values_match_the_notes()
    {
        var catalog = GpuDriverCatalog.Default;
        var driver = ThresholdNotes.For("M09.driver.x")!;
        Assert.Contains($"{catalog.MaxDriverAgeDays} jours", driver, StringComparison.Ordinal);
        Assert.Contains($"{catalog.SignificantGapDays} jours", driver, StringComparison.Ordinal);
        Assert.Contains($"{catalog.CatalogMaxAgeDays} jours", driver, StringComparison.Ordinal);
        Assert.StartsWith($"Seuil : {SafetyLimits.Load().GpuIdleHotMarginC} °C", ThresholdNotes.For("M16.gpu-temperature.0"), StringComparison.Ordinal);
    }
}
