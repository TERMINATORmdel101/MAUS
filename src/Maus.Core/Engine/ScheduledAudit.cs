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

    /// <summary>
    /// Raison de refuser la tâche, ou <c>null</c>. Elle lance MAUS avec les droits les plus élevés sans rien demander :
    /// son exécutable doit être dans un dossier que seul un administrateur peut modifier (Program Files, dont celui des
    /// applications du Microsoft Store). Ailleurs (Téléchargements, Bureau, clé USB), n'importe quel programme ouvert par
    /// l'utilisateur pourrait remplacer MAUS.exe et obtenir ainsi les droits administrateur à la prochaine exécution.
    /// </summary>
    /// <param name="executable">Chemin complet de MAUS.exe.</param>
    /// <param name="protectedRoots">Dossiers réservés aux administrateurs (voir <see cref="ProtectedRoots"/>).</param>
    public static string? Refusal(string executable, IReadOnlyList<string> protectedRoots)
    {
        var isProtected = Path.IsPathFullyQualified(executable)
            && !executable.Contains("..", StringComparison.Ordinal)
            && protectedRoots.Any(root => root.Length > 0 && executable.StartsWith(root.TrimEnd('\\') + @"\", StringComparison.OrdinalIgnoreCase));
        return isProtected
            ? null
            : T("L'audit automatique n'est proposé que si MAUS est installé dans un dossier protégé (Program Files, ou par le Microsoft Store). Depuis {0}, un autre programme pourrait remplacer MAUS.exe et profiter des droits administrateur de la tâche.",
                Path.GetDirectoryName(executable) ?? executable);
    }

    /// <summary>
    /// Vrai si la tâche lance une version précédente du même paquet du Microsoft Store que <paramref name="currentExecutable"/> :
    /// le dossier d'une application du Store contient son numéro de version (« Nom_0.7.5.0_x64__éditeur », sous
    /// Program Files\WindowsApps) et change à chaque mise à jour. MAUS remet alors la tâche sur son nouveau dossier.
    /// </summary>
    public static bool IsOlderStoreFolder(string taskCommand, string currentExecutable)
    {
        if (StoreFolder(taskCommand) is not { } previous || StoreFolder(currentExecutable) is not { } current)
        {
            return false;
        }

        return string.Equals(previous.Family, current.Family, StringComparison.OrdinalIgnoreCase)
            && string.Equals(previous.File, current.File, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(previous.Version, current.Version, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Famille du paquet (nom, architecture, éditeur), version et fichier d'un exécutable rangé sous WindowsApps.</summary>
    private static (string Family, string Version, string File)? StoreFolder(string path)
    {
        // Découpé sur « \ » explicitement : les tests tournent aussi sous Linux.
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !string.Equals(parts[^3], "WindowsApps", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Nom_Version_Architecture_IdentifiantDeRessource_Éditeur (le nom d'un paquet ne contient jamais « _ »).
        var fields = parts[^2].Split('_');
        return fields.Length == 5 && fields[0].Length > 0 && fields[4].Length > 0
            ? (fields[0] + "_" + fields[2] + "_" + fields[4], fields[1], parts[^1])
            : null;
    }

    /// <summary>Program Files (64 et 32 bits) : modifiables par les seuls administrateurs, WindowsApps compris.</summary>
    public static IReadOnlyList<string> ProtectedRoots() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
    ];

    public static IReadOnlyList<string> CreateArguments(string xmlPath) => ["/Create", "/TN", TaskName, "/XML", xmlPath, "/F"];

    public static IReadOnlyList<string> DeleteArguments() => ["/Delete", "/TN", TaskName, "/F"];

    public static IReadOnlyList<string> QueryArguments() => ["/Query", "/TN", TaskName];

    /// <summary>Problèmes rouges qui justifient de déranger l'utilisateur (les constats « voulus » n'en font plus partie).</summary>
    public static IReadOnlyList<Finding> WorthNotifying(IEnumerable<ModuleResult> results) =>
        results.SelectMany(r => r.Findings).Where(f => f.Status == FindingStatus.Problem).ToList();

    /// <summary>
    /// Texte de la notification : combien de problèmes, et les premiers ; puis les modules qui n'ont pas pu être vérifiés
    /// (erreur ou délai dépassé), pour ne pas laisser croire que le reste est en ordre. Un module non vérifié ne suffit pas à
    /// afficher la notification : il ne dit rien de l'état du PC, et la tâche promet de ne déranger que pour un problème rouge.
    /// </summary>
    /// <param name="problems">Problèmes rouges (<see cref="WorthNotifying"/>).</param>
    /// <param name="results">Résultats de l'audit, pour citer les modules non vérifiés ; <c>null</c> = ne pas les citer.</param>
    public static string NotificationText(IReadOnlyList<Finding> problems, IEnumerable<ModuleResult>? results = null)
    {
        var text = T("L'audit de la semaine a trouvé {0} problème(s) : {1}{2}", problems.Count,
            string.Join(" ; ", problems.Take(3).Select(p => p.Title)), problems.Count > 3 ? "…" : string.Empty);
        var failed = results is null ? [] : Reporting.HealthScore.UncheckedModules(results);
        return failed.Count switch
        {
            0 => text,
            1 => text + Environment.NewLine + T("1 module n'a pas pu être vérifié : {0}.", failed[0].Title),
            var count => text + Environment.NewLine + T("{0} modules n'ont pas pu être vérifiés : {1}.", count, string.Join(", ", failed.Select(f => f.Title))),
        };
    }
}
