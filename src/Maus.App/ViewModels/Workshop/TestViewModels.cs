namespace Maus.App.ViewModels.Workshop;

/// <summary>Une durée ou une taille proposée pour un test (libellé traduit, valeur).</summary>
public sealed record TestOption<T>(string Label, T Value);
