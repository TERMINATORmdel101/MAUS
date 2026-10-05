using Maus.Core.Diagnostics;

namespace Maus.Core.Tests.Diagnostics;

/// <summary>Tâches lancées sans être attendues : leur erreur est notée et affichée, jamais perdue ni relancée.</summary>
public class BackgroundTasksTests
{
    /// <summary>Journal simulé : rien n'est écrit dans le vrai dossier des journaux de MAUS.</summary>
    private sealed class FakeLog
    {
        public List<(Exception Error, string What)> Entries { get; } = [];

        public string? Write(Exception error, string what)
        {
            Entries.Add((error, what));
            return null;
        }
    }

    [Fact]
    public async Task An_error_is_logged_then_shown_and_never_rethrown()
    {
        var log = new FakeLog();
        var error = new IOException("dossier temporaire plein");
        Exception? shown = null;

        await BackgroundTasks.ObserveAsync(Task.FromException(error), "audit hebdomadaire : changement", ex => shown = ex, log.Write);

        Assert.Same(error, shown);
        var entry = Assert.Single(log.Entries);
        Assert.Same(error, entry.Error);
        Assert.StartsWith("audit hebdomadaire : changement", entry.What, StringComparison.Ordinal);
        Assert.Contains(Breadcrumbs.Snapshot(), s => s.Contains("audit hebdomadaire : changement : erreur imprévue (IOException)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_error_after_an_await_is_caught_too()
    {
        var log = new FakeLog();
        var shown = new TaskCompletionSource<Exception>();

        static async Task FailLaterAsync()
        {
            await Task.Yield();
            throw new InvalidOperationException("plus tard");
        }

        await BackgroundTasks.ObserveAsync(FailLaterAsync(), "test", ex => shown.SetResult(ex), log.Write);

        Assert.IsType<InvalidOperationException>(await shown.Task);
        Assert.Single(log.Entries);
    }

    [Fact]
    public async Task A_successful_task_reports_nothing()
    {
        var log = new FakeLog();
        var shown = false;

        await BackgroundTasks.ObserveAsync(Task.CompletedTask, "test", _ => shown = true, log.Write);

        Assert.False(shown);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task A_cancelled_task_is_not_an_error()
    {
        var log = new FakeLog();
        var shown = false;

        await BackgroundTasks.ObserveAsync(Task.FromCanceled(new CancellationToken(canceled: true)), "test", _ => shown = true, log.Write);

        Assert.False(shown);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task An_error_while_showing_the_message_is_logged_and_swallowed()
    {
        var log = new FakeLog();
        var displayError = new InvalidOperationException("fenêtre déjà fermée");

        await BackgroundTasks.ObserveAsync(Task.FromException(new IOException("boum")), "test", _ => throw displayError, log.Write);

        Assert.Equal(2, log.Entries.Count);
        Assert.Same(displayError, log.Entries[1].Error);
    }

    [Fact]
    public async Task Without_a_display_the_error_is_still_logged()
    {
        var log = new FakeLog();

        await BackgroundTasks.ObserveAsync(Task.FromException(new IOException("boum")), "test", null, log.Write);

        Assert.Single(log.Entries);
    }
}
