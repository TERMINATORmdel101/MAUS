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

/// <summary>Coin de l'écran principal où s'affiche le compteur d'images par seconde au-dessus du jeu.</summary>
public enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// Constat marqué « voulu » par l'utilisateur. Il est lié à la valeur constatée ce jour-là, écrite dans la langue
/// de MAUS à ce moment-là : si la situation change, MAUS le signale de nouveau.
/// </summary>
public sealed record Acknowledgement(string FindingId, string? Current, DateTimeOffset At, string? Language = null);

/// <summary>Choix de l'utilisateur qui changent ce que MAUS recommande. Aucun ne modifie le PC par lui-même.</summary>
public sealed record UserPreferences
{
    public static UserPreferences Default { get; } = new();

    /// <summary>Profil Game Bar choisi (1, 2 ou 3) ; <c>null</c> = profil détecté par MAUS.</summary>
    public int? GameBarProfile { get; init; }

    public LaptopPowerChoice LaptopPower { get; init; }

    /// <summary>Version des avertissements (<see cref="Legal.Disclaimer.Version"/>) acceptée au lancement ; 0 = jamais acceptés.</summary>
    public int DisclaimerAccepted { get; init; }

    /// <summary>Moteur de recherche pour « Rechercher sur le web » (atelier) ; DuckDuckGo par défaut, au choix de l'utilisateur.</summary>
    public Workshop.SearchEngine SearchEngine { get; init; }

    /// <summary>Langue de MAUS (« fr », « en », « es ») ; <c>null</c> = langue de Windows.</summary>
    public string? Language { get; init; }

    /// <summary>Intervalles proposés pour les mesures en direct, en millisecondes.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public static IReadOnlyList<int> RefreshChoices { get; } = [500, 1000, 2000, 5000];

    public ThemeChoice Theme { get; init; }

    public AccentChoice Accent { get; init; }

    public AnimationChoice Animations { get; init; }

    /// <summary>Intervalle des mesures en direct (atelier, fenêtre de surveillance), en millisecondes.</summary>
    public int RefreshMilliseconds { get; init; } = 1000;

    /// <summary>La fenêtre de surveillance reste au premier plan.</summary>
    public bool MonitorOnTop { get; init; }

    /// <summary>Compteur d'images par seconde affiché au-dessus du jeu pendant la mesure.</summary>
    public bool OverlayEnabled { get; init; } = true;

    /// <summary>Taille du compteur au-dessus du jeu (1 = normale) ; bornée entre 0,6 et 2,5.</summary>
    public double OverlayScale { get; init; } = 1;

    /// <summary>Opacité du compteur au-dessus du jeu (1 = opaque) ; bornée entre 0,2 et 1.</summary>
    public double OverlayOpacity { get; init; } = 0.85;

    public OverlayCorner OverlayCorner { get; init; }

    /// <summary>Taille réellement utilisée (un fichier modifié à la main ne peut pas rendre le compteur invisible ou géant).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double OverlayScaleUsed => double.IsFinite(OverlayScale) ? Math.Clamp(OverlayScale, 0.6, 2.5) : 1;

    [System.Text.Json.Serialization.JsonIgnore]
    public double OverlayOpacityUsed => double.IsFinite(OverlayOpacity) ? Math.Clamp(OverlayOpacity, 0.2, 1) : 0.85;

    /// <summary>Entrée de la carte mère choisie par l'utilisateur comme tension de la mémoire (étalonnage), ou <c>null</c>.</summary>
    public Workshop.Memory.DramVoltageCalibration? DramVoltage { get; init; }

    /// <summary>Intervalle réellement utilisé : une valeur hors de la liste proposée (fichier modifié à la main) revient à 1 seconde.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan RefreshInterval => TimeSpan.FromMilliseconds(RefreshChoices.Contains(RefreshMilliseconds) ? RefreshMilliseconds : 1000);

    public IReadOnlyList<Acknowledgement> Acknowledged { get; init; } = [];

    /// <summary>Marque « voulu » la valeur actuelle du constat (remplace une marque précédente).</summary>
    public UserPreferences Acknowledge(Finding finding, DateTimeOffset now) => this with
    {
        Acknowledged = [.. Acknowledged.Where(a => a.FindingId != finding.Id), new Acknowledgement(finding.Id, finding.Current, now, Localization.Texts.Language)],
    };

    public UserPreferences Unacknowledge(string findingId) => this with
    {
        Acknowledged = Acknowledged.Where(a => a.FindingId != findingId).ToList(),
    };

    /// <summary>
    /// Marque valable pour ce constat, c'est-à-dire posée sur la même valeur constatée. Une marque posée dans une autre
    /// langue ne peut pas être comparée mot pour mot : elle reste valable jusqu'à <see cref="RebindLanguage"/>.
    /// </summary>
    public Acknowledgement? AcknowledgementFor(Finding finding) =>
        Acknowledged.FirstOrDefault(a => a.FindingId == finding.Id
            && (string.Equals(a.Current, finding.Current, StringComparison.Ordinal) || IsOtherLanguage(a)));

    /// <summary>
    /// Après un changement de langue : réécrit dans la langue active la valeur des marques qui s'appliquent aux constats
    /// de cet audit, pour que la comparaison mot pour mot reprenne. Renvoie la même instance si rien ne change.
    /// </summary>
    public UserPreferences RebindLanguage(IEnumerable<Finding> findings)
    {
        var byId = findings.Where(f => f.AcknowledgedFrom is not null).GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var changed = false;
        var list = Acknowledged.Select(a =>
        {
            if (!IsOtherLanguage(a) || !byId.TryGetValue(a.FindingId, out var finding))
            {
                return a;
            }

            changed = true;
            return a with { Current = finding.Current, Language = Localization.Texts.Language };
        }).ToList();
        return changed ? this with { Acknowledged = list } : this;
    }

    private static bool IsOtherLanguage(Acknowledgement mark) =>
        mark.Language is { } language && !string.Equals(language, Localization.Texts.Language, StringComparison.Ordinal);
}
