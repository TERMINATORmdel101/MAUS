using System.Windows.Threading;

namespace Maus.App.Tests;

/// <summary>
/// La question du moteur arrive sur un fil de fond et s'affiche sur le fil de l'application (0.8.6). Sans fil d'application,
/// ou si la fenêtre ne peut pas s'afficher, la réponse est « Non ». La fenêtre elle-même est remplacée : rien ne s'affiche.
/// </summary>
public sealed class EngineConfirmationPromptTests
{
    [Fact]
    public void Without_an_application_the_answer_is_no_and_nothing_is_shown()
    {
        var asked = false;
        var prompt = new EngineConfirmationPrompt(() => null, (_, _) => asked = true);

        Assert.False(prompt.Confirm("pilote graphique", "Redémarrer la carte ?"));
        Assert.False(asked);
    }

    [Fact]
    public void An_application_thread_that_has_stopped_answers_no()
    {
        Dispatcher? stopped = null;
        var thread = new Thread(() =>
        {
            stopped = Dispatcher.CurrentDispatcher;
            stopped.InvokeShutdown();
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));

        var asked = false;
        var prompt = new EngineConfirmationPrompt(() => stopped, (_, _) => asked = true);

        Assert.False(prompt.Confirm("pilote graphique", "Redémarrer la carte ?"));
        Assert.False(asked);
    }

    [Fact]
    public void A_window_that_cannot_be_shown_answers_no() => UiThread.Run(async () =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var prompt = new EngineConfirmationPrompt(() => dispatcher, (_, _) => throw new InvalidOperationException("fenêtre impossible"));

        Assert.False(await Task.Run(() => prompt.Confirm("pilote graphique", "Redémarrer la carte ?")));
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_question_asked_from_a_background_thread_is_shown_on_the_application_thread(bool answer) => UiThread.Run(async () =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var shownOn = -1;
        (string Title, string Message)? asked = null;
        var prompt = new EngineConfirmationPrompt(() => dispatcher, (title, message) =>
        {
            shownOn = Environment.CurrentManagedThreadId;
            asked = (title, message);
            return answer;
        });

        var background = await Task.Run(() => (Answer: prompt.Confirm("pilote graphique", "Redémarrer la carte ?"), Thread: Environment.CurrentManagedThreadId));

        Assert.Equal(answer, background.Answer);
        Assert.Equal(Environment.CurrentManagedThreadId, shownOn);
        Assert.NotEqual(background.Thread, shownOn);
        Assert.Equal("pilote graphique", asked?.Title);
        Assert.Equal("Redémarrer la carte ?", asked?.Message);
    });
}
