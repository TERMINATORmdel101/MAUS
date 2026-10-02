using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Maus.Core.Engine;

namespace Maus.App;

/// <summary>
/// Tâche « audit hebdomadaire » dans le Planificateur de tâches de Windows, par <c>schtasks.exe</c> (System32).
/// Créée et retirée seulement à la demande de l'utilisateur.
/// </summary>
public static class TaskSchedulerClient
{
    private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>
    /// Jour de la tâche existante (<c>null</c> si elle n'existe pas) et programme qu'elle lance (<c>null</c> si illisible).
    /// </summary>
    public static async Task<(DayOfWeek? Day, string? Command)> GetAsync()
    {
        var (code, output) = await RunAsync([.. ScheduledAudit.QueryArguments(), "/XML"]);
        if (code != 0)
        {
            return (null, null);
        }

        try
        {
            var document = XDocument.Parse(output);
            var day = document.Descendants(TaskNs + "DaysOfWeek").Elements().FirstOrDefault()?.Name.LocalName;
            var command = document.Descendants(TaskNs + "Command").FirstOrDefault()?.Value.Trim('"', ' ');
            return (Enum.TryParse<DayOfWeek>(day, out var parsed) ? parsed : DayOfWeek.Sunday, command);
        }
        catch (System.Xml.XmlException)
        {
            return (DayOfWeek.Sunday, null);
        }
    }

    /// <summary>Chemin de MAUS.exe qu'utiliserait la tâche.</summary>
    public static string Executable => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "MAUS.exe");

    /// <summary>Raison de ne pas créer la tâche depuis ce dossier (voir <see cref="ScheduledAudit.Refusal"/>), ou <c>null</c>.</summary>
    public static string? Refusal(string? executable = null) => ScheduledAudit.Refusal(executable ?? Executable, ScheduledAudit.ProtectedRoots());

    /// <summary>Crée (ou remplace) la tâche ; renvoie le message d'erreur, ou <c>null</c> si tout va bien.</summary>
    public static async Task<string?> CreateAsync(DayOfWeek day)
    {
        var executable = Executable;
        if (Refusal(executable) is { } refusal)
        {
            return refusal;
        }

        var xml = ScheduledAudit.TaskXml(executable, day, WindowsIdentity.GetCurrent().Name, DateTime.Now);
        var path = Path.Combine(Path.GetTempPath(), $"maus-task-{Guid.NewGuid():N}.xml");
        try
        {
            // schtasks attend un fichier UTF-16, comme l'annonce l'en-tête XML.
            await File.WriteAllTextAsync(path, xml, Encoding.Unicode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Dossier temporaire plein, protégé ou inaccessible : aucune tâche n'est créée, et on le dit.
            DeleteQuietly(path);
            return ex.Message;
        }

        try
        {
            var (code, output) = await RunAsync(ScheduledAudit.CreateArguments(path));
            return code == 0 ? null : output.Trim();
        }
        finally
        {
            DeleteQuietly(path);
        }
    }

    /// <summary>Retire le fichier XML temporaire ; s'il résiste (antivirus qui l'analyse), il reste dans le dossier temporaire.</summary>
    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Maus.Core.Diagnostics.Breadcrumbs.Add("audit hebdomadaire : fichier temporaire non supprimé (" + ex.GetType().Name + ")");
        }
    }

    public static async Task<string?> DeleteAsync()
    {
        var (code, output) = await RunAsync(ScheduledAudit.DeleteArguments());
        return code == 0 ? null : output.Trim();
    }

    private static async Task<(int Code, string Output)> RunAsync(IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, output + error);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, ex.Message);
        }
    }
}
