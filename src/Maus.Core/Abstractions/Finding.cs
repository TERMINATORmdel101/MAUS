using static Maus.Core.Localization.Texts;

namespace Maus.Core;

/// <summary>Un constat produit par un module : ce qui a été vu, ce qui est attendu, et pourquoi cela compte.</summary>
public sealed record Finding
{
    /// <summary>Identifiant stable, préfixé par le module (par exemple « M05.fast-startup »).</summary>
    public required string Id { get; init; }

    /// <summary>Libellé court affiché à l'utilisateur.</summary>
    public required string Title { get; init; }

    public required FindingStatus Status { get; init; }

    public Severity Severity { get; init; } = Severity.Info;

    /// <summary>Valeur constatée, en clair (par exemple « 60 Hz » ou « absente »).</summary>
    public string? Current { get; init; }

    /// <summary>Valeur attendue ou recommandée.</summary>
    public string? Expected { get; init; }

    /// <summary>Explication en langage simple : ce que fait ce réglage et pourquoi il compte.</summary>
    public required string Explanation { get; init; }

    /// <summary>Conseil ou prochaine étape, y compris un renvoi vers un autre module.</summary>
    public string? Advice { get; init; }

    /// <summary>Regroupement facultatif dans l'interface (par exemple « Defender » ou « Windows Update »).</summary>
    public string? Category { get; init; }

    /// <summary>
    /// Page des Paramètres de Windows où l'utilisateur agit lui-même (adresse « ms-settings: » documentée par Microsoft,
    /// « Launch Windows Settings ») : l'interface propose un bouton qui l'ouvre, sans rien modifier.
    /// </summary>
    public string? SettingsPage { get; init; }

    /// <summary>Vrai si une correction réversible est proposée.</summary>
    public bool Fixable { get; init; }

    /// <summary>Verdict d'origine d'un constat marqué « voulu » par l'utilisateur (affiché alors comme une information).</summary>
    public FindingStatus? AcknowledgedFrom { get; init; }

    public static Finding Unknown(string id, string title, string reason, string? category = null) => new()
    {
        Id = id,
        Title = title,
        Status = FindingStatus.Unknown,
        Explanation = reason,
        Category = category,
    };

    public static Finding AdminRequired(string id, string title, string? category = null) =>
        Unknown(id, title, T("Lecture impossible sans droits administrateur. Relancez MAUS en tant qu'administrateur."), category);
}
