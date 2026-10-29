# Reprise — aide-mémoire de Claude

À lire après `CLAUDE.md` (mémoire complète du projet). Ce fichier ne garde que ce qui sert encore pour reprendre le travail ; l'historique des reprises de septembre et d'octobre 2026 (sessions cloud, ancien PC `C:\Users\CARO`, branches des agents de la 0.5.3) est dans l'historique Git de ce fichier (`git log -p REPRISE.md`).

## 1. Où en est le projet (29/10/2026)

- **0.8.2 publiée le 27/10** (accord du porteur « 3 oui », « tu peux le faire ») : https://github.com/TERMINATORmdel101/MAUS/releases/tag/0.8.2 (préversion, commit `a2c1f58`, installateur du workflow, SHA-256 `b4f6be09…4a29`, provenance vérifiée, téléchargement public vérifié), demande winget **microsoft/winget-pkgs#463072** (en attente). Le système d'autorisations de Claude Code bloque `gh release create` tant que le porteur n'a pas confirmé une seconde fois dans la conversation. Contenu : suggestions 1, 4, 5, 7 à 16 acceptées par le porteur (6 refusée : pas de vérification de mise à jour, à cause du Store), toutes faites, 1 583 tests, 0 avertissement. Vérifié à l'écran (copie `_smoke`, script `v082.ps1`, sans droits) : « Quoi de neuf », bouton « ? », carte des points de restauration. **Pas vérifié** : Wi-Fi sur un PC équipé (le PC du porteur n'en a pas), désactivation réelle d'une tâche planifiée (`schtasks /Change`), essai réel de la fréquence d'écran (le PC du porteur est déjà au maximum), Recall (pas de PC Copilot+), « Valeurs Windows » appliquées pour de vrai, création réelle d'un point de restauration depuis la carte. **Question posée au porteur** : retirer le seuil « signal faible sous 50 % » du Wi-Fi (M18), sans source publiée. Publication (fusion dans `main`, GitHub, winget) : seulement avec son accord.
- Version **0.8.1**, publiée sur GitHub (préversion, 22/10) et acceptée dans **winget** (demande #458592 ; versions dans le catalogue : 0.8.1, 0.8.0, 0.7.6, 0.7.5). Contenu de chaque version : `CHANGELOG.md`.
- Branche de travail : `claude/youthful-ride-ivazlw` ; `main` = ligne de publication (un envoi sur `main` lance les workflows « Tests » et « Installateur »).
- **0.8.4 prête sur la branche, pas publiée** (29/10, choix du porteur « les deux pour la 0.8.4 ») : contient la 0.8.3 (jamais publiée, commit `7b4177b`) + les 6 points du rapport de failles privé (benchmark administrateur protégé et sans cache de shaders, « Ouvrir MAUS » sans droits, rapport de batterie sans droits, PresentMon verrouillé, **confirmation affichée par le moteur** `IEngineConfirmation`, `packages.lock.json` + `RestoreLockedMode` sur GitHub). 1 620 tests. Le document du Store décrit la **0.8.4**. Version d'essai et paquet du Store fabriqués sur le PC du porteur le 29/10 (voir `CLAUDE.md`, journal). **À vérifier à l'écran** : confirmation du moteur (fermer un processus, PawnIO, « voulu »), bandeau et options grisées de la 0.8.3, « Ouvrir MAUS » après l'audit de la semaine, nouvel accueil après un audit (score qui monte, « Votre PC en un coup d'œil », ligne M·A·U·S comptée), fond Mica sur un PC où la transparence de Windows est activée. Script d'interface : `.worktrees\_smoke\accueil2.ps1` (`-Screen` pour Mica ; `bin-mica` = copie sans rendu logiciel). Publication (GitHub, winget) : avec l'accord du porteur ; Store : envoyé par le porteur.
- Microsoft Store : soumis par le porteur lui-même. 4e refus le 29/10 (10.6.3, liste de 10 points) : réponse = `docs/store-security-design.md`, à publier sur le site d'aide (page `security-design`) et à citer dans les notes de certification. La 0.8.0 a été refusée (motif 10.6.3, `allowElevation` : demande d'autorisation dès l'ouverture) ; la 0.8.1 ne demande plus rien à l'ouverture, son paquet et ses textes sont dans `publish/store-0.8.1` (site web et assistance = https://sites.google.com/view/maus-help/accueil). **Ne pas** soumettre à sa place.

## 2. Publier une version (procédure suivie pour la 0.7.6, la 0.8.0 et la 0.8.1)

Seulement avec l'accord du porteur.

1. Numéro (règle du porteur, `CLAUDE.md`), `Directory.Build.props`, `AppVersionTests`, `CHANGELOG.md`, `README.md`.
2. Fusion dans `main` (`git fetch` d'abord : le porteur modifie parfois `main` sur GitHub). Attendre que le workflow « Installateur » réussisse (il lance d'abord tous les tests).
3. `gh` s'utilise avec `GH_TOKEN` tiré de `git credential fill` (ne jamais l'afficher) ; pas de portée `read:org` : `gh pr edit` échoue, passer par `gh api`.
4. Télécharger l'installateur du workflow (`gh run download`), vérifier son SHA-256 et sa provenance (`gh attestation verify … -R TERMINATORmdel101/MAUS`), écrire les notes en anglais (`publish\release-X.Y.Z\notes-de-version-X.Y.Z.md`, mention « non signé » exigée), `gh release create X.Y.Z --prerelease`, puis retélécharger depuis la page publique et comparer le SHA-256.
5. winget : copier le dossier de la version précédente dans `installer/winget/manifests/t/TERMINATORmdel101/MAUS/`, changer version, date, adresse et SHA-256, `winget validate --manifest`, puis dans le clone `.worktrees\_winget` : branche `TERMINATORmdel101.MAUS-X.Y.Z` partie de `upstream/master`, commit « New version: TERMINATORmdel101.MAUS version X.Y.Z », envoi sur le fork, `gh pr create -R microsoft/winget-pkgs` (texte calqué sur #451236). Validation automatique puis modérateur bénévole (1 à 2 jours).

## 3. Microsoft Store (avis donné au porteur le 06/10, sources Microsoft Learn vérifiées)

(1) Installateur .exe/.msi : « The binary and all of its Portable Executable (PE) files must be digitally signed with a code signing certificate that chains up to … the Microsoft Trusted Root Program » (page « App package requirements for MSI/EXE app ») : impossible tant que MAUS n'est pas signé. (2) Paquet MSIX (signé gratuitement par Microsoft) : l'élévation demande la capacité restreinte `allowElevation`, « subject to approval under strict criteria » ; les écritures dans HKCU d'une application empaquetée vont dans une copie privée sauf avec `unvirtualizedResources` (autre capacité restreinte). **Depuis la 0.8.1**, l'interface ne demande plus rien à l'ouverture (manifeste `asInvoker`) et lance le moteur par `runas` à la première action administrateur. **Vérifié le 22/10** sur le PC du porteur avec le paquet installé (signé par un certificat d'essai) : le moteur s'élève depuis `WindowsApps` avec `allowElevation` ; l'API DISM échoue dans un paquet (remplacée par `dism.exe /CheckHealth`). **Pas vérifié** : la même chose sans `allowElevation` (variante prête dans `artifacts/msix-essai`). Avis : ne rien payer ; redemander la signature gratuite à la SignPath Foundation quand le projet sera plus connu (refus du 03/10 : manque de visibilité).

