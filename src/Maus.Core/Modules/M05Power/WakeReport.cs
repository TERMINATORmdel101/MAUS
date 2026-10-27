using System.Globalization;
using System.Text.RegularExpressions;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M05Power;

/// <summary>
/// « Pourquoi mon PC se réveille la nuit ? » (demande du porteur, 27/10/2026), en lecture seule et sans rien interpréter :
/// les derniers réveils notés par Windows (journal Système, Power-Troubleshooter, événement 1 : heures et cause écrite en clair
/// par Windows, champs <c>WakeSourceText</c> et <c>WakeTimerOwner</c> relevés sur le PC du porteur), les appareils autorisés
/// à réveiller le PC (<c>MSPower_DeviceWakeEnable</c>) et les tâches planifiées qui peuvent le réveiller (<c>WakeToRun</c>).
/// Le champ numérique <c>WakeSourceType</c> n'est pas utilisé : son sens n'est publié nulle part (principe 7).
/// </summary>
public static partial class WakeReport
{
    internal const string Id = "M05.wake-sources";
    internal const string Provider = "Microsoft-Windows-Power-Troubleshooter";
    internal const string DeviceWakeQuery = "SELECT InstanceName, Enable FROM MSPower_DeviceWakeEnable";
    internal const string DevicesQuery = "SELECT DeviceID, Name FROM Win32_PnPEntity";
    internal const string TasksQuery = "SELECT TaskName, TaskPath, State, Settings FROM MSFT_ScheduledTask";
    private const int Days = 30;
    private const int Shown = 8;
    private const long DisabledTaskState = 1;

    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    public static Finding Detect(AuditContext context, string category)
    {
        ArgumentNullException.ThrowIfNull(context);
        var title = T("Réveils du PC (veille et veille prolongée)");
        IReadOnlyList<EventRecordInfo> wakes;
        try
        {
            wakes = context.EventLogs.Query("System", Provider, [1], context.Now.DateTime.AddDays(-Days), maxEvents: 200);
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(Id, title, category);
        }
        catch (DataSourceUnavailableException)
        {
            return Finding.Unknown(Id, title, T("Le journal Système est illisible sur ce PC."), category);
        }

        var parts = new List<string>
        {
            T("Windows note chaque sortie de veille, avec sa cause quand il la connaît. Ces informations aident à trouver ce qui réveille le PC la nuit : une souris ou un clavier, la carte réseau, ou une tâche planifiée (mises à jour, maintenance)."),
        };

        var latest = wakes.OrderByDescending(e => e.TimeCreated).Take(Shown).Select(DescribeWake).ToList();
        if (latest.Count > 0)
        {
            parts.Add(T("Derniers réveils : {0}.", string.Join(" ; ", latest)));
        }

        var devices = ReadWakeDevices(context.Cim);
        parts.Add(devices switch
        {
            null => T("Liste des appareils autorisés à réveiller le PC : illisible (droits administrateur souvent nécessaires)."),
            { Count: 0 } => T("Aucun appareil n'est autorisé à réveiller le PC."),
            _ => T("Appareils autorisés à réveiller le PC : {0}.", string.Join(", ", devices)),
        });

        var tasks = ReadWakeTasks(context.Cim);
        parts.Add(tasks switch
        {
            null => T("Liste des tâches planifiées : illisible."),
            { Count: 0 } => T("Aucune tâche planifiée active n'est réglée pour réveiller le PC."),
            _ => T("Tâches planifiées réglées pour réveiller le PC : {0}.", string.Join(", ", tasks)),
        });

        return new Finding
        {
            Id = Id,
            Title = title,
            Category = category,
            Status = FindingStatus.Info,
            Current = T("{0} réveil(s) ces {1} derniers jours", wakes.Count, Days),
            Explanation = string.Join(" ", parts),
            Advice = T("Pour empêcher un appareil de réveiller le PC : Gestionnaire de périphériques, propriétés de l'appareil, onglet « Gestion de l'alimentation », décochez « Autoriser ce périphérique à sortir l'ordinateur du mode veille ». Pour les tâches : Options d'alimentation, paramètres avancés, « Veille » > « Autoriser les minuteurs de réveil »."),
            SettingsPage = "ms-settings:powersleep",
        };
    }

    /// <summary>Un réveil : heure locale et cause écrite par Windows (appareil, ou minuteur et son programme).</summary>
    private static string DescribeWake(EventRecordInfo wake)
    {
        var at = ParseUtc(wake.Data.GetValueOrDefault("WakeTime"))?.ToLocalTime() ?? wake.TimeCreated;
        var when = at.ToString("g", Culture);
        var source = wake.Data.GetValueOrDefault("WakeSourceText")?.Trim();
        var timer = wake.Data.GetValueOrDefault("WakeTimerOwner")?.Trim();
        var cause = !string.IsNullOrEmpty(source) ? source
            : !string.IsNullOrEmpty(timer) ? T("minuteur de {0}", timer)
            : T("cause non précisée par Windows");
        return T("{0} ({1})", when, cause);
    }

    private static DateTime? ParseUtc(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value)
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : null;

    /// <summary>
    /// Appareils armés pour le réveil, par leur nom dans le Gestionnaire de périphériques. Le nom d'instance WMI est
    /// l'identifiant de l'appareil suivi de « _0 » : sans correspondance, l'identifiant brut est affiché.
    /// </summary>
    private static List<string>? ReadWakeDevices(ICimReader cim)
    {
        IReadOnlyList<CimRow> armed;
        try
        {
            armed = cim.Query(DeviceWakeQuery, CimScopes.Wmi);
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or DataSourceUnavailableException)
        {
            return null;
        }

        var ids = armed.Where(r => r.GetBool("Enable") == true)
            .Select(r => r.GetString("InstanceName"))
            .OfType<string>()
            .Select(name => InstanceSuffix().Replace(name, string.Empty))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var device in cim.Query(DevicesQuery))
            {
                if (device.GetString("DeviceID") is { } id && device.GetString("Name") is { Length: > 0 } name)
                {
                    names.TryAdd(id, name);
                }
            }
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or DataSourceUnavailableException)
        {
            // Noms indisponibles : les identifiants bruts restent affichés.
        }

        return ids.Select(id => names.GetValueOrDefault(id) ?? id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Tâches actives dont le réglage « Sortir l'ordinateur du mode veille pour exécuter cette tâche » est coché.</summary>
    private static List<string>? ReadWakeTasks(ICimReader cim)
    {
        try
        {
            return cim.Query(TasksQuery, CimScopes.TaskScheduler)
                .Where(t => t.GetInt64("State") != DisabledTaskState && t.GetRow("Settings")?.GetBool("WakeToRun") == true)
                .Select(t => $"{t.GetString("TaskPath") ?? @"\"}{t.GetString("TaskName")}".TrimStart('\\'))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or DataSourceUnavailableException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"_\d+$")]
    private static partial Regex InstanceSuffix();
}
