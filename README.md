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
- **Mémoire en détail** (avec le pilote libre PawnIO, installé seulement si vous le voulez) : puce SPD de chaque barrette (profils JEDEC, XMP, EXPO), timings réels primaires, secondaires et tertiaires, FCLK / UCLK / MCLK sur Ryzen ; vraie température, tension et consommation du processeur.
- **Relevé pendant une partie** : températures, fréquences et goulots d'étranglement, avec un bilan clair et un export CSV ; **historique des scores** pour voir l'effet d'un réglage.
- **Audit automatique chaque semaine** (au choix) : MAUS ne se montre que s'il trouve un problème rouge.
- **Stockage** : ce qui prend de la place sur le disque (sans rien supprimer, les dossiers de Windows sont expliqués) et test de vitesse du disque.
- **Réparation officielle** des fichiers de Windows (DISM puis SFC) et **mise à jour des logiciels** choisis (winget), dans une fenêtre visible.
- **Test de connexion** : latence, gigue et pertes vers la box et vers Internet, pour savoir si le souci vient du Wi-Fi ou de la ligne.
- **Rapport HTML** avant/après, à garder ou à imprimer.
- **« Demander de l'aide »** : un résumé de votre PC à coller sur un forum, relu par vous, sans nom d'utilisateur, nom du PC, adresse e-mail ni numéro de série.
- **Trois langues** : français, anglais, espagnol.

## État du projet

Version actuelle : **0.6.1** (voir [CHANGELOG.md](CHANGELOG.md) ; le porteur a choisi de revenir à des numéros en 0.x tant que MAUS est une alpha). Tout est testé avec des simulations et, depuis la 0.3.3-alpha, sur le PC Windows du porteur ; ce qui n'a pas encore été vérifié sur un vrai matériel est signalé dans l'application. Projet encore jeune : à essayer d'abord sur un PC dont les données sont sauvegardées.

