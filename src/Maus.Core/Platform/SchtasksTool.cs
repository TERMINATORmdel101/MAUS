using System.Diagnostics;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Platform;

/// <summary>Sortie de <c>schtasks.exe</c> ; <paramref name="TimedOut"/> : il a été arrêté au bout du délai.</summary>
internal sealed record SchtasksResult(int Code, string Output, bool TimedOut);

/// <summary>Lancement de <c>schtasks.exe</c> de System32, arguments séparés : chaque appelant garde son interprétation du code de sortie.</summary>
internal static class SchtasksTool
{
    /// <exception cref="System.ComponentModel.Win32Exception">Le programme n'a pas pu démarrer.</exception>
    /// <exception cref="InvalidOperationException">Le programme n'a pas pu démarrer.</exception>
    public static async Task<SchtasksResult> RunAsync(IEnumerable<string> arguments, TimeSpan timeLimit)
    {
        // schtasks écrit dans la page de code OEM du système, y compris ses messages d'erreur (« Erreur : Le fichier spécifié
        // est introuvable. » : octets 0xFF et 0x82 en page 850, relevé le 30/09/2026) : lus en UTF-8, les accents seraient cassés.
        var oem = OemEncoding.Current;
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = oem,
            StandardErrorEncoding = oem,
            WorkingDirectory = SystemTool.WorkingFolder,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException(T("Impossible de lancer schtasks.exe."));

        // schtasks répond en moins d'une seconde ; au-delà du délai (outil bloqué par un antivirus ou un verrou), il est arrêté
        // et l'appelant le dit, au lieu d'attendre sans fin.
        using var timeout = new CancellationTokenSource(timeLimit);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return new SchtasksResult(process.ExitCode, await output.ConfigureAwait(false) + await error.ConfigureAwait(false), false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Déjà terminé.
            }

            return new SchtasksResult(-1, string.Empty, true);
        }
    }
}
