# CLAUDE.md — mémoire du projet MAUS

Ce fichier est la mémoire de Claude entre les sessions. Lis-le en entier avant de travailler. Garde-le court (moins de 20 Ko) et juste : à la fin d'une session importante, mets à jour « État » en quelques lignes ; le contenu des versions va dans `CHANGELOG.md`.

**Ce dépôt est public** : n'y écris ni faille, ni contenu privé, ni détail personnel. Le privé reste hors dépôt (`Documents\MAUS-sauvegarde-20261030`, `LISEZMOI.txt`, à lire pour toute session sur la sécurité ou l'architecture).

## Le porteur du projet

- Réponds **en français**, simplement, sans jargon inutile, en expliquant le « pourquoi ». Il dicte souvent : comprends les fautes de frappe. Il répond souvent par numéros.
- Il veut garder la main : partout où c'est possible, **le choix revient à l'utilisateur final**, avec une valeur recommandée pré-cochée.
- **Budget : zéro euro.** Gratuit, open source. Le logiciel **assume d'être fabriqué par une IA**.
- **Commite souvent**.
- C'est lui qui écrit à la communauté (forums, messages) ; textes pour Microsoft = oui. C'est lui qui soumet au Store : **ne jamais soumettre à sa place**. Publication GitHub et winget : seulement avec son accord.

## Le projet

**MAUS** (Maintenance · Audit · Updates · Sécurité) : utilitaire Windows 11 qui audite, répare, met à jour et optimise le PC en toute transparence.

Principes non négociables :
1. **Audit d'abord, action ensuite** : lecture seule, rapport vert / bleu / orange / rouge / gris, puis corrections choisies.
2. **Tout est réversible** : point de restauration vérifié + journal des valeurs d'origine + bouton « Annuler ».
3. **Tout est expliqué** en français clair : ce que fait le réglage, le gain, le risque, le retour arrière.
4. **Rien de dangereux en automatique** : BIOS, XMP/EXPO, overclocking sont guidés (liens officiels, tutoriels), jamais exécutés.
5. **Aucune télémétrie** de l'outil. Donc pas de comparaison en ligne avec d'autres PC (le code de partage du benchmark se passe à la main).
6. **Honnêteté sur les gains** : gains réalistes (« quelques pour cent », « gain faible », « aucun gain de performance »), jamais +10 % ; ne jamais retirer une fonction importante de Windows ni une protection pour un gain de performance.
7. **Aucune donnée inventée** : chaque seuil, limite, décalage de registre ou affirmation technique vient d'une source publiée (citée dans le catalogue, le commentaire ou l'ancienne fiche). **Pas de source = pas de seuil ni d'alarme** ; une valeur non vérifiée s'affiche comme telle, jamais comme un fait. Registres mémoire (`intel-memory-controller.json`) : un champ n'entre qu'avec ses sources ; une seule = `"confidence": "single"` ; sources en désaccord = champ écarté.

Décisions prises :
- Windows 11 uniquement, à partir de 23H2 (build 22631). Windows 10 hors périmètre.
- Licence **GPL-3.0-only** + conditions additionnelles article 7(c) et 7(e) : le nom « MAUS » et son logo ne sont pas cédés (voir `TRADEMARKS.md`).
- Distribution : Microsoft Store (accepté le 10/11/2026, 0.8.5, `9NXNPZX2LFQF`), GitHub, winget. Signature gratuite SignPath Foundation refusée le 03/10 : à redemander plus tard ; aucun abonnement payant.
- **Choix laissés à l'utilisateur** : portable = question au premier lancement ; Ryzen X3D à deux CCD = Utilisation normale + Game Bar recommandées ; pilotes par Windows Update jamais bloqués par défaut (blocage au choix, M09) ; anti-triche (Vanguard, FACEIT) = avertissement renforcé en M13, pas de blocage ; Copilot, Recall, OneDrive au choix ; profils en un clic + chaque réglage modifiable ligne par ligne.
- **MAUS ne propose jamais** : nettoyeur de registre, « libérer la RAM », supprimer le fichier d'échange, réglages TCP « gaming », résolution du minuteur, options `bcdedit` de débogage, couper les atténuations CPU, Defender, SmartScreen, UAC ou Windows Update, désinstaller Edge, WebView2 ou le Store, forcer le mode MSI, supprimer dans WinSxS, couper des services en masse, bloquer la télémétrie par `hosts`, images allégées, « driver updaters », overclocking automatique. M01 signale ce qu'il en trouve.
- **BitLocker** : MAUS ne le suspend ni ne le désactive jamais lui-même et n'en fait ni correction ni entrée de profil, mais il peut informer, sans décrire de faiblesse, que le désactiver est un choix de l'utilisateur, avec ce que cela retire et la procédure officielle de Microsoft (décision du porteur, 2/11/2026).
- Écartés : vérification de mise à jour dans MAUS (à cause du Store) ; arrêt du PC en cas d'attaque sur le moteur (une arme pour un programme malveillant).
- **Numéros de version** : corrections et améliorations (même nombreuses) = **dernier chiffre** (0.6.0 → 0.6.1) ; **seulement les modifications majeures** = chiffre du milieu (0.6.x → 0.7.0), et en cas de doute **demander au porteur** ; le premier chiffre reste **0** tant que MAUS est une alpha.
- Légal : une clause ne peut pas retirer ses droits à un consommateur (Code de la consommation, R212-1, 6°) ; marques de tiers en texte seul, sans logo.

