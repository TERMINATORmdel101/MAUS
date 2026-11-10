using Maus.Cli;
using Maus.Core.Fixes;
using Maus.Core.Preferences;

namespace Maus.Core.Tests.Cli;

[Collection(nameof(ConsoleInput))]
public sealed class PreferenceCommandsTests
{
    private sealed class RefusingStore(Exception refusal) : IPreferencesStore
    {
        public UserPreferences Load() => UserPreferences.Default;

        public void Save(UserPreferences preferences) => throw refusal;
    }

    private static Finding Warning(string id = "M01.x") => new()
    {
        Id = id,
        Title = "Constat",
        Status = FindingStatus.Warning,
        Severity = Severity.Medium,
        Current = "valeur",
        Explanation = "Explication",
    };

    private static ModuleResult[] Results(Finding finding) => [new("M01", "Module", [finding], TimeSpan.Zero)];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("gamebar")]
    public void Without_an_assignment_the_syntax_is_explained(string? assignment)
    {
        var output = new StringWriter();

        Assert.Equal(1, PreferenceCommands.Set(new MemoryPreferencesStore(), assignment, output));
        Assert.Contains("Syntaxe", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gamebar=9")]
    [InlineData("gamebar=")]
    [InlineData("alimentation=maximum")]
    [InlineData("autre=1")]
    public void An_unknown_choice_changes_nothing(string assignment)
    {
        var store = new MemoryPreferencesStore();

        Assert.Equal(1, PreferenceCommands.Set(store, assignment, new StringWriter()));
        Assert.Equal(UserPreferences.Default.GameBarProfile, store.Load().GameBarProfile);
        Assert.Equal(UserPreferences.Default.LaptopPower, store.Load().LaptopPower);
    }

    [Theory]
    [InlineData("gamebar=2", 2, LaptopPowerChoice.NotChosen)]
    [InlineData("GameBar = 3", 3, LaptopPowerChoice.NotChosen)]
    [InlineData("gamebar=auto", null, LaptopPowerChoice.NotChosen)]
    [InlineData("alimentation=performance", null, LaptopPowerChoice.Performance)]
    [InlineData("alimentation=partout", null, LaptopPowerChoice.PerformanceEverywhere)]
    [InlineData("alimentation=autonomie", null, LaptopPowerChoice.Battery)]
    [InlineData("alimentation=auto", null, LaptopPowerChoice.NotChosen)]
    [InlineData("power=performance", null, LaptopPowerChoice.Performance)]
    [InlineData("power=everywhere", null, LaptopPowerChoice.PerformanceEverywhere)]
    [InlineData("POWER=Battery", null, LaptopPowerChoice.Battery)]
    [InlineData("power=auto", null, LaptopPowerChoice.NotChosen)]
    [InlineData("energia=rendimiento", null, LaptopPowerChoice.Performance)]
    [InlineData("energia=siempre", null, LaptopPowerChoice.PerformanceEverywhere)]
    [InlineData("energía=batería", null, LaptopPowerChoice.Battery)]
    [InlineData("energia=auto", null, LaptopPowerChoice.NotChosen)]
    public void Known_choices_are_saved(string assignment, int? profile, LaptopPowerChoice power)
    {
        var store = new MemoryPreferencesStore();

        Assert.Equal(0, PreferenceCommands.Set(store, assignment, new StringWriter()));
        Assert.Equal(profile, store.Load().GameBarProfile);
        Assert.Equal(power, store.Load().LaptopPower);
    }

    [Fact]
    public void A_refused_save_is_explained_and_is_an_error_for_a_script()
    {
        foreach (var refusal in new Exception[] { new UnauthorizedAccessException("refus"), new IOException("refus"), new JournalUnsafeException("refus") })
        {
            var output = new StringWriter();

            Assert.Equal(3, PreferenceCommands.Set(new RefusingStore(refusal), "gamebar=1", output));
            Assert.Contains("refus", output.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_finding_that_does_not_deviate_cannot_be_marked()
    {
        var store = new MemoryPreferencesStore();
        var context = TestContext.Create();

        Assert.Equal(1, PreferenceCommands.Acknowledge(store, context, Results(Warning()), "M01.inconnu", assumeYes: true, new StringWriter()));
        Assert.Equal(1, PreferenceCommands.Acknowledge(store, context, Results(Warning() with { Status = FindingStatus.Ok }), "M01.x", assumeYes: true, new StringWriter()));
        Assert.Empty(store.Load().Acknowledged);
    }

    [Fact]
    public void A_warning_is_marked_and_can_be_unmarked()
    {
        var store = new MemoryPreferencesStore();

        Assert.Equal(0, PreferenceCommands.Acknowledge(store, TestContext.Create(), Results(Warning()), "m01.X", assumeYes: true, new StringWriter()));
        Assert.Equal("M01.x", store.Load().Acknowledged.Single().FindingId);

        Assert.Equal(0, PreferenceCommands.Unacknowledge(store, "M01.x", new StringWriter()));
        Assert.Empty(store.Load().Acknowledged);
    }

    [Fact]
    public void A_warning_is_marked_only_after_yes_like_in_the_window()
    {
        var store = new MemoryPreferencesStore();
        var input = Console.In;
        Console.SetIn(new StringReader("n" + Environment.NewLine));
        try
        {
            Assert.Equal(1, PreferenceCommands.Acknowledge(store, TestContext.Create(), Results(Warning()), "M01.x", assumeYes: false, new StringWriter()));
        }
        finally
        {
            Console.SetIn(input);
        }

        Assert.Empty(store.Load().Acknowledged);
    }

    [Fact]
    public void A_red_problem_is_marked_only_after_yes()
    {
        var store = new MemoryPreferencesStore();
        var problem = Warning() with { Status = FindingStatus.Problem, Severity = Severity.High };
        var input = Console.In;
        Console.SetIn(new StringReader("n" + Environment.NewLine));
        try
        {
            Assert.Equal(1, PreferenceCommands.Acknowledge(store, TestContext.Create(), Results(problem), "M01.x", assumeYes: false, new StringWriter()));
        }
        finally
        {
            Console.SetIn(input);
        }

        Assert.Empty(store.Load().Acknowledged);

        Assert.Equal(0, PreferenceCommands.Acknowledge(store, TestContext.Create(), Results(problem), "M01.x", assumeYes: true, new StringWriter()));
        Assert.Single(store.Load().Acknowledged);
    }

    [Fact]
    public void Unticked_modules_and_the_weekly_notice_are_printed()
    {
        var preferences = UserPreferences.Default with { SkippedModules = ["M03", "M20"], WeeklyNotification = WeeklyNotification.Never };
        var output = new StringWriter();

        PreferenceCommands.Print(preferences, output);

        var text = output.ToString();
        Assert.Contains("Modules décochés pour l'audit : M03, M20", text, StringComparison.Ordinal);
        Assert.Contains("vous prévenir : jamais", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void Unmarking_needs_an_identifier(string? id) =>
        Assert.Equal(1, PreferenceCommands.Unacknowledge(new MemoryPreferencesStore(), id, new StringWriter()));

    [Fact]
    public void Choices_are_printed_with_each_marked_finding()
    {
        var preferences = UserPreferences.Default.Acknowledge(Warning(), TestContext.Create().Now) with { GameBarProfile = 2 };
        var output = new StringWriter();

        Assert.Equal(0, PreferenceCommands.Print(preferences, output));

        var text = output.ToString();
        Assert.Contains("profil 2", text, StringComparison.Ordinal);
        Assert.Contains("pas encore choisi", text, StringComparison.Ordinal);
        Assert.Contains("seulement pour un problème rouge", text, StringComparison.Ordinal);
        Assert.Contains("Modules décochés pour l'audit : aucun", text, StringComparison.Ordinal);
        Assert.Contains("M01.x", text, StringComparison.Ordinal);
    }
}