## 4. Règles de sécurité (rappel)

- **Ne jamais** lancer MAUS en administrateur ni une commande qui modifie le PC du porteur **sans son accord explicite** ; ne jamais fermer un MAUS ouvert par le porteur. Les corrections, PawnIO, la tâche planifiée et les actions sur les pilotes se testent d'abord dans **Windows Sandbox** (pas activé sur le PC du porteur).
- En lecture seule, sans accord particulier : `dotnet build`, `dotnet test`, `maus` (audit), `maus --plan`, `maus --summary`.
- Le terminal de Claude Code tourne en administrateur sur le PC du porteur : une copie d'essai lancée depuis lui prend le chemin « interface déjà administrateur » (moteur dans le même processus, pas d'écran d'autorisation). Le vrai moteur séparé s'essaie par un petit client (`RemoteMausEngine` avec une `EngineKey` + `MAUS.exe --engine <canal> <pid> <empreinte>`).

## 5. Encore à valider sur d'autres PC que celui du porteur

Le PC du porteur (i7-8700K, MSI Z390, DDR4, RTX 2080 Ti, Windows 11) a servi à tout vérifier en lecture. Restent, faute de matériel ou de Windows Sandbox :
1. Corrections réelles (`--apply-recommended`, point de restauration, journal, `--revert`) dans Windows Sandbox.
2. Processeurs AMD (fiche mémoire : registres UMC, table PM, à comparer avec ZenTimings), DDR5, autres familles Intel (comparer avec CPU-Z puis passer `verifiedOnHardware` à `true`), cartes graphiques AMD et Intel (benchmark, lancer de rayons absent).
3. Actions sur le pilote graphique (redémarrer, retirer, supprimer, réinstaller une sauvegarde) dans Windows Sandbox.
4. Points « à vérifier » de `CLAUDE.md` et de `docs/fiche-technique/`.

