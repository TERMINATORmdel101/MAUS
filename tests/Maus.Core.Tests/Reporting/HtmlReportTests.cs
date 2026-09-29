using Maus.Core.Fixes;
using Maus.Core.Hardware;
using Maus.Core.Platform;
using Maus.Core.Reporting;

namespace Maus.Core.Tests.Reporting;

public class HtmlReportTests
{
    private static readonly WindowsInfo Windows = new("Windows 11 Famille", "Core", "25H2", 26200, 1000);

    private static AuditReport Report(FindingStatus taskView, string current) => new(
        new DateTimeOffset(2026, 9, 25, 14, 0, 0, TimeSpan.FromHours(2)),
        "0.2.0",
        Windows,
        new HardwareProfile
        {
            FormFactor = FormFactor.Laptop,
            Cpu = new CpuInfo("AMD Ryzen 7 5800H", HardwareVendor.Amd, 8, 16, 3200),
            Model = "Numéro-secret-123",
        },
        true,
        [
            new ModuleResult("M06", "Interface et effets visuels",
            [
                new Finding { Id = "M06.taskview", Title = "Bouton Vue des tâches masqué", Status = taskView, Current = current, Expected = "masqué (0)", Explanation = "Explication <script>alert(1)</script>" },
                new Finding { Id = "M06.peek", Title = "Peek", Status = FindingStatus.Ok, Explanation = "e" },
            ], TimeSpan.FromSeconds(1)),
        ]);

    [Fact]
    public void Before_after_report_lists_changes_and_how_to_undo_them()
    {
        var session = new JournalSession
        {
            Id = "20260925-120000-abcdef",
            CreatedAt = DateTimeOffset.Now,
            RestorePoint = new RestorePointInfo(42, "MAUS", null),
            Entries =
            {
                new JournalEntry
                {
                    ChangeId = "M06.taskview",
                    ModuleId = "M06",
                    ChangeTitle = "Masquer le bouton Vue des tâches",
                    Key = SettingKey.Registry("HKCU", @"Software\X", "ShowTaskViewButton"),
                    Before = SettingValue.Dword(1),
                    After = SettingValue.Dword(0),
                    State = EntryState.Applied,
                },
            },
        };

        var html = HtmlReport.Build(new HtmlReportInput
        {
            Before = Report(FindingStatus.Improvable, "1"),
            After = Report(FindingStatus.Ok, "0"),
            Session = session,
        });

        Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.Ordinal);
        Assert.Contains("Rapport avant/après MAUS", html, StringComparison.Ordinal);
        Assert.Contains("Ce qui a changé", html, StringComparison.Ordinal);
        Assert.Contains("maus --revert 20260925-120000-abcdef", html, StringComparison.Ordinal);
        Assert.Contains("point n° 42", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Numéro-secret-123", html, StringComparison.Ordinal);

        // Aucun lien vers l'extérieur ; le logo encodé (base64) est retiré avant, ses lettres pourraient former « http ».
        var withoutLogo = System.Text.RegularExpressions.Regex.Replace(html, "data:image/jpeg;base64,[A-Za-z0-9+/=]+", "data:");
        Assert.DoesNotContain("http", withoutLogo, StringComparison.OrdinalIgnoreCase);

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "maus-rapport-exemple.html"), html);
    }

    [Fact]
    public void Simple_audit_report_has_no_before_after_sections()
    {
        var html = HtmlReport.Build(new HtmlReportInput { After = Report(FindingStatus.Improvable, "1") });

        Assert.Contains("Rapport d&#39;audit MAUS", html, StringComparison.Ordinal);
        // Le logo est dans la page elle-même : le rapport reste un seul fichier, lisible hors connexion.
        Assert.Contains("<img class=\"logo\" alt=\"MAUS\" src=\"data:image/jpeg;base64,/9j/", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Ce qui a changé", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Corrections faites", html, StringComparison.Ordinal);
    }
}
