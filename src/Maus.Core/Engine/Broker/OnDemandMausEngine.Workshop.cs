using Maus.Core.Fixes;
using Maus.Core.Workshop;
using Maus.Core.Workshop.Memory;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Engine.Broker;

/// <summary>Opérations de l'Atelier : réservées à l'administrateur, ou lisibles sans droits.</summary>
public sealed partial class OnDemandMausEngine
{
    // Lisibles sans droits (en moins complet) : moteur administrateur s'il tourne, sinon dans ce processus.
    public Task<InventoryReading> ReadInventoryAsync(CancellationToken cancellationToken) =>
        Available(e => e.ReadInventoryAsync(cancellationToken));

    public Task<DriversReading> ReadDriversAsync(CancellationToken cancellationToken) =>
        Available(e => e.ReadDriversAsync(cancellationToken));

    public Task<SensorReading> SampleSensorsAsync(CancellationToken cancellationToken) =>
        Available(e => e.SampleSensorsAsync(cancellationToken));

    public Task<ProcessReading> ReadProcessesAsync(CancellationToken cancellationToken) =>
        Available(e => e.ReadProcessesAsync(cancellationToken));

    public Task<string?> TerminateProcessAsync(int pid, DateTime? startTime, CancellationToken cancellationToken) =>
        Available(e => e.TerminateProcessAsync(pid, startTime, cancellationToken));

    public Task<SpaceReport> ScanSpaceAsync(string root, IProgress<long>? progress, CancellationToken cancellationToken) =>
        Available(e => e.ScanSpaceAsync(root, progress, cancellationToken));

    public Task<ScheduleState> GetScheduleAsync(CancellationToken cancellationToken) =>
        Available(e => e.GetScheduleAsync(cancellationToken));

    public Task<SoftwareListing> ListSoftwareUpdatesAsync(CancellationToken cancellationToken) =>
        Available(e => e.ListSoftwareUpdatesAsync(cancellationToken));

    public Task<string?> CreateBatteryReportAsync(CancellationToken cancellationToken) =>
        Available(e => e.CreateBatteryReportAsync(cancellationToken));

    public Task<int> RunBenchmarkAsync(BenchmarkLaunch launch, CancellationToken cancellationToken) =>
        Available(e => e.RunBenchmarkAsync(launch, cancellationToken));

    // Réservées à l'administrateur : le moteur est lancé au besoin.
    public Task<IReadOnlyList<string>> DriverBackupsAsync(CancellationToken cancellationToken) =>
        Elevated(e => e.DriverBackupsAsync(cancellationToken), cancellationToken);

    public Task<MemoryDetailReport> ReadMemoryDetailsAsync(CancellationToken cancellationToken) =>
        Elevated(e => e.ReadMemoryDetailsAsync(cancellationToken), cancellationToken);

    public Task<DriverLatencyResult> MeasureLatencyAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        Elevated(e => e.MeasureLatencyAsync(duration, cancellationToken), cancellationToken);

    public Task<bool> StartFramesAsync(string session, TimeSpan? keep, CancellationToken cancellationToken) =>
        Elevated(e => e.StartFramesAsync(session, keep, cancellationToken), cancellationToken);

    /// <summary>Mesure tenue par le moteur qui l'a lancée : jamais de nouvelle demande d'autorisation pour la lire.</summary>
    public Task<FrameReading> ReadFramesAsync(string session, TimeSpan? last, TimeSpan? silentAfter, int minimumFrames, CancellationToken cancellationToken) =>
        Live() is { } live
            ? live.Engine.ReadFramesAsync(session, last, silentAfter, minimumFrames, cancellationToken)
            : throw new EngineException(T("La mesure des images s'est arrêtée : le moteur de MAUS a été fermé. Relancez-la."));

    /// <summary>Moteur arrêté : il a refermé ses mesures en s'arrêtant, il n'y a rien à arrêter.</summary>
    public Task<FrameSummary?> StopFramesAsync(string session, CancellationToken cancellationToken) =>
        Live() is { } live ? live.Engine.StopFramesAsync(session, cancellationToken) : Task.FromResult<FrameSummary?>(null);

    public Task<ConsoleOutcome> OpenConsoleAsync(ConsoleRequest request, CancellationToken cancellationToken) =>
        Elevated(e => e.OpenConsoleAsync(request, cancellationToken), cancellationToken);

    public Task<RestorePointOutcome> CreateDriverRestorePointAsync(CancellationToken cancellationToken) =>
        Elevated(e => e.CreateDriverRestorePointAsync(cancellationToken), cancellationToken);

    public Task<RestorePointsReading> ReadRestorePointsAsync(CancellationToken cancellationToken) =>
        Elevated(e => e.ReadRestorePointsAsync(cancellationToken), cancellationToken);

    public Task<RestorePointOutcome> CreateRestorePointAsync(CancellationToken cancellationToken) =>
        Elevated(e => e.CreateRestorePointAsync(cancellationToken), cancellationToken);

    public Task<string?> SetScheduleAsync(DayOfWeek? day, CancellationToken cancellationToken) =>
        Elevated(e => e.SetScheduleAsync(day, cancellationToken), cancellationToken);
}
