# Paquet MSIX pour le Microsoft Store

Identité réservée dans le Partner Center (07/10/2026) :

| Champ | Valeur |
|---|---|
| Nom du produit | MAUS - Maintenance Audit Updates Security |
| Package/Identity/Name | `TERMINATEURmdel101.MAUS-MaintenanceAuditUpdatesSec` |
| Package/Identity/Publisher | `CN=6B0C0F7F-C76C-4C86-B80A-B15B69E93EBB` |
| PublisherDisplayName | `TERMINATEURmdel101` |
| Store ID | `9NXNPZX2LFQF` (https://apps.microsoft.com/detail/9NXNPZX2LFQF) |

`TERMINATEURmdel101` est l'identité réservée au Partner Center ; le compte GitHub et le paquet winget s'écrivent `TERMINATORmdel101`.

## Pourquoi un paquet MSIX

Un installateur .exe ou .msi n'entre au Store que signé par un certificat reconnu par Microsoft : impossible tant que MAUS n'est pas signé. Un paquet MSIX est signé gratuitement par le Store, mais l'élévation demande la capacité restreinte `allowElevation` (accordée sous conditions strictes) et les écritures dans HKCU n'atteignent les vrais réglages qu'avec `unvirtualizedResources`. Vérifié le 22/10/2026 avec le paquet installé : le moteur s'élève depuis `WindowsApps` ; l'API DISM y échoue (remplacée par `dism.exe /CheckHealth`). Pas vérifié : le fonctionnement sans `allowElevation`.

Les textes ci-dessous renvoient au document de sécurité, `docs/store-security-design.md`.

## Fabriquer le paquet

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer\msix\make-store-images.ps1   # seulement si le logo change
powershell -NoProfile -ExecutionPolicy Bypass -File installer\msix\build-msix.ps1
```

Résultat : `artifacts\msix\MAUS-<version>.msix` et son empreinte, non signé (le Store le signe après la certification).
Les outils `makeappx` et `makepri` viennent du paquet NuGet officiel `Microsoft.Windows.SDK.BuildTools`
(`msix-tools.csproj`) ; ils ne sont pas livrés avec MAUS.

Le script ne fabrique le paquet que si tout compile sans avertissement et si tous les tests passent, avec exactement les
bibliothèques des `packages.lock.json`. Il refuse d'écraser un paquet de la même version (`-Overwrite` pour passer outre).

**À partir de la 0.8.6, fabriquer le paquet à soumettre sur GitHub**, onglet Actions, « Paquet du Store » (lancé aussi à
chaque version publiée) : même script, à partir du code public, avec une attestation de provenance vérifiable par
`gh attestation verify MAUS-<version>.msix -R TERMINATORmdel101/MAUS`.

Images de la fiche du Store (zone 1:1, affiche 9:16) : `make-listing-images.ps1 -Out publish\store-<version>`.

Différences avec l'installateur classique :
- le dossier de MAUS change à chaque mise à jour (le numéro de version en fait partie) : entre la mise à jour et la
  prochaine ouverture de MAUS, la tâche de l'audit de la semaine ne trouve plus l'ancien dossier. À l'ouverture, MAUS le
  voit (`ScheduledAudit.IsOlderStoreFolder`) et le dit dans une carte de l'accueil, avec un bouton « Remettre en place ».
  Seul ce bouton recrée la tâche, après la question de MAUS ; le lire ne change rien, et Windows demande son autorisation à ce clic si le moteur est arrêté ;
- la désinstallation par Windows ne retire pas la tâche de l'audit de la semaine (l'installateur Inno le faisait) :
  la retirer avant (Vos choix > « Désactivé ») ; une tâche oubliée échoue sans rien faire ;
- le paquet n'exécute rien à la désinstallation : restent aussi `%ProgramData%\MAUS` (`journal`, `pilotes`, `logs`,
  `settings`, `tasks`), `%LOCALAPPDATA%\MAUS`, le dossier `\MAUS` du Planificateur et le pilote PawnIO s'il a été
  installé. À reprendre sur la fiche du Store.

## Textes à coller dans la soumission (en anglais)

### Restricted capabilities notes (« Notes sur les fonctionnalités restreintes »)

> **runFullTrust** — MAUS is a WPF desktop application (.NET 10) that runs as a full-trust packaged desktop app.
>
> **allowElevation** — Since version 0.8.1, MAUS opens without administrator rights and without any UAC prompt. In this limited mode, the read-only audit stays available without rights (with a partial score), and every option that needs administrator rights is greyed out (applying or undoing a fix, the history of fixes, Windows repair with DISM/SFC, graphics driver actions, memory module details, driver latency measurement, the frame counter, the optional weekly audit task; reading sensors through the optional PawnIO driver also needs the rights, installing it from MAUS does not). Only when the user clicks "All features" (startup window "How do you want to use MAUS?", or the banner at the top of the window), "Enable advanced sensors", or "Put back in place" on the notice of the home page (shown only for a weekly task left by an earlier version, after MAUS's own question) does MAUS start a separate "engine" process: the same MAUS.exe with the `--engine` argument, through ShellExecute "runas", which shows the standard UAC prompt. If the user declines, only that action is cancelled and MAUS stays usable without rights. Only the engine runs elevated: it accepts a closed list of operations from the window that started it, over a private named pipe restricted to the current user (closed to the network; Windows checks the process on each side; since 0.8.4 every request is signed with a per-launch secret key and can be used only once; since 0.8.4 the engine itself shows its own confirmation, which a non-elevated program cannot answer, before the most sensitive actions: terminating a process, graphics driver actions, marking a finding as wanted), and it exits when the window closes, when the user clicks "Close administrator rights" or after 15 minutes without a request (the options are then greyed out again). Elevation is needed because MAUS audits and repairs Windows: the audit reads data that is only available to administrators (component store health through `dism.exe /CheckHealth`, system event logs, some WMI classes, hardware sensors), and the fixes, which are only applied after the user selects them and confirms, create a system restore point (a recommended option the user can untick), write machine-wide settings (HKLM, Group Policy values on Pro editions) and can create an optional weekly read-only audit task.
>
> **New in this submission (0.8.6).** The engine was tightened again, and the design document below marks each change "since 0.8.6": the accessor itself now refuses any registry key outside the allowlist of §3.1, on apply and on undo; the weekly audit uses no network and runs without administrator rights; `MAUS.exe` is a small native host that removes the .NET environment variables before starting the runtime; the engine no longer starts `cmd.exe` and leaves no elevated prompt (it runs DISM, SFC and pnputil directly, and MAUS shows their progress); winget and the PawnIO installer start without administrator rights; the disk-space analysis accepts only a local folder, in any spelling, and never reads another account's profile; terminating a process refuses services, other sessions and other accounts; the preferences are bounded and checked when saved; reading the weekly task is a pure read that asks nothing and changes nothing, and the task is only re-created from the "Put back in place" button. New reads add no engine operation (§2.6): the BitLocker state, through two read-only WMI methods (the recovery key is never read, and MAUS never suspends or turns off BitLocker), the update history kept by Windows, what Windows notes about a held-back version, more of the System event log, and the past battery capacity (`powercfg /batteryreport /output <fixed path> /xml`, run by the window without administrator rights, the file deleted once read). The complete design is in `docs/store-security-design.md`, checked against the 0.8.6 source: https://github.com/TERMINATORmdel101/MAUS/blob/main/docs/store-security-design.md (also on the help site, https://sites.google.com/view/maus-help/full-technical-details). It gives, in this order: the exact allowlist of the 31 engine operations (§1); the commands, APIs, executables, arguments and targets of each (§2); the complete registry, file, folder, service, task, driver and configuration scope (§3); per-operation inputs, permitted values and validation (§4); pipe authentication, authorization, process verification and anti-replay (§5); why arbitrary commands, scripts, paths, arguments and executable launches are impossible (§6); elevation timing and engine termination per workflow (§7); pre-change confirmation and the declined-UAC behaviour (§8); restore-point failure, partial failure, cancellation, logging and undo (§9); the separation of read-only audit from privileged modification (§10).
>
> **unvirtualizedResources** (RegistryWriteVirtualization and FileSystemWriteVirtualization disabled) — many fixes are user settings stored under HKEY_CURRENT_USER (visual effects, privacy options, Game Bar, startup apps). With write virtualization, these changes would only go to a private copy and would not change Windows' actual settings, so the fixes would silently have no effect. Changes are logged with their original values and can be undone from the app.

### Notes for certification (« Notes pour la certification »)

> Please see the restricted capabilities notes above for runFullTrust, allowElevation and unvirtualizedResources.
>
> How to test:
>
> 1. Launch MAUS. It opens without administrator rights and shows no UAC prompt. A single MAUS window summarizes the warnings ("Read the full text of the warnings" shows all of them), asks to accept them, then offers two modes: "All features" shows the UAC prompt; "Limited mode" keeps MAUS without administrator rights. The language can be switched to English with the selector at the bottom left.
> 2. In limited mode, the options that need administrator rights are greyed out and a banner at the top says so. Click "All features" in that banner: Windows shows a UAC prompt for MAUS at this point; accept it. This starts the MAUS engine (the same MAUS.exe, with administrator rights), and the options become available. Then click "Run the audit" on the home page: the audit changes no setting and can take a few minutes.
> 3. If the UAC prompt is declined, nothing runs and a message says so: MAUS stays open in limited mode (for example Workshop > My PC, Live readings without the optional driver, Processes, Storage, Benchmark), and "All features" can be clicked again later.
> 4. The "Fixes" page lists the suggested changes; nothing is changed until the user ticks a fix and confirms. A verified restore point is created first (a pre-checked, recommended option the user can untick; if it fails, MAUS asks whether to continue). Applied fixes can be undone from the "History" page, unless a fix says otherwise beforehand or something else has changed a value since.
> 5. The engine stops by itself after 15 minutes without a request, when "Close administrator rights" is clicked, and when the MAUS window is closed; the options are then greyed out again, and clicking "All features" shows the UAC prompt again.
>
> No account, no sign-in and no internet connection are needed. An audit started by the user asks Windows Update for pending updates and lists available software upgrades with winget (offline, both simply report that they could not check); the optional weekly audit uses no network; everything else that uses the network is started by the user (software upgrades through winget, a web search, the ping test). MAUS collects no data.
>
> The optional visual benchmark (Benchmark page) runs in full screen for about twelve minutes and can be stopped at any time with Esc.
>
> Security design of the elevated engine (answers to the ten points of the 10.6.3 review, checked against the source code of this version, 0.8.6): https://github.com/TERMINATORmdel101/MAUS/blob/main/docs/store-security-design.md (also published on the MAUS help site: https://sites.google.com/view/maus-help/full-technical-details).
>
> Source code (GPL-3.0): https://github.com/TERMINATORmdel101/MAUS

### Nouveautés de la version (« Nouveautés de cette version », 0.8.6)

Le lien vise `main` : il ne décrit la 0.8.6 qu'après la fusion. À la soumission, le remplacer par un lien figé sur le commit soumis.

> **Français** — Nouvelle page Suivi : ce que Windows a défait, ce qui a changé entre deux audits, carnet de santé, plantages des 30 derniers jours, avant et après une séance, dossier du PC à imprimer. L'audit marche aussi sans autorisation administrateur. Il dit si BitLocker chiffre le disque (MAUS ne lit jamais la clé), avec un encadré avant une mise à jour du BIOS, quelle mise à jour échoue, et si Microsoft retient une nouvelle version. « Mon PC a planté » distingue les causes notées par Windows, et le dernier vrai redémarrage est indiqué. Plus de sécurité : le moteur refuse toute clé de registre hors de sa liste, et l'audit de la semaine tourne sans droits ni réseau. Document de sécurité : https://github.com/TERMINATORmdel101/MAUS/blob/main/docs/store-security-design.md
>
> **English** — New Follow-up page: what Windows undid, what changed between two audits, health log, crashes of the last 30 days, before and after a session, a PC file to print. The audit also works without administrator permission. It says whether BitLocker encrypts the disk (MAUS never reads the key), with a box before a BIOS update, which update fails, and whether Microsoft is holding back a new version. "My PC crashed" separates the causes noted by Windows, and the last real restart is shown. More security: the engine refuses any registry key outside its list, and the weekly audit runs without rights or network. Security design: https://github.com/TERMINATORmdel101/MAUS/blob/main/docs/store-security-design.md
>
> **Español** — Nueva página Seguimiento: lo que Windows ha deshecho, lo que ha cambiado entre dos auditorías, registro de salud, bloqueos de los últimos 30 días, antes y después de una sesión, expediente del PC para imprimir. La auditoría también funciona sin autorización de administrador. Indica si BitLocker cifra el disco (MAUS nunca lee la clave), con un recuadro antes de una actualización de la BIOS, qué actualización falla y si Microsoft retiene una nueva versión. «Mi PC se ha bloqueado» distingue las causas anotadas por Windows, y se indica el último reinicio real. Más seguridad: el motor rechaza cualquier clave del registro fuera de su lista, y la auditoría semanal funciona sin derechos ni red. Documento de seguridad: https://github.com/TERMINATORmdel101/MAUS/blob/main/docs/store-security-design.md

#### 0.8.5 (pour mémoire)

> **Français** — Relecture complète du code de MAUS. Le moteur administrateur est plus strict : il confirme lui-même les actions sur le pilote graphique, refuse une annulation forcée venue de la fenêtre, et ne fait plus jamais le rapport de batterie (la fenêtre s'en charge, sans droits). La fenêtre ne se fige plus pendant l'installation de PawnIO, un audit ne peut plus en doubler un autre, et les profils de corrections sont traduits dans les trois langues. Le document de sécurité publié avec MAUS décrit cette version.
>
> **English** — Complete review of the MAUS code. The administrator engine is stricter: it confirms graphics-driver actions itself, refuses a forced undo coming from the window, and never runs the battery report any more (the window does, without rights). The window no longer freezes while PawnIO is being installed, an audit can no longer overlap another, and the fix profiles are translated in all three languages. The security document published with MAUS describes this version.
>
> **Español** — Revisión completa del código de MAUS. El motor de administrador es más estricto: confirma él mismo las acciones sobre el controlador gráfico, rechaza una anulación forzada procedente de la ventana y ya no genera nunca el informe de batería (lo hace la ventana, sin derechos). La ventana ya no se bloquea durante la instalación de PawnIO, una auditoría ya no puede solaparse con otra y los perfiles de correcciones están traducidos a los tres idiomas. El documento de seguridad publicado con MAUS describe esta versión.

Nouveautés des versions précédentes : `CHANGELOG.md`.

## Fiche du Store

- **Catégorie** : Utilitaires et outils (Utilities & tools).
- **Prix** : gratuit. **Marchés** : tous.
- **Politique de confidentialité** : https://github.com/TERMINATORmdel101/MAUS/blob/main/PRIVACY
- **Site web** : https://sites.google.com/view/maus-help/accueil
- **Contact d'assistance** : https://sites.google.com/view/maus-help/accueil
- **Captures** : `docs/captures` (accueil, Suivi, Atelier > Tests, Benchmark). La capture de la fenêtre de surveillance est en
  hauteur ; le Store demande des captures d'ordinateur d'au moins 1366 × 768 : la garder pour GitHub seulement.
- **Classification par âge** : questionnaire du Partner Center (aucun contenu sensible, pas d'achats, pas de discussion
  entre utilisateurs).

### Description (français)

> MAUS (Maintenance · Audit · Updates · Sécurité) vérifie votre PC Windows 11 sans rien modifier, puis vous propose des corrections que vous choisissez vous-même.
>
> Chaque constat est expliqué en français clair. Chaque correction dit ce qu'elle change, avec le gain attendu et les risques connus. Avant d'appliquer vos corrections, MAUS crée un point de restauration vérifié (option recommandée, que vous pouvez décocher ; s'il échoue, MAUS vous demande s'il faut continuer) et note les valeurs d'origine : un bouton « Annuler » les rétablit, sauf si autre chose a modifié une valeur entre-temps.
>
> La page Suivi garde sur votre PC de quoi voir ce qui change avec le temps : ce que Windows a défait de vos corrections, ce qui diffère entre deux audits, un carnet de santé (disques, batterie, démarrage), les plantages récents. L'audit marche aussi sans droits administrateur (score partiel).
>
> L'Atelier va plus loin : fiche complète du matériel, températures et fréquences en direct, tests de stabilité du processeur, de la mémoire vive et de la mémoire de la carte graphique, avec arrêt automatique en cas de surchauffe quand la température et sa limite sont lisibles (la température du processeur demande le pilote facultatif PawnIO). Un benchmark visuel mesure la carte graphique (DirectX 11 et 12, lancer de rayons) et le processeur.
>
> Aucune donnée collectée, aucune publicité. Gratuit et libre (GPL-3.0). En français, anglais et espagnol.
>
> MAUS est conçu et codé avec Claude, une IA d'Anthropic, sous la direction de son auteur. Version alpha : sauvegardez vos données avant d'appliquer des corrections.

### Description (English)

> MAUS (Maintenance · Audit · Updates · Security) checks your Windows 11 PC without changing anything, then suggests fixes that you choose yourself.
>
> Every finding is explained in plain language. Each fix says what it changes, with its expected benefit and any known risk. Before applying your fixes, MAUS creates a verified restore point (a recommended option you can untick; if it fails, MAUS asks whether to continue) and logs the original values: an "Undo" button restores them, unless something else has changed a value since.
>
> The Follow-up page keeps on your PC what you need to see what changes over time: what Windows undid among your fixes, what differs between two audits, a health log (disks, battery, startup), recent crashes. The audit also works without administrator rights (partial score).
>
> The Workshop goes further: full hardware details, live temperatures and clocks, stability tests for the processor, the RAM and the graphics card's memory, with automatic stop on overheating when the temperature and its limit can be read (the processor temperature needs the optional PawnIO driver). A visual benchmark measures the graphics card (DirectX 11 and 12, ray tracing) and the processor.
>
> No data collected, no ads. Free and open source (GPL-3.0). In English, French and Spanish.
>
> MAUS is designed and coded with Claude, an AI by Anthropic, under the direction of its author. Alpha version: back up your data before applying fixes.

### Description (español)

> MAUS (Mantenimiento · Auditoría · Updates · Seguridad) revisa su PC con Windows 11 sin modificar nada y luego le propone correcciones que usted mismo elige.
>
> Cada resultado se explica con claridad. Cada corrección indica qué cambia, con la ganancia esperada y los riesgos conocidos. Antes de aplicar sus correcciones, MAUS crea un punto de restauración verificado (opción recomendada que puede desmarcar; si falla, MAUS le pregunta si desea continuar) y anota los valores originales: un botón «Deshacer» los restablece, salvo si otra cosa ha cambiado un valor después.
>
> La página Seguimiento guarda en el PC lo necesario para ver qué cambia con el tiempo: lo que Windows ha deshecho de las correcciones, lo que difiere entre dos auditorías, un registro de salud (discos, batería, inicio) y los bloqueos recientes. La auditoría también funciona sin derechos de administrador (puntuación parcial).
>
> El Taller va más allá: ficha completa del hardware, temperaturas y frecuencias en directo, pruebas de estabilidad del procesador, de la memoria RAM y de la memoria de la tarjeta gráfica, con parada automática por sobrecalentamiento cuando la temperatura y su límite se pueden leer (la temperatura del procesador requiere el controlador opcional PawnIO). Un benchmark visual mide la tarjeta gráfica (DirectX 11 y 12, trazado de rayos) y el procesador.
>
> Sin recopilación de datos y sin publicidad. Gratuito y libre (GPL-3.0). En español, francés e inglés.
>
> MAUS está diseñado y programado con Claude, una IA de Anthropic, bajo la dirección de su autor. Versión alfa: haga una copia de seguridad de sus datos antes de aplicar correcciones.

