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
| `src/Maus.Cli` | `maus` : audit en ligne de commande (`--module Mxx`, `--json`) |
| `src/Maus.App` | Interface WPF (thème Fluent), manifeste `requireAdministrator`, filet de sécurité qui journalise les plantages dans `%LOCALAPPDATA%\MAUS\logs` |
| `tests/Maus.Core.Tests` | xUnit avec faux (`Fakes.cs` : FakeRegistry, FakeCim, FakeCommands, FakeEventLogs, FakePackages, FakeFiles, `TestContext.Create`) |

Règles de code :
- **V0.1 = lecture seule absolue** : jamais de clé de registre ouverte en écriture, jamais de `Process.Start` hors `context.Commands`, jamais d'appel qui modifie un réglage. Tout accès système passe par `AuditContext` ou par une interface interne au module (P/Invoke, COM) avec un faux dans les tests.
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

- `nuget.config` du dépôt force nuget.org (la config NuGet globale du PC est vide).
- Sur le PC Windows : PowerShell 5.1 (pas de `&&`) ; chemins longs du dossier temporaire > 260 caractères = erreurs, travailler dans le dépôt.
- Tester l'interface sans fenêtre UAC : remplacer temporairement `requireAdministrator` par `asInvoker` dans `src/Maus.App/app.manifest`, compiler dans un dossier ignoré, lancer, cliquer « Lancer l'audit » via UI Automation, vérifier le journal « .NET Runtime », puis **restaurer le manifeste**.
- Ne jamais lancer MAUS élevé ni une commande qui modifie le PC du porteur sans son accord explicite. Les corrections (V0.2) se testent dans **Windows Sandbox**.

### Dans le cloud (Linux)

Les sessions cloud tournent sous Linux : l'application WPF et les API Windows (registre, WMI, P/Invoke) n'y fonctionnent pas. `EnableWindowsTargeting` est activé dans `Directory.Build.props` pour permettre la **compilation**. Les tests qui appellent de vraies API Windows échoueront sous Linux : écrire le code et les tests avec les faux, puis faire valider sur le PC Windows (build, tests, `maus` en lecture seule).

## État (25/09/2026)

**V0.1 terminée** : les 15 modules détectent en lecture seule ; 902 tests verts ; 0 avertissement ; application WPF testée (audit complet sans plantage en ~15 s).

Reste à faire / points connus :
- [ ] Fichier `LICENSE` (texte officiel GPL-3.0 de gnu.org) : le porteur n'a pas encore autorisé le téléchargement.
- [ ] Relecture juridique de `TRADEMARKS.md` et des mentions légales avant publication publique.
- [ ] Module 11 : le benchmark actif (CPU, RAM, GPU, stockage) est prévu en V0.3 ; seule la santé passive existe.
- [ ] Module 14 : contrôle « écran branché sur la carte mère » par comparaison d'adaptateur, sans le drapeau D3D12 UMA.
- [ ] Module 9 : versions du catalogue `m09-gpu-drivers.json` à revérifier chaque mois ; Module 15 : URLs NVIDIA App / AMD / Intel Arc à revérifier.
- [ ] Cas « élévation par un autre compte administrateur » (réglages HKCU du mauvais profil) non traité.
- [ ] Les nombreux « (à vérifier) » de la fiche technique.

## Feuille de route

- **V0.2 — corrections réversibles** : contrat `Plan / Apply / Verify / Revert` à côté de `Detect` ; journal JSON des valeurs d'origine sous `%ProgramData%\MAUS\journal` (ACL SYSTEM + Administrateurs) ; point de restauration vérifié (`SystemRestorePointCreationFrequency` = 0 le temps de la création, relecture de la liste, protection système activée avec accord) ; corrections bloquées sur PC géré ; interface : cases à cocher, profils, aperçu, bouton Annuler. Premières corrections : M06 visuels, M04 confidentialité, M05 alimentation, M07 Game Bar, M12 démarrage (via `StartupApproved`, comme le Gestionnaire des tâches), M01 valeurs par défaut (dont réactivation des atténuations Spectre/Meltdown), M09 HAGS.
- **V0.3** : benchmark actif (M11), réglage fréquence/HDR (M14), paquet Microsoft Store, catalogue signé mis à jour chaque mois, anglais.

## Journal des sessions

- 24/09/2026 : fiche technique (15 modules), décisions, nom MAUS, licence, socle V0.1, module 6.
- 25/09/2026 : 14 autres modules codés par des agents en parallèle (worktrees Git), intégration, correction du plantage WPF au démarrage (liaison TwoWay sur propriété en lecture seule), correction « PC géré » (pseudo-inscriptions Windows ignorées). Dépôt poussé sur GitHub.
