using Maus.Core.Fixes;
using Maus.Core.Workshop;
using Maus.Core.Workshop.Memory;

namespace Maus.Core.Engine.Broker;

/// <summary>Opérations de l'Atelier demandées au moteur administrateur.</summary>
public sealed partial class RemoteMausEngine
{
    public async Task<InventoryReading> ReadInventoryAsync(CancellationToken cancellationToken) =>
        await CallAsync<InventoryReading>("inventory", null, null, cancellationToken).ConfigureAwait(false) ?? throw Empty();

    public async Task<DriversReading> ReadDriversAsync(CancellationToken cancellationToken) =>
        await CallAsync<DriversReading>("drivers", null, null, cancellationToken).ConfigureAwait(false) ?? throw Empty();

    public async Task<IReadOnlyList<string>> DriverBackupsAsync(CancellationToken cancellationToken) =>
        await CallAsync<List<string>>("drivers.backups", null, null, cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<SensorReading> SampleSensorsAsync(CancellationToken cancellationToken) =>
        await CallAsync<SensorReading>("sensors", null, null, cancellationToken).ConfigureAwait(false) ?? throw Empty();

    public async Task<ProcessReading> ReadProcessesAsync(CancellationToken cancellationToken) =>
        await CallAsync<ProcessReading>("processes", null, null, cancellationToken).ConfigureAwait(false) ?? throw Empty();

    public Task<string?> TerminateProcessAsync(int pid, DateTime? startTime, CancellationToken cancellationToken) =>
        CallAsync<string>("process.terminate", new { pid, startTime }, null, cancellationToken);

    public async Task<MemoryDetailReport> ReadMemoryDetailsAsync(CancellationToken cancellationToken) =>
        await CallAsync<MemoryDetailReport>("memory", null, null, cancellationToken).ConfigureAwait(false) ?? throw Empty();

    public async Task<DriverLatencyResult> MeasureLatencyAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        await CallAsync<DriverLatencyResult>("latency", new { duration }, null, cancellationToken).ConfigureAwait(false) ?? throw Empty();

    public async Task<bool> StartFramesAsync(string session, TimeSpan? keep, CancellationToken cancellationToken) =>
        await CallAsync<bool>("frames.start", new { session, keep }, null, cancellationToken).ConfigureAwait(false);

    public async Task<FrameReading> ReadFramesAsync(string session, TimeSpan? last, TimeSpan? silentAfter, int minimumFrames, CancellationToken cancellationToken) =>
        await CallAsync<FrameReading>("frames.read", new { session, last, silentAfter, minimumFrames }, null, cancellationToken).ConfigureAwait(false) ?? new FrameReading(null, true);

    public Task<FrameSummary?> StopFramesAsync(string session, CancellationToken cancellationToken) =>
        CallAsync<FrameSummary>("frames.stop", new { session }, null, cancellationToken);

    public async Task<SpaceReport> ScanSpaceAsync(string root, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var report = await CallAsync<SpaceReport>("space", new { root }, element =>
        {
            if (element is { } value)
            {
                progress?.Report(value.GetInt64());
            }
        }, cancellationToken).ConfigureAwait(false) ?? throw Empty();
        SpaceAnalyzer.RestoreParents(report.Root);
        return report;
    }

    public async Task<ConsoleOutcome> OpenConsoleAsync(ConsoleRequest request, CancellationToken cancellationToken) =>
        await CallAsync<ConsoleOutcome>("console", new { request }, null, cancellationToken).ConfigureAwait(false) ?? throw Empty();

    public async Task<RestorePointOutcome> CreateDriverRestorePointAsync(CancellationToken cancellationToken) =>
        await CallAsync<RestorePointOutcome>("restorepoint.driver", null, null, cancellationToken).ConfigureAwait(false) ?? throw Empty();

    public async Task<ScheduleState> GetScheduleAsync(CancellationToken cancellationToken) =>
        await CallAsync<ScheduleState>("schedule.get", null, null, cancellationToken).ConfigureAwait(false) ?? throw Empty();

    public Task<string?> SetScheduleAsync(DayOfWeek? day, CancellationToken cancellationToken) =>
        CallAsync<string>("schedule.set", new { day }, null, cancellationToken);

    public async Task<RestorePointsReading> ReadRestorePointsAsync(CancellationToken cancellationToken) =>
        await CallAsync<RestorePointsReading>("restorepoint.list", null, null, cancellationToken).ConfigureAwait(false) ?? throw Empty();

    public async Task<RestorePointOutcome> CreateRestorePointAsync(CancellationToken cancellationToken) =>
        await CallAsync<RestorePointOutcome>("restorepoint.create", null, null, cancellationToken).ConfigureAwait(false) ?? throw Empty();

    public Task<string?> CreateBatteryReportAsync(CancellationToken cancellationToken) =>
        CallAsync<string>("battery.report", null, null, cancellationToken);

    public async Task<SoftwareListing> ListSoftwareUpdatesAsync(CancellationToken cancellationToken) =>
        await CallAsync<SoftwareListing>("software", null, null, cancellationToken).ConfigureAwait(false) ?? throw Empty();

    public async Task<int> RunBenchmarkAsync(BenchmarkLaunch launch, CancellationToken cancellationToken) =>
        await CallAsync<int>("benchmark", new { launch }, null, cancellationToken).ConfigureAwait(false);
}
