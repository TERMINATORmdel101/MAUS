# CLAUDE.md — mémoire du projet MAUS

Ce fichier est la mémoire de Claude entre les sessions (sur le PC du porteur du projet comme dans le cloud). Lis-le en entier avant de travailler, et mets-le à jour à la fin de chaque session importante (section « État » et « Journal »).

**Reprise sur le PC après les sessions cloud : lis aussi [`REPRISE.md`](REPRISE.md)** (branche à récupérer, ordre de validation sur Windows, demandes non terminées).

## Le porteur du projet

- Francophone, **non-développeur**, dicte souvent à la voix (fautes de frappe fréquentes : « lesse le chois » = « laisse le choix »). Réponds **en français**, simplement, sans jargon inutile, en expliquant le « pourquoi ».
- Il veut garder la main : partout où c'est possible, **le choix revient à l'utilisateur final**, avec une valeur recommandée pré-sélectionnée.
- **Budget : zéro euro.** Gratuit, open source, publication sur le Microsoft Store.
- Le logiciel **assume d'être fabriqué par une IA** (mention dans « À propos », le README, la fiche du Store).
- Il a une limite d'utilisation Claude : **commite souvent** pour ne jamais perdre de travail.

## Le projet

**MAUS** (Maintenance · Audit · Updates · Sécurité) : utilitaire Windows 11 qui audite, répare, met à jour et optimise le PC en toute transparence. Fiche technique complète : `docs/fiche-technique/` (un fichier par module ; `00-decisions.md` prime sur le reste). Version vivante de la fiche : document Claude privé du porteur (claude.ai, « Fiche technique — MAUS »).

Principes non négociables :
1. **Audit d'abord, action ensuite** : lecture seule, rapport vert / bleu / orange / rouge / gris, puis corrections choisies.
2. **Tout est réversible** : point de restauration vérifié + journal des valeurs d'origine + bouton « Annuler » (à partir de la V0.2).
3. **Tout est expliqué** en français clair : ce que fait le réglage, le gain, le risque, le retour arrière.
4. **Rien de dangereux en automatique** : BIOS, XMP/EXPO, overclocking sont guidés (liens officiels, tutoriels), jamais exécutés.
5. **Aucune télémétrie** de l'outil.
6. **Honnêteté sur les gains** (demande du porteur, 25/09/2026) : n'annoncer que des optimisations reconnues comme utiles, avec des gains réalistes (« quelques pour cent », « gain faible », « aucun gain de performance »), jamais +10 % ; ne jamais retirer une fonction importante de Windows ni une protection pour un gain de performance.
7. **Aucune donnée inventée** (demande du porteur, 25/09/2026, version 0.3.2-alpha) : chaque seuil, limite, décalage de registre ou affirmation technique vient d'une source publiée (citée dans le catalogue, le commentaire ou la fiche). **Pas de source = pas de seuil ni d'alarme** ; une valeur non vérifiée s'affiche comme telle, jamais comme un fait.

Décisions prises (24-25/09/2026) :
- Windows 11 uniquement, à partir de 23H2 (build 22631). Windows 10 hors périmètre.
- Licence **GPL-3.0-only** + conditions additionnelles article 7(c) et 7(e) : le nom « MAUS » et son logo ne sont pas cédés (voir `TRADEMARKS.md`). Pas de dépôt de marque payant pour l'instant (INPI ~190 € plus tard si le projet décolle).
- Distribution : Microsoft Store (canal officiel), GitHub, winget. Signature gratuite envisagée via SignPath Foundation (conditions à vérifier). Compatibilité Store + élévation administrateur **à vérifier**.
- Portable : choix au premier lancement (proposé : performance sur secteur, Équilibré sur batterie).
- Ryzen X3D double CCD : Utilisation normale + Game Bar recommandées, l'utilisateur peut refuser.
- Pilotes via Windows Update : jamais bloqués par défaut (option du Module 9).
- Module 13 (intégrité mémoire) : avertissement renforcé si Vanguard / FACEIT, mais l'utilisateur décide. Les atténuations CPU (Spectre…) ne sont **jamais** proposées à la désactivation.
- Profils en un clic + chaque réglage modifiable ligne par ligne.

## Architecture (C# / .NET 10, WPF)

