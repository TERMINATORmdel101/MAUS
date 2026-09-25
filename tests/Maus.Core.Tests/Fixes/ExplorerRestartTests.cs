using Maus.Core.Fixes;
using Maus.Core.Platform;

namespace Maus.Core.Tests.Fixes;

public class ExplorerRestartTests
{
    private sealed class FakeShell(bool autoRestart = true, bool comesBack = true) : IShellProcesses
    {
        private int _count = 1;

        public int Stops { get; private set; }

        public bool AutoRestartEnabled() => autoRestart;

        public int Count() => _count;

        public void StopShell()
        {
            Stops++;
            _count = comesBack ? 1 : 0;
        }
    }

    private static Task NoWait(TimeSpan delay, CancellationToken token) => Task.CompletedTask;

    [Fact]
    public async Task Explorer_is_stopped_and_its_return_is_verified()
    {
        var shell = new FakeShell();

        var result = await new ExplorerRestart(shell, NoWait).RunAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, shell.Stops);
    }

    [Fact]
    public async Task Explorer_is_never_stopped_when_windows_would_not_restart_it()
    {
        var shell = new FakeShell(autoRestart: false);

        var result = await new ExplorerRestart(shell, NoWait).RunAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(0, shell.Stops);
    }

    [Fact]
    public async Task Missing_return_gives_manual_instructions()
    {
        var result = await new ExplorerRestart(new FakeShell(comesBack: false), NoWait).RunAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("Ctrl+Maj+Échap", result.Message, StringComparison.Ordinal);
    }
}
