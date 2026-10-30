# CLAUDE.md — mémoire du projet MAUS

Ce fichier est la mémoire de Claude entre les sessions (sur le PC du porteur du projet comme dans le cloud). Lis-le en entier avant de travailler. À la fin de chaque session importante, mets à jour la section « État » ici, **en quelques lignes**, et écris le détail dans [`docs/historique-claude.md`](docs/historique-claude.md) (état détaillé de chaque version et journal des sessions, à ne lire qu'au besoin). Ce fichier est relu en entier à chaque session : garde-le sous 20 Ko.

**Lis aussi [`REPRISE.md`](REPRISE.md)** (état court, procédure de publication GitHub + winget, Microsoft Store, ce qui reste à valider et à faire).

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
- **Numéros de version** (règle du porteur, 29/09/2026, **précisée le 05/10/2026**) : corrections et améliorations (même nombreuses : boutons, présentation, nouvelles fonctions de l'Atelier) = **dernier chiffre** (0.6.0 → 0.6.1) ; **seulement les modifications majeures** = chiffre du milieu (0.6.x → 0.7.0), et en cas de doute **demander au porteur** ; le premier chiffre reste **0** tant que MAUS est une alpha. Changer `Directory.Build.props`, `AppVersionTests`, `CHANGELOG.md`, `README.md`. (Erreur du 05/10 : 0.7.0 donné à des améliorations, rectifié en 0.6.1.)

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
| `src/Maus.Core/Engine/Broker` | **0.8.0** (signature et anti-rejeu en 0.8.3 : `EngineKey`, `ReplayWindow`) : `IMausEngine` (liste fermée d'opérations : audit, plan, application par identifiants, annulation, journal, choix, et Atelier), `LocalMausEngine` (garde son propre audit), `EngineServer` / `RemoteMausEngine` (canal nommé `MAUS-<guid>`, réservé à l'utilisateur, refusé au réseau, première instance, processus vérifié des deux côtés, messages bornés), `EngineSensorSource` / `EngineProcessSource`, `EnginePreferencesStore` |
| `src/Maus.App` | Interface WPF (thème Fluent + `Themes/Maus.xaml` aux couleurs du logo), manifeste **`asInvoker`** depuis la 0.8.0 ; depuis la 0.8.1, **rien au démarrage** : `EngineHost.Configure` installe `OnDemandMausEngine` (moteur sans droits dans le processus + `EngineLauncher` qui lance `MAUS.exe --engine <canal> <pid> <empreinte de la clé>` par `runas` à la première action administrateur ; refus = `EngineDeclinedException`, cette action seulement) et `SplitPreferencesStore` (affichage dans `%LOCALAPPDATA%\MAUS\settings`, choix protégés par le moteur) ; MAUS lancé en administrateur = `LocalMausEngine` dans le processus, filet de sécurité qui journalise les plantages dans `%LOCALAPPDATA%\MAUS\logs`. Navigation à gauche : Accueil (score, M·A·U·S, actions rapides), Constats, Corrections, Atelier (Mon PC, En direct, Processus, Tests, Stockage, Mémoire, Pilotes), Historique, Benchmark. Contrôles dessinés dans `Controls/` (anneau, courbe, jauge de sécurité, tuile lettre) ; textes fixes dans `Ui.cs` |
| `tests/Maus.Core.Tests` | xUnit avec faux (`Fakes.cs` : FakeRegistry, FakeCim, FakeCommands, FakeEventLogs, FakePackages, FakeFiles, `TestContext.Create`) ; `App/XamlConsistencyTests` (garde-fou de l'interface, qui ne peut pas tourner sous Linux) ; `Localization/*` (couverture des traductions, bascule de langue) |
| `assets/logo/` | Logo fourni par le porteur (ne pas le redessiner) ; `tools/make-app-images.ps1` génère l'icône et les images de l'application |

Règles de code :
- **Detect et Plan = lecture seule absolue** : jamais de clé de registre ouverte en écriture, jamais de `Process.Start` hors `context.Commands`, jamais d'appel qui modifie un réglage. Tout accès système passe par `AuditContext` ou par une interface interne au module (P/Invoke, COM) avec un faux dans les tests.
- **Écrire (V0.2)** : uniquement par le `FixEngine`, via `FixContext` (jamais dans `AuditContext`). Un module corrigeable implémente `IFixableModule.Plan` et décrit ses corrections comme des écritures élémentaires (`SettingWrite` : registre, SPI, mode de gestion) ; le moteur journalise la valeur d'origine avant d'écrire, relit, défait la correction entière si une écriture échoue, et sait tout annuler. Nouveau type de réglage = nouveau `SettingKind` + lecture/écriture dans `SettingsAccessor` + faux.
- Stratégies (`Policies`) : restaurer = **supprimer** la valeur, jamais écrire la valeur « activée ». Stratégies posées seulement sur Pro et plus. Pré-coché (`Recommended`) = sans risque connu ; `Advanced` = jamais dans un profil hors de son domaine ; `Warning` = avertissement renforcé.
- Test de chaque module corrigeable : `RoundTrip.AssertAsync` (critère d'acceptation de la fiche : après Apply les constats sont conformes, après Revert un nouveau Detect est identique à l'état initial).
- Données illisibles → `Finding.Unknown` / `Finding.AdminRequired`, **jamais** un faux Problem.
- Écart → `FindingStatusExtensions.ForDeviation(severity)` : Critical/High = rouge, Medium = orange, Low = bleu (optimisation).
- Textes utilisateur en français clair ; identifiants `Mxx.nom-en-kebab`.
- **Trois langues (fr, en, es)** : tout texte affiché s'écrit en français dans `T("…")` (ou `T("… {0}", valeur)` pour un texte à trous, jamais d'interpolation `$"…"` affichée) ; la traduction se fait au moment où le texte est produit. Puis `dotnet run tools/i18n.cs -- missing en` (et `es`) liste ce qui manque ; ajouter les traductions dans `src/Maus.Core/Localization/i18n/*.json` (`merge`, `prune`). Le test `TranslationCoverageTests` échoue tant qu'un texte manque, ou si les `{0}` diffèrent.
  - Un champ `static readonly` qui contient `T(…)` doit être une propriété calculée (`=>`), sinon il resterait dans la langue du premier accès.
  - Catalogues JSON : traduire au moment de l'usage (`T(rule.Title)`, `Optional(rule.Advice)`) et déclarer les champs dans `tools/i18n.cs` (`CatalogTexts`) **et** dans `TranslationCoverageTests.Catalogs`.
  - Ne **jamais** passer par `T` un texte qui sert à reconnaître une sortie de Windows (`bcdedit`, `netsh`… en français ou en anglais), et ne jamais faire dépendre une logique d'un texte affiché (comparer à l'objet, au code, ou au même `T(…)`).
  - Dans une méthode générique `<T>`, écrire `Texts.T(…)` (le paramètre de type masque la fonction).
- Interface WPF : toute liaison bidirectionnelle (`IsChecked`, `SelectedItem`, `Value`, colonnes de `DataGrid`…) vers une propriété sans `set` doit porter `Mode=OneWay`, sinon la fenêtre plante au chargement ; `StaticResource` seulement pour des clés définies dans nos dictionnaires, `DynamicResource` pour les pinceaux Fluent. `XamlConsistencyTests` vérifie tout cela.
- Style : espaces de noms `file-scoped`, accolades toujours, **0 avertissement** (analyseurs `latest-recommended`), pas de trait de soulignement dans les espaces de noms.
- **Sécurité** (directives du porteur du 27/10/2026, inspirées du C++ et transposées au C#) : lancement de programme, `UseShellExecute = true`, `cmd.exe`, mémoire réservée à la main (rendue dans un `finally`), copie de mémoire brute, code `unsafe` et `string.Format` ne sont permis que dans les fichiers listés par `tests/…/Security/SecurityGuardTests.cs` ; un nouveau fichier qui en a besoin est relu (tailles en `checked(…)`, données externes bornées, arguments construits par MAUS et filtrés) avant d'être ajouté à la liste. Jamais de fonction C sans limite (msvcrt, `lstrcpy`…). Pages des Paramètres : `SettingsPages.IsValid`. CodeQL (`.github/workflows/codeql.yml`, `security-extended`) analyse chaque envoi : 0 alerte le 27/10.
- Nouveau module : classe `public sealed` avec constructeur sans paramètre (découverte automatique), `Order = N*10`.

## Commandes

```bash
dotnet build Maus.slnx --disable-build-servers
dotnet test Maus.slnx
dotnet run --project src/Maus.Cli -- --module M06
dotnet publish src/Maus.App -c Release -o publish/MAUS
```

- Dépôt : https://github.com/TERMINATORmdel101/MAUS (**public** depuis le 02/10/2026). Branche `main` = publication ; travail sur `claude/youthful-ride-ivazlw`.
- Identité Git des commits : `TERMINATORmdel101 <213405999+TERMINATORmdel101@users.noreply.github.com>`. Ne jamais utiliser l'adresse e-mail personnelle du porteur : GitHub refuse le push (adresse privée protégée).
- `nuget.config` du dépôt force nuget.org (la config NuGet globale du PC est vide).
- Sur le PC Windows : PowerShell 5.1 (pas de `&&`) ; chemins longs du dossier temporaire > 260 caractères = erreurs, travailler dans le dépôt.
- Tester l'interface : depuis la 0.8.0 le manifeste est déjà `asInvoker` ; lancée depuis le terminal de Claude Code (administrateur), une copie d'essai garde son moteur dans le même processus, sans écran d'autorisation. Copie jetable `.worktrees\_smoke`, clics par UI Automation, puis vérifier `%LOCALAPPDATA%\MAUS\logs` et le journal « .NET Runtime ».
- Ne jamais lancer MAUS élevé ni une commande qui modifie le PC du porteur sans son accord explicite. Les corrections (V0.2) se testent dans **Windows Sandbox**.

### Dans le cloud (Linux)

SDK : `apt-get install -y dotnet-sdk-10.0` (dépôt Ubuntu ; `dot.net/v1/dotnet-install.sh` est bloqué par le proxy). Les sessions cloud tournent sous Linux : l'application WPF et les API Windows (registre, WMI, P/Invoke) n'y fonctionnent pas. `EnableWindowsTargeting` est activé dans `Directory.Build.props` pour permettre la **compilation**. Les tests qui appellent de vraies API Windows échoueront sous Linux : écrire le code et les tests avec les faux, puis faire valider sur le PC Windows (build, tests, `maus` en lecture seule).

**Fichier de verrouillage sous Linux** : la compilation réécrit `src/Maus.App/packages.lock.json` (elle y ajoute `System.CodeDom` et `System.Diagnostics.EventLog`, que Windows fournit déjà). **Ne jamais commiter ce changement** : l'annuler avec `git checkout -- src/Maus.App/packages.lock.json`, sinon la restauration verrouillée de GitHub Actions risque d'échouer.

Échecs normaux sous Linux (27 sur 1 621 le 30/10, à comparer avant de croire à un défaut) : `EngineBrokerTests` 9, `StartupAppsModuleTests` 4, `ScheduledAuditTests` 3, `RiskyChangesAuditModuleTests` 3, `WindowsHealthModuleTests` 2, `RiskyChangesParsersTests` 2, et 1 pour `PresentMonCaptureTests`, `OnDemandEngineTests`, `EventLogReaderTests`, `EngineConfirmationTests`. Un seul avertissement, sous Linux seulement (CA1859, `WorkshopViewModel.cs:56`) ; 0 sous Windows.

## État (30/10/2026)

- **0.8.4 publiée** (préversion, 29/10) : GitHub https://github.com/TERMINATORmdel101/MAUS/releases/tag/0.8.4, winget #465525 **fusionnée le 30/10**. Contient le nouvel accueil et la sécurité du moteur. 1 621 tests, 0 avertissement.
- **Microsoft Store** : paquet prêt, `publish/store-0.8.4` ; c'est le porteur qui l'envoie. Voir `REPRISE.md` (« À faire en premier »).
- Tout ce qui n'est pas sur GitHub : `Documents\MAUS-sauvegarde-20261030` (`LISEZMOI.txt`), dont le rapport de failles **privé, à ne jamais écrire dans ce dépôt public**.
- Analyse privée de Claude (qualités, défauts, sécurité, écarts documents ↔ code, plan de relecture du code) : `Documents\MAUS-sauvegarde-20261030\analyse-claude\00-synthese.md`, à lire après ce fichier pour toute session sur la sécurité ou l'architecture. Elle nomme des faiblesses : **jamais dans ce dépôt**.
- Suite, procédure de publication, ce qui reste à vérifier : [`REPRISE.md`](REPRISE.md). Passé détaillé : `docs/historique-claude.md`.

## Pièges encore utiles

Windows et outils :
- **Terminal de Claude Code = administrateur** : pour essayer MAUS sans droits, le lancer par `explorer.exe`. Vérifier `asInvoker` dans l'exe d'une copie d'essai ; une publication incrémentale peut ignorer `-p:ApplicationManifest`.
- **Git Bash** : un heredoc Python casse les `\` et les longs scripts → écrire le script avec l'outil d'écriture, chaînes brutes `r"…"`. `perl -pi` abîme les chaînes C# contenant `\` suivi d'un chiffre. Un script Python à guillemets triples casse les `\"` : éditer ces lignes avec l'outil d'édition. `export MSYS_NO_PATHCONV=1` si un argument contient « / ». Jamais de `cat > fichier` sans entrée : la commande reste bloquée.
- **PowerShell 5.1** lit un `.ps1` en UTF-8 sans BOM comme de l'ANSI : pas d'accents dans les motifs de recherche d'un script. `$button` et `$Button` sont la même variable.
- **Copies d'essai** (`.worktrees\_smoke`, `accueil2.ps1`) : elles écrivent dans les vraies préférences du porteur (`LastSeenVersion`…), à sauver et remettre (ces scripts le font). Ne jamais tuer un MAUS ouvert par le porteur.
- `PrintWindow` ne voit pas le fond Mica (capture d'écran : `-Screen`). **Chez le porteur, la transparence de Windows est coupée** : Mica ne s'y affiche pas, c'est voulu.
- **Publication** : `gh release create` est bloqué par les autorisations de Claude Code tant que le porteur n'a pas confirmé dans la conversation. `GH_TOKEN` par `git credential fill`, ne jamais l'afficher. Le porteur modifie parfois `main` sur GitHub : `git fetch` d'abord. Un envoi qui ne touche qu'un test ne déclenche pas le workflow « Installateur ». Inno Setup n'est pas installé sur ce PC.
- Les agents échouent quand la limite de 5 h de l'abonnement est atteinte. La machine de GitHub est plus lente : pas de test dépendant du temps sans marge.

Code :
- WPF : un pinceau rangé dans `Application.Resources` est figé (`Pastel.Apply` y met des copies). Le thème Fluent pose lui-même le fond de la fenêtre : ne pas faire `ClearValue` dessus. Fluent pose aussi l'attribut Mica sans rendre le fond transparent.
- Ne jamais mettre `ThrowIfNull` sur un paramètre `T?` (`SettingsAccessor.Write(key, null)` = supprimer). `RoundTrip.AssertAsync` applique tout le plan : une correction qui défait les autres se teste à part. Depuis Windows 11 24H2, l'API Wlan répond ERROR_ACCESS_DENIED sans accès à la position. L'API DISM échoue dans un paquet MSIX (`dism.exe /CheckHealth` à la place).
- Benchmark : le porteur voit les passes en plein écran, préférer `--capture` (hors écran) ; une scène prépare ses données dans `Load`, à l'intérieur d'une image ; en HLSL 2018, `a ? b : c` évalue b et c ; ne jamais libérer un `GlyphRun` (il libère la police).