| Version | Contenu | État |
|---|---|---|
| V0.1 | Socle, profil matériel, mode « audit seul » des modules | Terminée |
| V0.2 | Corrections réversibles (journal, point de restauration, Annuler), atelier matériel, nouvelle interface, trois langues | Codée : à valider sur Windows |
| 0.3.2-alpha | Capteurs avancés (PawnIO), fiche mémoire complète, relevé de partie, historique des scores, audit hebdomadaire, nettoyage « aucune donnée inventée » | Codée : à valider sur Windows |
| 0.3.3-alpha | Thème clair / sombre, couleurs selon les composants, animations, fenêtre de surveillance (températures, consommations, fréquences, erreurs WHEA / PCIe et Windows), timings réels Intel Sandy Bridge et Skylake à Comet Lake, tension mémoire honnête | Testée sur le PC du porteur |
| 3.9.1 | Timings réels sur tous les Intel Core depuis Haswell (jusqu'aux Core Ultra), APU Ryzen Raven Ridge, comparaison de fiches mémoire, étalonnage de la tension, relevé avec bilan, revue de sécurité | Timings vérifiés sur Coffee Lake ; autres générations à comparer à CPU-Z |
| 4.0.0 | Sept charges pour les tests du processeur (dont AVX2 + FMA et AVX-512), programme complet de validation du Curve Optimizer (cœur par cœur puis transitoires, 1 h ou 4 h), température du processeur pendant les tests, plus de fausse alerte PCIe sur portable, anti-veille pendant les tests | Codée et relue ; programme à essayer sur un vrai Ryzen |
| 0.4.2 / 0.5.1 | Score de santé selon la gravité (« Pourquoi ce score ? »), liste complète des pilotes, actions sur le pilote de la carte graphique (redémarrer, retirer, sauvegarder, supprimer avec point de restauration), pilotes vérifiés dans l'audit de base | Testées sur le PC du porteur ; actions sur le pilote à essayer dans Windows Sandbox |
| 0.5.2 | Neuf corrections (tests arrêtés proprement, corrections interrompues annulables, rapport HTML sans nom d'utilisateur…), bouton « Ouvrir dans Windows » sur les constats, installateur | Testée sur le PC du porteur |
| 0.5.3 | Sept défauts corrigés : arrêt des tests longs si les températures ne sont plus lues, score dit « partiel » quand un module échoue, dates et nombres dans la langue choisie, filet de sécurité sur tous les fils, un seul MAUS à la fois, fichiers de MAUS jamais écrasés après une lecture ratée | Compilée et testée (1 421 tests) ; interface à vérifier sur le PC du porteur |
| 0.6.0 | Nouvelle présentation : accueil simplifié qui propose de corriger dès la fin de l'audit, constats rangés par importance (le reste à la demande), page Corrections allégée, tests de l'Atelier en parties bien distinctes, couleurs pastel en thème clair et sombre | Vérifiée à l'écran sur le PC du porteur (captures), à essayer par le porteur |
| 0.6.1 | Bouton « Corriger » sur les constats, doublons regroupés, liens web en bouton, score avant / après, récapitulatif avant d'appliquer, corrections rangées par module, carte « Bienvenue », pastel dans toutes les fenêtres, thème sombre corrigé | Vérifiée à l'écran (captures), à essayer par le porteur |
| Suite | Écran et HDR, signature du code, publication sur le Microsoft Store | À venir |

**Aucune donnée inventée.** Chaque seuil (température, tension) cite sa source publiée dans `src/Maus.Core/Catalog/hw-safety-limits.json`. Sans source, MAUS n'affiche pas de seuil et ne déclenche pas d'alarme.

Configuration requise : Windows 11 23H2 (build 22631) ou plus récent, processeur x64.

## Installer

Téléchargez `MAUS-<version>-installation.exe` dans les *Releases* du dépôt et lancez-le. MAUS s'installe dans `Program Files` (dossier protégé, nécessaire à l'audit automatique de la semaine), avec un raccourci dans le menu Démarrer ; .NET est inclus, rien d'autre à installer. Un fichier `.sha256` permet de vérifier que le téléchargement est intact.

Tant que l'installateur n'est pas signé (voir *Code signing policy* ci-dessous), Windows SmartScreen affiche « Windows a protégé votre ordinateur » : « Informations complémentaires », puis « Exécuter quand même ».

Désinstaller : Paramètres > Applications > Applications installées > MAUS > Désinstaller. Les fichiers, les raccourcis et la tâche de l'audit automatique sont retirés. Le journal des corrections (`%ProgramData%\MAUS`) est gardé : il contient les valeurs d'origine des réglages modifiés. Annulez vos corrections avant de désinstaller si vous voulez retrouver ces valeurs. Le pilote PawnIO, s'il a été installé, se retire depuis MAUS ou depuis la liste des applications.

Fabriquer l'installateur (SDK .NET 10 et [Inno Setup 7](https://jrsoftware.org/isinfo.php)) :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

**Status:** the application to the SignPath Foundation is being prepared; current releases are **not signed yet**. Once accepted, only installers built by the project's own build from this repository's source code will be signed.

Team roles:

- Committers and reviewers: [TERMINATORmdel101](https://github.com/TERMINATORmdel101) (maintainer; code written with Claude, an AI by Anthropic, and reviewed by the maintainer)
- Approvers: [TERMINATORmdel101](https://github.com/TERMINATORmdel101)

Privacy policy: this program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. (MAUS has no telemetry. Network access happens only for actions the user starts: Windows Update search during an audit, software update list with winget, web search about a process, connection test, links to GitHub.)

*En français :* la signature du code sera fournie gratuitement par SignPath.io, avec un certificat de la SignPath Foundation. La demande est en préparation : les versions actuelles ne sont **pas encore signées**. MAUS n'envoie aucune information sur le réseau sans une action de l'utilisateur.

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
dotnet run --project src/Maus.Cli -- --version
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
This project uses the SignPath Foundation for code signing.
