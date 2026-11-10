using System.Management;
using System.Runtime.InteropServices;
using Maus.Core.Modules.M03Updates;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M02Repair;

/// <summary>
/// « Depuis quand ? Qu'est-ce qui a changé ? » : sur 30 jours, les écrans bleus, les arrêts brutaux, les correctifs installés
/// et les arrêts ou redémarrages demandés par un programme, par Windows Update ou par l'utilisateur (journal Système, User32,
/// événement 1074, qui couvre les deux d'après Microsoft ; champ <c>param1</c> : programme à l'origine, relevé sur le PC du porteur).
/// Lecture seule, sans jugement.
/// </summary>
internal static class RecentEvents
{
    internal const string Id = "M02.recent-events";
    internal const string RestartProvider = "User32";
    internal const int RestartEventId = 1074;
    private const int Days = 30;
    private const int Shown = 6;

    public static Finding Detect(AuditContext context, int blueScreens, int unexpectedShutdowns, string category)
    {
        ArgumentNullException.ThrowIfNull(context);
        var title = T("Ce qui s'est passé ces 30 derniers jours");
        IReadOnlyList<EventRecordInfo> restarts;
        try
        {
            restarts = context.EventLogs.Query(SystemLogQuery.LogName, RestartProvider, [RestartEventId], context.Now.LocalDateTime.AddDays(-Days), maxEvents: 100);
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(Id, title, category);
        }
        catch (DataSourceUnavailableException)
        {
            return Finding.Unknown(Id, title, T("Le journal Système est illisible sur ce PC."), category);
        }

        var today = DateOnly.FromDateTime(context.Now.LocalDateTime);
        var fixes = ReadFixes(context.Cim, today);
        var summary = new List<string>
        {
            T("écrans bleus : {0}", blueScreens),
            T("arrêts brutaux : {0}", unexpectedShutdowns),
            T("arrêts ou redémarrages demandés : {0}", restarts.Count),
            fixes is null ? T("correctifs : illisibles") : T("correctifs installés : {0}", fixes.Count),
        };

        var details = new List<string>();
        if (restarts.Count > 0)
        {
            details.Add(T("Derniers arrêts ou redémarrages demandés : {0}.", string.Join(" ; ", restarts.OrderByDescending(e => e.TimeCreated).Take(Shown).Select(Describe))));
        }

        if (fixes is { Count: > 0 })
        {
            details.Add(T("Derniers correctifs : {0}.", string.Join(" ; ", fixes.OrderByDescending(f => f.Date).Take(Shown).Select(f => $"{f.Id} ({f.Date.ToString("d", Culture)})"))));
        }

        details.Add(T("Si un souci a commencé à une date précise, comparez-la à cette liste : c'est la première question d'un dépanneur."));
        return new Finding
        {
            Id = Id,
            Title = title,
            Category = category,
            Status = FindingStatus.Info,
            Severity = Severity.Info,
            Current = string.Join(", ", summary),
            Explanation = string.Join(" ", details),
        };
    }

    /// <summary>Date et nom du programme seulement : le chemin complet et le reste de l'événement peuvent nommer l'utilisateur.</summary>
    private static string Describe(EventRecordInfo restart)
    {
        var date = restart.TimeCreated.ToString("g", Culture);
        return restart.Data.TryGetValue("param1", out var process) && !string.IsNullOrWhiteSpace(process)
            ? $"{date} ({Name(process)})"
            : date;
    }

    /// <summary>
    /// « C:\Program Files (x86)\Outil\maj.exe (PC) » : le nom du fichier, sans le chemin ni le nom du PC, écrit à la fin entre parenthèses.
    /// Un texte tout entier entre parenthèses (le nom du PC seul), ou rien après le retrait : « un programme non noté ».
    /// </summary>
    internal static string Name(string process)
    {
        var path = process.Trim();
        if (path.StartsWith('(') && path.EndsWith(')'))
        {
            return T("un programme non noté");
        }

        var computer = path.LastIndexOf(" (", StringComparison.Ordinal);
        if (computer > 0 && path.EndsWith(')'))
        {
            path = path[..computer].TrimEnd();
        }

        var name = path[(path.LastIndexOfAny(['\\', '/']) + 1)..].Trim();
        return name.Length == 0 ? T("un programme non noté") : name;
    }

    internal static List<(string Id, DateOnly Date)>? ReadFixes(ICimReader cim, DateOnly today)
    {
        try
        {
            return cim.Query(WindowsUpdateModule.QfeQuery)
                .Select(row => (Id: row.GetString("HotFixID") ?? "?", Date: UpdateParsers.ParseQfeDate(row.GetString("InstalledOn"))))
                .Where(fix => fix.Date is { } date && today.DayNumber - date.DayNumber is >= 0 and <= Days)
                .Select(fix => (fix.Id, fix.Date!.Value))
                .ToList();
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or DataSourceUnavailableException or ManagementException or COMException)
        {
            return null;
        }
    }
}
