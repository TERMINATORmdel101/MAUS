namespace Maus.Core.Platform;

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>Lance des outils Windows en lecture seule (powercfg /list, bcdedit /enum…).</summary>
public interface ICommandRunner
{
    /// <exception cref="InvalidOperationException">La commande ne figure pas dans la liste des commandes en lecture seule.</exception>
    Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}
