using Maus.Core.Fixes;
using Maus.Core.Workshop;
using Maus.Core.Workshop.Benchmark;
using Maus.Core.Workshop.Memory;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Engine.Broker;

/// <summary>Opérations de l'Atelier : réservées à l'administrateur, ou lisibles sans droits.</summary>
public sealed partial class OnDemandMausEngine
{
    // Lisibles sans droits (en moins complet) : moteur administrateur s'il tourne, sinon dans ce processus.
    public Task<InventoryReading> ReadInventoryAsync(CancellationToken cancellationToken) =>
        AvailableRead(e => e.ReadInventoryAsync(cancellationToken));

    public Task<DriversReading> ReadDriversAsync(CancellationToken cancellationToken) =>
        AvailableRead(e => e.ReadDriversAsync(cancellationToken));

    public Task<SensorReading> SampleSensorsAsync(CancellationToken cancellationToken) =>
        AvailableRead(e => e.SampleSensorsAsync(cancellationToken));

    public Task<ProcessReading> ReadProcessesAsync(CancellationToken cancellationToken) =>
        AvailableRead(e => e.ReadProcessesAsync(cancellationToken));

    public Task<string?> TerminateProcessAsync(int pid, DateTime? startTime, CancellationToken cancellationToken) =>
        Available(e => e.TerminateProcessAsync(pid, startTime, cancellationToken));

    public Task<SpaceReport> ScanSpaceAsync(string root, IProgress<long>? progress, CancellationToken cancellationToken) =>
        AvailableRead(e => e.ScanSpaceAsync(root, progress, cancellationToken));

    public Task<ScheduleState> GetScheduleAsync(CancellationToken cancellationToken) =>
        AvailableRead(e => e.GetScheduleAsync(cancellationToken));

    // La liste des logiciels à mettre à jour n'est plus ici (0.8.6) : la fenêtre la lit elle-même, sans droits
    // (SoftwareListing.ReadHereAsync), même quand le moteur tourne.

    /// <summary>Moteur en marche : benchmark avec ses droits, qui rend sa passe sans rien écrire dans le profil (voir <see cref="IMausEngine.RunBenchmarkAsync"/>).</summary>
    public Task<BenchmarkOutcome> RunBenchmarkAsync(BenchmarkLaunch launch, CancellationToken cancellationToken) =>
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

    public Task<ToolOutcome> RunToolAsync(ToolRequest request, IProgress<string>? progress, CancellationToken cancellationToken) =>
        Elevated(e => e.RunToolAsync(request, progress, cancellationToken), cancellationToken);

    public Task<RestorePointOutcome> CreateDriverRestorePointAsync(CancellationToken cancellationToken) =>
        Elevated(e => e.CreateDriverRestorePointAsync(cancellationToken), cancellationToken);

    public Task<RestorePointsReading> ReadRestorePointsAsync(CancellationToken cancellationToken) =>
        Elevated(e => e.ReadRestorePointsAsync(cancellationToken), cancellationToken);

    public Task<RestorePointOutcome> CreateRestorePointAsync(CancellationToken cancellationToken) =>
        Elevated(e => e.CreateRestorePointAsync(cancellationToken), cancellationToken);

    /// <summary>
    /// Seulement si le moteur tourne : refermer ne demande jamais d'autorisation. Dès que le moteur a accepté, il compte comme
    /// arrêté (il finit de s'arrêter juste après) : rien ne doit plus partir vers lui, ni relancer un moteur que l'utilisateur vient de fermer.
    /// </summary>
    public async Task<bool> CloseRightsAsync(CancellationToken cancellationToken)
    {
        if (Live() is not { } live)
        {
            return false;
        }

        var closed = await live.Engine.CloseRightsAsync(cancellationToken).ConfigureAwait(false);
        if (closed)
        {
            _closed = live;
        }

        return closed;
    }

    public Task<string?> SetScheduleAsync(DayOfWeek? day, CancellationToken cancellationToken) =>
        Elevated(e => e.SetScheduleAsync(day, cancellationToken), cancellationToken);
}
