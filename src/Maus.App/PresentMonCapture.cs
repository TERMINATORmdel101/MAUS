using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Maus.Core.Diagnostics;
using Maus.Core.Workshop;

namespace Maus.App;

/// <summary>
/// Mesure des images par seconde pendant le relevé de partie, par PresentMon (Intel, licence MIT, copie dans
/// <c>third-party/PresentMon</c>, signée par Intel). PresentMon ne fait qu'écouter les événements d'affichage de Windows
/// (ETW) : il ne touche ni au jeu ni aux réglages. Lancé uniquement depuis le dossier de MAUS, et seulement si son empreinte
/// est exactement celle du fichier publié par Intel (une copie remplacée n'est jamais lancée).
/// </summary>
public sealed class PresentMonCapture : IDisposable
{
    /// <summary>SHA-256 de PresentMon-2.6.0-x64.exe (github.com/GameTechDev/PresentMon, publication v2.6.0, signature Intel valide, 05/10/2026).</summary>
    internal const string ExpectedSha256 = "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af";

    private const string SessionName = "MAUS_Releve";

    private readonly Process _process;

    private readonly string _sessionName;

    private PresentMonCapture(Process process, FrameTimeLog log, string sessionName)
    {
        _process = process;
        Log = log;
        _sessionName = sessionName;
    }

    public FrameTimeLog Log { get; }

    public static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, "PresentMon", "PresentMon.exe");

    /// <summary>Démarre la mesure ; <c>null</c> si PresentMon est absent, modifié ou refuse de démarrer.</summary>
    /// <param name="keep">Durée gardée en mémoire (compteur en direct) ; <c>null</c> = toute la mesure (relevé de partie).</param>
    /// <param name="sessionName">Nom de la session d'écoute de Windows (une par usage, pour que les deux puissent coexister).</param>
    public static PresentMonCapture? TryStart(TimeSpan? keep = null, string sessionName = SessionName)
    {
        var path = ExecutablePath;
        if (!IsGenuine(path))
        {
            Breadcrumbs.Add("PresentMon absent ou différent de la copie d'Intel : pas de mesure des images");
            return null;
        }

        var log = new FrameTimeLog(keep);
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // Le compositeur de Windows et MAUS ne sont jamais le jeu : PresentMon ne les suit pas (moins de lignes à écrire et à lire).
        foreach (var argument in new[] { "--output_stdout", "--v1_metrics", "--no_console_stats", "--stop_existing_session", "--session_name", sessionName, "--exclude", "dwm.exe", "--exclude", "MAUS.exe" })
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => log.AddCsvLine(e.Data);
            process.ErrorDataReceived += (_, _) => { };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return new PresentMonCapture(process, log, sessionName);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Breadcrumbs.Add("PresentMon n'a pas démarré (" + ex.GetType().Name + ")");
            return null;
        }
    }

    /// <summary>Arrête PresentMon tout de suite, sans attendre (fermeture d'une fenêtre) ; <see cref="Stop"/> finit le travail.</summary>
    public void Halt()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Déjà terminé.
        }
    }

    /// <summary>Arrête la mesure et referme la session d'écoute de Windows ; renvoie le bilan (ou <c>null</c>). Quelques secondes au plus.</summary>
    public FrameSummary? Stop()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Déjà terminé.
        }

        CloseSession(_sessionName);
        return Log.Summarize();
    }

    public void Dispose() => _process.Dispose();

    /// <summary>Une session d'écoute laissée ouverte par un arrêt brutal est refermée par PresentMon lui-même.</summary>
    private static void CloseSession(string sessionName)
    {
        if (!IsGenuine(ExecutablePath))
        {
            return;
        }

        try
        {
            var start = new ProcessStartInfo(ExecutablePath) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--terminate_existing_session");
            start.ArgumentList.Add("--session_name");
            start.ArgumentList.Add(sessionName);
            using var closer = Process.Start(start);
            closer?.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Breadcrumbs.Add("session PresentMon non refermée (" + ex.GetType().Name + ")");
        }
    }

    private static bool IsGenuine(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream)) == ExpectedSha256;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
