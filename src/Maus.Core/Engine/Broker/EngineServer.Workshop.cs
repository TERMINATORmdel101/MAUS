using System.Text.Json;

namespace Maus.Core.Engine.Broker;

/// <summary>Opérations de l'Atelier servies par le moteur.</summary>
public sealed partial class EngineServer
{
    private async Task<JsonElement?> DispatchWorkshopAsync(EngineRequest request, SerialWriter pipe, CancellationToken cancellationToken)
    {
        var args = request.Args;
        switch (request.Op)
        {
            case "inventory":
                return EngineProtocol.ToElement(await engine.ReadInventoryAsync(cancellationToken).ConfigureAwait(false));
            case "drivers":
                return EngineProtocol.ToElement(await engine.ReadDriversAsync(cancellationToken).ConfigureAwait(false));
            case "drivers.backups":
                return EngineProtocol.ToElement(await engine.DriverBackupsAsync(cancellationToken).ConfigureAwait(false));
            case "sensors":
                return EngineProtocol.ToElement(await engine.SampleSensorsAsync(cancellationToken).ConfigureAwait(false));
            case "processes":
                return EngineProtocol.ToElement(await engine.ReadProcessesAsync(cancellationToken).ConfigureAwait(false));
            case "process.terminate":
                return EngineProtocol.ToElement(await engine.TerminateProcessAsync(Arg<int>(args, "pid"), OptionalArg<DateTime?>(args, "startTime"), cancellationToken).ConfigureAwait(false));
            case "memory":
                return EngineProtocol.ToElement(await engine.ReadMemoryDetailsAsync(cancellationToken).ConfigureAwait(false));
            case "latency":
                return EngineProtocol.ToElement(await engine.MeasureLatencyAsync(Arg<TimeSpan>(args, "duration"), cancellationToken).ConfigureAwait(false));
            case "frames.start":
                return EngineProtocol.ToElement(await engine.StartFramesAsync(Id(args, "session"), OptionalArg<TimeSpan?>(args, "keep"), cancellationToken).ConfigureAwait(false));
            case "frames.read":
                return EngineProtocol.ToElement(await engine.ReadFramesAsync(Id(args, "session"), OptionalArg<TimeSpan?>(args, "last"),
                    OptionalArg<TimeSpan?>(args, "silentAfter"), Arg<int>(args, "minimumFrames"), cancellationToken).ConfigureAwait(false));
            case "frames.stop":
                return EngineProtocol.ToElement(await engine.StopFramesAsync(Id(args, "session"), cancellationToken).ConfigureAwait(false));
            case "space":
            {
                var writes = new List<Task>();
                var progress = new InlineProgress<long>(files =>
                {
                    lock (writes)
                    {
                        writes.Add(pipe.WriteAsync(new EngineResponse("progress", EngineProtocol.ToElement(files)), cancellationToken));
                    }
                });
                var report = await engine.ScanSpaceAsync(Arg<string>(args, "root"), progress, cancellationToken).ConfigureAwait(false);
                await Task.WhenAll(SnapshotOf(writes)).ConfigureAwait(false);
                return EngineProtocol.ToElement(report);
            }

            case "console":
                return EngineProtocol.ToElement(await engine.OpenConsoleAsync(Arg<ConsoleRequest>(args, "request"), cancellationToken).ConfigureAwait(false));
            case "restorepoint.list":
                return EngineProtocol.ToElement(await engine.ReadRestorePointsAsync(cancellationToken).ConfigureAwait(false));
            case "restorepoint.create":
                return EngineProtocol.ToElement(await engine.CreateRestorePointAsync(cancellationToken).ConfigureAwait(false));
            case "restorepoint.driver":
                return EngineProtocol.ToElement(await engine.CreateDriverRestorePointAsync(cancellationToken).ConfigureAwait(false));
            case "schedule.get":
                return EngineProtocol.ToElement(await engine.GetScheduleAsync(cancellationToken).ConfigureAwait(false));
            case "schedule.set":
                return EngineProtocol.ToElement(await engine.SetScheduleAsync(OptionalArg<DayOfWeek?>(args, "day"), cancellationToken).ConfigureAwait(false));
            case "software":
                return EngineProtocol.ToElement(await engine.ListSoftwareUpdatesAsync(cancellationToken).ConfigureAwait(false));
            case "battery.report":
                return EngineProtocol.ToElement(await engine.CreateBatteryReportAsync(cancellationToken).ConfigureAwait(false));
            case "benchmark":
                return EngineProtocol.ToElement(await engine.RunBenchmarkAsync(Arg<BenchmarkLaunch>(args, "launch"), cancellationToken).ConfigureAwait(false));
            default:
                throw new EngineException("Opération inconnue : " + request.Op);
        }
    }

    private static T? OptionalArg<T>(JsonElement? args, string name) =>
        args is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var item) && item.ValueKind != JsonValueKind.Null
            ? item.Deserialize<T>(EngineProtocol.Json)
            : default;
}
