using Maus.Core.Engine.Broker;
using Maus.Core.Preferences;

namespace Maus.Core.Tests.Engine;

/// <summary>Bornes de <c>prefs.save</c> (0.8.6) : la liste des constats « voulus » et les énumérations sont vérifiées avant d'atteindre le moteur.</summary>
public class CheckedPreferencesTests
{
    private static Acknowledgement Wanted(string id, string? current = "x") => new(id, current, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Ordinary_choices_pass_unchanged()
    {
        var preferences = UserPreferences.Default with
        {
            GameBarProfile = 2,
            Acknowledged = [Wanted("M09.driver.nvidia-geforce-rtx-4070", new string('v', 2000)), Wanted("M11.disk-c-nvme", null)],
        };

        Assert.Same(preferences, EngineServer.CheckedPreferences(preferences));
    }

    [Fact]
    public void Too_many_acknowledgements_are_refused() =>
        Assert.Throws<EngineException>(() => EngineServer.CheckedPreferences(UserPreferences.Default with
        {
            Acknowledged = [.. Enumerable.Range(0, 501).Select(i => Wanted("M01.x" + i))],
        }));

    [Theory]
    [InlineData("")]
    [InlineData("M01.\n")]
    public void A_malformed_finding_id_is_refused(string id) =>
        Assert.Throws<EngineException>(() => EngineServer.CheckedPreferences(UserPreferences.Default with { Acknowledged = [Wanted(id)] }));

    [Fact]
    public void An_id_or_value_that_is_too_long_is_refused()
    {
        Assert.Throws<EngineException>(() => EngineServer.CheckedPreferences(UserPreferences.Default with { Acknowledged = [Wanted(new string('a', 201))] }));
        Assert.Throws<EngineException>(() => EngineServer.CheckedPreferences(UserPreferences.Default with { Acknowledged = [Wanted("M01.x", new string('v', 2001))] }));
    }

    [Fact]
    public void A_new_long_mark_passes_the_engine_and_a_short_value_stays_as_it_is()
    {
        var value = new string('v', 5000);
        var marked = UserPreferences.Default.Acknowledge(new Finding { Id = "M17.x", Title = "t", Status = FindingStatus.Warning, Current = value, Explanation = "e" }, DateTimeOffset.UnixEpoch);

        Assert.Same(marked, EngineServer.CheckedPreferences(marked));
        Assert.Equal(Acknowledgement.Bounded(value), Acknowledgement.Bounded(Acknowledgement.Bounded(value)));
        Assert.NotEqual(Acknowledgement.Bounded(value), Acknowledgement.Bounded(value[..4999] + "w"));
        Assert.Equal("court", Acknowledgement.Bounded("court"));
        Assert.Null(Acknowledgement.Bounded(null));
    }

    /// <summary>Après un changement de langue, le moteur reconnaît une marque longue reprise de son propre audit, sans reposer la question.</summary>
    [Fact]
    public void A_long_mark_taken_over_after_a_language_change_is_still_known_to_the_engine()
    {
        var value = new string('v', 5000);
        var audited = new Finding { Id = "M17.x", Title = "t", Status = FindingStatus.Info, Current = value, Explanation = "e" };
        var old = new Acknowledgement("M17.x", "ancien texte", DateTimeOffset.UnixEpoch, "fr");
        var taken = old with { Current = Acknowledgement.Bounded(value), Language = "en" };

        Assert.True(LocalMausEngine.IsKnownAcknowledgement(taken, [old], [audited], "en"));
        Assert.False(LocalMausEngine.IsKnownAcknowledgement(old with { Current = Acknowledgement.Bounded(value + "x"), Language = "en" }, [old], [audited], "en"));
    }

    [Fact]
    public void Undefined_enumerations_and_profiles_are_refused()
    {
        Assert.Throws<EngineException>(() => EngineServer.CheckedPreferences(UserPreferences.Default with { LaptopPower = (LaptopPowerChoice)99 }));
        Assert.Throws<EngineException>(() => EngineServer.CheckedPreferences(UserPreferences.Default with { WeeklyNotification = (WeeklyNotification)99 }));
        Assert.Throws<EngineException>(() => EngineServer.CheckedPreferences(UserPreferences.Default with { Theme = (ThemeChoice)99 }));
        Assert.Throws<EngineException>(() => EngineServer.CheckedPreferences(UserPreferences.Default with { Accent = (AccentChoice)99 }));
        Assert.Throws<EngineException>(() => EngineServer.CheckedPreferences(UserPreferences.Default with { Animations = (AnimationChoice)99 }));
        Assert.Throws<EngineException>(() => EngineServer.CheckedPreferences(UserPreferences.Default with { OverlayCorner = (OverlayCorner)99 }));
        Assert.Throws<EngineException>(() => EngineServer.CheckedPreferences(UserPreferences.Default with { SearchEngine = (Maus.Core.Workshop.SearchEngine)99 }));
        Assert.Throws<EngineException>(() => EngineServer.CheckedPreferences(UserPreferences.Default with { GameBarProfile = 4 }));
        Assert.Throws<EngineException>(() => EngineServer.CheckedPreferences(UserPreferences.Default with { GameBarProfile = 0 }));
    }
}
