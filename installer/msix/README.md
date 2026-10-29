# Paquet MSIX pour le Microsoft Store

Identité réservée par le porteur dans le Partner Center (07/10/2026) :

| Champ | Valeur |
|---|---|
| Nom du produit | MAUS - Maintenance Audit Updates Security |
| Package/Identity/Name | `TERMINATEURmdel101.MAUS-MaintenanceAuditUpdatesSec` |
| Package/Identity/Publisher | `CN=6B0C0F7F-C76C-4C86-B80A-B15B69E93EBB` |
| PublisherDisplayName | `TERMINATEURmdel101` |
| Store ID | `9NXNPZX2LFQF` (https://apps.microsoft.com/detail/9NXNPZX2LFQF) |

## Fabriquer le paquet

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer\msix\make-store-images.ps1   # seulement si le logo change
powershell -NoProfile -ExecutionPolicy Bypass -File installer\msix\build-msix.ps1
```

Résultat : `artifacts\msix\MAUS-<version>.msix`, non signé (le Store le signe après la certification). Les outils
`makeappx` et `makepri` viennent du paquet NuGet officiel `Microsoft.Windows.SDK.BuildTools` (`msix-tools.csproj`) ;
ils ne sont pas livrés avec MAUS.

Différences avec l'installateur classique :
- le dossier de MAUS change à chaque mise à jour (le numéro de version en fait partie) : au démarrage, MAUS remet la
  tâche de l'audit de la semaine sur son nouveau dossier (`ScheduledAudit.IsOlderStoreFolder`) ; entre la mise à jour et
  la prochaine ouverture de MAUS, la tâche ne trouve plus l'ancien dossier ;
- la désinstallation par Windows ne retire pas la tâche de l'audit de la semaine (l'installateur Inno le faisait) :
  la retirer avant (Vos choix > « Désactivé ») ; une tâche oubliée échoue sans rien faire.

## Textes à coller dans la soumission (en anglais)

### Restricted capabilities notes (« Notes sur les fonctionnalités restreintes »)

> **runFullTrust** — MAUS is a WPF desktop application (.NET 10) that runs as a full-trust packaged desktop app.
>
> **allowElevation** — Since version 0.8.1, MAUS opens without administrator rights and without any UAC prompt. In this limited mode, every option that needs administrator rights is greyed out (the audit, applying or undoing a fix, the history of fixes, Windows repair with DISM/SFC, graphics driver actions, the optional advanced sensors through the PawnIO driver, memory module details, driver latency measurement, the frame counter, the optional weekly audit task). Only when the user clicks "All features" (startup window "How do you want to use MAUS?", or the banner at the top of the window) does MAUS start a separate "engine" process: the same MAUS.exe with the `--engine` argument, through ShellExecute "runas", which shows the standard UAC prompt. If the user declines, only that action is cancelled and MAUS stays usable without rights. Only the engine runs elevated: it accepts a closed list of operations from the window that started it, over a private named pipe restricted to the current user (closed to the network; Windows checks the process on each side; since 0.8.3 every request is signed with a per-launch secret key and can be used only once; since 0.8.4 the engine itself shows its own confirmation, which a non-elevated program cannot answer, before the most sensitive actions: terminating a process, graphics driver actions, PawnIO, marking a finding as wanted), and it exits when the window closes or after 15 minutes without a request (the next action asks again). Elevation is needed because MAUS audits and repairs Windows: the audit reads data that is only available to administrators (component store health through the DISM API, system event logs, some WMI classes, hardware sensors), and the fixes, which are only applied after the user selects them and confirms, create a system restore point, write machine-wide settings (HKLM, Group Policy values on Pro editions) and can create an optional weekly read-only audit task.
>
> **unvirtualizedResources** (RegistryWriteVirtualization and FileSystemWriteVirtualization disabled) — many fixes are user settings stored under HKEY_CURRENT_USER (visual effects, privacy options, Game Bar, startup apps). With write virtualization, these changes would only go to a private copy and would not change Windows' actual settings, so the fixes would silently have no effect. Every change is logged with its original value and can be undone from the app.

### Notes for certification (« Notes pour la certification »)

> Please see the restricted capabilities notes above for runFullTrust, allowElevation and unvirtualizedResources.
>
> How to test:
>
> 1. Launch MAUS. It opens without administrator rights and shows no UAC prompt. A MAUS window, "How do you want to use MAUS?", explains the two modes: "All features" shows the UAC prompt; "Limited mode" keeps MAUS without administrator rights (a checkbox stops this question at startup; it can be turned back on in Settings). The language can be switched to English with the selector at the bottom left.
> 2. In limited mode, the options that need administrator rights are greyed out and a banner at the top says so. Click "All features" in that banner: Windows shows a UAC prompt for MAUS at this point; accept it. This starts the MAUS engine (the same MAUS.exe, with administrator rights), and the options become available. Then click "Run the audit" on the home page: the audit is read-only and takes about 15 seconds.
> 3. If the UAC prompt is declined, nothing runs and a message says so: MAUS stays open in limited mode (for example Workshop > My PC, Live readings without the optional driver, Processes, Storage, Benchmark), and "All features" can be clicked again later.
> 4. The "Fixes" page lists the suggested changes; nothing is changed until the user ticks a fix and confirms, and a restore point is created first. Every applied fix can be undone from the "History" page.
> 5. The engine stops by itself after 15 minutes without a request, and when the MAUS window is closed; the next action that needs administrator rights shows the UAC prompt again.
>
> No account, no sign-in and no internet connection are needed (the network is only used on explicit user request, for example to check software updates through winget). MAUS collects no data.
>
> The optional visual benchmark (Benchmark page) runs in full screen for about ten minutes and can be stopped at any time with Esc.
>
> Security design of the elevated engine (answers to the ten points of the 10.6.3 review, checked against the source code of this version): `docs/store-security-design.md` in the source repository, also published on the MAUS help site.
>
> Source code (GPL-3.0): https://github.com/TERMINATORmdel101/MAUS

### Nouveautés de la version (« Nouveautés de cette version », 0.8.4)

> **Français** — Nouvelle fenêtre d'ouverture « Comment utiliser MAUS ? » : mode limité, sans droits administrateur, ou toutes les fonctionnalités. En mode limité, les options qui demandent les droits sont grisées et un bandeau les active. Plus de sécurité : messages signés entre la fenêtre et le moteur administrateur, protections de Windows sur MAUS, et confirmation affichée par le moteur lui-même avant les actions sensibles (fermer un programme, pilote graphique, PawnIO).
>
> **English** — New opening window "How do you want to use MAUS?": limited mode, without administrator rights, or all features. In limited mode, options that need administrator rights are greyed out and a banner enables them. More security: signed messages between the window and the administrator engine, Windows protections on MAUS, and a confirmation shown by the engine itself before sensitive actions (closing a program, graphics driver, PawnIO).
>
> **Español** — Nueva ventana de inicio «¿Cómo quiere usar MAUS?»: modo limitado, sin derechos de administrador, o todas las funciones. En modo limitado, las opciones que necesitan derechos aparecen en gris y una banda las activa. Más seguridad: mensajes firmados entre la ventana y el motor administrador, protecciones de Windows en MAUS, y confirmación mostrada por el propio motor antes de las acciones sensibles (cerrar un programa, controlador gráfico, PawnIO).

#### 0.8.1 (pour mémoire)

> **Français** — MAUS s'ouvre sans demander l'autorisation administrateur : Windows la demande seulement quand vous lancez une action qui en a besoin (audit, corrections, réparation, capteurs avancés…). Un refus n'annule que cette action. Les droits administrateur se referment seuls après 15 minutes sans utilisation. Onglet Mémoire des processeurs Intel : horloge contrôlée avec la vitesse déclarée par Windows, durées en nanosecondes corrigées, tWR et tRTP affichés.
>
> **English** — MAUS opens without asking for administrator permission: Windows asks only when you start an action that needs it (audit, fixes, repair, advanced sensors…). Declining cancels only that action. Administrator rights close by themselves after 15 minutes without use. Memory tab for Intel processors: clock checked against the speed reported by Windows, corrected durations in nanoseconds, tWR and tRTP shown.
>
> **Español** — MAUS se abre sin pedir la autorización de administrador: Windows la pide solo cuando inicias una acción que la necesita (auditoría, correcciones, reparación, sensores avanzados…). Rechazarla solo cancela esa acción. Los derechos de administrador se cierran solos tras 15 minutos sin uso. Pestaña Memoria de los procesadores Intel: reloj comprobado con la velocidad que declara Windows, duraciones en nanosegundos corregidas, tWR y tRTP visibles.

#### 0.8.0 (pour mémoire)

> **Français** — Version de sécurité et de stabilité. La fenêtre de MAUS ne tourne plus avec les droits administrateur : seul un moteur séparé, lancé avec l'autorisation de Windows, lit le système en profondeur et applique les corrections. Corrections dont le retour arrière a échoué gardées visibles, recherche Windows Update vraiment interrompue à l'annulation, arrêt de sécurité du benchmark en cas de surchauffe, plus de faux résultat « stable » dans les tests, comparaisons de benchmark plus justes.
>
> **English** — Security and stability release. The MAUS window no longer runs with administrator rights: only a separate engine, started with the Windows permission prompt, reads the system in depth and applies fixes. Fixes whose rollback failed now stay visible, the Windows Update search is really stopped on cancel, the benchmark stops on overheating, no more false "stable" test results, fairer benchmark comparisons.
>
> **Español** — Versión de seguridad y estabilidad. La ventana de MAUS ya no se ejecuta con derechos de administrador: solo un motor separado, iniciado con la autorización de Windows, lee el sistema en profundidad y aplica las correcciones. Las correcciones cuya restauración falló siguen visibles, la búsqueda de Windows Update se detiene de verdad al cancelar, el benchmark se detiene en caso de sobrecalentamiento, ya no hay falsos resultados «estable» en las pruebas, comparaciones de benchmark más justas.

## Fiche du Store

- **Catégorie** : Utilitaires et outils (Utilities & tools).
- **Prix** : gratuit. **Marchés** : tous.
- **Politique de confidentialité** : https://github.com/TERMINATORmdel101/MAUS/blob/main/PRIVACY
- **Site web** : https://sites.google.com/view/maus-help/accueil
- **Contact d'assistance** : https://sites.google.com/view/maus-help/accueil
- **Captures** : `docs/captures` (accueil, Atelier > Tests, Benchmark). La capture de la fenêtre de surveillance est en
  hauteur ; le Store demande des captures d'ordinateur d'au moins 1366 × 768 : la garder pour GitHub seulement.
- **Classification par âge** : questionnaire du Partner Center (aucun contenu sensible, pas d'achats, pas de discussion
  entre utilisateurs).

### Description (français)

> MAUS (Maintenance · Audit · Updates · Sécurité) vérifie votre PC Windows 11 sans rien modifier, puis vous propose des corrections que vous choisissez vous-même.
>
> • Audit en lecture seule : mises à jour, confidentialité, démarrage, alimentation, pilotes, santé du matériel, sauvegardes. Chaque constat est expliqué en français clair : ce qu'il fait, le gain réel, le risque.
> • Corrections réversibles : point de restauration vérifié avant toute modification, valeurs d'origine notées, bouton « Annuler ».
> • Atelier : fiche du matériel, températures et fréquences en direct, tests du processeur, de la mémoire vive et de la mémoire de la carte graphique avec arrêt automatique en cas de surchauffe.
> • Benchmark visuel : carte graphique (DirectX 11 et 12, lancer de rayons) et processeur.
> • Aucune donnée collectée, aucune publicité. Gratuit et libre (GPL-3.0). En français, anglais et espagnol.
>
> MAUS est conçu et codé avec Claude, une IA d'Anthropic, sous la direction de son auteur. Version alpha : sauvegardez vos données avant d'appliquer des corrections.

### Description (English)

> MAUS (Maintenance · Audit · Updates · Security) checks your Windows 11 PC without changing anything, then suggests fixes that you choose yourself.
>
> • Read-only audit: updates, privacy, startup, power, drivers, hardware health, backups. Every finding is explained in plain language: what it does, the real benefit, the risk.
> • Reversible fixes: a verified restore point before any change, original values logged, one-click undo.
> • Workshop: hardware details, live temperatures and clocks, processor, RAM and video memory stability tests with automatic stop on overheating.
> • Visual benchmark: graphics card (DirectX 11 and 12, ray tracing) and processor.
> • No data collected, no ads. Free and open source (GPL-3.0). In English, French and Spanish.
>
> MAUS is designed and coded with Claude, an AI by Anthropic, under the direction of its author. Alpha version: back up your data before applying fixes.

### Description (español)

> MAUS (Maintenance · Audit · Updates · Security) revisa tu PC con Windows 11 sin modificar nada y luego te propone correcciones que eliges tú mismo.
>
> • Auditoría de solo lectura: actualizaciones, privacidad, inicio, energía, controladores, estado del hardware, copias de seguridad. Cada resultado se explica con claridad: qué hace, la ganancia real, el riesgo.
> • Correcciones reversibles: punto de restauración verificado antes de cualquier cambio, valores originales anotados, botón «Deshacer».
> • Taller: ficha del hardware, temperaturas y frecuencias en directo, pruebas de estabilidad del procesador, de la memoria RAM y de la memoria de vídeo con parada automática en caso de sobrecalentamiento.
> • Benchmark visual: tarjeta gráfica (DirectX 11 y 12, trazado de rayos) y procesador.
> • No recopila ningún dato, sin publicidad. Gratuito y libre (GPL-3.0). En español, francés e inglés.
>
> MAUS está diseñado y programado con Claude, una IA de Anthropic, bajo la dirección de su autor. Versión alfa: haz una copia de seguridad de tus datos antes de aplicar correcciones.
