using Maus.Core.Hardware;
using Maus.Core.Platform;
using Maus.Core.Reporting;

namespace Maus.Core.Tests.Reporting;

public class HelpSummaryTests
{
    private static readonly PrivacyFilter Privacy = new("Alex", "DESKTOP-ALEX42", @"C:\Users\Alex");

    private static AuditReport Report(params Finding[] findings) => new(
        new DateTimeOffset(2026, 9, 25, 14, 0, 0, TimeSpan.FromHours(2)),
        "0.2.0",
        new WindowsInfo("Windows 11 Pro", "Professional", "24H2", 26100, 2033),
        new HardwareProfile
        {
            FormFactor = FormFactor.Desktop,
            BoardManufacturer = "ASUSTeK COMPUTER INC.",
            BoardProduct = "ROG STRIX B650-A GAMING WIFI",
            Cpu = new CpuInfo("AMD Ryzen 7 7800X3D 8-Core Processor", HardwareVendor.Amd, 8, 16, 4200),
            Gpus = [new GpuInfo("NVIDIA GeForce RTX 4070", HardwareVendor.Nvidia, "32.0.15.6094", new DateTime(2026, 9, 12), "PCI\\VEN_10DE", false)],
        },
        true,
        [new ModuleResult("MXX", "Test", findings, TimeSpan.FromSeconds(1))]);

    private static Finding F(string id, FindingStatus status, string? current = null, Severity severity = Severity.Medium) =>
        new() { Id = id, Title = "Titre " + id, Status = status, Severity = severity, Current = current, Explanation = "e" };

    [Fact]
    public void Summary_gives_the_configuration_and_the_points_to_fix_first()
    {
        var text = HelpSummary.Build(Report(
            F("M10.capacity", FindingStatus.Info, "32 Go au total (2 × 16 Go), DDR5"),
            F("M02.warn", FindingStatus.Warning, "3 écrans bleus"),
            F("M02.bad", FindingStatus.Problem, "disque en fin de vie", Severity.High),
            F("M06.opt", FindingStatus.Improvable),
            F("M06.ok", FindingStatus.Ok)), Privacy);

        Assert.Contains("Windows 11 Pro 24H2 (build 26100.2033)", text, StringComparison.Ordinal);
        Assert.Contains("ROG STRIX B650-A GAMING WIFI", text, StringComparison.Ordinal);
        Assert.Contains("AMD Ryzen 7 7800X3D 8-Core Processor (8 cœurs, 16 threads)", text, StringComparison.Ordinal);
        Assert.Contains("32 Go au total (2 × 16 Go), DDR5", text, StringComparison.Ordinal);
        Assert.Contains("NVIDIA GeForce RTX 4070 (pilote 32.0.15.6094", text, StringComparison.Ordinal);
        Assert.Contains("- [PROBLÈME] Titre M02.bad : disque en fin de vie (M02.bad)", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("M02.bad", StringComparison.Ordinal) < text.IndexOf("M02.warn", StringComparison.Ordinal));
        Assert.Contains("1 optimisation(s) possible(s)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("M06.ok", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Personal_names_paths_and_emails_are_masked()
    {
        var text = HelpSummary.Build(Report(
            F("M19.files", FindingStatus.Warning, @"C:\Users\Alex\OneDrive · alex.dupont@example.com"),
            F("M17.device", FindingStatus.Problem, "iPhone de Alex, DESKTOP-ALEX42")), Privacy);

        Assert.DoesNotContain("Alex", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@example.com", text, StringComparison.Ordinal);
        Assert.Contains(@"%USERPROFILE%\OneDrive", text, StringComparison.Ordinal);
        Assert.Contains("iPhone de [utilisateur], [nom-du-pc]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Short_account_names_do_not_erase_ordinary_words()
    {
        var filter = new PrivacyFilter("PC", "PC", null);

        Assert.Equal("PC fixe", filter.Mask("PC fixe"));
        Assert.Equal("Jeannette", new PrivacyFilter("Jean", null, null).Mask("Jeannette"));
    }

    [Fact]
    public void Long_lists_are_cut_and_wanted_changes_are_listed_apart()
    {
        var findings = Enumerable.Range(1, HelpSummary.MaxDetailed + 5)
            .Select(i => F($"M01.w{i:00}", FindingStatus.Warning, new string('x', 300)))
            .Append(F("M04.wanted", FindingStatus.Info) with { AcknowledgedFrom = FindingStatus.Improvable })
            .ToArray();

        var text = HelpSummary.Build(Report(findings), Privacy);

        Assert.Contains("… et 5 autre(s)", text, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 200), text, StringComparison.Ordinal);
        Assert.Contains("choix de l'utilisateur", text, StringComparison.Ordinal);
        Assert.Contains("Titre M04.wanted (M04.wanted)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Partial_score_is_announced_with_the_modules_that_could_not_be_checked()
    {
        var report = Report(F("M06.ok", FindingStatus.Ok));
        report = report with { Modules = [.. report.Modules, new ModuleResult("M03", "Mises à jour Windows", [], TimeSpan.FromSeconds(180), "Délai dépassé (180 s).")] };

        var text = HelpSummary.Build(report, Privacy);

        Assert.Contains("- Score partiel : 1 module n'a pas pu être vérifié (Mises à jour Windows).", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Score partiel", HelpSummary.Build(Report(F("M06.ok", FindingStatus.Ok)), Privacy), StringComparison.Ordinal);
    }

    [Fact]
    public void Clean_pc_says_so()
    {
        var text = HelpSummary.Build(Report(F("M06.ok", FindingStatus.Ok)), Privacy);

        Assert.Contains("Aucun problème ni point à surveiller", text, StringComparison.Ordinal);
    }
}
