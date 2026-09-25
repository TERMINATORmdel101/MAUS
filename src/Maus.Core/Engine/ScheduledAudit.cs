using System.Globalization;
using System.Security;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Engine;

/// <summary>
/// Audit automatique chaque semaine, activé seulement par l'utilisateur : une tâche du Planificateur de tâches lance MAUS
/// sans fenêtre (<c>--scheduled-audit</c>), en priorité basse ; MAUS ne se montre que si un problème rouge apparaît.
/// Aucune donnée n'est envoyée ; la tâche se retire en un clic.
/// </summary>
public static class ScheduledAudit
{
    public const string TaskName = @"MAUS\WeeklyAudit";
    public const string Argument = "--scheduled-audit";

    /// <summary>Définition XML de la tâche (format du Planificateur de tâches, enregistrée par <c>schtasks /Create /XML</c>).</summary>
    /// <param name="executable">Chemin complet de MAUS.exe.</param>
    /// <param name="day">Jour de la semaine.</param>
    /// <param name="userId">Compte qui ouvre la session (la tâche ne tourne que lorsqu'il est connecté).</param>
    /// <param name="now">Date de création (la première exécution suit).</param>
    public static string TaskXml(string executable, DayOfWeek day, string userId, DateTime now)
    {
        var start = now.Date.AddHours(12);
        while (start.DayOfWeek != day || start <= now)
        {
            start = start.AddDays(1);
        }

        string E(string text) => SecurityElement.Escape(text);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>MAUS</Author>
                <Description>{E(T("Audit hebdomadaire de MAUS, en lecture seule. MAUS ne s'affiche que s'il trouve un problème rouge. Pour l'arrêter : MAUS, onglet Corrections, « Vos choix »."))}</Description>
              </RegistrationInfo>
              <Triggers>
                <CalendarTrigger>
                  <StartBoundary>{start.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}</StartBoundary>
                  <Enabled>true</Enabled>
                  <ScheduleByWeek>
                    <DaysOfWeek>
                      <{day} />
                    </DaysOfWeek>
                    <WeeksInterval>1</WeeksInterval>
                  </ScheduleByWeek>
                </CalendarTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{E(userId)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <ExecutionTimeLimit>PT30M</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{E(executable)}</Command>
                  <Arguments>{Argument}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    public static IReadOnlyList<string> CreateArguments(string xmlPath) => ["/Create", "/TN", TaskName, "/XML", xmlPath, "/F"];

    public static IReadOnlyList<string> DeleteArguments() => ["/Delete", "/TN", TaskName, "/F"];

    public static IReadOnlyList<string> QueryArguments() => ["/Query", "/TN", TaskName];

    /// <summary>Problèmes rouges qui justifient de déranger l'utilisateur (les constats « voulus » n'en font plus partie).</summary>
    public static IReadOnlyList<Finding> WorthNotifying(IEnumerable<ModuleResult> results) =>
        results.SelectMany(r => r.Findings).Where(f => f.Status == FindingStatus.Problem).ToList();

    /// <summary>Texte de la notification : combien de problèmes, et les premiers.</summary>
    public static string NotificationText(IReadOnlyList<Finding> problems) =>
        T("L'audit de la semaine a trouvé {0} problème(s) : {1}{2}", problems.Count,
            string.Join(" ; ", problems.Take(3).Select(p => p.Title)), problems.Count > 3 ? "…" : string.Empty);
}
