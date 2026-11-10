namespace Maus.App.ViewModels.Workshop;

/// <summary>Une durée ou une taille proposée pour un test (libellé traduit, valeur).</summary>
public sealed record TestOption<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

/// <summary>Pastille d'un cœur dans le bilan du test cœur par cœur.</summary>
public sealed record CoreChipViewModel(string Text, System.Windows.Media.Brush Background)
{
    public override string ToString() => Text;

    /// <summary>Contraste élevé : fond et texte viennent de la même paire de Windows ; le verdict reste écrit dans le texte.</summary>
    public System.Windows.Media.Brush Background { get; init; } = System.Windows.SystemParameters.HighContrast ? System.Windows.SystemColors.HighlightBrush : Background;
}
