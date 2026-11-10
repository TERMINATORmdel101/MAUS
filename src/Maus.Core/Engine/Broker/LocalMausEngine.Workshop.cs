using System.Diagnostics;
using System.Runtime.CompilerServices;
using Maus.Core.Diagnostics;
using Maus.Core.Fixes;
using Maus.Core.Modules.M02Repair;
using Maus.Core.Platform;
using Maus.Core.Workshop;
using Maus.Core.Workshop.Benchmark;
using Maus.Core.Workshop.Memory;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Engine.Broker;

/// <summary>Atelier côté moteur : chaque opération vérifie elle-même ce qu'elle reçoit (voir <see cref="IMausEngine"/>).</summary>
public sealed partial class LocalMausEngine
{
    /// <summary>Sessions d'écoute de PresentMon que l'interface peut demander.</summary>
    private static readonly HashSet<string> FrameSessions = new(StringComparer.Ordinal) { "MAUS_Compteur", "MAUS_Releve" };

    /// <summary>Liste des processus et lecture de la mémoire. Ni la création du contexte ni une mesure des capteurs ne le tiennent.</summary>
    private readonly Lock _workshopGate = new();

    /// <summary>Création du contexte de l'Atelier, à part : ses requêtes WMI ne retiennent aucune autre opération.</summary>
    private readonly Lock _contextGate = new();

    /// <summary>Verrou des capteurs, à part : une mesure peut attendre le pilote PawnIO jusqu'à 1,5 s.</summary>
    private readonly Lock _sensorsGate = new();

    /// <summary>Verrou des mesures d'images, à part, pour la même raison.</summary>
    private readonly Lock _framesGate = new();
    private readonly Dictionary<string, PresentMonCapture> _frames = new(StringComparer.Ordinal);
    private AuditContext? _workshopContext;
    private ISensorSource? _sensors;
    private WindowsProcessSource? _processSource;
    private Task<MemoryDetailReport>? _memoryRead;

    /// <summary>Limite du processeur d'après le catalogue, lue une fois : ni le processeur ni le catalogue ne changent pendant la vie du moteur.</summary>
    private StrongBox<int?>? _cpuMax;

    /// <summary>Attente de la fin d'une mesure de latence à la fermeture : la session se referme dès l'arrêt demandé.</summary>
    private static readonly TimeSpan LatencyStopWait = TimeSpan.FromSeconds(5);
    private readonly CancellationTokenSource _latencyStop = new();
    private readonly List<Task> _latencyRuns = [];

    /// <summary>Fabrique de la mesure de latence : les tests remplacent la vraie trace du noyau, qui demande les droits administrateur.</summary>
    internal Func<IDriverLatencyTracer> LatencyTracer { get; init; } = () => new EtwDriverLatencyTracer();

    /// <summary>
    /// Processus de l'interface servie par ce moteur : jamais arrêté par « Arrêter le processus » (le moteur se protège déjà
    /// lui-même). 0 dans un moteur sans interface séparée.
    /// </summary>
    public int ProtectedProcessId { get; init; }

