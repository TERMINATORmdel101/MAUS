namespace Maus.Core.Platform;

/// <summary>Lecture refusée faute de droits suffisants. Les modules la traduisent en constat « indéterminé ».</summary>
public sealed class MausAccessDeniedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Source de données absente sur ce PC (classe WMI, espace de noms ou commande indisponible), ou qui ne répond pas.</summary>
public sealed class DataSourceUnavailableException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>
    /// Vrai seulement si Windows a répondu que la source n'existe pas (espace de noms ou classe WMI absent) ; faux s'il ne répond pas
    /// (délai, service arrêté, fournisseur en panne) ou si rien ne permet de le dire.
    /// </summary>
    public bool IsAbsent { get; init; }
}
