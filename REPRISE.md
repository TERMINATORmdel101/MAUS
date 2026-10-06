# Reprise en local — aide-mémoire de Claude

Fichier écrit à la fin des sessions cloud du 25/09/2026 pour reprendre le travail **sur le PC Windows du porteur**. À lire après `CLAUDE.md` (qui reste la mémoire complète du projet). Le mettre à jour, ou le vider, une fois la reprise faite.

## À FAIRE EN PREMIER : reprise locale du 05/10/2026

Le porteur repasse en local (Claude Code installé sur son nouveau PC). Branche de travail : **`claude/youthful-ride-ivazlw`** (sessions cloud du 03 au 05/10), à récupérer par `git fetch origin` puis `git checkout claude/youthful-ride-ivazlw`. Elle contient, par-dessus `main` :
- défaut 2 (module en échec = faux vert) : repris de `8fd44d6`, relu, testé ;
- défaut 1 (`ThermalWatchdog`) : terminé et testé. **À dire au porteur** : l'agent a retiré les alarmes « carte graphique très chaude » (90 °C, carte sans seuil NVML) et « zone thermique » (95 °C), faute de seuil publié (principe 7) ; seuls le seuil de la carte, celui du processeur et la mémoire pleine déclenchent encore une alarme ;
- défauts 3 et 6 : **en cours**, commit `4b10b8b` marqué « NON vérifié ».

**06/10/2026 (suite) : 0.6.2 PUBLIÉE** sur GitHub (https://github.com/TERMINATORmdel101/MAUS/releases/tag/0.6.2, préversion, installateur du workflow vérifié SHA-256 77b4a1e9…a3, texte honnête : non signé, SignPath refusé pour l'instant ; README corrigé dans le même sens). `gh` n'est pas connecté : il s'utilise avec `GH_TOKEN` tiré de `git credential fill` (portées gist, repo, workflow ; pas de read:org, donc `gh pr edit` échoue : passer par `gh api` REST). Demande winget **#445892 mise à jour en 0.6.2** (titre et fichiers) ; toujours **bloquée par le CLA de Microsoft, que seul le porteur peut signer** (commentaire `@microsoft-github-policy-service agree` sur la demande), puis approbation d'un modérateur.

**06/10/2026 : version 0.6.2 FAITE et fusionnée dans `main`** : compteur d'images par seconde en direct et au-dessus du jeu (PresentMon d'Intel livré, taille / opacité / coin réglables), latence des pilotes (trace ETW du noyau), santé NVMe, idées reçues sourcées, pages Pilotes et En direct découpées. Copie d'essai : raccourci « MAUS 0.6.2 (essai) » → `publish\MAUS-0.6.2`. **Reste** : (1) essai du porteur en jeu (compteur au-dessus du jeu en plein écran) ; (2) **publication** : `gh` (GitHub CLI 2.102.0, `C:\Program Files\GitHub CLI\gh.exe`) installé le 06/10 mais **pas connecté** : le porteur doit lancer `gh auth login` ; ensuite créer la version 0.6.2 sur GitHub (installateur fabriqué par le workflow « Installateur », mention SignPath exigée sur la page) et mettre à jour winget (0.5.2 → 0.6.2) ; (3) piste « pilote en cause d'un écran bleu » en attente d'un vrai vidage ; (4) relecture des traductions par des natifs.

**05/10/2026 (soir) : version 0.6.1 FAITE (numérotée 0.7.0 par erreur, corrigée) et fusionnée dans `main`** (suggestions 1 à 12 acceptées par le porteur : « tu peux tout faire »). L'installateur est fabriqué par GitHub Actions (onglet Actions, workflow « Installateur »). Copie d'essai sur le bureau du porteur : raccourci « MAUS 0.6.1 (essai) » → `publish\MAUS-0.6.1`. **Reste, avec l'accord du porteur après son essai** : publier la version 0.6.1 sur GitHub (Releases, avec la mention SignPath exigée), mettre à jour le paquet winget (0.5.2 → 0.6.1, demande winget-pkgs), faire relire les traductions par des locuteurs natifs (n° 13, impossible pour Claude). Pistes « experts » : `docs/pistes-experts.md` (5 pistes sourcées, à choisir par le porteur).

