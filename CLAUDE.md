# CLAUDE.md — mémoire du projet MAUS

Ce fichier est la mémoire de Claude entre les sessions (sur le PC du porteur du projet comme dans le cloud). Lis-le en entier avant de travailler, et mets-le à jour à la fin de chaque session importante (section « État » et « Journal »).

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
| `src/Maus.Core/Modules/MxxNom/` | Un dossier par module (M01 à M15) |
| `src/Maus.Core/Fixes` | V0.2 : `IFixableModule.Plan`, `PlannedChange` / `SettingWrite` / `SettingKey` / `SettingValue`, `FixEngine` (Apply, Verify, Revert), `FixContext`, journal (`FileJournalStore` + `WindowsDirectoryProtector`), `RestorePointCreator` + `WmiSystemRestore`, `FixProfile` |
| `src/Maus.Cli` | `maus` : audit en ligne de commande (`--module Mxx`, `--json`) |
| `src/Maus.App` | Interface WPF (thème Fluent), manifeste `requireAdministrator`, filet de sécurité qui journalise les plantages dans `%LOCALAPPDATA%\MAUS\logs` |
| `tests/Maus.Core.Tests` | xUnit avec faux (`Fakes.cs` : FakeRegistry, FakeCim, FakeCommands, FakeEventLogs, FakePackages, FakeFiles, `TestContext.Create`) |

Règles de code :
- **Detect et Plan = lecture seule absolue** : jamais de clé de registre ouverte en écriture, jamais de `Process.Start` hors `context.Commands`, jamais d'appel qui modifie un réglage. Tout accès système passe par `AuditContext` ou par une interface interne au module (P/Invoke, COM) avec un faux dans les tests.
- **Écrire (V0.2)** : uniquement par le `FixEngine`, via `FixContext` (jamais dans `AuditContext`). Un module corrigeable implémente `IFixableModule.Plan` et décrit ses corrections comme des écritures élémentaires (`SettingWrite` : registre, SPI, mode de gestion) ; le moteur journalise la valeur d'origine avant d'écrire, relit, défait la correction entière si une écriture échoue, et sait tout annuler. Nouveau type de réglage = nouveau `SettingKind` + lecture/écriture dans `SettingsAccessor` + faux.
- Stratégies (`Policies`) : restaurer = **supprimer** la valeur, jamais écrire la valeur « activée ». Stratégies posées seulement sur Pro et plus. Pré-coché (`Recommended`) = sans risque connu ; `Advanced` = jamais dans un profil hors de son domaine ; `Warning` = avertissement renforcé.
- Test de chaque module corrigeable : `RoundTrip.AssertAsync` (critère d'acceptation de la fiche : après Apply les constats sont conformes, après Revert un nouveau Detect est identique à l'état initial).
- Données illisibles → `Finding.Unknown` / `Finding.AdminRequired`, **jamais** un faux Problem.
- Écart → `FindingStatusExtensions.ForDeviation(severity)` : Critical/High = rouge, Medium = orange, Low = bleu (optimisation).
- Textes utilisateur en français clair ; identifiants `Mxx.nom-en-kebab`.
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

## État (25/09/2026, fin de session cloud)

**V0.1 terminée** : les 15 modules détectent en lecture seule ; application WPF testée (audit complet sans plantage en ~15 s).

