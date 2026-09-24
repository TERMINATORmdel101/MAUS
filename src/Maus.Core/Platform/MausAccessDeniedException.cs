namespace Maus.Core.Platform;

/// <summary>Lecture refusée faute de droits suffisants. Les modules la traduisent en constat « indéterminé ».</summary>
public sealed class MausAccessDeniedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Source de données absente sur ce PC (classe WMI, espace de noms ou commande indisponible).</summary>
public sealed class DataSourceUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
