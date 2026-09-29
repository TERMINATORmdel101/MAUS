using static Maus.Core.Localization.Texts;

namespace Maus.Core.Legal;

/// <summary>
/// Avertissements et limitation de responsabilité (demande du porteur, 29/09/2026). Rédigés comme une information sur les
/// risques : MAUS est fourni sans garantie (licence GPL-3.0, articles 15 et 16), l'utilisateur agit sous sa responsabilité,
/// et la responsabilité des auteurs est écartée « dans toute la mesure permise par la loi ». La fiche technique rappelle qu'une
/// clause ne peut pas supprimer les droits d'un consommateur (code de la consommation, article R212-1) : le texte le dit.
/// Les mêmes phrases servent à l'écran du premier lancement, à « À propos » et aux rappels avant chaque action.
/// </summary>
public static class Disclaimer
{
    /// <summary>Version du texte : un changement de fond le fait approuver de nouveau au lancement suivant.</summary>
    public const int Version = 1;

    public static string Title => T("Avant d'utiliser MAUS : avertissements");

    /// <summary>Texte complet, un paragraphe par élément.</summary>
    public static IReadOnlyList<string> Paragraphs =>
    [
        T("MAUS est un logiciel libre fourni « tel quel », SANS AUCUNE GARANTIE, ni de bon fonctionnement, ni d'adaptation à votre PC (licence GPL-3.0, articles 15 et 16)."),
        T("Les corrections modifient des réglages de Windows. Les tests poussent le processeur, la mémoire, la carte graphique ou le disque à leur maximum. Même quand une modification ou un test est censé ne poser aucun problème, un incident reste possible : plantage, redémarrage, instabilité, perte de données non enregistrées, ou panne d'un composant déjà fragile."),
        T("Vous lancez les corrections, les réparations et les tests sous votre seule responsabilité. Dans toute la mesure permise par la loi, les auteurs et les contributeurs de MAUS ne sont pas responsables des modifications apportées à Windows, des tests, ni de leurs conséquences."),
        T("Avant toute correction ou tout test, sauvegardez vos données importantes. MAUS crée un point de restauration et garde les valeurs d'origine pour tout annuler, mais cela ne remplace pas une sauvegarde."),
        T("Ces avertissements ne limitent pas les droits que la loi de votre pays accorde aux consommateurs."),
    ];

    public static string Full => string.Join(Environment.NewLine + Environment.NewLine, Paragraphs);

    /// <summary>Case à cocher du premier lancement.</summary>
    public static string Acceptance => T("J'ai lu ces avertissements et j'utilise MAUS sous ma responsabilité.");

    /// <summary>Rappel en bas de la confirmation de chaque test.</summary>
    public static string TestReminder => T("Rappel : MAUS est fourni sans garantie. Vous lancez ce test sous votre responsabilité : même un test censé ne poser aucun problème peut faire planter le PC ou révéler la panne d'un composant fragile. Enregistrez votre travail et sauvegardez vos données avant.");

    /// <summary>Rappel en bas de la confirmation des corrections (modifications de Windows).</summary>
    public static string ChangeReminder => T("Rappel : MAUS est fourni sans garantie. Vous appliquez ces modifications de Windows sous votre responsabilité, même quand elles sont censées ne poser aucun problème. Elles restent annulables (bouton « Annuler », point de restauration), mais cela ne remplace pas une sauvegarde de vos données.");

    /// <summary>Rappel en bas de la confirmation des autres opérations (réparation de Windows, mises à jour, pilote, Explorateur).</summary>
    public static string OperationReminder => T("Rappel : MAUS est fourni sans garantie. Vous lancez cette opération sous votre responsabilité, même si elle est censée ne poser aucun problème. Enregistrez votre travail et sauvegardez vos données avant.");
}
