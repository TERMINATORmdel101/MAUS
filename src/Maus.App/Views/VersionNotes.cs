using static Maus.Core.Localization.Texts;

namespace Maus.App.Views;

/// <summary>« Quoi de neuf » (demande du porteur, 27/10/2026) : les nouveautés d'une version, pour la carte de l'accueil.</summary>
public static class VersionNotes
{
    /// <summary>Nouveautés de chaque version, en quelques lignes ; vide si rien n'est prévu pour cette version.</summary>
    public static IReadOnlyList<string> For(string version) => version switch
    {
        "0.8.6" =>
        [
            T("Nouvelle page Suivi (Ctrl+7) : ce que Windows a défait, ce qui a changé entre deux audits, carnet de santé, plantages des 30 derniers jours, avant et après une séance, dossier du PC à imprimer."),
            T("L'audit marche aussi sans autorisation administrateur, et le dernier audit est retrouvé à l'ouverture."),
            T("Sur l'accueil simple, « Que se passe-t-il ? » part de ce que vous constatez et ouvre le bon outil."),
            T("Un seul écran au premier lancement, et des Paramètres en trois onglets où chaque option dit à quoi elle sert."),
            T("Constats plus clairs : « Pourquoi ça compte » d'abord, des filtres, et des messages d'erreur qui disent quoi faire."),
            T("BitLocker : le constat dit si le disque est chiffré, en entier ou en partie, et un encadré dit quoi faire avant une mise à jour du BIOS. MAUS ne lit jamais la clé et ne suspend rien lui-même."),
            T("Mises à jour : laquelle échoue, combien de fois et avec quel code, si Microsoft retient une nouvelle version de Windows, et ce que Microsoft dit des mises à jour et pilotes facultatifs."),
            T("« Mon PC a planté » distingue écran bleu, bouton, arrêt non expliqué, mise à jour et arrêt demandé ; « Pourquoi mon PC est lent ? » donne le dernier vrai redémarrage."),
            T("Batterie : sur un clic, la capacité passée, depuis le début réel des données de Windows (pas encore essayé sur un vrai portable)."),
            T("Des phrases qui affirmaient plus que ce que Windows permet de savoir sont corrigées, et davantage d'erreurs matérielles sont comptées."),
            T("Sécurité renforcée : MAUS refuse de modifier tout réglage de Windows qui n'est pas dans sa liste, et l'audit de la semaine tourne sans droits administrateur ni réseau."),
        ],
        "0.8.5" =>
        [
            T("Le code de MAUS a été relu en entier, et le moteur administrateur est plus strict (actions sur le pilote graphique confirmées par lui, annulation forcée refusée)."),
            T("La fenêtre ne se fige plus pendant l'installation de PawnIO, et un audit ne peut plus en doubler un autre."),
            T("Profils de corrections et mode d'alimentation traduits dans les trois langues ; les erreurs imprévues sont journalisées dans le dossier des journaux de MAUS."),
        ],
        // 0.8.3 n'a pas été publiée : ses nouveautés sont annoncées avec la 0.8.4.
        "0.8.4" or "0.8.3" =>
        [
            T("Accueil plus simple : le score, la prochaine étape et trois outils ; le reste est dans « Plus d'outils » (l'accueil complet reste au choix dans les Paramètres)."),
            T("Plus joli : score qui se remplit, « Votre PC en un coup d'œil », fond Mica de Windows 11 au choix, raccourcis Ctrl+1 à Ctrl+6."),
            T("Nouvelle fenêtre d'ouverture « Comment utiliser MAUS ? » : le mode limité, sans droits administrateur, ou toutes les fonctionnalités."),
            T("Mode limité plus clair : les options qui demandent les droits administrateur sont grisées, et le bandeau « Toutes les fonctionnalités » les active."),
            T("Sécurité renforcée : messages signés entre la fenêtre et le moteur administrateur, protections de Windows sur MAUS, avertissement si MAUS n'est pas installé dans un dossier protégé."),
            T("Pour les actions sensibles (fermer un programme, pilote graphique, PawnIO, constat « voulu »), le moteur administrateur demande lui-même confirmation dans sa propre fenêtre."),
        ],
        "0.8.2" =>
        [
            T("Bouton « ? » en bas à gauche : le site d'aide de MAUS."),
            T("Pourquoi mon PC démarre lentement ? Pourquoi se réveille-t-il la nuit ? Les réponses de Windows dans l'audit (démarrage, alimentation)."),
            T("Écrans bleus : les 5 derniers, avec leur code et le lien vers l'explication de Microsoft."),
            T("Historique : vos points de restauration de Windows, et un bouton pour en créer un."),
            T("Corrections : désactiver une tâche planifiée lancée à l'ouverture de session, bloquer les instantanés de Recall, remettre les effets visuels aux valeurs de Windows."),
            T("Écran sous sa fréquence maximale : bouton « Essayer la fréquence maximale », l'ancien réglage revient seul après 15 secondes."),
            T("Batterie : cycles de charge et rapport de batterie de Windows ; Wi-Fi : réseaux voisins par canal."),
        ],
        _ => [],
    };
}
