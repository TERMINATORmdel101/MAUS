namespace Maus.App.ViewModels.Workshop;

/// <summary>Une durée ou une taille proposée pour un test (libellé traduit, valeur).</summary>
public sealed record TestOption<T>(string Label, T Value);

/// <summary>Pastille d'un cœur dans le bilan du test cœur par cœur.</summary>
public sealed record CoreChipViewModel(string Text, System.Windows.Media.Brush Background);
