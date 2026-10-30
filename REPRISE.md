# Reprise — aide-mémoire de Claude

À lire après `CLAUDE.md`. Ce fichier ne garde que ce qui sert encore pour reprendre le travail ; l'historique des reprises de septembre et d'octobre 2026 (sessions cloud, ancien PC `C:\Users\CARO`, branches des agents de la 0.5.3) est dans l'historique Git de ce fichier (`git log -p REPRISE.md`), le reste dans `docs/historique-claude.md`.

## 1. Où en est le projet (30/10/2026)

- **0.8.5 publiée** le 30/10 : https://github.com/TERMINATORmdel101/MAUS/releases/tag/0.8.5 (préversion, commit `f0106f8`, SHA-256 `18bc9597…50b9`, provenance et téléchargement public vérifiés). Relecture complète du code (douze cartes, quatre angles d'attaque : aucune faille critique ni haute), moteur plus strict, document du Store rendu exact. Détail : `Documents\MAUS-sauvegarde-20261030\analyse-claude\00-synthese.md` (§14). La 0.8.4 (29/10) contenait le nouvel accueil et la sécurité du moteur.
- **winget** : #466403 (0.8.5) en attente d'un modérateur ; #465525 (0.8.4) fusionnée le 30/10.
- **Microsoft Store** : 5e refus de `allowElevation` reçu le 30/10 (même liste de 10 points que le 29/10). Paquet **0.8.5** prêt dans `publish/store-0.8.5` (MSIX SHA-256 `d7eb1a33…20c4`, textes, nouveautés fr/en/es, document de sécurité en .md et .html). Textes de soumission enrichis dans `installer/msix/README.md` (paragraphe « New in this submission (0.8.5) », lien GitHub du document). Document de sécurité : https://github.com/TERMINATORmdel101/MAUS/blob/main/docs/store-security-design.md, et Google Doc « MAUS – Security design of the elevated engine (Microsoft Store, 0.8.5) » créé le 30/10 dans le dossier Drive « MAUS Support » (https://docs.google.com/document/d/1Cnp1OoTvAcZBMT-uBmJiibfN-cRLaN8Swy1Ui4Yj8qw/edit). **Reste à faire par le porteur** : partager ce Doc « Tous les utilisateurs disposant du lien » (lecture), l'insérer dans Google Sites (Insérer → Docs) sur une page au chemin `security-design`, publier le site, vérifier https://sites.google.com/view/maus-help/security-design (404 le 30/10) ; puis soumettre. C'est le porteur qui soumet : **ne pas** le faire à sa place.
- Branche de travail : `claude/youthful-ride-ivazlw` ; `main` = ligne de publication (un envoi sur `main` lance les workflows « Tests » et « Installateur »).
- **Sauvegarde hors dépôt** : `Documents\MAUS-sauvegarde-20261030` (paquets, scripts d'essai, rapport de failles privé). Historique détaillé : `docs/historique-claude.md`.
- Rapport de failles (privé, 10 points) : 6 corrigés ; 4 faibles ou voulus laissés tels quels, au choix du porteur. **Ne rien faire sans lui, ne rien en écrire dans le dépôt.** Pour Microsoft, l'UAC est une défense en profondeur, pas une frontière de sécurité (Windows Security Servicing Criteria, https://www.microsoft.com/en-us/msrc/windows-security-servicing-criteria).

### À faire en premier

1. **Site d'aide** : le Google Doc du document de sécurité doit être en ligne sur la page `security-design` (voir ci-dessus) **avant** la soumission au Store.
2. **Store** : soumettre `publish/store-0.8.5/MAUS-0.8.5.msix` avec les textes de `installer/msix/README.md` (notes des capacités restreintes, notes pour la certification, nouveautés 0.8.5). Le refus du 30/10 dit que seule une information nouvelle ou élargie change l'issue : le paragraphe « New in this submission » et le lien du document sont cette information.
3. **winget** : vérifier la fusion de #466403 ; `winget show TERMINATORmdel101.MAUS --versions --source winget` doit montrer 0.8.5.
4. **Essai du porteur** (installateur `publish/release-0.8.5/artefact`) : ce que la 0.8.5 change à l'écran (PawnIO sans gel, confirmation du moteur pour redémarrer ou sauvegarder le pilote, message de la fiche mémoire si le moteur s'est arrêté) et ce qui n'a jamais été vu depuis la 0.8.4 (score après un audit, « Votre PC en un coup d'œil », bandeau et retour au gris après 15 minutes, « Ouvrir MAUS » après l'audit de la semaine, Mica avec la transparence activée). Test fragile sur GitHub : `CurveOptimizerProgramTests.Silent_core_in_phase_1…` (dépend du temps ; relancer suffit, marge à ajouter).
5. **Décisions du porteur** (il répond par numéros ; l'ordre de travail complet, 1 à 31, est dans la synthèse privée §9.7, §10.8, §11.7, §12.6, §13.6) : 15 réseau pendant l'audit hebdomadaire (Windows Update, winget) ; 18 seuils sans source (une trentaine ; principe 7) ; 16 « Garder » de l'essai de fréquence hors journal ; 10 espace disque et arrêt de processus en administrateur ; 28 `schedule.get` qui recrée la tâche ; 20 fiches techniques périmées, doublons M01/M03 au score, M04 sur Famille ; 2 liste blanche de `SettingsAccessor` ; 4 paquet du Store fabriqué par GitHub Actions ; 7 tests de `Maus.Cli` et de `SettingsAccessor`. Aussi : « Le plus CC++ possible » compris comme « le plus C# possible » : à confirmer.
6. **Idées proposées, à choisir** (il répond souvent « 1 ok, 3 ok ») ; ordre conseillé 1, 2, 3 :
   1. Essai automatique de la fenêtre à chaque envoi (GitHub Actions, UI Automation, aucun journal de plantage attendu).
   2. Tests « données abîmées » sur tout ce qui lit l'extérieur (SPD, winget, powercfg, préférences, journal, canal).
   3. Pannes au pire moment (disque plein, moteur tué, registre qui refuse, à chaque étape du `FixEngine`) : « Annuler » doit tout remettre.
   4. Endurance 24 h, mesures en direct ouvertes (sur son PC, avec son accord).
   5. Stryker.NET (« tester les tests »).
   8. Badge OpenSSF Scorecard (gratuit, aide pour SignPath).
   9. SBOM jointe à chaque version.
   11. Passage dans Accessibility Insights.
   12. Messages d'erreur tous utiles.
   13. Discipline : GitHub = bêta, Store = stable après quelques jours sans problème signalé.

### Conseils déjà donnés au porteur (rester cohérent)

- Lancement, dans l'ordre : acceptation au Store ; essais sur des PC variés (AMD, portables, autres cartes graphiques) ; visibilité (**c'est lui qui écrit sur les forums**, Claude ne rédige pas ses messages à la communauté ; textes pour Microsoft = oui) ; étoiles GitHub, nouvelle demande à SignPath. Garder l'honnêteté au centre.
- Abonnement (il envisage l'offre supérieure) : une session = un sujet ; grouper les demandes dans un message ; pas deux sessions en même temps sur le même travail ; workflows à plusieurs agents et effort maximal seulement pour les grosses vérifications et la sécurité.

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
4. Points « à vérifier » de `docs/historique-claude.md` et de `docs/fiche-technique/`.

## 6. Demandes du porteur non terminées

- **Vraies tensions VDD / VDDQ des barrettes DDR5** (demande du 25/09/2026) : lire le PMIC de chaque barrette par le SMBus, **en lecture seule** (une écriture dans un PMIC change la tension). Rien n'est codé. Avant d'écrire une ligne : trouver une **source vérifiable** de la carte des registres (spécification JEDEC du PMIC5100 / PMIC5010, ou code libre qui la lit). Impossible à essayer sur le PC du porteur (DDR4).
- Relecture des traductions anglaises et espagnoles par des locuteurs natifs ; relecture juridique de `TRADEMARKS.md`.
- Catalogue des pilotes graphiques `m09-gpu-drivers.json` : à revérifier chaque mois (dernière vérification : 27/10/2026 ; un ticket GitHub le rappelle chaque mois).

## 7. Outils utiles

- Traductions : `dotnet run tools/i18n.cs -- missing en` (et `es`) ; écrire un fichier JSON `{ "texte français": "traduction" }` puis `dotnet run tools/i18n.cs -- merge en fichier.json` ; `prune en|es` retire les traductions inutiles. Les `{0}` et les espaces en début / fin doivent être identiques.
- Seuils de sécurité : `src/Maus.Core/Catalog/hw-safety-limits.json`. **Pas de source = pas de seuil.**
- Sources des emplacements SPD et des registres mémoire AMD : `THIRD-PARTY-NOTICES.md` et l'en-tête de `src/Maus.Core/Workshop/Memory/SpdDecoder.cs`.
- Copie d'interface jetable : `.worktrees\_smoke` (`sync.sh`, `tour.ps1`, `atelier.ps1`, `constats.ps1`, `fps2.ps1`, `fps3.ps1`, `v082.ps1`, `accueil2.ps1` (`-Screen` pour Mica ; `bin-mica` = sans rendu logiciel), qui sauvent et remettent les préférences du porteur ; copie des scripts dans `Documents\MAUS-sauvegarde-20261030\worktrees-smoke` ; captures dans `shots-light` / `shots-dark`).
- Benchmark : éviter les passes accélérées en plein écran (le porteur les voit défiler) ; préférer `maus --benchmark --capture DOSSIER --scene ID --times 5,30` (hors écran). Pièges HLSL notés dans le journal de `docs/historique-claude.md` (06/10).
- Code mort : construire avec `-p:EnforceCodeStyleInBuild=true` et, le temps de la mesure, `IDE0051`, `IDE0052`, `IDE0005`, `CA1823` en `warning` dans `.editorconfig` (à remettre ensuite). Nettoyé le 13/10.
