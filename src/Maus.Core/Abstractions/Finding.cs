using static Maus.Core.Localization.Texts;

namespace Maus.Core;

/// <summary>
/// Morceau d'identifiant de constat (Modules 11, 17 et 18). Les identifiants sont enregistrés par les constats « voulus » : ce calcul
/// ne doit pas changer. Le Module 12 garde le sien, qui réunit les tirets et donne donc d'autres identifiants.
/// </summary>
internal static class FindingSlug
{
    public static string Of(string value, int maxLength = int.MaxValue)
    {
        var chars = value.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars).Trim('-');
        return slug.Length > maxLength ? slug[..maxLength] : slug.Length == 0 ? "x" : slug;
    }
}

/// <summary>Un objet suivi d'un audit à l'autre : un nom et une version, jamais un chemin ni une commande.</summary>
public sealed record FollowedItem(string Name, string? Version = null);

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

    /// <summary>
    /// L'explication cite un texte lu sur le PC (commande d'un programme, nom d'un appareil) : elle s'affiche à l'identique.
    /// </summary>
    public bool ExplanationHasPcData { get; init; }

    /// <summary>Conseil ou prochaine étape, y compris un renvoi vers un autre module.</summary>
    public string? Advice { get; init; }

    /// <summary>Encadré d'avertissement à lire avant d'agir, indépendant de l'état du constat : il ne change ni le score ni le verdict.</summary>
    public string? Callout { get; init; }

    /// <summary>Note factuelle sur un choix laissé à l'utilisateur : une information, jamais une correction ni un conseil de MAUS.</summary>
    public string? SideNote { get; init; }

    /// <summary>
    /// Valeur affichée à l'écran seulement (par exemple l'identifiant d'une clé) : elle voyage du moteur à l'interface, mais aucun
    /// rapport, export ni copie ne la reprend (<see cref="Reporting.AuditReport.ToJson"/> la retire).
    /// </summary>
    public string? ScreenOnlyNote { get; init; }

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

    /// <summary>Valeurs chiffrées gardées pour les courbes et les comparaisons (usure, température, nombre…), indépendantes de la langue.</summary>
    public IReadOnlyDictionary<string, double>? Measures { get; init; }

    /// <summary>Entrée de démarrage : activée ou désactivée, gardée dans le résumé de l'audit ; <c>null</c> pour tout autre constat.</summary>
    public bool? Enabled { get; init; }

    /// <summary>Tâches ou pilotes relevés par ce contrôle, gardés dans le résumé de l'audit pour les comparer au suivant ; <c>null</c> si la liste n'a pas pu être lue.</summary>
    public IReadOnlyList<FollowedItem>? Followed { get; init; }

    /// <summary>Ce qui a été mesuré, tel que le matériel se nomme (modèle du disque) : deux disques ne se mélangent pas dans une même courbe.</summary>
    public string? MeasuredItem { get; init; }

    public static Finding Unknown(string id, string title, string reason, string? category = null) => new()
    {
        Id = id,
        Title = title,
        Status = FindingStatus.Unknown,
        Explanation = reason,
        Category = category,
    };

    /// <summary>
    /// Un contrôle qui échoue ne fait jamais échouer son module : droits manquants, source absente ou lecture impossible donnent
    /// un constat « indéterminé », jamais un faux problème.
    /// </summary>
    public static Finding Guard(string id, string title, string? category, Func<Finding> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        try
        {
            return check();
        }
        catch (Exception ex) when (ex is Platform.MausAccessDeniedException or UnauthorizedAccessException)
        {
            return AdminRequired(id, title, category);
        }
        catch (Exception ex) when (ex is Platform.DataSourceUnavailableException or System.Management.ManagementException or System.Runtime.InteropServices.COMException)
        {
            return Unknown(id, title, Localization.Texts.T("Information indisponible sur ce PC."), category);
        }
        catch (IOException ex)
        {
            return Unknown(id, title, Localization.Texts.T("Lecture impossible : {0}", ex.Message), category);
        }
    }

    public static Finding AdminRequired(string id, string title, string? category = null) =>
        Unknown(id, title, T("Lecture impossible sans droits administrateur. Relancez MAUS en tant qu'administrateur."), category);
}