**V0.2 codée, à valider sur Windows** (écrite sous Linux : compilée, testée avec les faux, jamais exécutée sur un vrai Windows) :
- Socle : journal protégé sous `%ProgramData%\MAUS\journal`, point de restauration vérifié, blocage sur PC géré / sans droits admin / élévation par un autre compte (réglages HKCU et SPI ignorés), corrections tout ou rien, Annuler qui respecte les valeurs changées depuis.
- Corrections branchées (registre, SPI, mode de gestion) : M01 (stratégies, UAC, services, Winlogon, AppInit, IFEO, pare-feu, pause/version/WSUS), M04 (lignes Standard du registre), M05 (mode de gestion, démarrage rapide), M06 (tout), M07 (selon le profil), M09 (HAGS, blocage des pilotes au choix), M12 (StartupApproved), M13 (Spectre/Meltdown, liste de blocage des pilotes).
- CLI : `maus --plan`, `--apply ID...`, `--apply-recommended`, `--journal`, `--revert SEANCE [--force]`.
- Interface : onglets Constats / Corrections (profils, cases, détails techniques, point de restauration) / Historique (Annuler). Jamais lancée : à tester (manifeste `asInvoker` interdit ici, il faut l'admin → **Windows Sandbox**).
- Tests : 954, dont 12 échecs **attendus sous Linux** (vraies API Windows, chemins) ; 0 avertissement. Sous Windows, tout doit être vert.

À valider en priorité dans Windows Sandbox : `maus --plan` ; `maus --apply-recommended` (point de restauration créé et relu, journal écrit, ACL du dossier) ; `maus --journal` puis `maus --revert` ; même chose dans l'interface. Points marqués « à vérifier » : effet de `ShowTaskViewButton` / `TaskbarAnimations` sans redémarrage de l'Explorateur, valeurs de pause de Windows Update, clé `SPP\Clients` pour l'état de la protection du système.

Reste à faire V0.2 (écritures d'autres natures, non commencées) : services via l'API (DiagTrack), tâches planifiées (CEIP, UpdateOrchestrator, tâches d'ouverture de session M12), Defender (exclusions), pare-feu local, BCD (DEP, signature des pilotes), fichier hosts, proxy WinHTTP, fichier d'échange, Copilot/Recall, réinstallation Game Bar (winget), modes secteur/batterie (M05, fonctions non documentées), retrait des valeurs de `Registry.pol`, bouton « Valeurs Windows » (défauts) du M06, redémarrage de l'Explorateur proposé.

Reste à faire / points connus (hors V0.2) :
- [ ] Fichier `LICENSE` (texte officiel GPL-3.0 de gnu.org) : le porteur n'a pas encore autorisé le téléchargement.
- [ ] Relecture juridique de `TRADEMARKS.md` et des mentions légales avant publication publique.
- [ ] Module 11 : le benchmark actif (CPU, RAM, GPU, stockage) est prévu en V0.3 ; seule la santé passive existe.
- [ ] Module 14 : contrôle « écran branché sur la carte mère » par comparaison d'adaptateur, sans le drapeau D3D12 UMA.
- [ ] Module 9 : versions du catalogue `m09-gpu-drivers.json` à revérifier chaque mois ; Module 15 : URLs NVIDIA App / AMD / Intel Arc à revérifier.
- [x] Cas « élévation par un autre compte administrateur » : détecté (`SessionUser`), réglages HKCU/SPI ignorés avec message (à valider sur Windows).
- [ ] Les nombreux « (à vérifier) » de la fiche technique.

## Feuille de route

- **V0.2 — corrections réversibles** : contrat `Plan / Apply / Verify / Revert` à côté de `Detect` ; journal JSON des valeurs d'origine sous `%ProgramData%\MAUS\journal` (ACL SYSTEM + Administrateurs) ; point de restauration vérifié (`SystemRestorePointCreationFrequency` = 0 le temps de la création, relecture de la liste, protection système activée avec accord) ; corrections bloquées sur PC géré ; interface : cases à cocher, profils, aperçu, bouton Annuler. Premières corrections : M06 visuels, M04 confidentialité, M05 alimentation, M07 Game Bar, M12 démarrage (via `StartupApproved`, comme le Gestionnaire des tâches), M01 valeurs par défaut (dont réactivation des atténuations Spectre/Meltdown), M09 HAGS.
- **V0.3** : benchmark actif (M11), réglage fréquence/HDR (M14), paquet Microsoft Store, catalogue signé mis à jour chaque mois, anglais.
- **Atelier matériel (M16, demande du 25/09/2026)** : identité du matériel, capteurs en direct, seuils de sécurité par composant, gestionnaire des tâches à la MAUS (« Qu'est-ce que c'est ? », recherche web à la demande), tests CPU / RAM / VRAM. **S'inspirer des logiciels existants sans jamais les copier** (ni présentation, ni noms, ni code, ni données). Voir `docs/fiche-technique/m16-atelier-materiel.md`. Question ouverte : pilote (PawnIO / LibreHardwareMonitor) pour les tensions et la température réelle du CPU.

## Journal des sessions

- 24/09/2026 : fiche technique (15 modules), décisions, nom MAUS, licence, socle V0.1, module 6.
- 25/09/2026 : 14 autres modules codés par des agents en parallèle (worktrees Git), intégration, correction du plantage WPF au démarrage (liaison TwoWay sur propriété en lecture seule), correction « PC géré » (pseudo-inscriptions Windows ignorées). Dépôt poussé sur GitHub.
- 25/09/2026 (cloud, branche `claude/keen-wozniak-93as1n`) : V0.2 codée (socle des corrections, 8 modules corrigeables, CLI, interface). SDK .NET installé par `apt-get install dotnet-sdk-10.0` (le script dotnet-install est bloqué par le proxy). Chemins Winlogon du M01 découpés explicitement sur `\` pour passer sous Linux.
