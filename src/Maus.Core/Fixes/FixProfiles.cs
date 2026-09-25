using static Maus.Core.Localization.Texts;

namespace Maus.Core.Fixes;

/// <summary>
/// Profils en un clic : une présélection cohérente des corrections, chaque case restant modifiable.
/// Aucun profil ne coche une correction « avancée » d'un autre domaine que le sien.
/// </summary>
public sealed record FixProfile(string Id, string Name, string Description, Func<PlannedChange, bool> Selects)
{
    /// <summary>Sécurité et retour aux valeurs par défaut : toujours utiles, quel que soit l'usage.</summary>
    private static readonly HashSet<string> SecurityModules = new(StringComparer.Ordinal) { "M01", "M13" };

    private static readonly HashSet<string> GamingModules = new(StringComparer.Ordinal) { "M06", "M07", "M09" };

    public static FixProfile Recommended { get; } = new(
        "recommended",
        T("Recommandé"),
        T("Les corrections pré-cochées par MAUS : sans risque connu, réversibles."),
        c => c.Recommended && !c.Advanced);

    public static FixProfile Gamer { get; } = new(
        "gamer",
        "Joueur",
        T("Sécurité rétablie, effets visuels allégés, Game Bar et carte graphique réglées pour le jeu."),
        c => c.Recommended && !c.Advanced && (SecurityModules.Contains(c.ModuleId) || GamingModules.Contains(c.ModuleId)));

    public static FixProfile PrivacyMax { get; } = new(
        "privacy",
        T("Confidentialité max"),
        T("Toutes les corrections de confidentialité, y compris les lignes avancées, plus la sécurité recommandée."),
        c => c.ModuleId == "M04" || c.Recommended && !c.Advanced && SecurityModules.Contains(c.ModuleId));

    public static FixProfile None { get; } = new("none", T("Tout décocher"), T("Aucune correction sélectionnée."), _ => false);

    public static IReadOnlyList<FixProfile> All { get; } = [Recommended, Gamer, PrivacyMax, None];
}
