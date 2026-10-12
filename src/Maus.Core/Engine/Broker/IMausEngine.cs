using Maus.Core.Fixes;
using Maus.Core.Hardware;
using Maus.Core.Platform;
using Maus.Core.Preferences;

namespace Maus.Core.Engine.Broker;

/// <summary>
/// Ce que l'interface sait du PC après un audit, sans accès direct au système : version de Windows, profil matériel,
/// droits du moteur, raison d'un blocage des corrections, choix de l'utilisateur.
/// </summary>
public sealed record EngineSnapshot(
    DateTimeOffset Now,
    WindowsInfo Windows,
    HardwareProfile Hardware,
    bool IsElevated,
    bool ElevatedAsAnotherUser,
    string? BlockingReason,
    UserPreferences Preferences);

/// <summary>Un audit complet : ce que l'interface sait du PC et les résultats de chaque module.</summary>
public sealed record AuditRun(EngineSnapshot Snapshot, IReadOnlyList<ModuleResult> Results);

/// <summary>
/// Le moteur de MAUS (demande du 12/10/2026, version 0.8.0 : séparer la partie administrateur de l'interface). Tout ce qui
/// lit le système en profondeur ou le modifie passe par lui, et seulement par cette liste fermée d'opérations :
/// l'interface ne lui envoie jamais d'écriture ni de commande toute faite, seulement des identifiants (corrections, séances)
/// qu'il vérifie lui-même d'après son propre audit.
/// </summary>
public interface IMausEngine : IDisposable
{
    /// <summary>Audit complet ; le moteur garde ses résultats, qui servent seuls à préparer les corrections.</summary>
    Task<AuditRun> RunAuditAsync(IProgress<ModuleResult>? progress, CancellationToken cancellationToken);

    /// <summary>Relance un seul module (après un choix de l'utilisateur) ; <c>null</c> s'il est inconnu ou sans audit.</summary>
    Task<ModuleResult?> RunModuleAsync(string moduleId, CancellationToken cancellationToken);

    /// <summary>Corrections proposées d'après le dernier audit du moteur.</summary>
    Task<IReadOnlyList<PlannedChange>> PlanAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Applique les corrections choisies, désignées par leur identifiant : le moteur les reprend dans son propre plan, il
    /// n'écrit jamais une valeur venue de l'interface.
    /// </summary>
    Task<ApplyResult> ApplyAsync(IReadOnlyList<string> changeIds, ApplyOptions options, CancellationToken cancellationToken);

    Task RecordVerificationAsync(string sessionId, IReadOnlyList<VerifiedOutcome> verified, CancellationToken cancellationToken);

    Task<RevertResult> RevertAsync(string sessionId, string? changeId, bool force, CancellationToken cancellationToken);

    Task<JournalListing> BrowseJournalAsync(CancellationToken cancellationToken);

    Task<JournalSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Choix de l'utilisateur, et l'explication si leur fichier n'a pas pu être lu normalement.</summary>
    Task<PreferencesState> LoadPreferencesAsync(CancellationToken cancellationToken);

    /// <summary>Enregistre les choix ; renvoie ceux qui valent désormais (gardés en mémoire si le fichier est illisible).</summary>
    Task<PreferencesState> SavePreferencesAsync(UserPreferences preferences, CancellationToken cancellationToken);
}

/// <summary>Choix de l'utilisateur et explication éventuelle sur leur fichier.</summary>
/// <param name="FileState">Résultat de la lecture du fichier (absent, lu, illisible…).</param>
/// <param name="NotSaved">Le choix n'a pas pu être écrit (fichier existant illisible) : il vaut jusqu'à la fermeture.</param>
public sealed record PreferencesState(UserPreferences Preferences, PreferencesFileState FileState, string? Notice, string? NotSaved = null);
