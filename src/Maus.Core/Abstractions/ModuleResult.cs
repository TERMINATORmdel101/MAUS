namespace Maus.Core;

/// <summary>Résultat complet d'un module après détection.</summary>
public sealed record ModuleResult(
    string ModuleId,
    string Title,
    IReadOnlyList<Finding> Findings,
    TimeSpan Duration,
    string? Error = null)
{
    /// <summary>Pire verdict du module ; une erreur d'exécution compte comme « indéterminé ».</summary>
    public FindingStatus WorstStatus
    {
        get
        {
            var worst = Error is null ? FindingStatus.Ok : FindingStatus.Unknown;
            foreach (var finding in Findings)
            {
                if (finding.Status.Rank() > worst.Rank())
                {
                    worst = finding.Status;
                }
            }

            return worst;
        }
    }

    public int Count(FindingStatus status) => Findings.Count(f => f.Status == status);
}
