namespace Maus.Core;

/// <summary>Résultat complet d'un module après détection.</summary>
/// <param name="NotChosen">Le module n'a pas été lancé parce que l'utilisateur ne l'a pas choisi : ce n'est pas une panne (<paramref name="Error"/> en donne la raison).</param>
public sealed record ModuleResult(
    string ModuleId,
    string Title,
    IReadOnlyList<Finding> Findings,
    TimeSpan Duration,
    string? Error = null,
    bool NotChosen = false)
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

    /// <summary>
    /// Verdict à afficher pour le module : une simple information ne signale rien, un module qui n'a que des constats
    /// conformes et des informations est donc « en ordre » (vert), pas gris comme un module non vérifié.
    /// </summary>
    public FindingStatus Verdict => WorstStatus == FindingStatus.Info ? FindingStatus.Ok : WorstStatus;

    public int Count(FindingStatus status) => Findings.Count(f => f.Status == status);
}
