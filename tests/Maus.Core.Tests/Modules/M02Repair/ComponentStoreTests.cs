using Maus.Core.Modules.M02Repair;
using Maus.Core.Platform;

namespace Maus.Core.Tests.Modules.M02Repair;

/// <summary>Faux lecteur DISM : renvoie un état, ou lève l'exception donnée.</summary>
internal sealed class FakeImageHealth(ImageHealth health, Exception? error = null) : IImageHealthChecker
{
    public int Calls { get; private set; }

    public ImageHealth Check(TimeSpan timeout)
    {
        Calls++;
        return error is null ? health : throw error;
    }
}

public class ComponentStoreTests
{
    private static async Task<Finding> Detect(FakeImageHealth dism, bool elevated = true)
    {
        var findings = await new WindowsHealthModule(@"C:\Windows", dism).DetectAsync(
            TestContext.Create(elevated: elevated, files: new FakeFiles().SetDrive(@"C:\", 100L << 30, 500L << 30)),
            CancellationToken.None);
        return findings.Single(f => f.Id == "M02.component-store");
    }

    [Theory]
    [InlineData(ImageHealth.Healthy, FindingStatus.Ok)]
    [InlineData(ImageHealth.Repairable, FindingStatus.Warning)]
    [InlineData(ImageHealth.NotRepairable, FindingStatus.Problem)]
    public async Task Dism_state_gives_the_verdict(ImageHealth health, FindingStatus expected)
    {
        var finding = await Detect(new FakeImageHealth(health));

        Assert.Equal(expected, finding.Status);
        Assert.NotNull(finding.Advice);
        Assert.False(finding.Fixable);

        // Seule la réinstallation sur place passe par les Paramètres (Récupération) ; sinon, l'onglet Corrections de MAUS.
        Assert.Equal(health == ImageHealth.NotRepairable ? "ms-settings:recovery" : null, finding.SettingsPage);
    }

    [Fact]
    public async Task Without_admin_rights_dism_is_not_called()
    {
        var dism = new FakeImageHealth(ImageHealth.Healthy);

        var finding = await Detect(dism, elevated: false);

        Assert.Equal(FindingStatus.Unknown, finding.Status);
        Assert.Equal(0, dism.Calls);
    }

    [Fact]
    public async Task Busy_or_missing_dism_is_unknown_never_a_problem()
    {
        var finding = await Detect(new FakeImageHealth(ImageHealth.Healthy, new DataSourceUnavailableException("maintenance en cours")));

        Assert.Equal(FindingStatus.Unknown, finding.Status);
        Assert.Contains("maintenance en cours", finding.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refused_elevation_is_admin_required()
    {
        var finding = await Detect(new FakeImageHealth(ImageHealth.Healthy, new MausAccessDeniedException("refusé")));

        Assert.Equal(FindingStatus.Unknown, finding.Status);
    }
}

public class RepairConsoleTests
{
    [Fact]
    public void Dism_runs_before_sfc_and_the_window_stays_open()
    {
        var arguments = RepairConsole.Arguments();

        Assert.StartsWith("/k ", arguments, StringComparison.Ordinal);
        var dism = arguments.IndexOf(RepairConsole.DismCommand, StringComparison.Ordinal);
        var sfc = arguments.IndexOf(RepairConsole.SfcCommand, StringComparison.Ordinal);
        Assert.True(dism > 0 && sfc > dism);
    }

    [Fact]
    public void Echoed_text_cannot_chain_commands()
    {
        Assert.Equal("a ^& b ^| c ^> d ^(e^) f 'g' h", RepairConsole.Escape("a & b | c > d (e) f \"g\" %h"));
    }
}
