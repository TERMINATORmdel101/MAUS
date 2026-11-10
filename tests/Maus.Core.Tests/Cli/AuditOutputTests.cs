using Maus.Cli;
using Maus.Core.Engine;
using Maus.Core.Reporting;

namespace Maus.Core.Tests.Cli;

/// <summary>Ce qu'un script voit d'un audit : son code de sortie, et un rapport qui existe vraiment quand le code le laisse croire.</summary>
public sealed class AuditOutputTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("maus-cli-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static ModuleResult Result(string id, FindingStatus status = FindingStatus.Ok, string? error = null, bool notChosen = false) =>
        new(id, id, [new Finding { Id = id + ".x", Title = "x", Status = status, Explanation = "x" }], TimeSpan.FromSeconds(1), error, notChosen);

    [Fact]
    public void The_exit_code_follows_the_worst_result()
    {
        Assert.Equal(0, AuditExit.Of([Result("M01"), Result("M02", FindingStatus.Warning)]));
        Assert.Equal(2, AuditExit.Of([Result("M01"), Result("M02", FindingStatus.Problem)]));
        Assert.Equal(4, AuditExit.Of([Result("M01"), Result("M02", error: "panne")]));
        Assert.Equal(2, AuditExit.Of([Result("M01", FindingStatus.Problem), Result("M02", error: "panne")]));
        Assert.Equal(0, AuditExit.Of([Result("M01"), Result("M02", error: "non choisi", notChosen: true)]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a<b>.html")]
    public void A_file_name_that_cannot_be_written_is_refused(string path) =>
        Assert.NotNull(HtmlReportFile.Problem(path));

    [Fact]
    public void A_folder_or_a_missing_folder_is_refused_and_nothing_is_created()
    {
        var missing = Path.Combine(_folder, "absent");

        Assert.NotNull(HtmlReportFile.Problem(_folder));
        Assert.NotNull(HtmlReportFile.Problem(Path.Combine(missing, "rapport.html")));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void The_null_device_stays_accepted() =>
        Assert.Null(HtmlReportFile.Problem("NUL"));

    [Fact]
    public void Checking_a_new_file_leaves_nothing_behind()
    {
        var path = Path.Combine(_folder, "rapport.html");

        Assert.Null(HtmlReportFile.Problem(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Checking_an_existing_file_leaves_it_untouched()
    {
        var path = Path.Combine(_folder, "rapport.html");
        File.WriteAllText(path, "ancien rapport");

        Assert.Null(HtmlReportFile.Problem(path));
        Assert.Equal("ancien rapport", File.ReadAllText(path));
    }

    [Fact]
    public void A_read_only_file_is_refused()
    {
        var path = Path.Combine(_folder, "rapport.html");
        File.WriteAllText(path, "ancien rapport");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.NotNull(HtmlReportFile.Problem(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void After_a_long_silence_the_open_modules_are_named_once()
    {
        var output = new StringWriter();
        using var progress = new ConsoleProgress(["M01", "M03"], output, Timeout.InfiniteTimeSpan);

        progress.Report(Result("M01"));
        progress.SayWhatIsStillOpen();
        progress.SayWhatIsStillOpen();

        var text = output.ToString();
        Assert.Equal(2, text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("M03", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_is_said_when_every_module_has_finished_or_was_not_chosen()
    {
        var output = new StringWriter();
        using var progress = new ConsoleProgress(["M01", "M03"], output, Timeout.InfiniteTimeSpan);

        progress.Report(Result("M03", notChosen: true));
        progress.Report(Result("M01"));
        progress.SayWhatIsStillOpen();

        Assert.DoesNotContain("M03", output.ToString(), StringComparison.Ordinal);
        Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void The_help_lists_every_module_and_every_exit_code()
    {
        var modules = AuditEngine.CreateWithBuiltInModules().Modules;
        var output = new StringWriter();

        CliHelp.Write(modules, output);

        var text = output.ToString();
        foreach (var module in modules)
        {
            Assert.Contains($"  {module.Id}  {module.Title}", text, StringComparison.Ordinal);
        }

        foreach (var code in new[] { "0", "1", "2", "3", "4", "130" })
        {
            Assert.Contains($"  {code}  ", text, StringComparison.Ordinal);
        }

        Assert.Contains("--json", text, StringComparison.Ordinal);
        Assert.Contains("--html", text, StringComparison.Ordinal);
    }
}
