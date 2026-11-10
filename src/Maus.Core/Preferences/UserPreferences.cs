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
/// Ce que l'audit hebdomadaire affiche à la fin. Le choix se change par le moteur, mais cet audit tourne sans droits et lit sa
/// copie dans le fichier de l'utilisateur : c'est un confort d'affichage, pas une barrière contre un programme du même compte.
/// </summary>
public enum WeeklyNotification
{
    /// <summary>Seulement s'il trouve un problème rouge (recommandé).</summary>
    RedOnly,

    /// <summary>Un résumé à chaque audit.</summary>
    Always,

    /// <summary>Jamais : le résultat se lit dans l'historique des scores.</summary>
    Never,
}

/// <summary>Taille et position de la fenêtre principale à sa dernière fermeture.</summary>
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized)
{
    /// <summary>Taille à rétablir : le minimum gagne, même sur un bureau plus petit que lui (écran à 200 %, bureau à distance).</summary>
    public static double FitSize(double saved, double minimum, double screen) => Math.Clamp(saved, minimum, Math.Max(screen, minimum));
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
public sealed record Acknowledgement(string FindingId, string? Current, DateTimeOffset At, string? Language = null)
{
    /// <summary>Longueur la plus grande d'une valeur gardée, celle que le moteur accepte (<c>prefs.save</c>).</summary>
    public const int MaxCurrentLength = 2000;

    /// <summary>
    /// Valeur telle qu'elle est gardée et comparée. Une valeur trop longue (une liste de périphériques, par exemple) devient son
    /// début suivi de son empreinte : la comparaison mot pour mot reste juste, un changement dans la partie coupée se voit encore.
    /// </summary>
    public static string? Bounded(string? current)
    {
        if (current is null || current.Length <= MaxCurrentLength)
        {
            return current;
        }

        var digest = " … #" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(current)));
        var kept = MaxCurrentLength - digest.Length;
        return current[..(char.IsHighSurrogate(current[kept - 1]) ? kept - 1 : kept)] + digest;
    }
}

/// <summary>Fréquence d'écran gardée après un essai : de quoi revenir en arrière depuis l'Historique (0.8.6).</summary>
public sealed record KeptRefreshRate(string SourceName, string Label, int Width, int Height, int FromHz, int ToHz, DateTimeOffset At);

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

    /// <summary>
    /// À l'ouverture, MAUS (sans droits administrateur) propose d'activer toutes ses fonctionnalités ; l'utilisateur peut
    /// ne plus le voir (case du message, ou Paramètres).
    /// </summary>
    public bool OfferAllFeatures { get; init; } = true;

    /// <summary>Date des dernières corrections qui demandent un redémarrage ; effacée dès que le PC a redémarré depuis.</summary>
    public DateTimeOffset? RestartPendingSince { get; init; }

    /// <summary>Lancer l'audit dès que l'utilisateur a accepté les droits administrateur (jamais de demande d'autorisation pour autant).</summary>
    public bool AuditWhenElevated { get; init; }

    /// <summary>Mode technicien (0.8.6) : les détails techniques sont dépliés partout et les erreurs gardent leur message d'origine.</summary>
    public bool TechnicianMode { get; init; }

    /// <summary>Petits messages d'aide au survol coupés (ils sont affichés par défaut).</summary>
    public bool HelpTipsOff { get; init; }

    public WeeklyNotification WeeklyNotification { get; init; }

    /// <summary>
    /// Modules à ne pas auditer (0.8.6). Choix protégé : un programme sans droits ne peut pas faire disparaître un module de
    /// l'audit du moteur. Le score devient partiel, et l'audit de la semaine reste complet.
    /// </summary>
    public IReadOnlyList<string> SkippedModules { get; init; } = [];

    /// <summary>Fréquences d'écran gardées après un essai, annulables depuis l'Historique.</summary>
    public IReadOnlyList<KeptRefreshRate> KeptRefreshRates { get; init; } = [];

    /// <summary>Taille de l'affichage de la fenêtre principale (1 = normale) : Paramètres, ou Ctrl + molette.</summary>
    public double UiZoom { get; init; } = 1;

    /// <summary>Taille réellement utilisée : un fichier modifié à la main ne peut pas rendre la fenêtre illisible.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double UiZoomUsed => double.IsFinite(UiZoom) ? Math.Clamp(UiZoom, 0.8, 1.5) : 1;

    /// <summary>Taille et position de la fenêtre ; <c>null</c> = celles par défaut.</summary>
    public WindowPlacement? MainWindow { get; init; }

    /// <summary>Dernier onglet ouvert (<c>0</c> = accueil) et sous-partie de l'atelier.</summary>
    public int LastTab { get; init; }

    public int LastWorkshopSection { get; init; }

    /// <summary>Dernière version dont les nouveautés ont été montrées (« Quoi de neuf ») ; <c>null</c> avant la 0.8.2.</summary>
    public string? LastSeenVersion { get; init; }

    /// <summary>Dernière fois que la carte « Les audits de la semaine » de la page Suivi a vraiment été vue ; <c>null</c> avant. Sert à l'affichage seulement.</summary>
    public DateTimeOffset? LastFollowVisit { get; init; }

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

    /// <summary>Intervalle réellement utilisé : une valeur hors de la liste proposée (fichier modifié à la main) revient à 1 seconde.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan RefreshInterval => TimeSpan.FromMilliseconds(RefreshChoices.Contains(RefreshMilliseconds) ? RefreshMilliseconds : 1000);

    public IReadOnlyList<Acknowledgement> Acknowledged { get; init; } = [];

    /// <summary>
    /// Réglages écrits par une version plus récente de MAUS : celle-ci ne s'en sert pas, mais les réécrit tels quels, pour
    /// qu'un retour à une version plus ancienne ne les efface pas. Ils restent dans le fichier d'où ils viennent.
    /// </summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? UnknownSettings { get; init; }

    /// <summary>Marque « voulu » la valeur actuelle du constat (remplace une marque précédente).</summary>
    public UserPreferences Acknowledge(Finding finding, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return this with
    {
        Acknowledged = [.. Acknowledged.Where(a => a.FindingId != finding.Id), new Acknowledgement(finding.Id, Acknowledgement.Bounded(finding.Current), now, Localization.Texts.Language)],
    };
    }

    public UserPreferences Unacknowledge(string findingId) => this with
    {
        Acknowledged = Acknowledged.Where(a => !a.FindingId.Equals(findingId, StringComparison.OrdinalIgnoreCase)).ToList(),
    };

    /// <summary>
    /// Marque valable pour ce constat, c'est-à-dire posée sur la même valeur constatée. Une marque posée dans une autre
    /// langue ne peut pas être comparée mot pour mot : elle reste valable jusqu'à <see cref="RebindLanguage"/>.
    /// </summary>
    public Acknowledgement? AcknowledgementFor(Finding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var current = Acknowledgement.Bounded(finding.Current);
        return Acknowledged.FirstOrDefault(a => a.FindingId == finding.Id
            && (string.Equals(a.Current, current, StringComparison.Ordinal) || IsOtherLanguage(a)));
    }

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
            return a with { Current = Acknowledgement.Bounded(finding.Current), Language = Localization.Texts.Language };
        }).ToList();
        return changed ? this with { Acknowledged = list } : this;
    }

    private static bool IsOtherLanguage(Acknowledgement mark) =>
        mark.Language is { } language && !string.Equals(language, Localization.Texts.Language, StringComparison.Ordinal);
}
