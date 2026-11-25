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

    /// <summary>
    /// 0.8.7.1 : « Annuler » seul est le bouton qui renonce (fenêtres de choix) : « Cancel », « Cancelar ». Défaire une correction s'écrit
    /// « Annuler cette correction » : « Undo », « Deshacer ».
    /// </summary>
    [Theory]
    [InlineData("en", "Cancel", "Undo this fix")]
    [InlineData("es", "Cancelar", "Deshacer esta corrección")]
    public void Cancel_gives_up_and_undo_takes_back_a_fix(string language, string cancel, string undo)
    {
        try
        {
            Texts.Use(language);
            Assert.Equal(cancel, Texts.T("Annuler"));
            Assert.Equal(undo, Texts.T("Annuler cette correction"));
        }
        finally
        {
            Texts.Use("fr");
        }
    }

    [Fact]
    public void Zero_is_singular_in_french_only()
    {
        try
        {
            Texts.Use("fr");
            Assert.Equal("0 ligne", Texts.Plural(0, "{0} ligne", "{0} lignes"));
            Assert.Equal("2 lignes", Texts.Plural(2, "{0} ligne", "{0} lignes"));

            Texts.Use("en");
            Assert.Equal("0 lines", Texts.Plural(0, "{0} line", "{0} lines"));
            Assert.Equal("1 line", Texts.Plural(1, "{0} line", "{0} lines"));
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

    /// <summary>
    /// Une marque posée en français ne se compare pas à un texte anglais : le constat est de nouveau signalé, avec la raison,
    /// jusqu'à une nouvelle marque ; revenu au français, la marque vaut de nouveau, mot pour mot.
    /// </summary>
    [Fact]
    public void Acknowledgement_of_another_language_is_shown_again_with_the_reason_and_holds_again_in_its_language()
    {
        try
        {
            Texts.Use("fr");
            var finding = new Finding { Id = "M06.widgets", Title = "Widgets", Status = FindingStatus.Improvable, Explanation = "…", Current = "activés" };
            var preferences = UserPreferences.Default.Acknowledge(finding, DateTimeOffset.Now);

            Texts.Use("en");
            var english = finding with { Current = "on", Advice = "Turn them off in Settings." };
            Assert.Null(preferences.AcknowledgementFor(english));
            Assert.NotNull(preferences.OtherLanguageMarkFor(english));
            var shown = Assert.Single(Acknowledgements.Apply(new ModuleResult("M06", "M", [english], TimeSpan.Zero), preferences).Findings);
            Assert.Equal(FindingStatus.Improvable, shown.Status);
            Assert.Null(shown.AcknowledgedFrom);
            Assert.EndsWith("Turn them off in Settings.", shown.Advice, StringComparison.Ordinal);
            Assert.True(shown.Advice!.Length > english.Advice!.Length);

            var marked = preferences.Acknowledge(english, DateTimeOffset.Now);
            Assert.Equal("en", Assert.Single(marked.Acknowledged).Language);
            Assert.NotNull(marked.AcknowledgementFor(english));
            Assert.Null(marked.AcknowledgementFor(english with { Current = "off" }));

            Texts.Use("fr");
            Assert.NotNull(preferences.AcknowledgementFor(finding));
            Assert.Null(preferences.OtherLanguageMarkFor(finding));
        }
        finally
        {
            Texts.Use("fr");
        }
    }
}
