namespace Maus.Core;

/// <summary>Verdict d'un constat, affiché sous forme de voyant.</summary>
public enum FindingStatus
{
    /// <summary>Conforme : voyant vert.</summary>
    Ok,

    /// <summary>Simple information, sans jugement.</summary>
    Info,

    /// <summary>Optimisation possible, sans risque pour le PC : voyant bleu.</summary>
    Improvable,

    /// <summary>À surveiller ou à corriger : voyant orange.</summary>
    Warning,

    /// <summary>Problème avéré : voyant rouge.</summary>
    Problem,

    /// <summary>Impossible à déterminer : droits insuffisants, matériel absent ou API indisponible.</summary>
    Unknown,
}

public static class FindingStatusExtensions
{
    /// <summary>Rang utilisé pour trouver le pire verdict d'un module.</summary>
    public static int Rank(this FindingStatus status) => status switch
    {
        FindingStatus.Problem => 5,
        FindingStatus.Warning => 4,
        FindingStatus.Improvable => 3,
        FindingStatus.Unknown => 2,
        FindingStatus.Info => 1,
        _ => 0,
    };

    /// <summary>Statut d'un écart selon sa gravité : rouge dès « Élevé », orange pour « Moyen », bleu pour une simple optimisation.</summary>
    public static FindingStatus ForDeviation(Severity severity) => severity switch
    {
        Severity.Critical or Severity.High => FindingStatus.Problem,
        Severity.Medium => FindingStatus.Warning,
        Severity.Low => FindingStatus.Improvable,
        _ => FindingStatus.Info,
    };
}
