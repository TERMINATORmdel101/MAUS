using System.Diagnostics;
using System.Globalization;
using System.Text;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Platform;

/// <summary>
/// Exécute uniquement des commandes connues pour ne rien modifier. Toute autre commande est refusée :
/// en mode audit, MAUS ne peut pas écrire sur le système, même par erreur.
/// </summary>
public sealed class ReadOnlyCommandRunner : ICommandRunner
{
    /// <summary>Exécutable autorisé et premiers arguments admis (comparaison insensible à la casse).</summary>
    private static readonly Dictionary<string, string[]> AllowedFirstArguments = new(StringComparer.OrdinalIgnoreCase)
    {
        ["powercfg.exe"] = ["/getactivescheme", "/list", "/l", "/a", "/availablesleepstates", "/query", "/q"],
        ["bcdedit.exe"] = ["/enum"],
        ["fsutil.exe"] = ["behavior"],
        ["reagentc.exe"] = ["/info"],
        ["dsregcmd.exe"] = ["/status"],
        ["manage-bde.exe"] = ["-status"],
        ["netsh.exe"] = ["winhttp"],
        ["nvidia-smi.exe"] = ["--query-gpu", "-q", "--help"],
    };

    /// <summary>Contraintes supplémentaires sur le reste de la ligne de commande.</summary>
    private static readonly Dictionary<string, Func<IReadOnlyList<string>, bool>> ExtraChecks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fsutil.exe"] = args => args.Count >= 2 && args[1].Equals("query", StringComparison.OrdinalIgnoreCase),
        ["netsh.exe"] = args => args.Count >= 2 && args[1].Equals("show", StringComparison.OrdinalIgnoreCase),
    };

    static ReadOnlyCommandRunner()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static bool IsAllowed(string executable, IReadOnlyList<string> arguments)
    {
        var name = Path.GetFileName(executable);
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name += ".exe";
        }

        if (arguments.Count == 0 || !AllowedFirstArguments.TryGetValue(name, out var allowed))
        {
            return false;
        }

        // Correspondance exacte : « /a » ne doit pas laisser passer « /attributes », qui écrit.
        var first = arguments[0].Split('=', 2)[0];
        if (!allowed.Contains(first, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return !ExtraChecks.TryGetValue(name, out var check) || check(arguments);
    }

    public async Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!IsAllowed(executable, arguments))
        {
            throw new InvalidOperationException(T("Commande refusée en mode lecture seule : {0} {1}", executable, string.Join(' ', arguments)));
        }

        var path = ResolveSystemExecutable(executable);
        var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        var startInfo = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = oem,
            StandardErrorEncoding = oem,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new DataSourceUnavailableException(T("Commande introuvable : {0}", executable), ex);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return new CommandResult(-1, string.Empty, string.Empty, TimedOut: true);
        }

        return new CommandResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), TimedOut: false);
    }

    /// <summary>Les outils système sont cherchés dans System32 pour éviter un exécutable homonyme placé ailleurs.</summary>
    private static string ResolveSystemExecutable(string executable)
    {
        if (Path.IsPathRooted(executable))
        {
            return executable;
        }

        var name = executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? executable : executable + ".exe";
        var system32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);
        return File.Exists(system32) ? system32 : name;
    }
}
