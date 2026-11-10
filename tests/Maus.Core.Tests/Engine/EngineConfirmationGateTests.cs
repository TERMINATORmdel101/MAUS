using Maus.Core.Engine.Broker;

namespace Maus.Core.Tests.Engine;

/// <summary>
/// La fenêtre de confirmation du moteur (0.8.6), sans fenêtre : boutons inactifs 2 secondes après chaque arrivée au premier
/// plan, « Oui » au clavier seulement après une pause, « Non » par défaut et au bout de 2 minutes. Horloge d'essai.
/// </summary>
public sealed class EngineConfirmationGateTests
{
    private TimeSpan _now = TimeSpan.FromSeconds(100);

    private EngineConfirmationGate Shown() => new(() => _now);

    private void Wait(double seconds) => _now += TimeSpan.FromSeconds(seconds);

    [Fact]
    public void The_buttons_answer_only_two_seconds_after_the_window_comes_to_the_front()
    {
        var gate = Shown();
        Assert.False(gate.ButtonsEnabled);
        Assert.Null(gate.SecondsBeforeButtons);

        Wait(5);
        Assert.False(gate.ButtonsEnabled);

        gate.Activated();
        Assert.Equal(2, gate.SecondsBeforeButtons);
        Wait(1.2);
        Assert.False(gate.ButtonsEnabled);
        Assert.Equal(1, gate.SecondsBeforeButtons);
        Wait(0.79);
        Assert.False(gate.ButtonsEnabled);

        Wait(0.01);
        Assert.True(gate.ButtonsEnabled);
        Assert.Equal(0, gate.SecondsBeforeButtons);
    }

    [Fact]
    public void A_click_that_arrives_too_early_answers_nothing()
    {
        var gate = Shown();
        gate.Activated();
        Wait(0.5);

        Assert.False(gate.Yes());
        Assert.Null(gate.Answer);

        Wait(1.5);
        Assert.True(gate.Yes());
        Assert.True(gate.Answer);
    }

    [Fact]
    public void Leaving_the_front_stops_the_buttons_and_coming_back_waits_two_seconds_again()
    {
        var gate = Shown();
        gate.Activated();
        Wait(3);
        Assert.True(gate.ButtonsEnabled);

        gate.Deactivated();
        Assert.False(gate.ButtonsEnabled);
        Assert.False(gate.Yes());

        Wait(10);
        gate.Activated();
        Assert.False(gate.ButtonsEnabled);
        Assert.False(gate.Yes());
        Wait(2);
        Assert.True(gate.ButtonsEnabled);
    }

    [Fact]
    public void A_key_on_yes_is_refused_while_the_keyboard_has_not_paused_for_two_seconds()
    {
        var gate = Shown();
        gate.Activated();

        // Une frappe continue : chaque touche relance l'attente, même une fois les boutons actifs.
        for (var i = 0; i < 10; i++)
        {
            Wait(0.4);
            gate.KeyPressed();
        }

        Assert.True(gate.ButtonsEnabled);
        Assert.False(gate.AcceptsKeyboardYes);
        Assert.Equal(2, gate.SecondsBeforeKeyboardYes);

        Wait(1.9);
        Assert.False(gate.AcceptsKeyboardYes);
        Wait(0.1);
        Assert.True(gate.AcceptsKeyboardYes);
        Assert.Equal(0, gate.SecondsBeforeKeyboardYes);
    }

    [Fact]
    public void The_keyboard_never_answers_yes_before_the_buttons_are_active()
    {
        var gate = Shown();
        Wait(30);
        Assert.False(gate.AcceptsKeyboardYes);

        gate.Activated();
        Wait(1);
        Assert.False(gate.AcceptsKeyboardYes);
        Assert.Equal(1, gate.SecondsBeforeKeyboardYes);
    }

    [Fact]
    public void No_closing_the_window_and_escape_answer_no_and_never_undo_a_yes()
    {
        var refused = Shown();
        refused.No();
        Assert.False(refused.Answer);
        refused.Activated();
        Wait(5);
        Assert.False(refused.Yes());
        Assert.False(refused.Answer);

        var accepted = Shown();
        accepted.Activated();
        Wait(2);
        Assert.True(accepted.Yes());

        // La fenêtre se ferme après « Oui » : sa fermeture ne change plus rien.
        accepted.No();
        Assert.True(accepted.Answer);
    }

    [Fact]
    public void Without_an_answer_after_two_minutes_the_answer_is_no()
    {
        var gate = Shown();
        gate.Activated();
        Wait(119);
        Assert.Null(gate.Tick());

        Wait(1);
        Assert.False(gate.Tick());
        Assert.False(gate.Answer);
        Assert.False(gate.ButtonsEnabled);
        Assert.False(gate.Yes());
    }

    [Fact]
    public void An_answer_given_in_time_is_kept_after_the_limit()
    {
        var gate = Shown();
        gate.Activated();
        Wait(60);
        Assert.True(gate.Yes());

        Wait(120);
        Assert.True(gate.Tick());
    }

    [Fact]
    public void Without_a_window_the_engine_answers_no() =>
        Assert.False(new RefusingEngineConfirmation().Confirm("pilote graphique", "Redémarrer la carte ?"));
}