| Dossier | Rôle |
|---|---|
| `src/Maus.Core/Abstractions` | `IAuditModule` (Detect), `Finding`, `FindingStatus` (Ok, Info, Improvable = bleu, Warning = orange, Problem = rouge, Unknown = gris), `Severity`, `AuditContext` |
| `src/Maus.Core/Platform` | Accès **en lecture seule** : registre, WMI/CIM (`ICimReader`), commandes (`ReadOnlyCommandRunner` avec liste blanche stricte), journaux d'événements, paquets Store, fichiers, SystemParametersInfo |
| `src/Maus.Core/Hardware` | Profil matériel commun (fixe/portable 2 indices sur 3, CPU X3D / Raptor Lake, GPU intégré/dédié, PC géré) |
| `src/Maus.Core/Rules` + `Catalog/*.json` | Règles de registre déclaratives, catalogues embarqués (`mXX-*.json`) |
| `src/Maus.Core/Engine` | `AuditEngine` : découverte des modules par réflexion, exécution parallèle avec délai |
| `src/Maus.Core/Modules/MxxNom/` | Un dossier par module (M01 à M20 ; M16 = Atelier matériel, M17 = périphériques et pilotes, M18 = réseau, M19 = sauvegardes, M20 = logiciels à mettre à jour par winget) |
| `src/Maus.Core/Fixes` | V0.2 : `IFixableModule.Plan`, `PlannedChange` / `SettingWrite` / `SettingKey` / `SettingValue`, `FixEngine` (Apply, Verify, Revert), `FixContext`, journal (`FileJournalStore` + `WindowsDirectoryProtector`), `RestorePointCreator` + `WmiSystemRestore`, `FixProfile` |
| `src/Maus.Core/Localization` | `Texts` : français = langue source, `T("…")` / `T("… {0}", args)`, `Optional(texte?)` pour les catalogues ; traductions `i18n/en.json` et `i18n/es.json` (embarquées) |
| `src/Maus.Core/Preferences` | Choix de l'utilisateur (`preferences.json` protégé sous `%ProgramData%\MAUS\settings`) : profil Game Bar, alimentation du portable, constats « voulus » (liés à la valeur et à la langue), langue, moteur de recherche |
| `src/Maus.Core/Reporting` | Rapports texte / JSON / HTML avant-après (`HtmlReport`), libellés, `HealthScore` (score sur 100, familles M·A·U·S) |
| `src/Maus.Core/Workshop` | Atelier sans pilote noyau : `HardwareInventory` (CPUID, WMI, registre, NVML), `SafetyLimits` (`Catalog/hw-safety-limits.json`), capteurs (`WindowsSensorSource` : PDH, D3DKMT, NVML, zones thermiques), processus (`ProcessMonitor`, `ProcessCatalog` = `Catalog/processes.json`, `ProcessRules`, `WebSearch`), `SlownessDiagnosis`, `CpuTest`, `MemoryTest`, `BenchmarkHistory` |
| `src/Maus.Cli` | `maus` : audit en ligne de commande (`--module Mxx`, `--json`, `--html`, `--lang fr|en|es`), corrections et choix (voir `--help`) |
| `src/Maus.App` | Interface WPF (thème Fluent + `Themes/Maus.xaml` aux couleurs du logo), manifeste `requireAdministrator`, filet de sécurité qui journalise les plantages dans `%LOCALAPPDATA%\MAUS\logs`. Navigation à gauche : Accueil (score, M·A·U·S, actions rapides), Constats, Corrections, Atelier (Mon PC, En direct, Processus, Tests), Historique. Contrôles dessinés dans `Controls/` (anneau, courbe, jauge de sécurité, tuile lettre) ; textes fixes dans `Ui.cs` |
| `tests/Maus.Core.Tests` | xUnit avec faux (`Fakes.cs` : FakeRegistry, FakeCim, FakeCommands, FakeEventLogs, FakePackages, FakeFiles, `TestContext.Create`) ; `App/XamlConsistencyTests` (garde-fou de l'interface, qui ne peut pas tourner sous Linux) ; `Localization/*` (couverture des traductions, bascule de langue) |
| `assets/logo/` | Logo fourni par le porteur (ne pas le redessiner) ; `tools/make-logo-assets.py` génère l'icône et les images de l'application |

Règles de code :
- **Detect et Plan = lecture seule absolue** : jamais de clé de registre ouverte en écriture, jamais de `Process.Start` hors `context.Commands`, jamais d'appel qui modifie un réglage. Tout accès système passe par `AuditContext` ou par une interface interne au module (P/Invoke, COM) avec un faux dans les tests.
- **Écrire (V0.2)** : uniquement par le `FixEngine`, via `FixContext` (jamais dans `AuditContext`). Un module corrigeable implémente `IFixableModule.Plan` et décrit ses corrections comme des écritures élémentaires (`SettingWrite` : registre, SPI, mode de gestion) ; le moteur journalise la valeur d'origine avant d'écrire, relit, défait la correction entière si une écriture échoue, et sait tout annuler. Nouveau type de réglage = nouveau `SettingKind` + lecture/écriture dans `SettingsAccessor` + faux.
- Stratégies (`Policies`) : restaurer = **supprimer** la valeur, jamais écrire la valeur « activée ». Stratégies posées seulement sur Pro et plus. Pré-coché (`Recommended`) = sans risque connu ; `Advanced` = jamais dans un profil hors de son domaine ; `Warning` = avertissement renforcé.
- Test de chaque module corrigeable : `RoundTrip.AssertAsync` (critère d'acceptation de la fiche : après Apply les constats sont conformes, après Revert un nouveau Detect est identique à l'état initial).
- Données illisibles → `Finding.Unknown` / `Finding.AdminRequired`, **jamais** un faux Problem.
- Écart → `FindingStatusExtensions.ForDeviation(severity)` : Critical/High = rouge, Medium = orange, Low = bleu (optimisation).
- Textes utilisateur en français clair ; identifiants `Mxx.nom-en-kebab`.
- **Trois langues (fr, en, es)** : tout texte affiché s'écrit en français dans `T("…")` (ou `T("… {0}", valeur)` pour un texte à trous, jamais d'interpolation `$"…"` affichée) ; la traduction se fait au moment où le texte est produit. Puis `python3 tools/i18n.py missing en` (et `es`) liste ce qui manque ; ajouter les traductions dans `src/Maus.Core/Localization/i18n/*.json` (`merge`, `prune`). Le test `TranslationCoverageTests` échoue tant qu'un texte manque, ou si les `{0}` diffèrent.
  - Un champ `static readonly` qui contient `T(…)` doit être une propriété calculée (`=>`), sinon il resterait dans la langue du premier accès.
  - Catalogues JSON : traduire au moment de l'usage (`T(rule.Title)`, `Optional(rule.Advice)`) et déclarer les champs dans `tools/i18n.py` (`CATALOGS`) **et** dans `TranslationCoverageTests.Catalogs`.
  - Ne **jamais** passer par `T` un texte qui sert à reconnaître une sortie de Windows (`bcdedit`, `netsh`… en français ou en anglais), et ne jamais faire dépendre une logique d'un texte affiché (comparer à l'objet, au code, ou au même `T(…)`).
  - Dans une méthode générique `<T>`, écrire `Texts.T(…)` (le paramètre de type masque la fonction).
