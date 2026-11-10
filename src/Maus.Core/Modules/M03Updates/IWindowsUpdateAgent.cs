namespace Maus.Core.Modules.M03Updates;

/// <summary>Une mise à jour proposée par Windows Update et pas encore installée.</summary>
internal sealed record PendingUpdate(
    string UpdateId,
    string Title,
    IReadOnlyList<string> CategoryIds,
    string? MsrcSeverity,
    bool BrowseOnly,
    bool IsDriver = false);

/// <summary>Date de la dernière recherche réussie du client Windows Update, en temps universel.</summary>
internal sealed record AutomaticUpdatesResults(DateTime? LastSearchSuccess);

/// <summary>Une ligne de l'historique de Windows Update (IUpdateHistoryEntry, Microsoft Learn), telle que Windows l'a notée.</summary>
/// <param name="UpdateId">Identifiant de la mise à jour, le titre s'il manque.</param>
/// <param name="Title">Titre noté par Windows.</param>
/// <param name="DateUtc">Date de l'opération, en temps universel.</param>
/// <param name="Operation">1 = installation, 2 = désinstallation (<c>UpdateOperation</c>).</param>
/// <param name="ResultCode"><c>OperationResultCode</c> : 2 = réussie, 3 = réussie avec des erreurs, 4 = échec, 5 = abandonnée.</param>
/// <param name="Code">Code d'erreur noté par Windows (<c>HResult</c>, sinon <c>UnmappedResultCode</c>).</param>
/// <param name="ClientApplicationId">Programme qui a demandé l'opération.</param>
/// <param name="ServiceId">Service de mises à jour d'où elle vient.</param>
internal sealed record UpdateHistoryEntry(
    string UpdateId,
    string Title,
    DateTime DateUtc,
    int Operation,
    int ResultCode,
    int Code,
    string? ClientApplicationId,
    string? ServiceId);

/// <summary>
/// Lecture de l'agent Windows Update (API COM <c>Microsoft.Update.*</c>). Aucune méthode ne télécharge ni n'installe :
/// la recherche interroge seulement le service de mises à jour configuré.
/// </summary>
internal interface IWindowsUpdateAgent
{
    /// <summary>Résultats du client automatique (<c>Microsoft.Update.AutoUpdate</c>), ou <c>null</c> si indisponibles.</summary>
    AutomaticUpdatesResults? GetAutomaticUpdatesResults();

    /// <summary>
    /// Les <paramref name="maxEntries"/> dernières lignes de l'historique local (<c>IUpdateSearcher::QueryHistory</c>), ou
    /// <c>null</c> si Windows ne les donne pas. Aucun accès réseau, aucun droit administrateur (vérifié le 2/11/2026).
    /// </summary>
    IReadOnlyList<UpdateHistoryEntry>? GetHistory(int maxEntries);

    /// <summary>
    /// Recherche des mises à jour selon un critère WUA. Peut durer plusieurs minutes.
    /// </summary>
    /// <param name="criteria">Critère WUA.</param>
    /// <param name="online">
    /// <c>false</c> : aucun accès réseau, l'agent répond d'après sa dernière recherche (Microsoft Learn, « IUpdateSearcher::Online »).
    /// </param>
    /// <exception cref="TimeoutException">La recherche a dépassé <paramref name="timeout"/>.</exception>
    /// <exception cref="System.Runtime.InteropServices.COMException">Erreur de l'agent (HRESULT dans <c>HResult</c>).</exception>
    /// <exception cref="InvalidOperationException">Recherche échouée ou abandonnée sans code d'erreur.</exception>
    Task<IReadOnlyList<PendingUpdate>> SearchAsync(string criteria, bool online, TimeSpan timeout, CancellationToken cancellationToken);
}
