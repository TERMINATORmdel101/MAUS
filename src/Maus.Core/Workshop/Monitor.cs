using Maus.Core.Modules.M02Repair;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Grandeur suivie par la fenêtre de surveillance.</summary>
public enum MonitorKind
{
    Temperature,
    Power,
    Clock,
    Load,
}

/// <summary>Une ligne de la fenêtre de surveillance : valeur actuelle, minimale et maximale depuis l'ouverture (ou la remise à zéro).</summary>
public sealed record MonitorRow(string Component, string Name, MonitorKind Kind, double Value, double Min, double Max);

/// <summary>
/// Suivi façon HWMonitor : pour chaque composant, températures, consommations, fréquences et charges, avec leurs extrêmes.
/// Il ne fait que relire les échantillons de l'atelier : aucune mesure en plus.
/// </summary>
public sealed class MonitorTracker
{
    private readonly Dictionary<(string Component, string Name, MonitorKind Kind), (double Min, double Max)> _extremes = [];

    /// <summary>Lignes de l'échantillon, extrêmes compris, dans l'ordre : processeur, cartes graphiques, mémoire, carte mère.</summary>
    public IReadOnlyList<MonitorRow> Update(SensorSnapshot snapshot)
    {
        var rows = new List<MonitorRow>();
        foreach (var (component, name, kind, value) in Extract(snapshot))
        {
            var key = (component, name, kind);
            var (min, max) = _extremes.TryGetValue(key, out var known) ? (Math.Min(known.Min, value), Math.Max(known.Max, value)) : (value, value);
            _extremes[key] = (min, max);
            rows.Add(new MonitorRow(component, name, kind, value, min, max));
        }

        return rows;
    }

    /// <summary>Oublie les extrêmes (bouton « Remettre à zéro »).</summary>
    public void Reset() => _extremes.Clear();

    /// <summary>Mesures utiles d'un échantillon, par composant ; les capteurs lus par le pilote remplacent les résumés sans pilote.</summary>
    public static IEnumerable<(string Component, string Name, MonitorKind Kind, double Value)> Extract(SensorSnapshot snapshot)
    {
        var cpuReadings = snapshot.Readings.Where(r => r.Group == ReadingGroup.Cpu).ToList();
        var cpuName = cpuReadings.FirstOrDefault()?.Hardware ?? T("Processeur");
        if (snapshot.CpuPercent is { } load)
        {
            yield return (cpuName, T("Charge totale"), MonitorKind.Load, load);
        }

        if (cpuReadings.Count == 0)
        {
            if (snapshot.CpuMhz is { } mhz)
            {
                yield return (cpuName, T("Fréquence moyenne"), MonitorKind.Clock, mhz);
            }

            if (snapshot.ThermalZoneC is { } zone)
            {
                yield return (cpuName, T("Zone thermique ACPI (approximative)"), MonitorKind.Temperature, zone);
            }
        }

        foreach (var reading in cpuReadings)
        {
            if (Kind(reading.Kind) is { } kind && kind != MonitorKind.Load)
            {
                yield return (cpuName, reading.Name, kind, reading.Value);
            }
        }

        foreach (var gpu in snapshot.Gpus)
        {
            if (gpu.TemperatureC is { } temperature)
            {
                yield return (gpu.Name, T("Température"), MonitorKind.Temperature, temperature);
            }

            if (gpu.PowerWatts is { } watts)
            {
                yield return (gpu.Name, T("Consommation"), MonitorKind.Power, watts);
            }

            if (gpu.GraphicsClockMhz is { } clock)
            {
                yield return (gpu.Name, T("Fréquence graphique"), MonitorKind.Clock, clock);
            }

            if (gpu.UtilizationPercent is { } utilization)
            {
                yield return (gpu.Name, T("Charge"), MonitorKind.Load, utilization);
            }
        }

        var memory = T("Mémoire vive");
        if (snapshot.MemoryPercent is { } used)
        {
            yield return (memory, T("Utilisée"), MonitorKind.Load, used);
        }

        foreach (var reading in snapshot.Readings.Where(r => r.Group is ReadingGroup.Memory or ReadingGroup.Motherboard))
        {
            if (Kind(reading.Kind) is MonitorKind.Temperature or MonitorKind.Power or MonitorKind.Clock)
            {
                yield return (reading.Group == ReadingGroup.Memory ? memory : reading.Hardware, reading.Name, Kind(reading.Kind)!.Value, reading.Value);
            }
        }
    }

