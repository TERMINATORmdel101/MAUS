using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Engine;

/// <summary>
/// Tâche « audit hebdomadaire » dans le Planificateur de tâches de Windows, par <c>schtasks.exe</c> (System32).
/// Créée et retirée seulement à la demande de l'utilisateur.
/// </summary>
public static class TaskSchedulerClient
{
    /// <summary>La tâche existante ; <c>null</c> si elle n'existe pas ou si sa définition est illisible.</summary>
    public static async Task<ExistingTask?> GetAsync()
    {
        var (code, output) = await RunAsync([.. ScheduledAudit.QueryArguments(), "/XML"]);
        return code == 0 ? ScheduledAudit.ParseTask(output) : null;
    }

    /// <summary>Les comptes auxquels la tâche de MAUS peut appartenir : la personne devant l'écran et le compte du processus, par SID ou par nom.</summary>
    public static IReadOnlyList<string?> AccountsOfThisSession()
    {
        using var self = WindowsIdentity.GetCurrent();
        return [Maus.Core.Platform.SessionUser.Sid(), self.User?.Value, self.Name];
    }

    /// <summary>Chemin de MAUS.exe qu'utiliserait la tâche.</summary>
    public static string Executable => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "MAUS.exe");

    /// <summary>
    /// Lancé avec les droits administrateur par une tâche d'une version précédente : la recrée sans droits, sans rien demander
    /// puisque cela ne fait que retirer des droits. Rend vrai si c'était le cas : ce passage ne doit alors rien auditer.
    /// </summary>
    public static async Task<bool> DropRightsOfOldTaskAsync()
    {
        if (await GetAsync() is not { Day: { } day } task || !ScheduledAudit.MustDropRights(task, AccountsOfThisSession()))
        {
            return false;
        }

        var error = await CreateAsync(day);
        Maus.Core.Diagnostics.Breadcrumbs.Add("audit hebdomadaire : ancienne tâche recréée sans droits" + (error is null ? string.Empty : " (échec)"));
        return true;
    }

    /// <summary>Crée (ou remplace) la tâche ; renvoie le message d'erreur, ou <c>null</c> si tout va bien.</summary>
    public static async Task<string?> CreateAsync(DayOfWeek day)
    {
        var executable = Executable;
        // Par SID : c'est sans ambiguïté, et c'est le compte de la personne devant l'écran même si un autre administrateur a validé.
        var xml = ScheduledAudit.TaskXml(executable, day, Maus.Core.Platform.SessionUser.Sid() ?? Environment.UserName, DateTime.Now);
        // Jamais dans le dossier temporaire de l'utilisateur : un programme sans droits pourrait y remplacer le fichier entre
        // son écriture et sa lecture par schtasks, et obtenir une tâche lancée avec les droits administrateur. Le dossier
        // %ProgramData%\MAUS\tasks n'est modifiable que par SYSTEM et les administrateurs (comme le journal des corrections).
        var folder = Path.Combine(Maus.Core.Platform.SystemPaths.MausData, "tasks");
        var path = Path.Combine(folder, $"maus-task-{Guid.NewGuid():N}.xml");
        try
        {
            Directory.CreateDirectory(folder);
            new Maus.Core.Fixes.WindowsDirectoryProtector().EnsureProtected(folder);
            // schtasks attend un fichier UTF-16, comme l'annonce l'en-tête XML.
            await File.WriteAllTextAsync(path, xml, Encoding.Unicode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.AccessControl.PrivilegeNotHeldException or Maus.Core.Fixes.JournalUnsafeException)
        {
            // Dossier plein, protégé ou inaccessible : aucune tâche n'est créée, et on le dit.
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

    /// <summary>Retire le fichier XML temporaire ; s'il résiste (antivirus qui l'analyse), il reste dans le dossier protégé <c>%ProgramData%\MAUS\tasks</c>.</summary>
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
        try
        {
            var result = await Maus.Core.Platform.SchtasksTool.RunAsync(arguments, TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            return result.TimedOut
                ? (-1, T("schtasks n'a pas répondu en une minute : la tâche n'a pas été modifiée."))
                : (result.Code, result.Output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, ex.Message);
        }
    }
}
