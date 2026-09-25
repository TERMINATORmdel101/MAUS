using System.Diagnostics.Eventing.Reader;
using Maus.Core.Modules.M02Repair;
using Maus.Core.Platform;

namespace Maus.Core.Workshop;

/// <summary>Erreurs matérielles signalées par le processeur (WHEA) depuis un instant donné : un signe d'instabilité même sans plantage.</summary>
public static class WheaEvents
{
    /// <summary>Nombre d'erreurs WHEA (corrigées ou non) dans le journal Système ; <c>null</c> si le journal est illisible.</summary>
    public static int? CountSince(IEventLogReader logs, DateTimeOffset since)
    {
        try
        {
            return logs.Query("System", WindowsHealthModule.WheaProvider, [.. WindowsHealthModule.WheaFatalIds, .. WindowsHealthModule.WheaCorrectedIds], since.LocalDateTime, 500).Count;
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or DataSourceUnavailableException or EventLogException or InvalidOperationException)
        {
            return null;
        }
    }
}
