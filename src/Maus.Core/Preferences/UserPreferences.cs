namespace Maus.Core.Preferences;

/// <summary>Choix du portable pour les modes d'alimentation (décision du projet : question posée au premier lancement).</summary>
public enum LaptopPowerChoice
{
    /// <summary>Pas encore choisi : MAUS applique la proposition par défaut et pose la question.</summary>
    NotChosen,

    /// <summary>Proposé par défaut : Meilleures performances sur secteur, Équilibré sur batterie.</summary>
    Performance,

    /// <summary>Meilleures performances sur secteur comme sur batterie : autonomie réduite.</summary>
    PerformanceEverywhere,

    /// <summary>Autonomie : Équilibré sur secteur, Meilleure efficacité énergétique sur batterie.</summary>
    Battery,
}

/// <summary>
/// Constat marqué « voulu » par l'utilisateur. Il est lié à la valeur constatée ce jour-là :
/// si la situation change, MAUS le signale de nouveau.
/// </summary>
public sealed record Acknowledgement(string FindingId, string? Current, DateTimeOffset At);

/// <summary>Choix de l'utilisateur qui changent ce que MAUS recommande. Aucun ne modifie le PC par lui-même.</summary>
public sealed record UserPreferences
{
    public static UserPreferences Default { get; } = new();

    /// <summary>Profil Game Bar choisi (1, 2 ou 3) ; <c>null</c> = profil détecté par MAUS.</summary>
    public int? GameBarProfile { get; init; }

    public LaptopPowerChoice LaptopPower { get; init; }

    public IReadOnlyList<Acknowledgement> Acknowledged { get; init; } = [];

    /// <summary>Marque « voulu » la valeur actuelle du constat (remplace une marque précédente).</summary>
    public UserPreferences Acknowledge(Finding finding, DateTimeOffset now) => this with
    {
        Acknowledged = [.. Acknowledged.Where(a => a.FindingId != finding.Id), new Acknowledgement(finding.Id, finding.Current, now)],
    };

    public UserPreferences Unacknowledge(string findingId) => this with
    {
        Acknowledged = Acknowledged.Where(a => a.FindingId != findingId).ToList(),
    };

    /// <summary>Marque valable pour ce constat, c'est-à-dire posée sur la même valeur constatée.</summary>
    public Acknowledgement? AcknowledgementFor(Finding finding) =>
        Acknowledged.FirstOrDefault(a => a.FindingId == finding.Id && string.Equals(a.Current, finding.Current, StringComparison.Ordinal));
}
