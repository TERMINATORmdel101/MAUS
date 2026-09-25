using System.Diagnostics.Eventing.Reader;
using Maus.Core.Platform;

namespace Maus.Core.Modules.M02Repair;

/// <summary>Résultat d'une lecture du journal Système : les événements, ou la raison de l'échec.</summary>
internal sealed class SystemLogQuery
{
    public const string LogName = "System";

    /// <summary>Plafond par requête : au-delà, le nombre affiché devient « au moins ».</summary>
    public const int MaxEvents = 500;

    private SystemLogQuery(IReadOnlyList<EventRecordInfo> events, bool accessDenied, string? error)
    {
        Events = events;
        AccessDenied = accessDenied;
        Error = error;
    }

    public IReadOnlyList<EventRecordInfo> Events { get; }

    public bool AccessDenied { get; }

    public string? Error { get; }

    public bool Succeeded => !AccessDenied && Error is null;

    /// <summary>Vrai si le plafond est atteint : le nombre réel peut être plus élevé.</summary>
    public bool Truncated => Events.Count >= MaxEvents;

    public static SystemLogQuery Run(IEventLogReader logs, string provider, IReadOnlyCollection<int> eventIds, DateTime since)
    {
        try
        {
            return new SystemLogQuery(logs.Query(LogName, provider, eventIds, since, MaxEvents), false, null);
        }
        catch (MausAccessDeniedException)
        {
            return new SystemLogQuery([], true, null);
        }
        catch (Exception ex) when (ex is DataSourceUnavailableException or EventLogException or InvalidOperationException)
        {
            return new SystemLogQuery([], false, ex.Message);
        }
    }

    /// <summary>Constat « indéterminé » pour la première requête en échec, ou <c>null</c> si toutes ont réussi.</summary>
    public static Finding? FirstFailure(string id, string title, string category, params SystemLogQuery[] queries)
    {
        foreach (var query in queries)
        {
            if (query.AccessDenied)
            {
                return Finding.AdminRequired(id, title, category);
            }

            if (query.Error is not null)
            {
                return Finding.Unknown(id, title, $"Lecture du journal Système impossible : {query.Error}", category);
            }
        }

        return null;
    }
}
