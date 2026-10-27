using System.Diagnostics;
using Maus.Core.Diagnostics;
using Maus.Core.Fixes;
using Maus.Core.Modules.M02Repair;
using Maus.Core.Modules.M20Software;
using Maus.Core.Platform;
using Maus.Core.Workshop;
using Maus.Core.Workshop.Memory;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Engine.Broker;

/// <summary>Atelier côté moteur : chaque opération vérifie elle-même ce qu'elle reçoit (voir <see cref="IMausEngine"/>).</summary>
public sealed partial class LocalMausEngine
{
    /// <summary>Sessions d'écoute de PresentMon que l'interface peut demander.</summary>
    private static readonly HashSet<string> FrameSessions = new(StringComparer.Ordinal) { "MAUS_Compteur", "MAUS_Releve" };

    private readonly Lock _workshopGate = new();
    private readonly Dictionary<string, PresentMonCapture> _frames = new(StringComparer.Ordinal);
    private AuditContext? _workshopContext;
    private ISensorSource? _sensors;
    private WindowsProcessSource? _processSource;
    private Task<MemoryDetailReport>? _memoryRead;
    private IReadOnlyList<SoftwareUpdate> _software = [];

    /// <summary>
    /// Processus de l'interface servie par ce moteur : jamais arrêté par « Arrêter le processus » (le moteur se protège déjà
    /// lui-même). 0 dans un moteur sans interface séparée.
    /// </summary>
    public int ProtectedProcessId { get; init; }

    private AuditContext WorkshopContext()
    {
        lock (_workshopGate)
        {
            return _workshopContext ??= _context ?? _createContext();
        }
    }

    public Task<InventoryReading> ReadInventoryAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var context = WorkshopContext();
        var inventory = HardwareInventoryReader.Read(context, new X86CpuIdSource(), new WindowsNvmlSource());
        MachineDetails? details;
        try
        {
            details = MachineDetailsReader.Read(context);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Breadcrumbs.Add("Mon PC : informations supplémentaires illisibles (" + ex.GetType().Name + ")");
            details = null;
        }

