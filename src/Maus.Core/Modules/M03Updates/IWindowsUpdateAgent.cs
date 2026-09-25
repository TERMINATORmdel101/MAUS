namespace Maus.Core.Modules.M03Updates;

/// <summary>Une mise à jour proposée par Windows Update et pas encore installée.</summary>
internal sealed record PendingUpdate(
    string UpdateId,
    string Title,
    IReadOnlyList<string> KbArticleIds,
    IReadOnlyList<string> CategoryIds,
    string? MsrcSeverity,
    bool BrowseOnly,
    bool IsDriver = false);

/// <summary>Dates des dernières opérations réussies du client Windows Update, en temps universel.</summary>
internal sealed record AutomaticUpdatesResults(DateTime? LastSearchSuccess, DateTime? LastInstallationSuccess);

/// <summary>
/// Lecture de l'agent Windows Update (API COM <c>Microsoft.Update.*</c>). Aucune méthode ne télécharge ni n'installe :
/// la recherche interroge seulement le service de mises à jour configuré.
/// </summary>
internal interface IWindowsUpdateAgent
{
    /// <summary>Résultats du client automatique (<c>Microsoft.Update.AutoUpdate</c>), ou <c>null</c> si indisponibles.</summary>
    AutomaticUpdatesResults? GetAutomaticUpdatesResults();

    /// <summary>
    /// Recherche des mises à jour selon un critère WUA. Peut durer plusieurs minutes.
    /// </summary>
    /// <exception cref="TimeoutException">La recherche a dépassé <paramref name="timeout"/>.</exception>
    /// <exception cref="System.Runtime.InteropServices.COMException">Erreur de l'agent (HRESULT dans <c>HResult</c>).</exception>
    /// <exception cref="InvalidOperationException">Recherche échouée ou abandonnée sans code d'erreur.</exception>
    Task<IReadOnlyList<PendingUpdate>> SearchAsync(string criteria, TimeSpan timeout, CancellationToken cancellationToken);
}
