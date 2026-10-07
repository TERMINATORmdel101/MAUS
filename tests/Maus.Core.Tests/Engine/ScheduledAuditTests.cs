using System.Xml.Linq;
using Maus.Core.Engine;

namespace Maus.Core.Tests.Engine;

public class ScheduledAuditTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void Task_runs_maus_weekly_at_noon_with_highest_rights_only_when_the_user_is_logged_on()
    {
        // Jeudi 24/09/2026 à 15 h : prochain dimanche midi = 27/09.
        var xml = XDocument.Parse(ScheduledAudit.TaskXml(@"C:\Program Files\MAUS & co\MAUS.exe", DayOfWeek.Sunday, @"PC\Alex", new DateTime(2026, 9, 24, 15, 0, 0)));

        Assert.Equal("2026-09-27T12:00:00", xml.Descendants(Ns + "StartBoundary").Single().Value);
        Assert.NotNull(xml.Descendants(Ns + "Sunday").SingleOrDefault());
        Assert.Equal(@"C:\Program Files\MAUS & co\MAUS.exe", xml.Descendants(Ns + "Command").Single().Value);
        Assert.Equal("--scheduled-audit", xml.Descendants(Ns + "Arguments").Single().Value);
        Assert.Equal("HighestAvailable", xml.Descendants(Ns + "RunLevel").Single().Value);
        Assert.Equal("InteractiveToken", xml.Descendants(Ns + "LogonType").Single().Value);
        Assert.Equal(@"PC\Alex", xml.Descendants(Ns + "UserId").Single().Value);
        Assert.Equal("true", xml.Descendants(Ns + "StartWhenAvailable").Single().Value);
        Assert.Equal("7", xml.Descendants(Ns + "Priority").Single().Value);
    }

    [Theory]
    [InlineData(@"C:\Program Files\MAUS\MAUS.exe", true)]
    [InlineData(@"C:\Program Files\WindowsApps\MAUS_3.9.1.0_x64__abc\MAUS.exe", true)]
    [InlineData(@"C:\Program Files (x86)\MAUS\MAUS.exe", true)]
    [InlineData(@"C:\Users\Alex\Downloads\MAUS\MAUS.exe", false)]
    [InlineData(@"C:\Users\Alex\Documents\MAUS\publish\MAUS-3.9.1\MAUS.exe", false)]
    [InlineData(@"C:\Program Files\..\Users\Alex\MAUS.exe", false)]
    [InlineData(@"C:\Program FilesEvil\MAUS.exe", false)]
    [InlineData(@"E:\MAUS.exe", false)]
    [InlineData("MAUS.exe", false)]
    public void Task_is_only_offered_when_maus_sits_in_an_admin_only_folder(string executable, bool allowed)
    {
        string[] roots = [@"C:\Program Files", @"C:\Program Files (x86)"];

        var refusal = ScheduledAudit.Refusal(executable, roots);

        Assert.Equal(allowed, refusal is null);
        if (!allowed)
        {
            Assert.Contains("dossier protégé", refusal, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Same_day_after_noon_waits_for_next_week()
    {
        var xml = XDocument.Parse(ScheduledAudit.TaskXml("maus.exe", DayOfWeek.Thursday, "u", new DateTime(2026, 9, 24, 15, 0, 0)));

        Assert.Equal("2026-10-01T12:00:00", xml.Descendants(Ns + "StartBoundary").Single().Value);
    }

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\Editeur.MAUS_0.7.5.0_x64__6c0b8wp3bzq1y\MAUS.exe", @"C:\Program Files\WindowsApps\Editeur.MAUS_0.7.6.0_x64__6c0b8wp3bzq1y\MAUS.exe", true)]
    [InlineData(@"C:\Program Files\WindowsApps\Editeur.MAUS_0.7.6.0_x64__6c0b8wp3bzq1y\MAUS.exe", @"C:\Program Files\WindowsApps\Editeur.MAUS_0.7.6.0_x64__6c0b8wp3bzq1y\MAUS.exe", false)]
    [InlineData(@"C:\Program Files\WindowsApps\Autre.Appli_1.0.0.0_x64__6c0b8wp3bzq1y\MAUS.exe", @"C:\Program Files\WindowsApps\Editeur.MAUS_0.7.6.0_x64__6c0b8wp3bzq1y\MAUS.exe", false)]
    [InlineData(@"C:\Program Files\WindowsApps\Editeur.MAUS_0.7.5.0_x64__autreediteur\MAUS.exe", @"C:\Program Files\WindowsApps\Editeur.MAUS_0.7.6.0_x64__6c0b8wp3bzq1y\MAUS.exe", false)]
    [InlineData(@"C:\Program Files\MAUS\MAUS.exe", @"C:\Program Files\WindowsApps\Editeur.MAUS_0.7.6.0_x64__6c0b8wp3bzq1y\MAUS.exe", false)]
    [InlineData(@"C:\Program Files\WindowsApps\Editeur.MAUS_0.7.5.0_x64__6c0b8wp3bzq1y\MAUS.exe", @"C:\Program Files\MAUS\MAUS.exe", false)]
    public void A_task_left_on_an_older_store_folder_is_recognised(string taskCommand, string current, bool expected)
    {
        Assert.Equal(expected, ScheduledAudit.IsOlderStoreFolder(taskCommand, current));
    }

    [Fact]
    public void Only_red_problems_trigger_a_notification()
    {
        IReadOnlyList<ModuleResult> results =
        [
            new ModuleResult("M02", "t",
            [
                new Finding { Id = "a", Title = "Disque en fin de vie", Status = FindingStatus.Problem, Severity = Severity.High, Explanation = "e" },
                new Finding { Id = "b", Title = "b", Status = FindingStatus.Warning, Severity = Severity.Medium, Explanation = "e" },
                new Finding { Id = "c", Title = "c", Status = FindingStatus.Info, AcknowledgedFrom = FindingStatus.Problem, Explanation = "e" },
            ], TimeSpan.Zero),
        ];

        var problems = ScheduledAudit.WorthNotifying(results);

        Assert.Single(problems);
        Assert.Contains("Disque en fin de vie", ScheduledAudit.NotificationText(problems), StringComparison.Ordinal);
        Assert.Equal(["/Delete", "/TN", ScheduledAudit.TaskName, "/F"], ScheduledAudit.DeleteArguments());
    }
}