    private AuditContext WorkshopContext()
    {
        lock (_contextGate)
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

        if (ProtectDriverBackups() is { } refused)
        {
            throw new EngineException(refused);
        }

        // Une sauvegarde qu'un autre compte a pu modifier (ou qui contient un lien) n'est jamais proposée (0.8.6).
        var protector = new WindowsDirectoryProtector();
        var backups = new DirectoryInfo(root).EnumerateDirectories().ToList();
        var trusted = backups.Where(d => protector.IsTrustedTree(d.FullName) && d.EnumerateFiles("*.inf", SearchOption.AllDirectories).Any()).ToList();
        if (trusted.Count < backups.Count)
        {
            Breadcrumbs.Add("pilotes : " + (backups.Count - trusted.Count) + " dossier(s) de sauvegarde écarté(s)");
        }

        return [.. trusted.OrderByDescending(d => d.CreationTimeUtc).Select(d => d.Name)];
    }, cancellationToken);

    public Task<SensorReading> SampleSensorsAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var context = WorkshopContext();
        var (installed, version) = PawnIo.State(context.Registry);
        lock (_sensorsGate)
        {
            // Mesures sans pilote, complétées par PawnIO s'il est installé (le moteur a les droits administrateur).
            _sensors ??= installed && Environment.IsPrivilegedProcess
                ? new CombinedSensorSource(new WindowsSensorSource(), () => new LhmAdvancedSensors())
                : new WindowsSensorSource();
            var snapshot = _sensors.Sample();
            var combined = _sensors as CombinedSensorSource;
            _cpuMax ??= new StrongBox<int?>(context.Hardware.Cpu.Name is { Length: > 0 } name ? SafetyLimits.Load().ForCpu(name)?.MaxC : null);
            return new SensorReading(snapshot, new SensorState(installed, version, Environment.IsPrivilegedProcess, combined?.Failure, combined?.IsStalled ?? false, _cpuMax.Value));
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

        // Refusé d'avance (processus vital, service, autre session) : inutile de poser la question.
        if (ProcessRules.CanTerminate(sample, Environment.ProcessId, ProcessRules.CurrentSessionId) is { Allowed: false, Reason: var refused })
        {
            return refused;
        }

        if (!Confirmed(T("fermer un programme"), T("Fermer le programme {0} (processus {1}) ? Ce qui n'est pas enregistré dans ce programme sera perdu.", found.Name, pid)
                + (string.IsNullOrEmpty(found.Path) ? string.Empty : Environment.NewLine + T("Emplacement : {0}", found.Path)), cancellationToken))
        {
            return T("Fermeture annulée : le programme continue.");
        }

        try
        {
            WindowsProcessTerminator.Terminate(sample);
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
                    var slots = WindowsMemoryConfiguration.Read(new WmiCimReader());
                    var windows = slots.Select(s => s.ConfiguredMts).OfType<int>().DefaultIfEmpty().Max();
                    return PawnIoMemoryDetails.Read(cpu, WindowsMemoryConfiguration.MemoryTypeOf(slots), step => Breadcrumbs.Add("mémoire : " + step), windows > 0 ? windows : null);
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

        // Le moteur garde la main sur la mesure : à sa fermeture, il l'arrête et attend que la session du noyau soit refermée.
        var link = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _latencyStop.Token);
        var run = LatencyTracer().MeasureAsync(duration, link.Token);
        lock (_latencyRuns)
        {
            _latencyRuns.RemoveAll(r => r.IsCompleted);
            _latencyRuns.Add(run);
        }

        _ = run.ContinueWith(_ => link.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return run;
    }

    public Task<bool> StartFramesAsync(string session, TimeSpan? keep, CancellationToken cancellationToken) => Task.Run(() =>
    {
        CheckSession(session);
        PresentMonCapture? previous;
        lock (_framesGate)
        {
            _frames.Remove(session, out previous);
        }

        previous?.StopInBackground("PresentMon : mesure précédente remplacée");

        // Un arrêt encore en cours referme la session d'écoute par son nom : il ne doit pas couper la nouvelle mesure.
        PresentMonCapture.WaitForBackgroundStops(TimeSpan.FromSeconds(8));
        var capture = PresentMonCapture.TryStart(keep, session);
        if (capture is null)
        {
            return false;
        }

        // Deux démarrages en parallèle, ou le moteur qui se ferme entre-temps : aucune capture ne reste sans personne pour l'arrêter.
        PresentMonCapture? replaced = null;
        bool closed;
        lock (_framesGate)
        {
            closed = _framesClosed;
            if (!closed)
            {
                _frames.Remove(session, out replaced);
                _frames[session] = capture;
            }
        }

        replaced?.StopInBackground("PresentMon : mesure précédente remplacée");
        if (closed)
        {
            capture.StopInBackground("PresentMon arrêté avec le moteur");
        }

        return !closed;
    }, cancellationToken);

    public Task<FrameReading> ReadFramesAsync(string session, TimeSpan? last, TimeSpan? silentAfter, int minimumFrames, CancellationToken cancellationToken)
    {
        CheckSession(session);
        return Task.Run(() =>
        {
            lock (_framesGate)
            {
                return _frames.TryGetValue(session, out var capture)
                    ? new FrameReading(capture.Log.Summarize(last, silentAfter, Math.Clamp(minimumFrames, 2, FrameTimeLog.MinimumFrames)), capture.HasExited)
                    : new FrameReading(null, true);
            }
        }, cancellationToken);
    }

    public Task<FrameSummary?> StopFramesAsync(string session, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        CheckSession(session);
        PresentMonCapture? capture;
        lock (_framesGate)
        {
            _frames.Remove(session, out capture);
        }

        return capture is null ? null : await capture.StopTracked().ConfigureAwait(false);
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
        if (!SpaceAnalyzer.IsLocalRoot(root) || !Directory.Exists(root))
        {
            throw new EngineException(T("Dossier refusé."));
        }

        // Le résultat revient à une interface sans droits : le profil d'un autre compte n'est jamais lu pour elle, même
        // désigné par un lien ou un nom court (chemin réel demandé à Windows).
        var real = FinalPath.Of(root);
        if (real is null || !SpaceAnalyzer.IsLocalRoot(real))
        {
            throw new EngineException(T("Dossier refusé."));
        }

        var own = SessionUser.Sid();
        string[] others = UserProfiles.WithRealPaths([.. UserProfiles.OfOthers(new WindowsRegistryReader(), own), .. SpaceAnalyzer.OtherAccountFolders(Path.GetPathRoot(real)!, own)]);
        if (SpaceAnalyzer.IsUnder(real, others))
        {
            throw new EngineException(T("Ce dossier appartient à un autre compte : MAUS ne le lit pas."));
        }

        return SpaceAnalyzer.ScanAsync(real, progress, others, cancellationToken);
    }

    public Task<ToolOutcome> RunToolAsync(ToolRequest request, IProgress<string>? progress, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        ArgumentNullException.ThrowIfNull(request);
        void Line(string text) => progress?.Report(text);
        Task<int> Run(string file, IReadOnlyList<string> arguments, System.Text.Encoding encoding) =>
            SystemTool.RunAsync(SystemTool.PathOf(file), arguments, encoding, Line, cancellationToken);
        Task<int> Pnputil(IReadOnlyList<string> arguments) => Run("pnputil.exe", arguments, OemEncoding.Ansi);

        try
        {
            switch (request.Kind)
            {
                case ToolKind.Repair:
                    return new ToolOutcome(true, await RepairConsole.RunAsync(Run, Line).ConfigureAwait(false));
                case ToolKind.DriverAction:
                {
                    // Une action hors de la liste fermée est refusée avant toute lecture et toute question.
                    if (request.Action is { } asked && !Enum.IsDefined(asked))
                    {
                        throw new EngineException(T("Tâche refusée."));
                    }

                    var drivers = (await ReadDriversAsync(cancellationToken).ConfigureAwait(false)).Drivers;
                    if (request.Action is not { } action || drivers.FirstOrDefault(d => d.InstanceId == request.InstanceId && d.Group == DriverGroup.Graphics) is not { } gpu)
                    {
                        return new ToolOutcome(false, T("Carte graphique introuvable : actualisez la liste des pilotes."));
                    }

                    // Avant la question : répondre « Oui » pour apprendre ensuite que c'est impossible ne sert à rien.
                    var folder = GraphicsDriverActions.BackupFolder(gpu.InfName ?? "pilote", DateTimeOffset.Now);
                    if (!GraphicsDriverActions.CanRun(action, gpu, folder))
                    {
                        return new ToolOutcome(false, T("Action impossible sur ce pilote."));
                    }

                    if (action is GraphicsDriverAction.Backup or GraphicsDriverAction.Remove && ProtectDriverBackups() is { } unsafeFolder)
                    {
                        return new ToolOutcome(false, unsafeFolder);
                    }

                    // Chaque action sur le pilote est confirmée par le moteur lui-même (0.8.5 : redémarrage et sauvegarde compris).
                    var question = action switch
                    {
                        GraphicsDriverAction.Remove => T("Sauvegarder puis supprimer le pilote de {0} ? L'écran peut devenir noir quelques secondes.", gpu.Device),
                        GraphicsDriverAction.Reinstall => T("Retirer {0} puis la laisser redétecter par Windows ? L'écran peut devenir noir quelques secondes.", gpu.Device),
                        GraphicsDriverAction.Restart => T("Redémarrer {0} ? L'écran peut devenir noir quelques secondes.", gpu.Device),
                        GraphicsDriverAction.Backup => T("Sauvegarder le pilote de {0} dans le dossier protégé de MAUS ?", gpu.Device),
                        _ => throw new EngineException(T("Tâche refusée.")),
                    };
                    if (!Confirmed(T("pilote graphique"), question, cancellationToken))
                    {
                        return new ToolOutcome(false, T("Action annulée : rien n'a changé."));
                    }

                    if (action is not (GraphicsDriverAction.Backup or GraphicsDriverAction.Remove))
                    {
                        return new ToolOutcome(true, await GraphicsDriverActions.RunAsync(action, gpu, folder, Pnputil, Line).ConfigureAwait(false));
                    }

                    Directory.CreateDirectory(folder);
                    var done = await GraphicsDriverActions.RunAsync(action, gpu, folder, Pnputil, Line).ConfigureAwait(false);
                    return new ToolOutcome(true, ReserveBackup(folder) ? done
                        : done + Environment.NewLine + T("Cette sauvegarde n'a pas pu être réservée aux administrateurs : MAUS ne la proposera pas à la réinstallation."));
                }

                case ToolKind.DriverRestore:
                {
                    if (ProtectDriverBackups() is { } unsafeRoot)
                    {
                        return new ToolOutcome(false, unsafeRoot);
                    }

                    var name = request.Backup ?? string.Empty;
                    var full = Path.Combine(GraphicsDriverActions.BackupRoot, name);
                    var named = IsBackupName(name) && string.Equals(Path.GetFileName(Path.GetFullPath(full)), name, StringComparison.Ordinal)
                        && Directory.Exists(full) && GraphicsDriverActions.CanRestore(full);
                    if (!named)
                    {
                        return new ToolOutcome(false, T("Choisissez un dossier de sauvegarde créé par MAUS (il contient un fichier .inf)."));
                    }

                    // Contrôlée avant d'y chercher le moindre fichier : pnputil n'installe que ce qu'aucun autre compte n'a pu changer (0.8.6).
                    if (!new WindowsDirectoryProtector().IsTrustedTree(full))
                    {
                        return new ToolOutcome(false, T("Cette sauvegarde n'est pas réservée aux administrateurs (un autre compte peut la modifier, ou elle contient un lien) : MAUS refuse de la réinstaller."));
                    }

                    if (!Directory.EnumerateFiles(full, "*.inf", SearchOption.AllDirectories).Any())
                    {
                        return new ToolOutcome(false, T("Choisissez un dossier de sauvegarde créé par MAUS (il contient un fichier .inf)."));
                    }

                    if (!Confirmed(T("pilote graphique"), T("Réinstaller le pilote sauvegardé par MAUS dans {0} ? L'écran peut devenir noir quelques secondes.", name), cancellationToken))
                    {
                        return new ToolOutcome(false, T("Action annulée : rien n'a changé."));
                    }

                    return new ToolOutcome(true, await GraphicsDriverActions.RestoreAsync(full, Pnputil, Line).ConfigureAwait(false));
                }

                default:
                    throw new EngineException(T("Tâche refusée."));
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ToolOutcome(false, T("Le programme de Windows n'a pas pu démarrer : {0}", ex.Message));
        }
    }, cancellationToken);

    /// <summary>
    /// Nom d'un dossier de sauvegarde : un seul élément de chemin, jamais une remontée ni un sous-dossier. Windows retire les points
    /// et les espaces de fin d'un nom : un nom qui en a ne désigne pas ce qu'il dit.
    /// </summary>
    internal static bool IsBackupName(string name) =>
        name.Length is > 0 and <= 200 && name.IndexOfAny([.. Path.GetInvalidFileNameChars(), '\\', '/']) < 0 && !name.EndsWith('.') && !name.EndsWith(' ');

    /// <summary>
    /// Dossier des sauvegardes de pilotes réservé à SYSTEM et aux Administrateurs, comme le journal (ACL protégée, lien
    /// refusé) : pnputil n'exporte ni ne réinstalle jamais dans un dossier qu'un utilisateur standard pourrait modifier.
    /// </summary>
    /// <returns>Le message à afficher si le dossier ne peut pas être protégé, sinon <c>null</c>.</returns>
    private static string? ProtectDriverBackups()
    {
        try
        {
            new WindowsDirectoryProtector().EnsureProtected(GraphicsDriverActions.BackupRoot);
            return null;
        }
        catch (JournalUnsafeException ex)
        {
            return T("Le dossier des sauvegardes de pilotes ({0}) ne peut pas être protégé : {1}", GraphicsDriverActions.BackupRoot, ex.Message);
        }
    }

    /// <summary>
    /// Sauvegarde neuve donnée aux Administrateurs, comme les fichiers du journal : Windows peut donner ce que pnputil crée au
    /// compte qui l'a lancé, et une telle sauvegarde serait ensuite écartée.
    /// </summary>
    /// <returns>Vrai si la sauvegarde passe le contrôle fait avant de la lister ou de la réinstaller.</returns>
    private static bool ReserveBackup(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return true;
        }

        var protector = new WindowsDirectoryProtector();
        try
        {
            protector.ProtectTree(folder);
        }
        catch (JournalUnsafeException)
        {
            Breadcrumbs.Add("pilotes : sauvegarde non réservée aux administrateurs");
        }

        return protector.IsTrustedTree(folder);
    }

    public Task<RestorePointOutcome> CreateDriverRestorePointAsync(CancellationToken cancellationToken) =>
        Task.Run(() => CreatePoint(T("MAUS : avant la suppression du pilote graphique")), cancellationToken);

    /// <summary>Espace des points de restauration : classe WMI Win32_ShadowStorage (UsedSpace, MaxSpace, en octets).</summary>
    internal const string ShadowStorageQuery = "SELECT UsedSpace, MaxSpace FROM Win32_ShadowStorage";

    public Task<RestorePointsReading> ReadRestorePointsAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var context = WorkshopContext();
        var restore = _createFixContext(context).SystemRestore;
        var drive = RestorePointCreator.SystemDrive;
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

    public Task<RestorePointOutcome> CreateRestorePointAsync(CancellationToken cancellationToken) =>
        Task.Run(() => CreatePoint(T("MAUS : point créé à la main")), cancellationToken);

    /// <summary>Le <c>try</c> couvre aussi la préparation du contexte, qui peut lever hors de la création du point.</summary>
    private RestorePointOutcome CreatePoint(string description)
    {
        try
        {
            var fix = _createFixContext(WorkshopContext());
            return new RestorePointCreator(fix.SystemRestore, fix.Settings, memo: fix.RestoreFrequencyMemo).Create(description, enableProtectionIfNeeded: false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new RestorePointOutcome(RestorePointStatus.Failed, null, T("Le point de restauration n'a pas pu être créé : {0}", ex.Message));
        }
    }

    /// <summary>
    /// Lit la tâche, et rien d'autre, dans le moteur comme sans droits : cette lecture se fait sans que l'utilisateur ait rien
    /// demandé (ouverture de MAUS, page Suivi, fin d'un audit), elle ne pose donc aucune question et ne recrée rien. Remettre la
    /// tâche en place passe par <see cref="SetScheduleAsync"/>, après le clic de l'utilisateur.
    /// </summary>
    public async Task<ScheduleState> GetScheduleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ScheduledAudit.StateOf(await TaskSchedulerClient.GetAsync().ConfigureAwait(false), TaskSchedulerClient.AccountsOfThisSession());
    }

    public async Task<string?> SetScheduleAsync(DayOfWeek? day, CancellationToken cancellationToken)
    {
        if (day is not { } chosen)
        {
            return await TaskSchedulerClient.DeleteAsync().ConfigureAwait(false);
        }

        if (!Enum.IsDefined(chosen))
        {
            // Le canal transporte l'énumération par son nom ; un nombre hors définition n'a rien à faire dans une tâche.
            throw new ArgumentOutOfRangeException(nameof(day), day, "Jour inconnu.");
        }

        return await TaskSchedulerClient.CreateAsync(chosen).ConfigureAwait(false);
    }

    public async Task<BenchmarkOutcome> RunBenchmarkAsync(BenchmarkLaunch launch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (launch.Api is not ("d3d11" or "d3d12") || launch.Resolution is not ("720p" or "1080p" or "1440p" or "4k")
            || launch.Scope is not ("all" or "gpu" or "cpu") || launch.Language is not ("fr" or "en" or "es") || Environment.ProcessPath is not { } exe)
        {
            throw new EngineException(T("Réglages du benchmark refusés."));
        }

        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = SystemTool.WorkingFolder };
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

        if (!Environment.IsPrivilegedProcess)
        {
            // Sans droits administrateur, le benchmark garde lui-même sa passe dans le profil, comme avant.
            return new BenchmarkOutcome(await RunToEndAsync(start, cancellationToken).ConfigureAwait(false));
        }

        // Avec ces droits, il n'écrit rien dans le profil (0.8.6) : sa passe et son image passent par un dossier neuf réservé à
        // SYSTEM et aux Administrateurs, relues et vérifiées ici, puis rendues à la fenêtre, qui les garde sans droits.
        string folder;
        try
        {
            folder = await Task.Run(BenchmarkOutput.CreateFolder, cancellationToken).ConfigureAwait(false);
        }
        catch (JournalUnsafeException ex)
        {
            throw new EngineException(ex.Message, ex);
        }

        try
        {
            start.ArgumentList.Add("--output");
            start.ArgumentList.Add(folder);
            var exitCode = await RunToEndAsync(start, cancellationToken).ConfigureAwait(false);
            return await Task.Run(() => BenchmarkOutput.Read(folder, exitCode), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            BenchmarkOutput.Delete(folder);
        }
    }

    private static async Task<int> RunToEndAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        using var process = Process.Start(start) ?? throw new EngineException(T("Le benchmark n'a pas pu démarrer."));
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>Fermeture du moteur : mesure de latence, capteurs, liste des processus et mesures PresentMon rendus à Windows.</summary>
    /// <summary>Le moteur se ferme : une capture qui finit de démarrer est arrêtée aussitôt.</summary>
    private bool _framesClosed;

    private void DisposeWorkshop()
    {
        StopLatencyRuns();
        List<PresentMonCapture> captures;
        lock (_framesGate)
        {
            _framesClosed = true;
            captures = [.. _frames.Values];
            _frames.Clear();
        }

        lock (_sensorsGate)
        {
            _sensors?.Dispose();
            _sensors = null;
        }

        lock (_workshopGate)
        {
            _processSource?.Dispose();
            _processSource = null;
        }

        foreach (var capture in captures)
        {
            capture.StopInBackground("PresentMon arrêté avec le moteur");
        }

        PresentMonCapture.WaitForBackgroundStops(TimeSpan.FromSeconds(8));
    }

    /// <summary>Une mesure de latence en cours s'arrête tout de suite : sa session du noyau ne doit pas survivre au moteur.</summary>
    private void StopLatencyRuns()
    {
        Task[] runs;
        lock (_latencyRuns)
        {
            runs = [.. _latencyRuns];
            _latencyRuns.Clear();
        }

        _latencyStop.Cancel();
        try
        {
            if (!Task.WaitAll(runs, LatencyStopWait))
            {
                Breadcrumbs.Add("latence : mesure pas encore finie à la fermeture du moteur");
            }
        }
        catch (AggregateException)
        {
            // Une mesure en erreur ou annulée n'empêche pas la fermeture : son résultat n'attend plus personne.
        }
    }
}
