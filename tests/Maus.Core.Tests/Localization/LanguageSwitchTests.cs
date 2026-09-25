using Maus.Core.Localization;
using Maus.Core.Modules.M06Visual;
using Maus.Core.Preferences;

namespace Maus.Core.Tests.Localization;

/// <summary>Ces tests changent la langue active (état global) : ils tournent seuls, après les autres.</summary>
[CollectionDefinition(nameof(LanguageSwitch), DisableParallelization = true)]
public sealed class LanguageSwitch;

[Collection(nameof(LanguageSwitch))]
public class LanguageSwitchTests
{
    [Theory]
    [InlineData("en", "Widgets turned off", "Disabled effects", "Task View button hidden", "Interface and visual effects")]
    [InlineData("es", "Widgets desactivados", "Efectos desactivados", "Botón Vista de tareas oculto", "Interfaz y efectos visuales")]
    public async Task Findings_code_and_catalogs_follow_the_active_language(string language, string widgets, string category, string catalogRule, string moduleTitle)
    {
        try
        {
            Texts.Use(language);
            var module = new VisualEffectsModule();
            var findings = await module.DetectAsync(TestContext.Create(), CancellationToken.None);

            Assert.Equal(widgets, findings.Single(f => f.Id == "M06.widgets").Title);
            Assert.Equal(category, findings.Single(f => f.Id == "M06.menu-animation").Category);
            Assert.Equal(catalogRule, findings.Single(f => f.Id == "M06.taskview").Title);
            Assert.Equal(moduleTitle, module.Title);
        }
        finally
        {
            Texts.Use("fr");
        }
    }

    [Fact]
    public async Task French_stays_the_source_after_switching_back()
    {
        Texts.Use("en");
        Texts.Use("fr");

        var findings = await new VisualEffectsModule().DetectAsync(TestContext.Create(), CancellationToken.None);

        Assert.Equal("Widgets désactivés", findings.Single(f => f.Id == "M06.widgets").Title);
    }

    [Fact]
    public void Acknowledgement_survives_a_language_change_then_is_rebound()
    {
        try
        {
            Texts.Use("fr");
            var finding = new Finding { Id = "M06.widgets", Title = "Widgets", Status = FindingStatus.Improvable, Explanation = "…", Current = "activés" };
            var preferences = UserPreferences.Default.Acknowledge(finding, DateTimeOffset.Now);

            Texts.Use("en");
            var english = finding with { Current = "on" };
            Assert.NotNull(preferences.AcknowledgementFor(english));

            var rebound = preferences.RebindLanguage([english with { AcknowledgedFrom = FindingStatus.Improvable }]);
            Assert.Equal("on", rebound.Acknowledged.Single().Current);
            Assert.Equal("en", rebound.Acknowledged.Single().Language);
            Assert.Null(rebound.AcknowledgementFor(english with { Current = "off" }));
        }
        finally
        {
            Texts.Use("fr");
        }
    }
}
