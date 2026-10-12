using Maus.Core.Fixes;
using Maus.Core.Modules.M20Software;
using Maus.Core.Workshop;
using Maus.Core.Workshop.Memory;

namespace Maus.Core.Engine.Broker;

/// <summary>Fiche du matériel lue par le moteur : composants, détails du PC, puces SPD des barrettes (si PawnIO).</summary>
/// <param name="SpdRead">Une lecture SPD a été tentée (PawnIO installé, moteur administrateur).</param>
public sealed record InventoryReading(HardwareInventory Inventory, MachineDetails? Details, IReadOnlyList<SpdModule>? Spd, bool SpdRead);

/// <summary>État du pilote PawnIO et des capteurs avancés, vu par le moteur.</summary>
/// <param name="Failure">Le pilote n'a pas pu s'ouvrir (message), ou <c>null</c>.</param>
/// <param name="IsStalled">Le pilote ne répond plus depuis quelques secondes.</param>
/// <param name="CpuMaxC">Limite du processeur d'après le catalogue des seuils (modèles vérifiés), si connue.</param>
public sealed record SensorState(bool PawnIoInstalled, string? PawnIoVersion, bool EngineElevated, string? Failure, bool IsStalled, int? CpuMaxC);

/// <summary>Une mesure des capteurs et l'état du pilote.</summary>
public sealed record SensorReading(SensorSnapshot Snapshot, SensorState State);

/// <summary>Processus lus par le moteur (tous les comptes, chemins complets).</summary>
public sealed record ProcessReading(IReadOnlyList<RawProcess> Processes, IReadOnlyDictionary<int, double> GpuPercent);

/// <summary>Fenêtres de commande visibles que le moteur sait ouvrir ; il construit lui-même chaque ligne de commande.</summary>
public enum ConsoleKind
{
    /// <summary>DISM /RestoreHealth puis SFC /scannow.</summary>
    Repair,

    /// <summary>winget upgrade des logiciels choisis (identifiants vérifiés).</summary>
    WingetUpgrade,

    PawnIoInstall,

    PawnIoUninstall,

    /// <summary>Action pnputil sur le pilote de la carte graphique désignée par son identifiant d'instance.</summary>
    DriverAction,

    /// <summary>Réinstallation d'une sauvegarde de pilote faite par MAUS, désignée par le nom de son dossier.</summary>
    DriverRestore,
}

public sealed record ConsoleRequest(
    ConsoleKind Kind,
    IReadOnlyList<string>? PackageIds = null,
    GraphicsDriverAction? Action = null,
    string? InstanceId = null,
    string? Backup = null);

/// <summary>Fenêtre ouverte ou non, et ce que l'utilisateur doit savoir (dossier de sauvegarde utilisé, outil absent…).</summary>
public sealed record ConsoleOutcome(bool Opened, string? Message = null);

/// <summary>Tâche de l'audit de la semaine : jour, programme lancé, et pourquoi elle ne peut pas être créée d'ici.</summary>
public sealed record ScheduleState(DayOfWeek? Day, string? Command, string? Refusal, string? CommandRefusal);

/// <summary>Logiciels à mettre à jour par winget.</summary>
public sealed record SoftwareListing(bool WingetFound, IReadOnlyList<SoftwareUpdate> Updates);

/// <summary>Réglages d'une passe du benchmark choisis dans la page : valeurs vérifiées par le moteur.</summary>
public sealed record BenchmarkLaunch(string Api, string Resolution, string Scope, bool RayTracing, string Language);

/// <summary>Images par seconde d'une mesure en cours, et PresentMon arrêté de lui-même.</summary>
public sealed record FrameReading(FrameSummary? Summary, bool Exited);

/// <summary>Atelier : lectures profondes et actions qui demandent les droits administrateur, toutes faites par le moteur.</summary>
public partial interface IMausEngine
{
    Task<InventoryReading> ReadInventoryAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<DriverEntry>> ReadDriversAsync(CancellationToken cancellationToken);

    /// <summary>Noms des dossiers de sauvegarde de pilotes faits par MAUS (dossier réservé aux administrateurs), du plus récent au plus ancien.</summary>
    Task<IReadOnlyList<string>> DriverBackupsAsync(CancellationToken cancellationToken);

    Task<SensorReading> SampleSensorsAsync(CancellationToken cancellationToken);

    Task<ProcessReading> ReadProcessesAsync(CancellationToken cancellationToken);

    /// <summary>Arrête un processus ; le moteur relit lui-même son nom et son heure de lancement. Renvoie la raison d'un refus.</summary>
    Task<string?> TerminateProcessAsync(int pid, DateTime? startTime, CancellationToken cancellationToken);

    Task<MemoryDetailReport> ReadMemoryDetailsAsync(CancellationToken cancellationToken);

    Task<DriverLatencyResult> MeasureLatencyAsync(TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Démarre PresentMon (session « MAUS_Compteur » ou « MAUS_Releve ») ; faux s'il est absent ou modifié.</summary>
    Task<bool> StartFramesAsync(string session, TimeSpan? keep, CancellationToken cancellationToken);

    Task<FrameReading> ReadFramesAsync(string session, TimeSpan? last, TimeSpan? silentAfter, int minimumFrames, CancellationToken cancellationToken);

    /// <summary>Arrête PresentMon, referme sa session d'écoute et renvoie le bilan de toute la mesure.</summary>
    Task<FrameSummary?> StopFramesAsync(string session, CancellationToken cancellationToken);

    Task<SpaceReport> ScanSpaceAsync(string root, IProgress<long>? progress, CancellationToken cancellationToken);

    Task<ConsoleOutcome> OpenConsoleAsync(ConsoleRequest request, CancellationToken cancellationToken);

    /// <summary>Point de restauration avant la suppression d'un pilote graphique, créé puis relu.</summary>
    Task<RestorePointOutcome> CreateDriverRestorePointAsync(CancellationToken cancellationToken);

    Task<ScheduleState> GetScheduleAsync(CancellationToken cancellationToken);

    /// <summary>Crée (jour donné) ou retire (<c>null</c>) la tâche de l'audit de la semaine ; renvoie l'erreur, ou <c>null</c>.</summary>
    Task<string?> SetScheduleAsync(DayOfWeek? day, CancellationToken cancellationToken);

    Task<SoftwareListing> ListSoftwareUpdatesAsync(CancellationToken cancellationToken);

    /// <summary>Lance le benchmark dans sa fenêtre plein écran et attend sa fin ; renvoie son code de sortie.</summary>
    Task<int> RunBenchmarkAsync(BenchmarkLaunch launch, CancellationToken cancellationToken);
}