## Architecture (C# / .NET 10, WPF)

| Dossier | Rôle |
|---|---|
| `src/Maus.Core/Abstractions` | `IAuditModule` (Detect), `Finding`, `FindingStatus`, `Severity`, `AuditContext` |
| `src/Maus.Core/Platform` | Lecteurs **en lecture seule** (registre, WMI, commandes par `ReadOnlyCommandRunner` et sa liste blanche, journaux, fichiers) et ce qui agit (`ProcessHardening`, tâches planifiées, pilotes graphiques ; `VisibleConsole` et winget, sans droits) |
| `src/Maus.Core/Hardware` | Profil matériel commun (fixe ou portable, CPU, GPU, PC géré) |
| `src/Maus.Core/Rules` + `Catalog/*.json` | Règles de registre déclaratives, catalogues embarqués (`mXX-*.json`) |
| `src/Maus.Core/Engine` | `AuditEngine` : découverte des modules par réflexion, exécution parallèle avec délai ; `ScheduledAudit` |
| `src/Maus.Core/Engine/Broker` | `IMausEngine` (liste fermée d'opérations), `LocalMausEngine`, `EngineServer` / `RemoteMausEngine` (canal nommé signé, `EngineKey`, `ReplayWindow`), `OnDemandMausEngine` ; description complète : `docs/store-security-design.md` |
| `src/Maus.Core/Modules/MxxNom/` | Un dossier par module (M01 à M20 ; M16 = Atelier matériel) |
| `src/Maus.Core/Fixes` | `IFixableModule.Plan`, `PlannedChange` / `SettingWrite`, `FixEngine` (Apply, Verify, Revert), `FixContext`, journal (`FileJournalStore`), `RestorePointCreator`, `FixProfile` |
| `src/Maus.Core/Localization` | `Texts` : français = langue source ; traductions `i18n/en.json` et `i18n/es.json` (embarquées) |
| `src/Maus.Core/Preferences` | Choix de l'utilisateur (`preferences.json` protégé sous `%ProgramData%\MAUS\settings`) |
| `src/Maus.Core/Reporting` | Rapports texte / JSON / HTML (`HtmlReport`), `HealthScore` (score sur 100, familles M·A·U·S) |
| `src/Maus.Core/Workshop` | Atelier sans pilote noyau embarqué (PawnIO en option) : inventaire, `SafetyLimits` (`Catalog/hw-safety-limits.json`), capteurs, processus (`Catalog/processes.json`), tests, mémoire, benchmark |
| `src/Maus.Core/Follow` | Page Suivi : compare des relevés gardés, carnet de santé, fiche du matériel, dossier du PC (fichiers du profil, jamais lus par le moteur) |
| `src/Maus.Bench` | Benchmark visuel Direct3D 11 et 12, lancé par `MAUS.exe --benchmark` |
| `src/Maus.Cli` | `maus` : audit en ligne de commande (`--module Mxx`, `--json`, `--html`, `--lang fr\|en\|es`), corrections et choix (voir `--help`) |
| `src/Maus.App` | Interface WPF (thème Fluent + `Themes/Maus.xaml`), manifeste **`asInvoker`**, **rien au démarrage** : `EngineHost.Configure` installe `OnDemandMausEngine` (`EngineLauncher` lance `MAUS.exe --engine` par `runas` à la première action administrateur ; refus = `EngineDeclinedException`, cette action seulement) et `SplitPreferencesStore` ; lancé en administrateur = `LocalMausEngine` dans le processus. Plantages : `%LOCALAPPDATA%\MAUS\logs`. Textes fixes dans `Ui.cs`. `Host/MausHost.cpp` : lanceur natif du `MAUS.exe` publié (Visual C++ exigé à la publication) |
| `tests/Maus.Core.Tests` | xUnit avec faux (`Fakes.cs`, `TestContext.Create`) ; `App/XamlConsistencyTests` (garde-fou de l'interface, pas sous Linux) ; `Localization/*` |
| `assets/logo/` | `maus-logo.jpg` : logo du porteur (hors GPL, ne pas le redessiner). `tools/make-app-images.ps1` en tire les images |

Règles de code :
- **Detect et Plan = lecture seule absolue** : jamais de clé de registre ouverte en écriture, jamais de `Process.Start` hors `context.Commands`, jamais d'appel qui modifie un réglage. Tout accès système passe par `AuditContext` ou par une interface interne au module (P/Invoke, COM) avec un faux dans les tests.
- **Écrire** : uniquement par le `FixEngine`, via `FixContext`. Un module corrigeable implémente `IFixableModule.Plan` et décrit ses corrections en `SettingWrite` ; le moteur journalise la valeur d'origine, relit, défait la correction entière si une écriture échoue, et sait tout annuler. Nouveau type de réglage = nouveau `SettingKind` + `SettingsAccessor` + faux.
- Stratégies (`Policies`) : restaurer = **supprimer** la valeur, jamais écrire la valeur « activée ». Sur Windows Famille, leur effet n'est pas garanti : l'audit juge l'état effectif et le plan le dit. Pré-coché (`Recommended`) = sans risque connu ; `Advanced` = jamais dans un profil hors de son domaine ; `Warning` = avertissement renforcé.
- Test de chaque module corrigeable : `RoundTrip.AssertAsync` (après Apply les constats sont conformes, après Revert un nouveau Detect est identique à l'état initial). Il applique tout le plan : une correction qui défait les autres se teste à part.
- Données illisibles → `Finding.Unknown` / `Finding.AdminRequired`, **jamais** un faux Problem.
- Écart → `FindingStatusExtensions.ForDeviation(severity)` : Critical/High = rouge, Medium = orange, Low = bleu (optimisation).
- Textes utilisateur en français clair ; identifiants `Mxx.nom-en-kebab`.
- **Trois langues (fr, en, es)** : tout texte affiché s'écrit en français dans `T("…")` (ou `T("… {0}", valeur)` pour un texte à trous, jamais d'interpolation `$"…"` affichée) ; la traduction se fait au moment où le texte est produit. Puis `dotnet run tools/i18n.cs -- missing en` (et `es`) liste ce qui manque ; ajouter les traductions dans `src/Maus.Core/Localization/i18n/*.json` (`merge <langue> fichier.json`, `prune <langue>`). Le test `TranslationCoverageTests` échoue tant qu'un texte manque, ou si les `{0}` diffèrent.
  - Un champ `static readonly` qui contient `T(…)` doit être une propriété calculée (`=>`), sinon il resterait dans la langue du premier accès.
  - Catalogues JSON : traduire au moment de l'usage (`T(rule.Title)`, `Optional(rule.Advice)`) et déclarer les champs dans `tools/i18n.cs` (`CatalogTexts`) **et** dans `TranslationCoverageTests.Catalogs`.
  - Ne **jamais** passer par `T` un texte qui sert à reconnaître une sortie de Windows (`bcdedit`, `netsh`… en français ou en anglais), et ne jamais faire dépendre une logique d'un texte affiché.
  - Dans une méthode générique `<T>`, écrire `Texts.T(…)` (le paramètre de type masque la fonction).
- Interface WPF : toute liaison bidirectionnelle (`IsChecked`, `SelectedItem`, `Value`, colonnes de `DataGrid`…) vers une propriété sans `set` doit porter `Mode=OneWay`, sinon la fenêtre plante au chargement ; `StaticResource` seulement pour des clés définies dans nos dictionnaires, `DynamicResource` pour les pinceaux Fluent. `XamlConsistencyTests` vérifie tout cela.
- Style : espaces de noms `file-scoped`, accolades toujours, **0 avertissement** (analyseurs `latest-recommended`).
- **Sécurité** : lancement de programme, `UseShellExecute = true`, `cmd.exe`, mémoire réservée à la main, copie de mémoire brute, code `unsafe` et `string.Format` ne sont permis que dans les fichiers listés par `SecurityGuardTests` (relu avant tout ajout) ; tout programme lancé a System32 pour dossier de travail. Pages des Paramètres : `SettingsPages.IsValid`. Processus élevé : jamais winget, rien lu ni écrit dans le profil (benchmark : `BenchmarkOutput`).
- Nouveau module : classe `public sealed` avec constructeur sans paramètre (découverte automatique), `Order = N*10`.
- Changement de scène ou de référence du benchmark : compléter `BenchmarkScoring.EditionOf`.

## Commandes

```bash
dotnet build Maus.slnx --disable-build-servers
dotnet test Maus.slnx
dotnet run --project src/Maus.Cli -- --module M06
dotnet publish src/Maus.App -c Release -o publish/MAUS
```

- Dépôt : https://github.com/TERMINATORmdel101/MAUS. `main` = publication (un envoi qui touche `src/`, `installer/` ou `Directory.Build.props` lance le workflow « Installateur », après tous les tests) ; travail sur une branche à part (`claude/youthful-ride-ivazlw`), fusionnée dans `main` à la publication. Le porteur modifie parfois `main` sur GitHub : `git fetch` d'abord.
- Identité Git des commits : `TERMINATORmdel101 <213405999+TERMINATORmdel101@users.noreply.github.com>`. Jamais d'adresse personnelle : GitHub refuse le push.
- Sur le PC Windows : PowerShell 5.1 (pas de `&&`) ; chemins de plus de 260 caractères = erreurs.
- **Ne jamais lancer MAUS élevé ni une commande qui modifie le PC du porteur sans son accord explicite** ; ne jamais fermer un MAUS qu'il a ouvert. Corrections, PawnIO, tâche planifiée et actions sur les pilotes se testent d'abord dans **Windows Sandbox**. En lecture seule, sans accord particulier : `dotnet build`, `dotnet test`, `maus` (audit), `maus --plan`, `maus --summary`.
- Tester l'interface : copie jetable `.worktrees\_smoke`, puis vérifier `%LOCALAPPDATA%\MAUS\logs` et le journal « .NET Runtime ». Lancée en administrateur, une copie garde son moteur dans le même processus : pour l'essayer sans droits, passer par `explorer.exe`. Une copie d'essai écrit dans les vraies préférences (`LastSeenVersion`…) : les sauver et les remettre.
- **Contrôle avant commit** (`gate`, script hors dépôt) : traductions, compilation à 0 avertissement, tous les tests. Jamais de `new App()` dans un test.
- Benchmark : préférer `maus --benchmark --capture DOSSIER --scene ID --times 5,30` (hors écran) aux passes en plein écran.

### Dans le cloud (Linux)

SDK : `apt-get install -y dotnet-sdk-10.0`. WPF et API Windows n'y fonctionnent pas (les tests qui les appellent y échouent) ; `EnableWindowsTargeting` permet la **compilation**. Écrire le code et les tests avec les faux, puis valider sur le PC Windows.

**Sous Linux**, la compilation réécrit `src/Maus.App/packages.lock.json` : **ne jamais commiter ce changement** (`git checkout --` sur ce fichier).

## Publier une version

Seulement avec l'accord du porteur.

1. Numéro : `Directory.Build.props`, `AppVersionTests`, `CHANGELOG.md`, `README.md`, « Quoi de neuf » (`Views/VersionNotes.cs`), textes du Store (`installer/msix/README.md`).
2. Fusion dans `main`. Attendre que le workflow « Installateur » réussisse (à la main si l'envoi ne touche ni `src/` ni `installer/`). Test fragile sur GitHub : `CurveOptimizerProgramTests.Silent_core_in_phase_1…` (relancer).
3. `gh pr edit` échoue, passer par `gh api`. `gh release create` demande une confirmation du porteur dans la conversation.
4. Télécharger l'installateur du workflow (`gh run download`), vérifier SHA-256 et provenance (`gh attestation verify … -R TERMINATORmdel101/MAUS`), notes en anglais (mention « non signé » exigée), `gh release create X.Y.Z` (version « Latest », sans `--prerelease`, décision du porteur depuis la 0.8.5), puis retélécharger depuis la page publique et comparer le SHA-256.
5. winget : copier le dossier de la version précédente dans `installer/winget/manifests/t/TERMINATORmdel101/MAUS/`, changer version, date, adresse et SHA-256, `winget validate --manifest`, puis dans le clone `.worktrees\_winget` : branche partie de `upstream/master`, commit « New version: TERMINATORmdel101.MAUS version X.Y.Z », envoi sur le fork, `gh pr create -R microsoft/winget-pkgs`.
6. Store : voir `installer/msix/README.md` ; garder `docs/store-security-design.md` exact pour la version soumise, et ne pas le déplacer (le Store le cite à ce chemin).

## État (10/11/2026)

- **0.8.5 publiée** (30/10) et winget ; **acceptée sur le Microsoft Store** (en ligne le 10/11/2026, `allowElevation` accordé).
- **0.8.6 non publiée** (contenu : `CHANGELOG.md` ; ajouts du 2/11 : BitLocker, mises à jour qui échouent, retenue, « Mon PC a planté », dernier redémarrage, batterie passée) ; 3 466 tests, 0 avertissement. Prête à fusionner dans `main` ; envoi et publication seulement sur son accord. Captures de `docs/captures` (encore 0.7.5) à refaire.
- Catalogue `m09-gpu-drivers.json` : **à revérifier chaque mois** (un ticket GitHub le rappelle ; `checkedOn` ; passé 45 jours, MAUS ne dit plus « à jour »).

À valider :
- Windows Sandbox : corrections réelles (`--apply-recommended`, point de restauration, journal, `--revert`) ; actions sur le pilote graphique.
- Autre matériel : processeurs AMD (fiche mémoire, à comparer avec ZenTimings), DDR5, autres familles Intel (comparer avec CPU-Z puis passer `verifiedOnHardware` à `true`), cartes graphiques AMD et Intel, Wi-Fi, Recall, batterie passée sur un portable.
- À l'écran : score après un audit, retour au gris après 15 minutes, « Ouvrir MAUS » après l'audit de la semaine.

Non terminé :
- **Écrans bleus, nommer le pilote probablement en cause** : lire `C:\Windows\Minidump` avec dbgeng, en lecture seule. Point dur : les symboles de Microsoft (réseau). Dire « probable », jamais une certitude.
- **Vraies tensions VDD / VDDQ DDR5** : lire le PMIC par le SMBus, **en lecture seule**. Rien n'est codé ; d'abord une **source vérifiable** des registres (JEDEC PMIC5100 / PMIC5010, ou code libre).
- Relecture des traductions anglaises et espagnoles par des locuteurs natifs ; relecture juridique de `TRADEMARKS.md`.
- Idées à choisir : 1 essai auto de la fenêtre ; 2 données abîmées ; 3 pannes au pire moment ; 4 endurance 24 h ; 5 Stryker.NET ; 8 OpenSSF Scorecard ; 9 SBOM ; 11 Accessibility Insights ; 13 GitHub = bêta, Store = stable.

## Documents

- Pour tous : `README.md`, `CHANGELOG.md`, `SECURITY.md`, `PRIVACY`, `TRADEMARKS.md`, `THIRD-PARTY-NOTICES.md`. Microsoft : `docs/store-security-design.md`, `installer/msix/README.md`.
- Dans l'historique Git seulement (périmés), au commit `3899236` : `docs/fiche-technique/` (avec leurs sources), `docs/historique-claude.md`, `docs/pistes-experts.md`, `REPRISE.md`.

## Pièges encore utiles

Windows et outils :
- Une publication incrémentale peut ignorer `-p:ApplicationManifest` : vérifier `asInvoker` dans l'exe d'une copie d'essai.
- **Git Bash** : un heredoc Python casse les `\` et les longs scripts → écrire le script avec l'outil d'écriture, chaînes brutes `r"…"`. `perl -pi` abîme les chaînes C# contenant `\` suivi d'un chiffre. `export MSYS_NO_PATHCONV=1` si un argument contient « / ».
- **PowerShell 5.1** lit un `.ps1` UTF-8 sans BOM comme de l'ANSI : pas d'accents dans les motifs de recherche.

Code :
- WPF : un pinceau rangé dans `Application.Resources` est figé (`Pastel.Apply` y met des copies). Le thème Fluent pose lui-même le fond de la fenêtre : ne pas faire `ClearValue` dessus.
- Ne jamais mettre `ThrowIfNull` sur un paramètre `T?` (`SettingsAccessor.Write(key, null)` = supprimer). Depuis Windows 11 24H2, l'API Wlan répond ERROR_ACCESS_DENIED sans accès à la position. L'API DISM échoue dans un paquet MSIX (`dism.exe /CheckHealth` à la place). `Win32_PnPSignedDriver` peut inverser jour et mois (dates des pilotes par `DEVPKEY_Device_DriverDate`).
- Benchmark : une scène prépare ses données dans `Load`, à l'intérieur d'une image ; en HLSL 2018, `a ? b : c` évalue b et c.