## 6. Demandes du porteur non terminées

- **Vraies tensions VDD / VDDQ des barrettes DDR5** (demande du 25/09/2026) : lire le PMIC de chaque barrette par le SMBus, **en lecture seule** (une écriture dans un PMIC change la tension). Rien n'est codé. Avant d'écrire une ligne : trouver une **source vérifiable** de la carte des registres (spécification JEDEC du PMIC5100 / PMIC5010, ou code libre qui la lit). Impossible à essayer sur le PC du porteur (DDR4).
- Relecture des traductions anglaises et espagnoles par des locuteurs natifs ; relecture juridique de `TRADEMARKS.md`.
- Catalogue des pilotes graphiques `m09-gpu-drivers.json` : à revérifier chaque mois (dernière vérification : 27/10/2026 ; un ticket GitHub le rappelle chaque mois).

## 7. Outils utiles

- Traductions : `dotnet run tools/i18n.cs -- missing en` (et `es`) ; écrire un fichier JSON `{ "texte français": "traduction" }` puis `dotnet run tools/i18n.cs -- merge en fichier.json` ; `prune en|es` retire les traductions inutiles. Les `{0}` et les espaces en début / fin doivent être identiques.
- Seuils de sécurité : `src/Maus.Core/Catalog/hw-safety-limits.json`. **Pas de source = pas de seuil.**
- Sources des emplacements SPD et des registres mémoire AMD : `THIRD-PARTY-NOTICES.md` et l'en-tête de `src/Maus.Core/Workshop/Memory/SpdDecoder.cs`.
- Copie d'interface jetable : `.worktrees\_smoke` (`sync.sh`, `tour.ps1`, `atelier.ps1`, `constats.ps1`, `fps2.ps1`, `fps3.ps1`, `v082.ps1` qui sauve et remet les préférences du porteur ; captures dans `shots-light` / `shots-dark`).
- Benchmark : éviter les passes accélérées en plein écran (le porteur les voit défiler) ; préférer `maus --benchmark --capture DOSSIER --scene ID --times 5,30` (hors écran). Pièges HLSL notés dans le journal de `CLAUDE.md` (06/10).
- Code mort : construire avec `-p:EnforceCodeStyleInBuild=true` et, le temps de la mesure, `IDE0051`, `IDE0052`, `IDE0005`, `CA1823` en `warning` dans `.editorconfig` (à remettre ensuite). Nettoyé le 13/10.