- Interface WPF : toute liaison bidirectionnelle (`IsChecked`, `SelectedItem`, `Value`, colonnes de `DataGrid`…) vers une propriété sans `set` doit porter `Mode=OneWay`, sinon la fenêtre plante au chargement ; `StaticResource` seulement pour des clés définies dans nos dictionnaires, `DynamicResource` pour les pinceaux Fluent. `XamlConsistencyTests` vérifie tout cela.
- Style : espaces de noms `file-scoped`, accolades toujours, **0 avertissement** (analyseurs `latest-recommended`), pas de trait de soulignement dans les espaces de noms.
- Nouveau module : classe `public sealed` avec constructeur sans paramètre (découverte automatique), `Order = N*10`.

## Commandes

```bash
dotnet build Maus.slnx --disable-build-servers
dotnet test Maus.slnx
dotnet run --project src/Maus.Cli -- --module M06
dotnet publish src/Maus.App -c Release -o publish/MAUS
```

- Dépôt : https://github.com/TERMINATORmdel101/MAUS (**privé** jusqu'à l'ajout du fichier `LICENSE` et la relecture juridique). Branche `main`.
- Identité Git des commits : `TERMINATORmdel101 <213405999+TERMINATORmdel101@users.noreply.github.com>`. Ne jamais utiliser l'adresse e-mail personnelle du porteur : GitHub refuse le push (adresse privée protégée).
- `nuget.config` du dépôt force nuget.org (la config NuGet globale du PC est vide).
- Sur le PC Windows : PowerShell 5.1 (pas de `&&`) ; chemins longs du dossier temporaire > 260 caractères = erreurs, travailler dans le dépôt.
- Tester l'interface sans fenêtre UAC : remplacer temporairement `requireAdministrator` par `asInvoker` dans `src/Maus.App/app.manifest`, compiler dans un dossier ignoré, lancer, cliquer « Lancer l'audit » via UI Automation, vérifier le journal « .NET Runtime », puis **restaurer le manifeste**.
- Ne jamais lancer MAUS élevé ni une commande qui modifie le PC du porteur sans son accord explicite. Les corrections (V0.2) se testent dans **Windows Sandbox**.

### Dans le cloud (Linux)

SDK : `apt-get install -y dotnet-sdk-10.0` (dépôt Ubuntu ; `dot.net/v1/dotnet-install.sh` est bloqué par le proxy). Les sessions cloud tournent sous Linux : l'application WPF et les API Windows (registre, WMI, P/Invoke) n'y fonctionnent pas. `EnableWindowsTargeting` est activé dans `Directory.Build.props` pour permettre la **compilation**. Les tests qui appellent de vraies API Windows échoueront sous Linux : écrire le code et les tests avec les faux, puis faire valider sur le PC Windows (build, tests, `maus` en lecture seule).

## État (29/09/2026, version 0.4.2, sessions locales sur le PC du porteur)

Compilé et testé **sous Windows** (0 avertissement, **1 330 tests réussis**), interface ouverte en clair et en sombre. Détail et ordre de validation : `REPRISE.md` section 0 ; contenu des versions : `CHANGELOG.md`.

