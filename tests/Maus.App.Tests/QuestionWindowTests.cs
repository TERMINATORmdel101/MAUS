using System.Windows;
using System.Windows.Controls;
using Maus.App.Views;

namespace Maus.App.Tests;

/// <summary>La réponse par défaut des messages de MAUS : Entrée et Échap ne doivent jamais accepter une action par surprise.</summary>
public sealed class QuestionWindowTests
{
    private static List<Button> Buttons(DependencyObject parent) =>
        LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()
            .SelectMany(child => child is Button button ? [button] : Buttons(child))
            .ToList();

    [Fact]
    public void A_question_answers_no_to_enter_and_to_escape() => UiThread.Run(() =>
    {
        var buttons = Buttons(new QuestionWindow("Titre", ["Texte"], null, QuestionTone.Question, "Oui", "Non", yesIsDefault: false));

        var yes = buttons.Single(b => (string)b.Content == "Oui");
        var no = buttons.Single(b => (string)b.Content == "Non");
        Assert.False(yes.IsDefault);
        Assert.False(yes.IsCancel);
        Assert.True(no.IsDefault);
        Assert.True(no.IsCancel);
        return Task.CompletedTask;
    });

    [Fact]
    public void Yes_answers_enter_only_when_asked_and_escape_stays_no() => UiThread.Run(() =>
    {
        var buttons = Buttons(new QuestionWindow("Titre", ["Texte"], null, QuestionTone.Warning, "Oui", "Non", yesIsDefault: true));

        var yes = buttons.Single(b => (string)b.Content == "Oui");
        var no = buttons.Single(b => (string)b.Content == "Non");
        Assert.True(yes.IsDefault);
        Assert.False(yes.IsCancel);
        Assert.False(no.IsDefault);
        Assert.True(no.IsCancel);
        return Task.CompletedTask;
    });

    [Fact]
    public void A_simple_message_has_one_button_for_enter_and_escape() => UiThread.Run(() =>
    {
        var button = Assert.Single(Buttons(new QuestionWindow("Titre", ["Texte"], null, QuestionTone.Information, "OK", null, yesIsDefault: true)));

        Assert.True(button.IsDefault);
        Assert.True(button.IsCancel);
        return Task.CompletedTask;
    });
}
