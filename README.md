<p align="center"><img src="assets/logo/maus-logo-800.jpg" alt="MAUS" width="420"></p>

# MAUS

**Maintenance · Audit · Updates · Sécurité** — l'utilitaire qui audite, répare et optimise Windows 11 en toute transparence.

> MAUS est conçu et codé avec Claude, une IA d'Anthropic, sous la direction de son auteur.

## Principes

- **Audit d'abord, action ensuite.** Un scan en lecture seule produit un rapport (vert, bleu, orange, rouge) avant toute modification.
- **Tout est réversible.** Point de restauration et sauvegarde des valeurs d'origine avant chaque changement (à partir de la V0.2).
- **Tout est expliqué.** Chaque réglage dit ce qu'il fait, ce qu'il apporte, ce qu'il risque et comment revenir en arrière.
- **Rien de dangereux en automatique.** BIOS, XMP/EXPO et overclocking sont guidés, jamais exécutés.
- **Honnête sur les gains.** Les réglages de Windows font gagner quelques pour cent, pas 30 % : MAUS le dit, et ne coupe jamais une protection ou une fonction utile de Windows pour gratter un chiffre.
- **Aucune télémétrie.** MAUS n'envoie rien.

## Ce que fait MAUS

- **Audit** de 20 domaines de Windows 11 (modifications risquées, réparation, mises à jour, confidentialité, alimentation, effets visuels, Game Bar, BIOS, carte graphique, mémoire, santé du matériel, démarrage, sécurité, écran, overclocking, atelier, périphériques, réseau, sauvegardes, logiciels à mettre à jour), résumé par un **score de santé** et les quatre familles **M·A·U·S**.
- **Corrections réversibles**, choisies ligne par ligne ou par profil, avec point de restauration vérifié, journal des valeurs d'origine et bouton **Annuler** (séance entière ou une seule correction).
- **Atelier matériel**, sans pilote : fiche d'identité du PC, jauges de sécurité (températures, tensions), mesures en direct, gestionnaire des tâches qui explique chaque processus, « Pourquoi mon PC est lent ? », tests du processeur (dont un test cœur par cœur pour valider un Curve Optimizer ou un undervolt), de la mémoire vive et de la mémoire vidéo, avec arrêt automatique en cas de surchauffe.
- **Stockage** : ce qui prend de la place sur le disque (sans rien supprimer, les dossiers de Windows sont expliqués) et test de vitesse du disque.
- **Réparation officielle** des fichiers de Windows (DISM puis SFC) et **mise à jour des logiciels** choisis (winget), dans une fenêtre visible.
- **Test de connexion** : latence, gigue et pertes vers la box et vers Internet, pour savoir si le souci vient du Wi-Fi ou de la ligne.
- **Rapport HTML** avant/après, à garder ou à imprimer.
- **« Demander de l'aide »** : un résumé de votre PC à coller sur un forum, relu par vous, sans nom d'utilisateur, nom du PC, adresse e-mail ni numéro de série.
- **Trois langues** : français, anglais, espagnol.

## État du projet

| Version | Contenu | État |
|---|---|---|
| V0.1 | Socle, profil matériel, mode « audit seul » des modules | Terminée |
| V0.2 | Corrections réversibles (journal, point de restauration, Annuler), atelier matériel, nouvelle interface, trois langues | Codée : à valider sur Windows |
| V0.3 | Écran et HDR, publication sur le Microsoft Store | À venir |

Configuration requise : Windows 11 23H2 (build 22631) ou plus récent.

## Compiler

Prérequis : SDK .NET 10.

```bash
dotnet build Maus.slnx
dotnet test Maus.slnx
```

Audit en ligne de commande (lecture seule, aucun droit particulier requis ; certains contrôles demandent l'administrateur) :

```bash
dotnet run --project src/Maus.Cli -- --module M06
dotnet run --project src/Maus.Cli -- --lang en --html rapport.html
dotnet run --project src/Maus.Cli -- --summary
```

Corrections en ligne de commande (V0.2, invite de commandes **administrateur**) : `--plan` affiche les corrections proposées sans rien modifier, `--apply ID...` ou `--apply-recommended` les applique après confirmation (point de restauration vérifié d'abord), `--journal` liste les séances et `--revert SEANCE` remet les valeurs d'origine. À tester d'abord dans Windows Sandbox.

L'application graphique (`src/Maus.App`) demande les droits administrateur au lancement.

## Organisation du code

| Dossier | Rôle |
|---|---|
| `src/Maus.Core/Abstractions` | Contrat des modules (`IAuditModule`), constats, verdicts |
| `src/Maus.Core/Platform` | Lecture du registre, de WMI et de commandes système ; écrivains séparés, réservés aux corrections |
| `src/Maus.Core/Fixes` | Corrections réversibles : journal, point de restauration vérifié, moteur Apply / Verify / Revert, profils |
| `src/Maus.Core/Hardware` | Profil matériel commun (fixe ou portable, CPU, GPU) |
| `src/Maus.Core/Rules` et `Catalog` | Règles de registre déclaratives en JSON |
| `src/Maus.Core/Modules` | Un dossier par module de la fiche technique (M01 à M20) |
| `src/Maus.Core/Workshop` | Atelier matériel : inventaire, capteurs, processus, tests |
| `src/Maus.Core/Localization` | Traductions (le français est la langue source) |
| `src/Maus.Core/Reporting` | Score de santé, rapports texte, JSON et HTML |
| `src/Maus.Cli` | Audit en ligne de commande |
| `src/Maus.App` | Interface WPF |
| `tests` | Tests unitaires avec registre et WMI simulés |

## Contribuer

Les suggestions d'amélioration sont les bienvenues : ouvrez une *issue* ou proposez une *pull request*. Lisez aussi [TRADEMARKS.md](TRADEMARKS.md) : le nom MAUS est réservé à la version officielle.

## Licence

MAUS est un logiciel libre distribué sous licence **GNU GPL version 3** (GPL-3.0-only, texte complet dans [LICENSE](LICENSE)), avec les conditions additionnelles de l'article 7 décrites dans [TRADEMARKS.md](TRADEMARKS.md). Il est fourni sans aucune garantie.

MAUS est un logiciel indépendant. Il n'est affilié ni à Microsoft, NVIDIA, AMD, Intel ou Anthropic, ni approuvé par eux. Windows et Xbox sont des marques du groupe Microsoft ; les autres marques citées appartiennent à leurs propriétaires respectifs.
