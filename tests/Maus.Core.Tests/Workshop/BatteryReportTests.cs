using Maus.Core.Platform;
using Maus.Core.Workshop;

namespace Maus.Core.Tests.Workshop;

public class BatteryReportTests
{
    private sealed class NothingRuns : ICommandRunner
    {
        public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("rien ne doit être lancé");
    }

    [Fact]
    public async Task An_elevated_process_refuses_the_report_with_a_reason_and_runs_nothing()
    {
        var result = await BatteryReport.CreateAsync(elevated: true, new NothingRuns(), CancellationToken.None);

        Assert.Null(result.File);
        Assert.False(string.IsNullOrWhiteSpace(result.Refusal));
    }
}
