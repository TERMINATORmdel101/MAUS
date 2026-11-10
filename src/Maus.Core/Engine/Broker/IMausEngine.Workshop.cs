using Maus.Core.Fixes;
using Maus.Core.Workshop;
using Maus.Core.Workshop.Benchmark;
using Maus.Core.Workshop.Memory;

namespace Maus.Core.Engine.Broker;

/// <summary>Fiche du matériel lue par le moteur : composants, détails du PC, puces SPD des barrettes (si PawnIO).</summary>
/// <param name="SpdRead">Une lecture SPD a été tentée (PawnIO installé, moteur administrateur).</param>
public sealed record InventoryReading(HardwareInventory Inventory, MachineDetails? Details, IReadOnlyList<SpdModule>? Spd, bool SpdRead);

/// <summary>Pilotes installés et profil matériel (pour relier chaque carte graphique à la page de son fabricant).</summary>
public sealed record DriversReading(IReadOnlyList<DriverEntry> Drivers, Hardware.HardwareProfile Hardware);

/// <summary>État du pilote PawnIO et des capteurs avancés, vu par le moteur.</summary>
/// <param name="Failure">Le pilote n'a pas pu s'ouvrir (message), ou <c>null</c>.</param>
/// <param name="IsStalled">Le pilote ne répond plus depuis quelques secondes.</param>
/// <param name="CpuMaxC">Limite du processeur d'après le catalogue des seuils (modèles vérifiés), si connue.</param>
/// <summary>Points de restauration de Windows : protection du lecteur système, points (du plus ancien au plus récent), espace utilisé.</summary>
public sealed record RestorePointsReading(ProtectionState Protection, IReadOnlyList<RestorePointInfo> Points, long? UsedBytes, long? MaxBytes);

public sealed record SensorState(bool PawnIoInstalled, string? PawnIoVersion, bool EngineElevated, string? Failure, bool IsStalled, int? CpuMaxC);

/// <summary>Une mesure des capteurs et l'état du pilote.</summary>
public sealed record SensorReading(SensorSnapshot Snapshot, SensorState State);

/// <summary>Processus lus par le moteur (tous les comptes, chemins complets).</summary>
public sealed record ProcessReading(IReadOnlyList<RawProcess> Processes, IReadOnlyDictionary<int, double> GpuPercent);

/// <summary>Tâches que le moteur sait lancer, sans cmd.exe ; il construit lui-même chaque liste d'arguments.</summary>
public enum ToolKind
{
    /// <summary>DISM /RestoreHealth puis SFC /scannow.</summary>
    Repair,

    /// <summary>Action pnputil sur le pilote de la carte graphique désignée par son identifiant d'instance.</summary>
    DriverAction,

    /// <summary>Réinstallation d'une sauvegarde de pilote faite par MAUS, désignée par le nom de son dossier.</summary>
    DriverRestore,
}

public sealed record ToolRequest(
    ToolKind Kind,
    GraphicsDriverAction? Action = null,
    string? InstanceId = null,
    string? Backup = null);

/// <summary>Tâche lancée ou non, et le résultat à montrer à l'utilisateur.</summary>
public sealed record ToolOutcome(bool Ran, string? Message = null);

/// <summary>Tâche de l'audit de la semaine : jour, programme lancé, et ce que l'interface doit en dire.</summary>
/// <param name="OfAnotherAccount">La tâche n'est pas celle que MAUS a posée pour ce compte : la remplacer ne se fait pas sans le dire.</param>
/// <param name="KeepsOldRights">Tâche de ce compte, posée par une version précédente qui lui donnait les droits administrateur : l'interface propose de la recréer sans eux.</param>
public sealed record ScheduleState(DayOfWeek? Day, string? Command, bool OfAnotherAccount = false, bool KeepsOldRights = false);

/// <summary>Réglages d'une passe du benchmark choisis dans la page : valeurs vérifiées par le moteur.</summary>
public sealed record BenchmarkLaunch(string Api, string Resolution, string Scope, bool RayTracing, string Language);

/// <summary>Images par seconde d'une mesure en cours, et PresentMon arrêté de lui-même.</summary>
public sealed record FrameReading(FrameSummary? Summary, bool Exited);

/// <summary>Atelier : lectures profondes et actions qui demandent les droits administrateur, toutes faites par le moteur.</summary>
public partial interface IMausEngine
{
    Task<InventoryReading> ReadInventoryAsync(CancellationToken cancellationToken);

    Task<DriversReading> ReadDriversAsync(CancellationToken cancellationToken);

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

    /// <param name="progress">Reçoit chaque ligne écrite par l'outil.</param>
    Task<ToolOutcome> RunToolAsync(ToolRequest request, IProgress<string>? progress, CancellationToken cancellationToken);

    /// <summary>Point de restauration avant la suppression d'un pilote graphique, créé puis relu.</summary>
    Task<RestorePointOutcome> CreateDriverRestorePointAsync(CancellationToken cancellationToken);

    /// <summary>Points de restauration de Windows et espace qu'ils occupent (lecture seule).</summary>
    Task<RestorePointsReading> ReadRestorePointsAsync(CancellationToken cancellationToken);

    /// <summary>Point de restauration demandé par l'utilisateur, créé puis relu (comme avant une correction).</summary>
    Task<RestorePointOutcome> CreateRestorePointAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Referme tout de suite les droits administrateur : le moteur s'arrête. Renvoie <c>false</c> si rien n'est à refermer, ou
    /// si une autre demande est en cours (elle va jusqu'au bout). Sans moteur à part, rien à faire.
    /// </summary>
    Task<bool> CloseRightsAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    Task<ScheduleState> GetScheduleAsync(CancellationToken cancellationToken);

    /// <summary>Crée (jour donné) ou retire (<c>null</c>) la tâche de l'audit de la semaine ; renvoie l'erreur, ou <c>null</c>.</summary>
    Task<string?> SetScheduleAsync(DayOfWeek? day, CancellationToken cancellationToken);

    /// <summary>
    /// Lance le benchmark dans sa fenêtre plein écran et attend sa fin. Sans droits administrateur, le benchmark garde lui-même
    /// sa passe dans le profil, et seul son code de sortie revient. Avec ces droits (moteur), il n'écrit rien dans le profil :
    /// la passe et son image reviennent à la fenêtre, qui les garde elle-même, sans droits (0.8.6).
    /// </summary>
    Task<BenchmarkOutcome> RunBenchmarkAsync(BenchmarkLaunch launch, CancellationToken cancellationToken);
}