    private static MonitorKind? Kind(ReadingKind kind) => kind switch
    {
        ReadingKind.Temperature => MonitorKind.Temperature,
        ReadingKind.Power => MonitorKind.Power,
        ReadingKind.Clock => MonitorKind.Clock,
        ReadingKind.Load => MonitorKind.Load,
        _ => null,
    };
}

/// <summary>Un événement relevé pendant la surveillance.</summary>
public sealed record WatchedEvent(DateTime Time, string Log, string Provider, int Id, string? Message, bool Hardware, bool PciExpress);

/// <summary>Erreurs relevées depuis l'ouverture de la fenêtre de surveillance.</summary>
public sealed record ErrorWatchResult(int Hardware, int PciExpress, int Windows, IReadOnlyList<WatchedEvent> Events, bool Incomplete);

/// <summary>
/// Erreurs matérielles signalées par le processeur et le chipset (WHEA-Logger, dont les erreurs PCI Express) et erreurs de
/// Windows (journaux Système et Application, niveaux Critique et Erreur), depuis un instant donné. Lecture seule.
/// </summary>
public static class ErrorWatch
{
    public const int MaxEvents = 200;

    private static readonly int[] ErrorLevels = [1, 2];

    public static ErrorWatchResult Read(IEventLogReader logs, DateTime since)
    {
        var events = new List<WatchedEvent>();
        var incomplete = false;

        try
        {
            foreach (var e in logs.Query("System", WindowsHealthModule.WheaProvider, [.. WindowsHealthModule.WheaFatalIds, .. WindowsHealthModule.WheaCorrectedIds], since, MaxEvents, includeMessage: true))
            {
                events.Add(new WatchedEvent(e.TimeCreated, "System", e.Provider, e.Id, e.Message, Hardware: true, IsPciExpress(e)));
            }
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or DataSourceUnavailableException or System.Diagnostics.Eventing.Reader.EventLogException)
        {
            incomplete = true;
        }

        foreach (var log in new[] { "System", "Application" })
        {
            try
            {
                foreach (var e in logs.QueryLevels(log, ErrorLevels, since, MaxEvents, includeMessage: true))
                {
                    if (!string.Equals(e.Provider, WindowsHealthModule.WheaProvider, StringComparison.OrdinalIgnoreCase))
                    {
                        events.Add(new WatchedEvent(e.TimeCreated, log, e.Provider, e.Id, e.Message, Hardware: false, PciExpress: false));
                    }
                }
            }
            catch (Exception ex) when (ex is MausAccessDeniedException or DataSourceUnavailableException or System.Diagnostics.Eventing.Reader.EventLogException)
            {
                incomplete = true;
            }
        }

        events.Sort((a, b) => b.Time.CompareTo(a.Time));
        return new ErrorWatchResult(
            events.Count(e => e.Hardware),
            events.Count(e => e.PciExpress),
            events.Count(e => !e.Hardware),
            events,
            incomplete);
    }

    /// <summary>Erreur WHEA venant du bus PCI Express : le message (ou les données) de l'événement nomme le composant.</summary>
    public static bool IsPciExpress(EventRecordInfo e) =>
        (e.Message?.Contains("PCI Express", StringComparison.OrdinalIgnoreCase) ?? false)
        || e.Data.Values.Any(v => v.Contains("PCI Express", StringComparison.OrdinalIgnoreCase));
}
