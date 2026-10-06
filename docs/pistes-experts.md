# Ce qui manque pour les experts du PC — pistes (05/10/2026)

Question du porteur (30/09/2026) : « qu'est-ce qui manque pour les experts du PC ? ». Recherche faite le 05/10/2026 : ce que font les outils reconnus, comparé à ce que MAUS fait déjà. **S'inspirer sans copier** (ni code, ni présentation, ni données) et **aucune donnée inventée** : chaque seuil devra venir d'une source publiée avant d'entrer dans MAUS.

## Ce que MAUS fait déjà (vérifié dans le code)

- Usure des SSD : `MSFT_StorageReliabilityCounter` (M11, alerte à 80 %), température, erreurs de lecture.
- Usure de la batterie (capacité d'origine / actuelle, M16).
- Écrans bleus : nombre, code d'arrêt traduit, vidages mémoire comptés (M02) ; erreurs matérielles WHEA ; Driver Verifier actif détecté (M01).
- Capteurs, tests de stabilité (processeur, mémoire, mémoire vidéo, cœur par cœur), fiche mémoire (SPD, timings), pilotes, processus expliqués, démarrage, disque, réseau.

## Pistes, de la plus utile à la plus lourde

### 1. Écrans bleus : nommer le pilote probablement en cause

- **Ce que font les experts** : WinDbg (`!analyze -v`, champs « Probably caused by » et « IMAGE_NAME ») ou BlueScreenView lisent les vidages de `C:\Windows\Minidump` et désignent le pilote le plus probable.
- **Ce qui manque à MAUS** : il compte les vidages et traduit le code, mais ne nomme pas le pilote.
- **Comment** : lire le vidage avec le moteur de débogage de Windows (dbgeng), en lecture seule. Point dur : les symboles de Microsoft (téléchargés à la demande, donc réseau) ; sans eux, l'analyse est moins sûre. À présenter comme « pilote probablement en cause », jamais comme une certitude.
- **Effort** : gros. **Risque** : aucun pour le PC (lecture seule).
- Sources : [Microsoft Learn, Analyze Bug Check Blue Screen Data](https://learn.microsoft.com/en-ie/windows-hardware/drivers/debugger/blue-screen-data) ; [Dell, How to Analyze Blue Screen Dump Files Using WinDbg](https://www.dell.com/support/kbdoc/en-us/000149411/how-to-read-mini-dump-files).

### 2. Craquements audio et saccades : latence DPC / ISR — FAIT le 06/10/2026 (Atelier > Tests, trace ETW lue par MAUS, vérifiée sur le PC du porteur)

- **Ce que font les experts** : LatencyMon (logiciel propriétaire) mesure le temps d'exécution des routines DPC et ISR des pilotes et nomme les pilotes qui bloquent le processeur trop longtemps (cause classique de craquements audio et de micro-saccades).
- **Ce qui manque à MAUS** : rien de tel aujourd'hui.
- **Comment** : session ETW « NT Kernel Logger » avec les événements DPC et INTERRUPT (les mêmes que `xperf -on DPC+INTERRUPT`), pendant une minute, en administrateur ; regrouper par pilote. Pas de seuil d'alarme tant qu'aucune source publiée n'est retenue (principe 7) : afficher les durées mesurées et le classement des pilotes.
- **Effort** : moyen à gros. **Risque** : aucun (mesure seule).
- Sources : [Resplendence, LatencyMon](https://www.resplendence.com/latencymon) ; [Microsoft Learn, About Event Tracing](https://learn.microsoft.com/en-us/windows/win32/etw/about-event-tracing) ; [Microsoft Learn, Windows Performance Toolkit Xperf](https://learn.microsoft.com/en-us/archive/blogs/ntdebugging/windows-performance-toolkit-xperf) ; [OSR, Collecting Detailed Performance Data with Xperf](https://www.osr.com/nt-insider/2010-issue1/collecting-detailed-performance-data-xperf/).

### 3. Jeux : images par seconde et « 1 % low » dans le relevé de partie — FAIT le 05/10/2026

- **Ce que font les experts** : PresentMon (Intel, **licence MIT**) mesure la durée de chaque image, la latence et l'affichage, pour DirectX, OpenGL et Vulkan.
- **Ce qui manque à MAUS** : le relevé de partie mesure processeur, carte graphique, températures et mémoire, mais pas les images par seconde.
- **Comment** : lancer la console PresentMon (licence MIT, compatible avec la GPL-3.0, à citer dans `THIRD-PARTY-NOTICES.md`) pendant le relevé ; elle exige l'administrateur ou le groupe « Performance Log Users », ce qui est le cas de MAUS.
- **Effort** : moyen. **Risque** : aucun.
- Sources : [GitHub, GameTechDev/PresentMon](https://github.com/gametechdev/presentmon).

### 4. Santé détaillée des SSD NVMe — FAIT le 05/10/2026 (module 11)

- **Ce que font les experts** : CrystalDiskInfo ou smartctl lisent le journal de santé NVMe : « Available Spare » (réserve restante) et son seuil, « Percentage Used » (estimation du fabricant, peut dépasser 100 %), erreurs de média.
- **Ce qui manque à MAUS** : il lit l'usure par le compteur de fiabilité de Windows, mais pas la réserve ni son seuil, qui sont fournis par le disque lui-même (donc des seuils sourcés).
- **Comment** : structure `NVME_HEALTH_INFO_LOG` de Windows (requête de propriété de stockage), en lecture seule.
- **Effort** : moyen. **Risque** : aucun.
- Sources : [Microsoft Learn, NVME_HEALTH_INFO_LOG](https://learn.microsoft.com/en-us/windows/win32/api/nvme/ns-nvme-nvme_health_info_log).

### 5. « Les mythes de l'optimisation » — FAIT le 05/10/2026 (onglet Corrections, « Idées reçues que MAUS ne suit pas »)

- **Idée** : une page qui explique, sources à l'appui, pourquoi MAUS ne propose pas certains réglages souvent conseillés sur Internet (dans l'esprit du principe 6). Chaque mythe n'entre que s'il a une source publiée.
- **Effort** : petit à moyen (surtout de la recherche). **Risque** : aucun.

## Écarté

- Comparer ses résultats à ceux d'autres PC en ligne : impossible sans envoyer de données (principe 5, aucune télémétrie).
- Réglages du BIOS, overclocking automatique : jamais exécutés (principe 4), seulement guidés.

## Autres sources consultées

[GitHub, organisation latencymon-dpc-isr-latency](https://github.com/latencymon-dpc-isr-latency/.github) (non officielle : ses seuils ne sont pas retenus).
