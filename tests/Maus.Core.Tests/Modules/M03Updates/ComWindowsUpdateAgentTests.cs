using System.Runtime.InteropServices;
using Maus.Core.Modules.M03Updates;
using Maus.Core.Platform;
using Microsoft.CSharp.RuntimeBinder;

namespace Maus.Core.Tests.Modules.M03Updates;

/// <summary>Fil dédié de la recherche Windows Update : aucune erreur n'en sort, toutes deviennent l'erreur de la tâche.</summary>
public class ComWindowsUpdateAgentTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Search_runs_on_its_own_thread_and_returns_its_result()
    {
        PendingUpdate[] updates = [new("id", "Mise à jour", [], [], null, false, false)];
        string? threadName = null;

        var result = await ComWindowsUpdateAgent.RunOnDedicatedThread(
            () =>
            {
                threadName = Thread.CurrentThread.Name;
                return updates;
            },
            Delay,
            CancellationToken.None);

        Assert.Same(updates, result);
        Assert.Equal("MAUS - recherche Windows Update", threadName);
    }

    [Theory]
    [InlineData("argument", typeof(ArgumentException))]
    [InlineData("not-implemented", typeof(NotImplementedException))]
    [InlineData("null-reference", typeof(NullReferenceException))]
    [InlineData("runtime-binder", typeof(RuntimeBinderException))]
    [InlineData("invalid-cast", typeof(InvalidCastException))]
    [InlineData("format", typeof(FormatException))]
    public async Task Any_other_error_becomes_an_unexpected_answer(string kind, Type expected)
    {
        Func<IReadOnlyList<PendingUpdate>> search = kind switch
        {
            "argument" => () => throw new ArgumentException("E_INVALIDARG"),
            "not-implemented" => () => throw new NotImplementedException(),
            "null-reference" => ReadThroughNullPointer,
            "runtime-binder" => () => throw new RuntimeBinderException("membre absent"),
            "invalid-cast" => () => throw new InvalidCastException(),
            _ => () => throw new FormatException(),
        };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ComWindowsUpdateAgent.RunOnDedicatedThread(search, Delay, CancellationToken.None));

        Assert.IsType(expected, failure.InnerException);
        Assert.Contains("Réponse inattendue de l'agent Windows Update", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Une vraie <see cref="NullReferenceException"/>, comme celle de l'interop COM pour E_POINTER.</summary>
    internal static IReadOnlyList<PendingUpdate> ReadThroughNullPointer()
    {
        string? missing = null;
        return [new(missing!.Trim(), "Mise à jour", [], [], null, false, false)];
    }

    [Fact]
    public async Task Errors_the_module_already_describes_pass_through_unchanged()
    {
        Exception[] errors =
        [
            Marshal.GetExceptionForHR(unchecked((int)0x80072EE7))!,
            new UnauthorizedAccessException(),
            new MausAccessDeniedException("refusé"),
            new InvalidOperationException("La recherche a échoué."),
            new DataSourceUnavailableException("Agent absent."),
        ];

        foreach (var error in errors)
        {
            var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
                ComWindowsUpdateAgent.RunOnDedicatedThread(() => throw error, Delay, CancellationToken.None));
            Assert.Same(error, failure);
        }
    }

    [Fact]
    public async Task A_search_that_never_answers_is_abandoned_after_the_delay()
    {
        var release = new TaskCompletionSource();
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => ComWindowsUpdateAgent.RunOnDedicatedThread(
                () =>
                {
                    release.Task.Wait();
                    return [];
                },
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None));
        }
        finally
        {
            release.SetResult();
        }
    }
}