**Étape 5 FAITE le 05/10/2026 : version 0.6.0** (accueil, constats, corrections, tests, pastel ; captures vérifiées en clair et en sombre). Reste : avis du porteur sur la nouvelle présentation ; fenêtres annexes (paramètres, à propos, score, surveillance) en pastel si le porteur le souhaite ; fusion de la branche dans `main` et nouvel installateur avec son accord.

**Étapes 1 à 4 FAITES le 05/10/2026 (local)** : défauts 3 et 6 finis (`c460a7c`), défauts 4 et 5 repris et fusionnés (`008d23f`), défaut 7 repris, testé et branché à l'écran (`9693700`), version **0.5.3** (0 avertissement, 1 421 tests). Reste : faire essayer l'interface de la 0.5.3 au porteur, puis l'étape 5 (0.6.0). Python 3.13 installé le 05/10 par winget (accord du porteur : « tu peux installer ce que tu as besoin »), dans `%LOCALAPPDATA%ProgramsPythonPython313python.exe` (absent du PATH des consoles ouvertes avant l'installation).

Ordre de travail (historique) :
1. **Finir les défauts 3 et 6** : supprimer les champs `French` devenus inutiles (M02 `WindowsHealthModule`, M10 `MemoryModule`, M11 `HardwareHealthModule`, M12 `StartupAppsModule`, M14 `DisplayModule`) ; traduire `{0:0.0} Go`, `{0:0.0} Go/s` et `{0:0.##} Go` dans `en.json` et `es.json` (« GB ») ; tests : dates et nombres en anglais (`Texts.Use("en")` dans la collection `LanguageSwitch` ; `WindowsUpdateModuleTests.Detect` et `FakeUpdateAgent` sont désormais internes pour cela), `OemEncoding.Get(850)` (octet 0x82 = « é »), CSV au format régional (`Texts.RegionalCulture`) ; compiler (0 avertissement) et tester.
2. **Défauts 4 et 5** : branche `worktree-wf_bb647255-789-4` (`8968aab`, NON vérifié) : filet de sécurité (`Diagnostics/CrashLog`, `BackgroundTasks`), un seul MAUS (`SingleInstance`), M03 `ComWindowsUpdateAgent`. Conflits attendus dans `App.xaml.cs`, `TaskSchedulerClient.cs` et les traductions : fusionner les JSON clé par clé (ajouts et retraits de la branche), jamais à la main ligne à ligne.
3. **Défaut 7** : branche `worktree-wf_bb647255-789-5` (`471d279`, NON vérifié, **sans tests**) : `Platform/AtomicFile`, `JournalStore`, `PreferencesStore`, `BenchmarkHistory`, `MemorySnapshots`. Écrire les tests (fichier tronqué, écriture interrompue, fichier illisible).
4. **Version 0.5.3** : `Directory.Build.props`, `AppVersionTests`, `CHANGELOG.md`.
5. **Demandes d'interface du porteur (05/10/2026)**, après la 0.5.3. C'est une modification moyenne, donc la version **0.6.0** :
   - **Accueil** plus simple ; une fois l'audit terminé, proposer directement de corriger.
   - **Constats** : ne pas montrer ce qui va bien ; montrer d'abord les plus importants (ce qui pose problème), le reste seulement à la demande.
   - **Corrections** : alléger la page, séparer les zones par de petites barres.
   - **Atelier > Tests** : bien démarquer visuellement chaque partie.
   - **Tout le programme** : beaucoup plus de couleurs pastel (thèmes clair et sombre, textes toujours lisibles).
   Vérifier chaque écran sur le PC (copie de test `asInvoker`, voir « Tests d'interface » dans `CLAUDE.md`) ; les scripts `_smoke` sont dans `Documents\MAUS-ancien\.worktrees\_smoke` (chemins `C:\Users\CARO` à adapter).

## Nouveau PC (03/10/2026)

L'ancien Windows (`C:\Users\CARO`) est perdu. Les chemins `C:\Users\CARO\Documents\MAUS\…` ci-dessous deviennent `C:\Users\T-800-MODEL-101\Documents\MAUS-ancien\…` (ancien dossier récupéré) ; le dépôt de travail est un clone neuf dans `C:\Users\T-800-MODEL-101\Documents\MAUS`. Les branches des agents de la 0.5.3 sont maintenant **sur GitHub** (`git fetch origin` puis `git checkout worktree-wf_bb647255-789-2`, etc.). `scan.md` et `wf-053.js` : `MAUS-ancien\.worktrees\_smoke\` ou `Documents\MAUS-notes-sauvegarde.zip`. À réinstaller au besoin : Inno Setup 7.1.0 (installateur ; GitHub Actions le fabrique aussi), Claude Code.

## 0. État au 29/09/2026 (session locale sur le PC du porteur)

- Version **4.0.0** (numéros choisis par le porteur : 0.3.3-alpha, puis 3.9.1, puis 4.0.0), plus les ajouts du 29/09 (numéro à choisir). Compilation 0 avertissement, **1 315 tests réussis** sous Windows.
- **0.5.3 — EN PAUSE (limite hebdomadaire Claude atteinte le 30/09, retour le 05/10)**. Le porteur a dit « OK pour TOUS » les 7 défauts, et a demandé « qu'est-ce qui manque pour les experts du PC ? » (recherche pas encore faite : relancer le script `.worktrees/_smoke/wf-053.js` avec `{"mode":"research"}`, ou le réécrire). Numéro de version remis à 0.5.2 le 02/10 (installateur de la 0.5.2) : repasser à 0.5.3 (`Directory.Build.props`, `AppVersionTests`) avec les corrections. Travail des agents, chacun dans sa branche locale :
  - Défaut 2 (module en échec = faux vert) : **terminé et testé** (1 363 tests, 0 avertissement), pas encore relu : `worktree-wf_bb647255-789-2`, commit 8fd44d6.
  - Défaut 1 (surchauffe pendant les tests longs, `ThermalWatchdog`) : partiel, `worktree-wf_bb647255-789-1` (09c21c2).
  - Défauts 3 et 6 (réglages régionaux, page OEM, dates) : partiel, `worktree-wf_bb647255-789-3` (c5c0d29).
  - Défauts 4 et 5 (filet de sécurité, un seul MAUS) : partiel, `worktree-wf_bb647255-789-4` (8968aab).
  - Défaut 7 (fichiers illisibles, `AtomicFile`) : partiel, `worktree-wf_bb647255-789-5` (471d279).
  Les commits partiels sont marqués « NON vérifié » : les relire, finir, compiler et tester avant de les reprendre (cherry-pick) ; le détail de chaque défaut est dans `.worktrees/_smoke/scan.md` et `.worktrees/_smoke/wf-053.js` (copies hors dépôt).
- **0.5.2 — à faire valider** : « Arrêter le test » pendant la préparation (mémoire vive, mémoire vidéo, disque), réponses des boutons de l'onglet Corrections affichées sur place, boutons « Ouvrir dans Windows » (chaque page doit s'ouvrir au bon endroit), Historique : correction « interrompue ». Dossier prêt : `C:\Users\CARO\Documents\MAUS\publish\MAUS-0.5.2`. Compilation 0 avertissement, **1 353 tests réussis**.
- **0.5.1 — à faire valider** : audit (constats « Mises à jour de pilotes proposées par Windows Update » et « Pilotes sans signature numérique », bouton « Ouvrir dans Windows »), bouton « Mises à jour de pilotes (Windows Update) ».
- **0.4.2 — à faire valider** : score (« Pourquoi ce score ? »), onglet Atelier > Pilotes (liste en lecture seule sur le PC du porteur ; les boutons redémarrer / retirer / sauvegarder / supprimer / réinstaller une sauvegarde se testent d'abord dans **Windows Sandbox**, jamais sur le PC du porteur sans son accord).
- **Ajouts du 29/09, à faire valider par le porteur** : fenêtre « À propos » (bouton i), écran de démarrage, icône M dans la barre des tâches, logo du rapport HTML, « Signaler sur GitHub » dans « Demander de l'aide » (dépôt privé : la page ne s'ouvre que pour ses membres), Mon PC enrichi et « Copier la fiche » (TPM lisible seulement en administrateur), animations. Si des fenêtres de MAUS restent blanches : redémarrer le PC (constaté le 29/09 pour toutes les applications WPF, pas seulement MAUS).
- **4.0.0** : sept charges pour les tests du processeur (`CpuStress`), programme complet Curve Optimizer (`CurveOptimizerProgram`, 1 h ou 4 h), température du processeur pendant les tests, lien PCIe sans fausse alerte sur portable, anti-veille (`KeepAwake`). Relu par workflow : 9 défauts confirmés et corrigés. **À essayer par le porteur** (PC Ryzen 9 9900X annoncé) : programme d'1 h d'abord, avec PawnIO pour la température ; vérifier l'épinglage par cœur sur les deux CCD.
- Vérifié sur le vrai PC du porteur (i7-8700K, MSI Z390, DDR4-3467) : timings réels Skylake à Comet Lake (`verifiedOnHardware: true` pour `skl-cml`), textes blancs du thème sombre. Tension mémoire : Windows donne celle déclarée par le BIOS (1,25 V) pour 1,45 V réglés ; l'étalonnage par l'utilisateur existe, **pas encore essayé par le porteur**.
- **Générations mémoire faites** (catalogue `src/Maus.Core/Catalog/intel-memory-controller.json`, chaque champ sourcé) : Sandy / Ivy Bridge, Haswell / Broadwell, Skylake à Comet Lake, Ice Lake / Rocket Lake, Tiger Lake, Alder / Raptor Lake, Meteor / Arrow / Lunar Lake. Seule `skl-cml` est vérifiée sur un vrai processeur ; les autres affichent « pas encore comparée à CPU-Z ». Champs écartés car les sources se contredisent : horloge mémoire Haswell / Broadwell, tRRD et tRDPRE Haswell, horloge Meteor / Arrow Lake en gear 4, horloge et réglages Lunar Lake.
- AMD : table Raven Ridge / Picasso (0x1E0004) ajoutée ; Renoir 0x370000 à 0x370002 en disposition générique « à vérifier » ; alerte UCLK > MCLK.
- Revue de sécurité des fonctions qui modifient le PC faite le 28/09 (voir `CHANGELOG.md`, 3.9.1) : trois corrections (retour arrière des corrections sur toute erreur, arrêt du test RAM en surchauffe, tâche planifiée seulement depuis un dossier protégé).
- Recherches de registres sauvegardées (une ligne JSON par fait sourcé) : `C:\Users\CARO\Documents\MAUS\.worktrees\_pdf\registers\*.partial.jsonl` et `v2\`.
- **À faire valider par le porteur** : ouvrir l'onglet Mémoire de la 3.9.1 (plus de blocage ? sinon lire `%LOCALAPPDATA%\MAUS\logs\interface-bloquee.txt` et `lecture-memoire.txt`), lecture des puces SPD avec PawnIO, étalonnage de la tension avec ses 1,45 V, fenêtre de surveillance (courbes, relevé, export).
- Toute personne ayant un autre processeur Intel peut comparer avec CPU-Z : si tout concorde, passer `verifiedOnHardware` à `true` pour sa famille, avec un commentaire daté (modèle, mémoire).

## 1. Où en est le code

- Version : **0.5.2** (`Directory.Build.props`, `CHANGELOG.md`).
- Le travail est sur la branche **`claude/keen-wozniak-93as1n`** (poussée sur GitHub). **Fusionnée dans `main` le 29/09/2026** (accord du porteur ; avance simple de `main`, sans conflit), version 0.5.2. Le dossier du dépôt principal (`C:\Users\CARO\Documents\MAUS`, branche `main`) doit faire `git pull` pour la récupérer.
- Sous Linux : compilation 0 avertissement, 1 185 tests dont **12 échecs attendus** (ils appellent de vraies API Windows : signature de fichiers, journaux d'événements, chemins `C:\`, raccourcis). **Sous Windows, les 1 185 doivent passer.**
- Rien de ce qui a été codé dans le cloud n'a tourné sur un vrai Windows : l'interface WPF n'a jamais été ouverte depuis la refonte.

## 2. Récupérer le travail sur le PC (PowerShell 5.1, pas de `&&`)

```powershell
cd <dossier du dépôt MAUS>
git status                      # vérifier qu'il n'y a pas de travail local non commité
git fetch origin
git checkout claude/keen-wozniak-93as1n
git pull origin claude/keen-wozniak-93as1n
dotnet build Maus.slnx --disable-build-servers
dotnet test Maus.slnx
dotnet run --project src/Maus.Cli -- --version    # doit afficher « MAUS 4.0.0 »
```

Identité Git des commits : `TERMINATORmdel101 <213405999+TERMINATORmdel101@users.noreply.github.com>` (jamais l'adresse personnelle : GitHub refuse le push).

## 3. Règles de sécurité (rappel)

- **Ne jamais** lancer MAUS en administrateur ni une commande qui modifie le PC du porteur **sans son accord explicite**. Les corrections, PawnIO, la tâche planifiée et les tests de charge se testent d'abord dans **Windows Sandbox**.
- En lecture seule, sans accord particulier : `dotnet build`, `dotnet test`, `maus` (audit), `maus --plan`, `maus --summary`.
- Interface sans fenêtre UAC : `requireAdministrator` → `asInvoker` temporairement dans `src/Maus.App/app.manifest`, compiler dans un dossier ignoré, tester, **puis restaurer le manifeste**.

## 4. À valider sur Windows, dans cet ordre

1. `dotnet test` : les 12 tests qui échouent sous Linux doivent passer.
2. L'interface s'ouvre (Accueil, Constats, Corrections, Atelier et ses onglets dont Mémoire et Stockage, Historique, changement de langue) sans plantage ; sinon lire `%LOCALAPPDATA%\MAUS\logs` et le journal « .NET Runtime ».
3. Windows Sandbox : `maus --plan`, `--apply-recommended` (point de restauration, journal, ACL), `--journal`, `--revert`.
4. Atelier sans pilote : valeurs comparées au Gestionnaire des tâches (PDH, D3DKMT, NVML), tests CPU / RAM / VRAM courts, test cœur par cœur, test de disque, test de connexion, analyse du stockage.
5. Avec l'accord du porteur : PawnIO (installation par winget dans une console visible), capteurs avancés, **fiche mémoire** comparée à ZenTimings / CPU-Z (SPD, timings, FCLK/UCLK/MCLK), relevé de partie, audit hebdomadaire (création, lancement sans fenêtre, notification, retrait).
6. `winget upgrade` réel (colonnes, encodage choisi d'après les octets, codes de retour) ; réparation DISM / SFC.
7. Points listés « à vérifier » dans `CLAUDE.md` (section État) et dans `docs/fiche-technique/`.

## 5. Ce qui reste à faire (demandes du porteur non terminées)

- **Vraies tensions VDD / VDDQ des barrettes DDR5** (demande du 25/09/2026) : lire le PMIC de chaque barrette par le SMBus, **en lecture seule** (jamais d'écriture : une écriture dans un PMIC change la tension). Rien n'est codé. Avant d'écrire une ligne : trouver une **source vérifiable** de la carte des registres (spécification JEDEC du PMIC5100 / PMIC5010, ou code libre qui la lit), et ne rien afficher tant qu'une valeur n'a pas été comparée à un outil de référence sur une vraie barrette. Aujourd'hui MAUS affiche seulement le type de PMIC (octet du SPD) et les tensions **demandées** par les profils XMP / EXPO.
- **Comparer deux fiches mémoire** (demande du 25/09/2026) : fait le 28/09 (`MemorySnapshots.cs`, bouton « Comparer avec une lecture précédente » de l'onglet Mémoire).
- Nettoyage « fichiers inutiles » : fait pour les deux fichiers évidents (voir `CHANGELOG.md`). Aucune classe inutilisée trouvée par un balayage des noms. Reste possible : méthodes jamais appelées (à chercher avec l'analyse de Visual Studio, « Find All References »).
- Ne **pas** faire l'étape 5 (publication sur le Microsoft Store) tant que le porteur ne l'a pas demandé.
- Liste complète des points ouverts : `CLAUDE.md`, sections « Reste à faire ».

## 6. Outils utiles

- Traductions : `python3 tools/i18n.py missing en` (et `es`) liste les textes à traduire ; écrire un fichier JSON `{ "texte français": "traduction" }` puis `python3 tools/i18n.py merge en fichier.json` ; `prune en|es` retire les traductions devenues inutiles. Les `{0}` et les espaces en début / fin doivent être identiques.
- Seuils de sécurité : `src/Maus.Core/Catalog/hw-safety-limits.json`. **Pas de source = pas de seuil.** Chaque ajout cite sa source dans le champ `source`.
- Sources des emplacements SPD et des registres mémoire AMD : `THIRD-PARTY-NOTICES.md` et le commentaire en tête de `src/Maus.Core/Workshop/Memory/SpdDecoder.cs`.

## Benchmark visuel (06/10/2026, en cours — demande du porteur)

Demande : ~8 min de carte graphique + 2-3 min de processeur, « ultra lourd », DirectX 11 **et** 12, scores processeur / carte graphique / combiné, 10 000 points = PC du porteur (i7-8700K + RTX 2080 Ti), ressources libres, chaque test vise une capacité (géométrie, bande passante, calcul, textures) pour montrer les points faibles.

Fait (projet `src/Maus.Bench`, bibliothèque ; essai : `maus --benchmark --api d3d11|d3d12 --capture DOSSIER --scene fractal --times 5,30 [--camera x,y,z,cx,cy,cz,champ]`) :
- Moteurs Direct3D 11 et 12 complets derrière `IGpuDevice` / `ICommandList` (barrières automatiques, descripteurs, mémoire upload, horodatages, capture PNG hors écran) : les deux rendent la même image.
- Post-traitement (anticrénelage temporel, halo, courbe filmique, grain), police à champ de distance (`Ui/FontAtlas.cs`, `UiRenderer`), bandes anti-TDR.
- Scène 1 « Forge fractale » (Mandelbox, rayons de lumière volumétriques) : 47-75 ms par image sur la 2080 Ti ; trajet de caméra calculé pour ne jamais traverser la fractale.

Reste : régler la brume de la fractale, scènes 2-5 (anneau d'astéroïdes = géométrie ; collision de galaxies = bande passante ; aube/nuages ; échecs glTF + HDRI CC0/CC-BY), tests processeur (lancer de rayons multi-cœurs / un cœur, physique), boucle de mesure plein écran + HUD + écran des résultats, étalonnage 10 000 points, intégration dans MAUS (Atelier > Tests, `MAUS.exe --benchmark`), traductions, tests, notices des ressources tierces, numéro de version (demander : 0.7.0 si majeur).