        // Profils XMP / EXPO des barrettes : puce SPD, lisible seulement avec PawnIO et en administrateur (lecture seule).
        var spdRead = PawnIo.State(context.Registry).Installed && Environment.IsPrivilegedProcess;
        return new InventoryReading(inventory, details, spdRead ? ReadSpdModules() : null, spdRead);
    }, cancellationToken);

    private static List<SpdModule>? ReadSpdModules()
    {
        try
        {
            var modules = new PawnIoSpdSource().ReadAll().Select(SpdDecoder.Decode).OfType<SpdModule>().ToList();
            return modules.Count > 0 ? modules : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Breadcrumbs.Add("Mon PC : puces SPD illisibles (" + ex.GetType().Name + ")");
            return null;
        }
    }

    public Task<DriversReading> ReadDriversAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var context = WorkshopContext();
        return new DriversReading(DriverInventoryReader.Read(context.Cim, new CfgMgrDriverDateSource()), context.Hardware);
    }, cancellationToken);

    public Task<IReadOnlyList<string>> DriverBackupsAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<string>>(() =>
    {
        var root = GraphicsDriverActions.BackupRoot;
        if (!Directory.Exists(root))
        {
            return [];
        }

        return [.. new DirectoryInfo(root).EnumerateDirectories()
            .Where(d => d.EnumerateFiles("*.inf", SearchOption.AllDirectories).Any())
            .OrderByDescending(d => d.CreationTimeUtc)
            .Select(d => d.Name)];
    }, cancellationToken);

    public Task<SensorReading> SampleSensorsAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var registry = WorkshopContext().Registry;
        var (installed, version) = PawnIo.State(registry);
        lock (_workshopGate)
        {
            // Mesures sans pilote, complétées par PawnIO s'il est installé (le moteur a les droits administrateur).
            _sensors ??= installed && Environment.IsPrivilegedProcess
                ? new CombinedSensorSource(new WindowsSensorSource(), () => new LhmAdvancedSensors())
                : new WindowsSensorSource();
            var snapshot = _sensors.Sample();
            var combined = _sensors as CombinedSensorSource;
            var cpuMax = WorkshopContext().Hardware.Cpu.Name is { Length: > 0 } name ? SafetyLimits.Load().ForCpu(name)?.MaxC : null;
            return new SensorReading(snapshot, new SensorState(installed, version, Environment.IsPrivilegedProcess, combined?.Failure, combined?.IsStalled ?? false, cpuMax));
        }
    }, cancellationToken);

    public Task<ProcessReading> ReadProcessesAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        lock (_workshopGate)
        {
            _processSource ??= new WindowsProcessSource();
            return new ProcessReading(_processSource.Read(), _processSource.GpuPercentByPid());
        }
    }, cancellationToken);

    public Task<string?> TerminateProcessAsync(int pid, DateTime? startTime, CancellationToken cancellationToken) => Task.Run<string?>(() =>
    {
        // Le moteur relit lui-même le processus : nom, chemin et heure de lancement viennent de Windows, pas de l'interface.
        RawProcess? found;
        lock (_workshopGate)
        {
            _processSource ??= new WindowsProcessSource();
            found = _processSource.Read().FirstOrDefault(p => p.Pid == pid);
        }

        if (found is null || (startTime is { } expected && found.StartTime != expected))
        {
            return T("Ce processus s'est déjà terminé.");
        }

        if (pid == ProtectedProcessId)
        {
            return T("C'est MAUS lui-même.");
        }

        var sample = new ProcessSample(found.Pid, found.Name, found.Path, 0, found.WorkingSetBytes, found.PrivateBytes, 0, null, found.SessionId, found.StartTime);
        try
        {
            new WindowsProcessTerminator().Terminate(sample);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }, cancellationToken);

    public Task<MemoryDetailReport> ReadMemoryDetailsAsync(CancellationToken cancellationToken)
    {
        lock (_workshopGate)
        {
            // Une lecture du bus des barrettes ne s'interrompt pas : tant qu'elle est bloquée, aucune autre ne part.
            if (_memoryRead is { IsCompleted: false })
            {
                throw new EngineException(T("La lecture précédente est toujours bloquée. Fermez toutes les fenêtres de MAUS et les logiciels de surveillance, puis relancez MAUS."));
            }

            var cpu = CpuIdParser.Read(new X86CpuIdSource());
            _memoryRead = Task.Run(
                () =>
                {
                    // Vitesse que le BIOS déclare à Windows : contrôle de l'horloge lue dans le contrôleur Intel.
                    var windows = WindowsMemoryConfiguration.Read(new WmiCimReader()).Select(s => s.ConfiguredMts).OfType<int>().DefaultIfEmpty().Max();
                    return PawnIoMemoryDetails.Read(cpu, null, step => Breadcrumbs.Add("mémoire : " + step), windows > 0 ? windows : null);
                },
                CancellationToken.None);
            return _memoryRead;
        }
    }

    public Task<DriverLatencyResult> MeasureLatencyAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5))
        {
            throw new EngineException(T("Durée de mesure refusée."));
        }

        return new EtwDriverLatencyTracer().MeasureAsync(duration, cancellationToken);
    }

    public Task<bool> StartFramesAsync(string session, TimeSpan? keep, CancellationToken cancellationToken) => Task.Run(() =>
    {
        CheckSession(session);
        PresentMonCapture? previous;
        lock (_workshopGate)
        {
            _frames.Remove(session, out previous);
        }

        previous?.StopInBackground("PresentMon : mesure précédente remplacée");
        var capture = PresentMonCapture.TryStart(keep, session);
        if (capture is null)
        {
            return false;
        }

        lock (_workshopGate)
        {
            _frames[session] = capture;
        }

        return true;
    }, cancellationToken);

    public Task<FrameReading> ReadFramesAsync(string session, TimeSpan? last, TimeSpan? silentAfter, int minimumFrames, CancellationToken cancellationToken)
    {
        CheckSession(session);
        lock (_workshopGate)
        {
            return Task.FromResult(_frames.TryGetValue(session, out var capture)
                ? new FrameReading(capture.Log.Summarize(last, silentAfter, Math.Clamp(minimumFrames, 2, FrameTimeLog.MinimumFrames)), capture.HasExited)
                : new FrameReading(null, true));
        }
    }

    public Task<FrameSummary?> StopFramesAsync(string session, CancellationToken cancellationToken) => Task.Run(() =>
    {
        CheckSession(session);
        PresentMonCapture? capture;
        lock (_workshopGate)
        {
            _frames.Remove(session, out capture);
        }

        if (capture is null)
        {
            return null;
        }

        using (capture)
        {
            return capture.Stop();
        }
    }, cancellationToken);

    private static void CheckSession(string session)
    {
        if (!FrameSessions.Contains(session))
        {
            throw new EngineException(T("Session de mesure refusée."));
        }
    }

    public Task<SpaceReport> ScanSpaceAsync(string root, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        // Un dossier local existant, désigné par un chemin complet : jamais un chemin de périphérique (\\?\, \\.\) ni réseau.
        if (!Path.IsPathFullyQualified(root) || root.StartsWith(@"\\", StringComparison.Ordinal) || !Directory.Exists(root))
        {
            throw new EngineException(T("Dossier refusé."));
        }

        return SpaceAnalyzer.ScanAsync(root, progress, cancellationToken);
    }

    public Task<ConsoleOutcome> OpenConsoleAsync(ConsoleRequest request, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = WorkshopContext();
        switch (request.Kind)
        {
            case ConsoleKind.Repair:
                return new ConsoleOutcome(VisibleConsole.Run(RepairConsole.Arguments()));
            case ConsoleKind.WingetUpgrade:
            {
                // Seulement des logiciels de la dernière liste lue par le moteur lui-même.
                var wanted = new HashSet<string>(request.PackageIds ?? [], StringComparer.OrdinalIgnoreCase);
                var chosen = _software.Where(u => wanted.Contains(u.Id) && u.CanTarget).ToList();
                if (chosen.Count == 0)
                {
                    return new ConsoleOutcome(false, T("Aucun logiciel de la liste n'a été choisi."));
                }

                return Winget.Locate(context.Packages, context.Files) is { } winget
                    ? new ConsoleOutcome(VisibleConsole.Run(Winget.UpgradeConsoleArguments(winget, chosen)))
                    : new ConsoleOutcome(false, WingetMissing);
            }

            case ConsoleKind.PawnIoInstall or ConsoleKind.PawnIoUninstall:
                return Winget.Locate(context.Packages, context.Files) is { } path
                    ? new ConsoleOutcome(VisibleConsole.Run(request.Kind == ConsoleKind.PawnIoInstall ? PawnIo.InstallConsoleArguments(path) : PawnIo.UninstallConsoleArguments(path)))
                    : new ConsoleOutcome(false, WingetMissing);
            case ConsoleKind.DriverAction:
            {
                var drivers = (await ReadDriversAsync(cancellationToken).ConfigureAwait(false)).Drivers;
                if (request.Action is not { } action || drivers.FirstOrDefault(d => d.InstanceId == request.InstanceId && d.Group == DriverGroup.Graphics) is not { } gpu)
                {
                    return new ConsoleOutcome(false, T("Carte graphique introuvable : actualisez la liste des pilotes."));
                }

                var folder = GraphicsDriverActions.BackupFolder(gpu.InfName ?? "pilote", DateTimeOffset.Now);
                return GraphicsDriverActions.ConsoleArguments(action, gpu, folder) is { } arguments
                    ? new ConsoleOutcome(VisibleConsole.Run(arguments), folder)
                    : new ConsoleOutcome(false, T("Action impossible sur ce pilote."));
            }

            case ConsoleKind.DriverRestore:
            {
                var name = request.Backup ?? string.Empty;
                var root = GraphicsDriverActions.BackupRoot;
                var full = Path.Combine(root, name);
                var valid = name.Length is > 0 and <= 200 && name.IndexOfAny([.. Path.GetInvalidFileNameChars(), '\\', '/']) < 0 && name is not "." and not ".."
                    && Directory.Exists(full) && Directory.EnumerateFiles(full, "*.inf", SearchOption.AllDirectories).Any();
                if (!valid || GraphicsDriverActions.RestoreArguments(full) is not { } arguments)
                {
                    return new ConsoleOutcome(false, T("Choisissez un dossier de sauvegarde créé par MAUS (il contient un fichier .inf)."));
                }

                return new ConsoleOutcome(VisibleConsole.Run(arguments), full);
            }

            default:
                throw new EngineException(T("Fenêtre de commande refusée."));
        }
    }, cancellationToken);

    private static string WingetMissing => T("winget est absent : réinstallez « Programme d'installation d'application » depuis le Microsoft Store.");

    public Task<RestorePointOutcome> CreateDriverRestorePointAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        try
        {
            var fix = _createFixContext(WorkshopContext());
            return new RestorePointCreator(fix.SystemRestore, fix.Settings)
                .Create(T("MAUS : avant la suppression du pilote graphique"), enableProtectionIfNeeded: false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new RestorePointOutcome(RestorePointStatus.Failed, null, T("Le point de restauration n'a pas pu être créé : {0}", ex.Message));
        }
    }, cancellationToken);

    /// <summary>Espace des points de restauration : classe WMI Win32_ShadowStorage (UsedSpace, MaxSpace, en octets).</summary>
    internal const string ShadowStorageQuery = "SELECT UsedSpace, MaxSpace FROM Win32_ShadowStorage";

    public Task<RestorePointsReading> ReadRestorePointsAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var context = WorkshopContext();
        var restore = _createFixContext(context).SystemRestore;
        var drive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        long? used = null, max = null;
        try
        {
            var rows = context.Cim.Query(ShadowStorageQuery);
            if (rows.Count > 0)
            {
                used = rows.Sum(r => r.GetInt64("UsedSpace") ?? 0);
                max = rows.Sum(r => r.GetInt64("MaxSpace") ?? 0);
            }
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or DataSourceUnavailableException)
        {
            // Espace illisible : la liste des points reste utile.
        }

        var points = restore.ListRestorePoints().OrderBy(p => p.CreatedAt ?? DateTimeOffset.MinValue).ToList();
        return new RestorePointsReading(restore.GetProtectionState(drive), points, used, max);
    }, cancellationToken);

    public Task<RestorePointOutcome> CreateRestorePointAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        try
        {
            var fix = _createFixContext(WorkshopContext());
            return new RestorePointCreator(fix.SystemRestore, fix.Settings)
                .Create(T("MAUS : point créé à la main"), enableProtectionIfNeeded: false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new RestorePointOutcome(RestorePointStatus.Failed, null, T("Le point de restauration n'a pas pu être créé : {0}", ex.Message));
        }
    }, cancellationToken);

    public async Task<ScheduleState> GetScheduleAsync(CancellationToken cancellationToken)
    {
        var (day, command) = await TaskSchedulerClient.GetAsync().ConfigureAwait(false);
        if (day is { } existing && command is not null && ScheduledAudit.IsOlderStoreFolder(command, TaskSchedulerClient.Executable)
            && TaskSchedulerClient.Refusal() is null)
        {
            // Version du Microsoft Store : chaque mise à jour change le dossier de MAUS ; la tâche suit le nouveau dossier.
            var error = await TaskSchedulerClient.CreateAsync(existing).ConfigureAwait(false);
            Breadcrumbs.Add("audit hebdomadaire : tâche remise sur le nouveau dossier du Store" + (error is null ? "" : " (échec)"));
            if (error is null)
            {
                command = TaskSchedulerClient.Executable;
            }
        }

        return new ScheduleState(day, command, TaskSchedulerClient.Refusal(), command is null ? null : TaskSchedulerClient.Refusal(command));
    }

    public async Task<string?> SetScheduleAsync(DayOfWeek? day, CancellationToken cancellationToken)
    {
        if (day is not { } chosen)
        {
            return await TaskSchedulerClient.DeleteAsync().ConfigureAwait(false);
        }

        return TaskSchedulerClient.Refusal() ?? await TaskSchedulerClient.CreateAsync(chosen).ConfigureAwait(false);
    }

    public async Task<string?> CreateBatteryReportAsync(CancellationToken cancellationToken)
    {
        var file = ReadOnlyCommandRunner.BatteryReportPath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (File.Exists(file))
        {
            File.Delete(file);
        }

        var powercfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "powercfg.exe");
        var result = await WorkshopContext().Commands.RunAsync(powercfg, ["/batteryreport", "/output", file], TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 && File.Exists(file) ? file : null;
    }

    public async Task<SoftwareListing> ListSoftwareUpdatesAsync(CancellationToken cancellationToken)
    {
        var context = WorkshopContext();
        if (Winget.Locate(context.Packages, context.Files) is not { } winget)
        {
            return new SoftwareListing(false, []);
        }

        var updates = await Winget.ListUpgradesAsync(context.Commands, winget, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        _software = updates;
        return new SoftwareListing(true, updates);
    }

    public async Task<int> RunBenchmarkAsync(BenchmarkLaunch launch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (launch.Api is not ("d3d11" or "d3d12") || launch.Resolution is not ("720p" or "1080p" or "1440p" or "4k")
            || launch.Scope is not ("all" or "gpu" or "cpu") || launch.Language is not ("fr" or "en" or "es") || Environment.ProcessPath is not { } exe)
        {
            throw new EngineException(T("Réglages du benchmark refusés."));
        }

        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var argument in new[] { "--benchmark", "--api", launch.Api, "--resolution", launch.Resolution, "--lang", launch.Language })
        {
            start.ArgumentList.Add(argument);
        }

        if (launch.Scope != "all")
        {
            start.ArgumentList.Add("--only");
            start.ArgumentList.Add(launch.Scope);
        }

        if (!launch.RayTracing)
        {
            start.ArgumentList.Add("--raytracing");
            start.ArgumentList.Add("off");
        }

        using var process = Process.Start(start) ?? throw new EngineException(T("Le benchmark n'a pas pu démarrer."));
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>Fermeture du moteur : capteurs, liste des processus et mesures PresentMon rendus à Windows.</summary>
    private void DisposeWorkshop()
    {
        List<PresentMonCapture> captures;
        lock (_workshopGate)
        {
            captures = [.. _frames.Values];
            _frames.Clear();
            _sensors?.Dispose();
            _sensors = null;
            _processSource?.Dispose();
            _processSource = null;
        }

        foreach (var capture in captures)
        {
            capture.StopInBackground("PresentMon arrêté avec le moteur");
        }

        PresentMonCapture.WaitForBackgroundStops(TimeSpan.FromSeconds(8));
    }
}
