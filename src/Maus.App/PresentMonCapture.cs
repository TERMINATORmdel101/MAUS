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

    private PresentMonCapture(Process process, FrameTimeLog log)
    {
        _process = process;
        Log = log;
    }

    public FrameTimeLog Log { get; }

    public static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, "PresentMon", "PresentMon.exe");

    /// <summary>Démarre la mesure ; <c>null</c> si PresentMon est absent, modifié ou refuse de démarrer.</summary>
    public static PresentMonCapture? TryStart()
    {
        var path = ExecutablePath;
        if (!IsGenuine(path))
        {
            Breadcrumbs.Add("PresentMon absent ou différent de la copie d'Intel : pas de mesure des images");
            return null;
        }

        var log = new FrameTimeLog();
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "--output_stdout", "--v1_metrics", "--no_console_stats", "--stop_existing_session", "--session_name", SessionName })
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
            return new PresentMonCapture(process, log);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Breadcrumbs.Add("PresentMon n'a pas démarré (" + ex.GetType().Name + ")");
            return null;
        }
    }

    /// <summary>Arrête la mesure et referme la session d'écoute de Windows ; renvoie le bilan (ou <c>null</c>).</summary>
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

        CloseSession();
        return Log.Summarize();
    }

    public void Dispose() => _process.Dispose();

    /// <summary>Une session d'écoute laissée ouverte par un arrêt brutal est refermée par PresentMon lui-même.</summary>
    private static void CloseSession()
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
            start.ArgumentList.Add(SessionName);
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
