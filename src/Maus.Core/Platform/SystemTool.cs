using System.Diagnostics;
using System.Text;

namespace Maus.Core.Platform;

/// <summary>
/// Lance un outil de Windows directement, sans <c>cmd.exe</c>, et transmet ce qu'il écrit au fur et à mesure (0.8.6) : chemin de
/// System32, arguments séparés, aucune fenêtre d'invite qui resterait ouverte avec les droits administrateur. Cesser d'attendre
/// ne tue pas l'outil : couper DISM ou pnputil en pleine écriture serait pire que le laisser finir.
/// </summary>
public static class SystemTool
{
    private const int MaxLineLength = 4000;

    public static string PathOf(string fileName) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), fileName);

    /// <summary>
    /// Dossier de travail de tout programme lancé par MAUS (0.8.6) : System32, donné par Windows. Un programme lancé ne prend
    /// jamais pour dossier courant celui du processus qui le lance (un dossier qu'un autre programme peut avoir choisi).
    /// </summary>
    public static string WorkingFolder => Environment.GetFolderPath(Environment.SpecialFolder.System);

    /// <returns>Le code de sortie de l'outil.</returns>
    public static async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, Encoding encoding, Action<string> line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
            WorkingDirectory = WorkingFolder,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException(executable);
        var output = PumpAsync(process.StandardOutput, line, cancellationToken);
        var errors = PumpAsync(process.StandardError, line, cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(output, errors).ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>Une ligne finit par un retour chariot ou un saut de ligne : DISM et SFC réécrivent leur pourcentage avec le premier.</summary>
    internal static async Task PumpAsync(TextReader reader, Action<string> line, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        var current = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] is '\r' or '\n' || current.Length >= MaxLineLength)
                {
                    Flush(current, line);
                }

                if (buffer[i] is not ('\r' or '\n'))
                {
                    current.Append(buffer[i]);
                }
            }
        }

        Flush(current, line);
    }

    private static void Flush(StringBuilder current, Action<string> line)
    {
        var text = current.ToString().Trim();
        current.Clear();
        if (text.Length > 0)
        {
            line(text);
        }
    }
}
