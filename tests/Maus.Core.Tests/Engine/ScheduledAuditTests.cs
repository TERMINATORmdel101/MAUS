using System.Xml.Linq;
using Maus.Core.Engine;

namespace Maus.Core.Tests.Engine;

public class ScheduledAuditTests
{
    [Fact]
    public void Only_the_task_maus_wrote_for_this_account_counts_as_ours()
    {
        var ours = ScheduledAudit.ParseTask(ScheduledAudit.TaskXml(@"C:\Program Files\WindowsApps\MAUS_1\MAUS.exe", DayOfWeek.Monday, "S-1-5-21-1", new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Local)))!;

        Assert.Equal(DayOfWeek.Monday, ours.Day);
        Assert.True(ScheduledAudit.IsMausTask(ours, [null, "s-1-5-21-1"]));
        Assert.False(ScheduledAudit.IsMausTask(ours, ["S-1-5-21-2"]));
        Assert.False(ScheduledAudit.IsMausTask(ours with { Author = "Autre" }, ["S-1-5-21-1"]));
        Assert.False(ScheduledAudit.IsMausTask(ours with { Arguments = "--scheduled-audit --tout" }, ["S-1-5-21-1"]));
        Assert.Null(ScheduledAudit.ParseTask("pas du XML"));
        Assert.False(ScheduledAudit.BelongsToSomeoneElse(null, ["S-1-5-21-1"]));
        Assert.False(ScheduledAudit.BelongsToSomeoneElse(ours, ["S-1-5-21-1"]));
        Assert.True(ScheduledAudit.BelongsToSomeoneElse(ours, ["S-1-5-21-2"]));
        Assert.True(ScheduledAudit.BelongsToSomeoneElse(ours with { Author = "Autre" }, ["S-1-5-21-1"]));
    }

    [Fact]
    public void The_state_read_from_a_task_says_who_owns_it_and_whether_it_keeps_old_rights()
    {
        var ours = new ExistingTask(DayOfWeek.Monday, "maus.exe", ScheduledAudit.Argument, "MAUS", "S-1-5-21-1", "LeastPrivilege");
        var elevated = ours with { RunLevel = "HighestAvailable" };

        Assert.Equal(new(DayOfWeek.Monday, "maus.exe"), ScheduledAudit.StateOf(ours, ["S-1-5-21-1"]));
        Assert.True(ScheduledAudit.StateOf(elevated, ["S-1-5-21-1"]).KeepsOldRights);

        // Une tâche qui n'est pas celle de ce compte ne passe jamais pour une tâche à remettre en place.
        var foreign = ScheduledAudit.StateOf(elevated, ["S-1-5-21-2"]);
        Assert.True(foreign.OfAnotherAccount);
        Assert.False(foreign.KeepsOldRights);
        Assert.Equal(new(null, null), ScheduledAudit.StateOf(null, ["S-1-5-21-1"]));
    }

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
        Assert.Equal("LeastPrivilege", xml.Descendants(Ns + "RunLevel").Single().Value);
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
    public void Rights_are_only_asked_when_maus_sits_in_an_admin_only_folder(string executable, bool allowed)
    {
        string[] roots = [@"C:\Program Files", @"C:\Program Files (x86)"];

        Assert.Equal(allowed, ScheduledAudit.IsInProtectedFolder(executable, roots));
    }

    [Theory]
    [InlineData("MAUS", ScheduledAudit.Argument, "u", "HighestAvailable", true)]
    [InlineData("MAUS", ScheduledAudit.Argument, "u", "LeastPrivilege", false)]
    [InlineData("MAUS", ScheduledAudit.Argument, "u", null, false)]
    [InlineData("Autre", ScheduledAudit.Argument, "u", "HighestAvailable", false)]
    [InlineData("MAUS", "--autre", "u", "HighestAvailable", false)]
    [InlineData("MAUS", ScheduledAudit.Argument, "quelqu'un d'autre", "HighestAvailable", false)]
    public void Only_an_elevated_task_written_by_maus_loses_its_rights(string author, string arguments, string user, string? level, bool expected) =>
        Assert.Equal(expected, ScheduledAudit.MustDropRights(new ExistingTask(DayOfWeek.Monday, "maus.exe", arguments, author, user, level), ["u"]));

    [Fact]
    public void A_task_without_a_day_is_not_recreated() =>
        Assert.False(ScheduledAudit.MustDropRights(new ExistingTask(null, "maus.exe", ScheduledAudit.Argument, "MAUS", "u", "HighestAvailable"), ["u"]));

    [Theory]
    [InlineData("LeastPrivilege")]
    [InlineData("HighestAvailable")]
    public void The_run_level_of_an_existing_task_is_read(string level)
    {
        var xml = ScheduledAudit.TaskXml("maus.exe", DayOfWeek.Monday, "u", new DateTime(2026, 9, 24, 15, 0, 0)).Replace("LeastPrivilege", level, StringComparison.Ordinal);

        Assert.Equal(level, ScheduledAudit.ParseTask(xml)!.RunLevel);
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

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\Editeur.MAUS_0.7.6.0_x64__6c0b8wp3bzq1y\MAUS.exe", true)]
    [InlineData(@"C:\Program Files\MAUS\MAUS.exe", false)]
    public void A_store_folder_is_recognised(string executable, bool expected) =>
        Assert.Equal(expected, ScheduledAudit.IsInStoreFolder(executable));

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