- **0.3.3-alpha** (26-28/09) : Paramètres (thème, couleurs selon les composants Intel / AMD / Nvidia, animations, vitesse d'actualisation), fenêtre de surveillance (température, consommation, fréquence, erreurs WHEA / PCIe et Windows), lecture SPD par MAUS lui-même (`SpdBusDriver`, garde-fou d'écriture), verrous SMBus / PCI abandonnés récupérés, un seul MAUS à la fois, garde de l'interface, textes blancs en thème sombre, tension mémoire « déclarée par le BIOS ».
- **3.9.1** (28/09, numéro choisi par le porteur) : timings réels Intel de Haswell aux Core Ultra (`Catalog/intel-memory-controller.json`, gear, contrôleur absent, champs limités à certains modèles), APU Ryzen Raven Ridge, alerte UCLK > MCLK, comparaison de fiches mémoire (`MemorySnapshots`), étalonnage de la tension par l'utilisateur (`DramVoltageCalibration`), courbes et relevé avec bilan dans la fenêtre de surveillance (`MonitorRecording`), revue de sécurité (retour arrière des corrections sur toute erreur, test RAM arrêté en surchauffe, tâche planifiée seulement depuis Program Files).
- **4.0.0** (28/09 au soir, numéro choisi par le porteur) : sept charges des tests du processeur (`CpuStress`), programme complet Curve Optimizer (`CurveOptimizerProgram` : cœur par cœur puis transitoires ; priorité haute, jamais « temps réel »), température du processeur pendant les tests, PCIe sans fausse alerte sur portable, anti-veille (`KeepAwake`), tests « occupés » dès le clic (`SetPreparing`).
- **0.4.2** (29/09, numéro choisi par le porteur après la 4.0.0) : score de santé selon la gravité (`HealthScore.Explain`, critique 20 / importante 10 / moyenne 4 / faible 1, 100 × e^(−points/100), plafonds 49 et 74, historique `health-v2`, fenêtre « Pourquoi ce score ? »), Atelier > Pilotes (`DriverInventoryReader` + dates `CfgMgrDriverDateSource`, actions pnputil `GraphicsDriverActions` : redémarrer, retirer et redétecter, sauvegarder, supprimer après sauvegarde, réinstaller une sauvegarde ; **à tester dans Windows Sandbox, jamais sur le PC du porteur sans son accord**), microcode dans Mon PC.
- **Après la 4.0.0** (29/09, numéro à choisir par le porteur) : fenêtre « À propos » (`AboutWindow`), écran de démarrage (`Assets/splash.png`), icône M lisible en petit (`tools/make-app-images.ps1`), logo dans le rapport HTML, « Signaler sur GitHub » (`ProjectLinks`, `.github/ISSUE_TEMPLATE`), Mon PC enrichi (`MachineDetailsReader` : Windows, UEFI, Secure Boot, TPM, écrans, réseau, son, emplacements mémoire) et « Copier la fiche » (masquée par `PrivacyFilter`), M14 robuste aux modes incohérents, animations `Motion.HoverLift` / `Press` / `EnterOnLoad`.
- **Vérifié sur le vrai matériel du porteur** (i7-8700K, MSI Z390, DDR4-3467) : timings Skylake à Comet Lake. Les autres familles Intel affichent « pas encore comparée à CPU-Z ».
- **Règle des registres** : un champ n'entre au catalogue qu'avec ses sources ; une seule = `"confidence": "single"` ; sources en désaccord = champ écarté et listé dans `unavailable` ou en commentaire (horloge Haswell, tRRD Haswell, horloge Core Ultra en gear 4, horloge Lunar Lake).
- **Tests d'interface** : copie jetable dans `.worktrees\_smoke` compilée avec `-p:ApplicationManifest=<copie asInvoker>` (le manifeste du dépôt n'est jamais touché). **Ne jamais tuer un MAUS ouvert par le porteur.** Script `_smoke/sync.sh` : recopie, correctifs de test (thème sombre, sans verrou d'instance ni avertissements) et **rendu logiciel** : le 29/09, toute fenêtre WPF (même hors MAUS) restait blanche en rendu par la carte graphique dès qu'une deuxième fenêtre s'ouvrait (état passager du PC). En PowerShell, `$button` et un paramètre `$Button` sont la même variable.

## État antérieur (25/09/2026, version 0.3.2-alpha, fin de la 3e session cloud)

**V0.1 terminée** : les 15 premiers modules détectent en lecture seule ; application WPF testée (audit complet sans plantage en ~15 s). Depuis : 20 modules d'audit (M17 périphériques en erreur, M18 réseau, M19 sauvegardes, M20 logiciels à mettre à jour).

**Tout ce qui suit est codé sous Linux, compilé (0 avertissement) et testé avec les faux, mais JAMAIS exécuté sur un vrai Windows.** Tests : 1 185, dont 12 échecs **attendus sous Linux** (vraies API Windows, chemins) ; sous Windows, tout doit être vert.

- **V0.2 — corrections réversibles** : journal protégé sous `%ProgramData%\MAUS\journal`, point de restauration vérifié, blocage sur PC géré / sans droits admin / élévation par un autre compte, corrections tout ou rien, Annuler par séance **ou par correction**, correction « écrite mais sans effet » signalée (relecture par un nouvel audit), redémarrage de l'Explorateur proposé. Modules corrigeables : M01, M04, M05, M06, M07 (profil Game Bar 1/2/3 au choix), M09, M12, M13. CLI : `--plan`, `--apply`, `--apply-recommended`, `--journal`, `--revert SEANCE [--change ID] [--force]`, `--restart-explorer`, `--ack/--unack`, `--set`, `--prefs`, `--html`.
- **Choix de l'utilisateur** : constat marqué « voulu » (il revient si la valeur change), profil Game Bar, question au premier lancement sur un portable, langue, moteur de recherche.
- **Rapport HTML** avant/après (autonome, score de santé, comment tout annuler).
- **Atelier matériel (M16)**, sans pilote noyau : fiche du matériel (processeur par CPUID, carte mère et BIOS, barrettes avec tension SMBIOS, cartes graphiques dont NVIDIA par NVML, disques SMART, batterie), jauges de sécurité (`hw-safety-limits.json`), mesures en direct (charge, fréquence, mémoire, GPU par D3DKMT/NVML, disque, réseau, alarmes), gestionnaire des tâches expliqué (catalogue de 77 processus, confiance selon l'emplacement, recherche web à la demande avec moteur au choix, arrêt protégé des processus critiques), diagnostic « Pourquoi mon PC est lent ? », tests du processeur (calculs vérifiés), de la mémoire vive (motifs, débit, latence) et de la mémoire vidéo (`VramTest` + `D3D11GpuMemoryProvider` : blocs Direct3D 11 en mémoire dédiée, motifs relus par copie, appels COM par index de table de méthodes) avec arrêt automatique sur alarme, historique des scores.
- **Test cœur par cœur** (`CoreCycleTest`, pour le Curve Optimizer / PBO et l'undervolt) : un seul fil épinglé par cœur, alternance charge soutenue / changements brusques de charge / repos (c'est là qu'un réglage trop bas fige ou fait redémarrer le PC), chien de garde (cœur « figé »), trace écrite sur disque en `WriteThrough` pour désigner le cœur fautif après un gel ou un redémarrage, erreurs WHEA comptées. Conseils honnêtes (remonter de +2/+3 le cœur fautif).
- **Stockage** (Atelier) : « Qu'est-ce qui prend de la place ? » (`SpaceAnalyzer`, lecture seule, dossiers de Windows expliqués par `SpaceHints`, suppression laissée à l'Assistant de stockage de Windows) et test de vitesse du disque (`DiskSpeedTest`, sans cache, fichier temporaire supprimé).
- **« Demander de l'aide »** (accueil, CLI `--summary`) : résumé court (`HelpSummary`) à coller sur un forum ; `PrivacyFilter` masque dossier du profil, nom d'utilisateur, nom du PC et e-mails ; l'utilisateur relit et complète avant de copier, MAUS n'envoie rien.
- **Réparation de Windows** : constat `M02.component-store` (API DISM, `DismCheckImageHealth` sans analyse = `/CheckHealth`) et bouton « Réparer les fichiers de Windows » (onglet Corrections) : console visible DISM `/RestoreHealth` puis `sfc /scannow`, après confirmation.
- **Logiciels (M20)** : liste `winget upgrade --source winget --disable-interactivity` (lecture seule, liste blanche argument par argument), winget.exe lancé **uniquement** depuis `Program Files\WindowsApps` (jamais l'alias du profil, modifiable sans droits admin) ; bouton « Mettre à jour les logiciels » : cases à cocher puis console visible `winget upgrade --id X --exact`, interactive (l'utilisateur accepte les conditions).
- **Capteurs avancés PawnIO** (Atelier, En direct) : `IAdvancedSensors` / `LhmAdvancedSensors` / `CombinedSensorSource`, tuile « Température du processeur » (jauge selon `hw-safety-limits.json`), tableau de tous les capteurs, alarmes processeur (attention à la limite, danger au-delà de +5 °C), boutons Installer / Retirer PawnIO.
- **Fiche mémoire** (Atelier, onglet Mémoire, PawnIO requis) : `Workshop/Memory` — `SpdDecoder` (DDR4/DDR5, JEDEC, XMP 2.0/3.0, EXPO, CRC), `ZenMemoryController` (timings primaires/secondaires/tertiaires lus dans les registres UMC par SMN, réglages GDM/1T-2T/Power Down/BGS, FCLK/UCLK/MCLK et tensions par la table PM), `PawnIoMemorySources` (RAMSPDToolkit, `AmdFamily17.ReadSmn`, `RyzenSmu`, verrous `Global\Access_PCI` et `Global\Access_SMBUS.HTP.Method`). Intel : timings réels depuis la 0.3.3-alpha (voir État 28/09). Sources des emplacements : ZenStates-Core (GPL-3.0) et memtest86+, citées dans `THIRD-PARTY-NOTICES.md`.
- **Relevé pendant une partie** (Atelier, En direct) : `SessionRecording` (bilan : surchauffe, goulot processeur / carte graphique, mémoire pleine ; CSV au format de la langue de Windows).
- **Historique des scores** : score de santé après chaque audit (`%LOCALAPPDATA%\MAUS\health-history.json`, courbe à l'accueil) et tests (`ScoreTrends`, carte dans Tests).
- **Audit automatique chaque semaine** (Vos choix) : `ScheduledAudit.TaskXml` + `TaskSchedulerClient` (schtasks /XML), démarrage `MAUS.exe --scheduled-audit` sans fenêtre (`App.xaml` n'a plus de `StartupUri`), notification seulement si problème rouge.
- **Test de connexion** (Atelier, Tests) : pings parallèles box + Cloudflare + Google, médiane, gigue, pertes ; verdict « réseau local » ou « ligne ».
- **Honnêteté des gains** (principe 6) : panneau « Ce que vous pouvez vraiment gagner » dans Corrections ; textes XMP / ReBAR ramenés aux gains mesurés (quelques pour cent).
- **Nouvelle interface** aux couleurs du logo (maquette : `docs/maquettes/maquette-accueil-atelier.png`).
- **Trois langues** : français, anglais, espagnol (choix dans la fenêtre ; CLI `--lang`), 2 810 textes traduits.
- **Version 0.3.2-alpha** (`Directory.Build.props`, `AppVersion.Display` dans « À propos », les rapports et `maus --version`, `CHANGELOG.md`). Nettoyage « aucune donnée inventée » : limites de température par modèle vérifié ou lues dans la puce (TjMax), plus de limite par défaut ; tensions DDR4/DDR5 sourcées ; décodeur SPD corrigé (EXPO 2, CL XMP 2.0, XMP 3.0, CRC des profils) ; encodage de winget choisi d'après les octets (`ReadOnlyCommandRunner.DecodeOutput`).

À valider en priorité dans **Windows Sandbox** (puis sur le PC du porteur, en lecture seule) :
1. L'interface se lance (nouvelle fenêtre jamais ouverte !) : Accueil (8 cartes, fenêtre « Demander de l'aide » et copie), Constats, Corrections, Atelier (5 onglets dont Stockage), Historique, changement de langue.
2. `maus --plan`, `maus --apply-recommended` (point de restauration créé et relu, journal écrit, ACL), `maus --journal`, `maus --revert`.
3. Atelier : valeurs cohérentes avec le Gestionnaire des tâches (compteurs PDH en anglais via `PdhAddEnglishCounterW`, GPU par D3DKMT, NVML sur carte NVIDIA), recherche web ouverte **sans** droits admin (`ShellLauncher` passe par explorer.exe, à vérifier), tests CPU/RAM/VRAM courts (le test VRAM appelle Direct3D 11 par les tables COM : à vérifier en priorité sur une vraie carte).
4. Réparation DISM/SFC (console, `DismCheckImageHealth`), `winget upgrade` réel (colonnes, UTF-8, codes de retour, lancement depuis `Program Files\WindowsApps` en administrateur — Windows Sandbox n'a pas winget : tester sur le PC du porteur, la liste est en lecture seule), test de connexion.
5. Fiche mémoire (SPD réel DDR4/DDR5, registres UMC sur Ryzen, table PM : comparer avec ZenTimings / CPU-Z), relevé de session, audit hebdomadaire (création de la tâche, lancement sans fenêtre, notification, retrait).
6. Test cœur par cœur (épinglage `SetThreadGroupAffinity`, lecture de la topologie `GetLogicalProcessorInformationEx`, trace dans `%LOCALAPPDATA%\MAUS\core-test.json`), test de disque (`FILE_FLAG_NO_BUFFERING`), analyse du stockage sur C:, modules M17 (`Win32_PnPEntity`), M18 (`MSFT_NetAdapter`, décalages de `wlanapi` à vérifier), M19 (`root\default:SystemRestore`).
7. Points « à vérifier » : `ShowTaskViewButton` / `TaskbarAnimations` sans redémarrage de l'Explorateur, valeurs de pause de Windows Update, clé `SPP\Clients` de la protection du système.

Reste à faire V0.2 (écritures d'autres natures, non commencées) : services via l'API (DiagTrack), tâches planifiées (CEIP, UpdateOrchestrator, tâches d'ouverture de session M12), Defender (exclusions), pare-feu local, BCD (DEP, signature des pilotes), fichier hosts, proxy WinHTTP, fichier d'échange, Copilot/Recall, réinstallation Game Bar (winget), modes secteur/batterie (M05, fonctions non documentées), retrait des valeurs de `Registry.pol`, bouton « Valeurs Windows » (défauts) du M06.

Reste à faire / points connus :
- [x] Fichier `LICENSE` : texte officiel GPL-3.0 (accord du porteur le 25/09/2026 ; gnu.org bloqué par le proxy, copie Debian `/usr/share/common-licenses/GPL-3`, SHA-256 `3972dc97…986` identique à gpl-3.0.txt).
- [ ] Relecture juridique de `TRADEMARKS.md` et des mentions légales avant publication publique.
- [x] **PawnIO** (accord du porteur le 25/09/2026) : `LibreHardwareMonitorLib` 0.9.6 (MPL-2.0, modules PawnIO signés inclus, voir `THIRD-PARTY-NOTICES.md`) ; ouvert seulement si PawnIO est installé (clé `Uninstall\PawnIO`) et MAUS administrateur ; installation / retrait par winget (`namazso.PawnIO`) dans une console visible, jamais automatique. À valider sur Windows : lecture réelle des capteurs, compatibilité intégrité de la mémoire et anti-triche. SPD des barrettes : fait (fiche mémoire). Pas encore fait : tensions réelles VDD/VDDQ des barrettes DDR5 (PMIC), puces mémoire de la carte graphique.
- [ ] Module 14 : contrôle « écran branché sur la carte mère » par comparaison d'adaptateur, sans le drapeau D3D12 UMA.
- [ ] Module 9 : versions du catalogue `m09-gpu-drivers.json` à revérifier chaque mois (dernière vérification : 25/09/2026) ; Module 15 : URLs NVIDIA App / AMD / Intel Arc à revérifier. `hw-safety-limits.json` sourcé et `processes.json` relu le 25/09/2026.
- [ ] **Demandes du porteur non terminées** (détail dans `REPRISE.md`) : vraies tensions VDD/VDDQ des barrettes DDR5 (PMIC, lecture seule, carte des registres à sourcer d'abord) (comparer deux fiches mémoire : fait en 3.9.1).
- [ ] Traductions anglaises et espagnoles écrites par l'IA : à faire relire par des locuteurs natifs avant publication.
- [x] Cas « élévation par un autre compte administrateur » : détecté (`SessionUser`), réglages HKCU/SPI ignorés avec message (à valider sur Windows).
- [ ] Les nombreux « (à vérifier) » de la fiche technique.

## Feuille de route

- **V0.2 — corrections réversibles** : contrat `Plan / Apply / Verify / Revert` à côté de `Detect` ; journal JSON des valeurs d'origine sous `%ProgramData%\MAUS\journal` (ACL SYSTEM + Administrateurs) ; point de restauration vérifié (`SystemRestorePointCreationFrequency` = 0 le temps de la création, relecture de la liste, protection système activée avec accord) ; corrections bloquées sur PC géré ; interface : cases à cocher, profils, aperçu, bouton Annuler. Premières corrections : M06 visuels, M04 confidentialité, M05 alimentation, M07 Game Bar, M12 démarrage (via `StartupApproved`, comme le Gestionnaire des tâches), M01 valeurs par défaut (dont réactivation des atténuations Spectre/Meltdown), M09 HAGS.
- **V0.3** : réglage fréquence/HDR (M14), paquet Microsoft Store, catalogue signé mis à jour chaque mois, relecture des traductions.
- **Atelier matériel (M16, demande du 25/09/2026)** : identité du matériel, capteurs en direct, seuils de sécurité par composant, gestionnaire des tâches à la MAUS (« Qu'est-ce que c'est ? », recherche web à la demande), tests CPU / RAM / VRAM. **S'inspirer des logiciels existants sans jamais les copier** (ni présentation, ni noms, ni code, ni données). Voir `docs/fiche-technique/m16-atelier-materiel.md`. Fait : identité, capteurs sans pilote, seuils, gestionnaire des tâches, tests CPU/RAM/VRAM. Question ouverte : pilote (PawnIO / LibreHardwareMonitor) pour les tensions et la température réelle du CPU.

## Journal des sessions

- 24/09/2026 : fiche technique (15 modules), décisions, nom MAUS, licence, socle V0.1, module 6.
- 25/09/2026 : 14 autres modules codés par des agents en parallèle (worktrees Git), intégration, correction du plantage WPF au démarrage (liaison TwoWay sur propriété en lecture seule), correction « PC géré » (pseudo-inscriptions Windows ignorées). Dépôt poussé sur GitHub.
- 25/09/2026 (cloud, branche `claude/keen-wozniak-93as1n`) : V0.2 codée (socle des corrections, 8 modules corrigeables, CLI, interface). SDK .NET installé par `apt-get install dotnet-sdk-10.0` (le script dotnet-install est bloqué par le proxy). Chemins Winlogon du M01 découpés explicitement sur `\` pour passer sous Linux.
- 25/09/2026 (cloud, 2e session, même branche) : suggestions du porteur (écrite sans effet, profil Game Bar, annuler une correction, redémarrer l'Explorateur, constat « voulu », rapport HTML, choix portable), Atelier matériel M16 complet sans pilote, logo intégré (`assets/logo`), refonte graphique (navigation à gauche, tableau de bord), trois langues (outil de transformation en `T(…)` appliqué à tout le code, 1 850 textes traduits). Textes « prévu en V0.2/V0.3 » remplacés par « prochaine version » ou un renvoi vers l'Atelier.
- 25/09/2026 (cloud, 3e session) : demandes « plein de fonctions utiles, gains honnêtes, casser la charge pour le Curve Optimizer ». Test cœur par cœur, modules M17-M20, stockage et test de disque, résumé « Demander de l'aide », réparation DISM/SFC, mises à jour des logiciels (winget), test de connexion, panneau d'honnêteté, principe 6. Puis, avec l'accord du porteur : LICENSE (GPL-3.0), PawnIO (capteurs avancés), fiche mémoire complète, relevé de partie, historique des scores, audit hebdomadaire. Enfin, version **0.3.2-alpha** : nettoyage « aucune donnée inventée » (sources vérifiées sur le web pour chaque seuil, pilote et texte), fichiers obsolètes supprimés, `CHANGELOG.md`, `REPRISE.md` pour repasser en local.
- 26-28/09/2026 (local, PC du porteur, branche `claude/keen-wozniak-93as1n`) : 0.3.3-alpha testée sur son PC (timings Coffee Lake confirmés, tension mémoire corrigée en « déclarée par le BIOS », blocages de l'onglet Mémoire et des mesures en direct avec PawnIO corrigés, thème sombre). Puis « Ok pour tout, on passera à la 3.9.1 » : générations Intel restantes, corrections AMD, comparaison de fiches, étalonnage de la tension, courbes et relevé, revue de sécurité, fiches et GitHub mis à jour.
- 28/09/2026 (soir, local) : retours du porteur sur la 3.9.1. Lien PCIe sans fausse alerte sur portable (Windows ne publie pas la largeur du port racine), température du processeur pendant les tests CPU, sept charges au choix (`CpuStress` : mélange, entiers, SSE2, AVX, AVX2 + FMA, AVX-512, caches et mémoire), **programme complet Curve Optimizer** (`CurveOptimizerProgram` : phase 1 pic / chute / réveil par cœur, phase 2 rafales simultanées de 5 à 10 s ; priorité haute, jamais « temps réel » ; aucun programme fermé ni suspendu), anti-veille pendant les tests (`KeepAwake`). Relecture par workflow (2 relecteurs + 2 vérificateurs) : 9 défauts confirmés et corrigés. Les agents échouent quand la limite de 5 h de l'abonnement est atteinte, même avec le crédit supplémentaire actif.
- 29/09/2026 (local) : 4.0.0 complétée. Cartes de l'accueil réparées (`OpenWorkshop` : sous-partie choisie après l'affichage de l'atelier), avertissements et limitation de responsabilité (`Legal/Disclaimer`, fenêtre du premier lancement `DisclaimerWindow`, rappels dans chaque confirmation, préférence `DisclaimerAccepted`), logo mieux intégré (recadrage serré, coins arrondis, ombre, barre aux quatre couleurs, sens du nom).
- 29/09/2026 (local) : demandes « embellir, animer, plus d'informations sur la machine, aide et signalement GitHub ». Fenêtre À propos, écran de démarrage, icône M lisible, logo du rapport HTML, bouton « Signaler sur GitHub » et modèle de signalement, Mon PC enrichi (Windows, UEFI, Secure Boot, TPM, écrans, réseau, son, emplacements mémoire) avec « Copier la fiche », animations (survol, appui, cascade), familles M·A·U·S visibles avant l'audit. Constaté sur le PC du porteur : liste de modes d'écran générique et mode préféré 1024×768 → M14 rendu robuste (indéterminé au lieu de fausses conclusions) ; fenêtres WPF blanches en rendu matériel dès la 2e fenêtre, y compris hors MAUS (état passager du PC, redémarrage conseillé). Score de santé tombé à 0 (3 problèmes, 12 à surveiller) : formule à rediscuter avec le porteur.
- 29/09/2026 (local, suite) : version **0.4.2** (numéro choisi par le porteur). Score de santé selon la gravité (PC du porteur : 0 → 51), « Pourquoi ce score ? ». Atelier > Pilotes : liste complète des pilotes, actions pnputil sur la carte graphique (redémarrer, retirer et redétecter, sauvegarder, supprimer après sauvegarde, réinstaller une sauvegarde). Date des pilotes : `Win32_PnPSignedDriver` inverse jour et mois sur le PC du porteur → `DEVPKEY_Device_DriverDate` par cfgmgr32.
