using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Fixes;

/// <summary>Active ou désactive une tâche planifiée (réglage <see cref="SettingKind.ScheduledTask"/>).</summary>
public interface IScheduledTaskAccessor
{
    /// <summary>Vrai si la tâche est active, faux si elle est désactivée, <c>null</c> si elle n'existe pas.</summary>
    /// <exception cref="MausAccessDeniedException">Lecture refusée.</exception>
    bool? IsEnabled(string taskPath, string taskName);

    /// <exception cref="InvalidOperationException">Windows a refusé le changement.</exception>
    void SetEnabled(string taskPath, string taskName, bool enabled);
}

/// <summary>
/// Lecture par CIM (<c>MSFT_ScheduledTask</c>, interrupteur <c>Settings.Enabled</c>) et écriture par l'outil de Windows
/// <c>schtasks /Change /TN … /ENABLE|/DISABLE</c> (Microsoft Learn, « schtasks change »), lancé depuis System32 avec ses
/// arguments séparés : aucun texte n'est interprété par un interpréteur de commandes.
/// </summary>
public sealed class WindowsScheduledTaskAccessor(ICimReader cim) : IScheduledTaskAccessor
{
    private const long DisabledState = 1;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public bool? IsEnabled(string taskPath, string taskName)
    {
        ArgumentNullException.ThrowIfNull(taskPath);
        ArgumentNullException.ThrowIfNull(taskName);
        var wql = $"SELECT State, Settings FROM MSFT_ScheduledTask WHERE TaskPath = '{Escape(taskPath)}' AND TaskName = '{Escape(taskName)}'";
        var rows = cim.Query(wql, CimScopes.TaskScheduler);
        return rows.Count == 0 ? null : IsEnabled(rows[0]);
    }

    /// <summary>
    /// Interrupteur de la tâche (<c>Settings.Enabled</c>, celui que change « Désactiver » ou <c>schtasks /DISABLE</c>), et non
    /// son état d'exécution : <c>State</c> ne vaut « Disabled » (1) que si la tâche est désactivée et qu'aucune instance n'est en
    /// file ou en cours (Microsoft Learn, « TASK_STATE enumeration (taskschd.h) ») ; une tâche désactivée dont le programme
    /// tourne encore reste « Running » (4). <c>State</c> ne sert que si l'interrupteur est illisible.
    /// </summary>
    public static bool IsEnabled(CimRow task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.GetRow("Settings")?.GetBool("Enabled") ?? task.GetInt64("State") != DisabledState;
    }

    public void SetEnabled(string taskPath, string taskName, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(taskPath);
        ArgumentNullException.ThrowIfNull(taskName);
        // Appelé hors du fil de l'interface, comme toute correction : l'attente est bornée par le délai.
        var result = Task.Run(() => Platform.SchtasksTool.RunAsync(["/Change", "/TN", FullName(taskPath, taskName), enabled ? "/ENABLE" : "/DISABLE"], Timeout)).GetAwaiter().GetResult();
        if (result.TimedOut)
        {
            throw new InvalidOperationException(T("schtasks.exe n'a pas répondu en {0} s.", (int)Timeout.TotalSeconds));
        }

        if (result.Code != 0)
        {
            throw new InvalidOperationException(T("Windows a refusé de modifier la tâche {0} (code {1}).", FullName(taskPath, taskName), result.Code));
        }
    }

    internal static string FullName(string taskPath, string taskName) => (taskPath.EndsWith('\\') ? taskPath : taskPath + "\\") + taskName;

    /// <summary>Chaîne WQL : barre oblique inverse et apostrophe protégées par une barre oblique inverse.</summary>
    internal static string Escape(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
}
