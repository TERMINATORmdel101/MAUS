using Maus.Cli;

namespace Maus.Core.Tests.Cli;

/// <summary>Une option mal écrite ne doit jamais élargir l'action demandée : elle est refusée avant tout audit.</summary>
public sealed class CliArgumentsTests
{
    [Theory]
    [InlineData]
    [InlineData("--json")]
    [InlineData("--html", "rapport.html")]
    [InlineData("--lang", "en", "--html", "rapport.html")]
    [InlineData("--module", "M01", "--module", "M06", "--json")]
    [InlineData("--MODULE", "m06")]
    [InlineData("--summary", "--module", "M06")]
    [InlineData("--plan", "--module", "M06")]
    [InlineData("--apply", "M06.a", "M06.b", "--yes", "--without-restore-point")]
    [InlineData("--apply-recommended", "--module", "M06", "--enable-protection", "--yes", "--html", "avant-apres.html")]
    [InlineData("--journal")]
    [InlineData("--revert", "SEANCE", "--change", "M06.a", "--force", "--yes")]
    [InlineData("--restart-explorer", "--yes")]
    [InlineData("--ack", "M06.x", "--yes")]
    [InlineData("--unack", "M06.x")]
    [InlineData("--set", "gamebar=1")]
    [InlineData("--prefs", "--lang", "es")]
    public void Every_documented_line_is_accepted(params string[] args) =>
        Assert.Null(CliArguments.Parse(args).Error);

    [Theory]
    [InlineData("--modul", "M06")]
    [InlineData("--apply-recomended")]
    [InlineData("-x")]
    [InlineData("--benchmark")]
    [InlineData("M06")]
    [InlineData("--json", "rapport.json")]
    [InlineData("--module", "M01", "M06")]
    [InlineData("--html")]
    [InlineData("--html", "--json")]
    [InlineData("--module")]
    [InlineData("--module", "--json")]
    [InlineData("--apply")]
    [InlineData("--apply", "--yes")]
    [InlineData("--revert")]
    [InlineData("--revert", "SEANCE", "--change")]
    [InlineData("--revert", "SEANCE", "--change", "--force")]
    [InlineData("--ack")]
    [InlineData("--unack")]
    [InlineData("--set")]
    [InlineData("--lang")]
    [InlineData("--lang", "de")]
    [InlineData("--html", "a.html", "--html", "b.html")]
    [InlineData("--json", "--html", "a.html")]
    [InlineData("--plan", "--apply-recommended")]
    [InlineData("--apply", "M06.a", "--revert", "SEANCE")]
    [InlineData("--plan", "--json")]
    [InlineData("--plan", "--yes")]
    [InlineData("--journal", "--module", "M06")]
    [InlineData("--yes")]
    [InlineData("--force")]
    [InlineData("--change", "M06.a")]
    [InlineData("--apply-recommended", "--force")]
    [InlineData("--apply-recommended", "--without-restore-point", "--enable-protection")]
    public void A_wrong_line_is_refused(params string[] args) =>
        Assert.NotNull(CliArguments.Parse(args).Error);

    [Theory]
    [InlineData("--help", "--bogus")]
    [InlineData("--version", "--plan", "--journal")]
    public void Help_and_version_answer_alone_but_the_error_stays_known(params string[] args)
    {
        var parsed = CliArguments.Parse(args);

        Assert.True(parsed.Has(args[0]));
        Assert.NotNull(parsed.Error);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("/?")]
    [InlineData("--HELP")]
    public void Every_form_of_help_is_accepted(string help)
    {
        var parsed = CliArguments.Parse([help]);

        Assert.Null(parsed.Error);
        Assert.True(parsed.Has(help));
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData("en-US", "en")]
    [InlineData("Es_es", "es")]
    [InlineData("FR-fr", "fr")]
    public void The_language_is_read_whatever_its_case_or_region(string asked, string language)
    {
        var parsed = CliArguments.Parse(["--lang", asked]);

        Assert.Null(parsed.Error);
        Assert.Equal(language, parsed.Language);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("-en")]
    [InlineData("")]
    public void An_unknown_language_stays_refused(string asked) =>
        Assert.NotNull(CliArguments.Parse(["--lang", asked]).Error);

    [Fact]
    public void Values_are_read_whatever_the_case_of_the_option()
    {
        var parsed = CliArguments.Parse(["--APPLY", "M06.a", "M06.b", "--Module", "M06", "--module", "M07", "--LANG", "es"]);

        Assert.Null(parsed.Error);
        Assert.True(parsed.Has("--apply"));
        Assert.Equal(["M06.a", "M06.b"], parsed.Values("--apply"));
        Assert.Equal(["M06", "M07"], parsed.Values("--module"));
        Assert.Equal("es", parsed.Language);
    }

    [Fact]
    public void An_unknown_language_is_not_used()
    {
        var parsed = CliArguments.Parse(["--lang", "de"]);

        Assert.Null(parsed.Language);
        Assert.Contains("de", parsed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_mistake_of_the_line_is_the_one_reported()
    {
        var error = CliArguments.Parse(["--bogus", "--module"]).Error;

        Assert.Contains("--bogus", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_change_identifier_names_the_revert_syntax()
    {
        // Sans identifiant, FixEngine.Revert annulerait toute la séance.
        var error = CliArguments.Parse(["--revert", "SEANCE", "--change"]).Error;

        Assert.Contains("--change CORRECTION", error, StringComparison.Ordinal);
    }
}
