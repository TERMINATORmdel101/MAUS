## Module 11 — Mini-benchmark santé

Ce module lance un test d'environ 2 min 30 s. Il cherche des anomalies (bridage, réglage aberrant, disque usé) et ne produit pas de score de compétition. Chaque composant reçoit un voyant vert, orange ou rouge, avec sa cause probable expliquée simplement.

**Conditions :** droits administrateur, car `Get-StorageReliabilityCounter` est refusé sans élévation (constaté sur le poste de test). Sur portable, le test tourne sur secteur, après 10 s de CPU au repos. Les résultats sont gardés dans un JSON local pour comparer avant et après optimisation : c'est la seule preuve honnête d'un gain.

**Détection CPU :** fréquence effective = `Processor Frequency` × `% Processor Performance` / 100 (objet PDH `Processor Information`), ou compteur `Actual Frequency` (présent en 25H2, à vérifier sous Windows 10). Sur le poste de test, 3 696 MHz × 132,4 % donnent 4 894 MHz, alors que `% of Maximum Frequency` reste à 100 et ignore donc le turbo. Les noms PDH sont traduits sous Windows français : passer par `PdhAddEnglishCounterW`.

**Températures :** `MSAcpi_ThermalZoneTemperature` lit une zone ACPI de la carte mère, pas le cœur du CPU. La vraie température CPU exige un pilote noyau. LibreHardwareMonitor ([MPL 2.0](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)) a remplacé WinRing0 par [PawnIO](https://github.com/namazso/PawnIO) (GPL 2.0, installation séparée) en septembre 2025 ([PR #1857](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/pull/1857)).

**Sans pilote :** WinRing0 est à proscrire, car Defender le signale comme `VulnerableDriver:WinNT/Winring0` ([Microsoft](https://support.microsoft.com/en-us/windows/microsoft-defender-antivirus-alert-vulnerabledriver-winnt-winring0-eb057830-d77b-41a2-9a34-015a5d203c42)). La température GPU se lit dans `D3DKMT_ADAPTER_PERFDATA.Temperature`, en dixièmes de degré ([doc](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/d3dkmthk/ns-d3dkmthk-_d3dkmt_adapter_perfdata)), et celle du NVMe dans `Get-StorageReliabilityCounter`. Sans PawnIO, la température CPU est omise : le bridage se déduit alors des fréquences.

**Référence de score :** les bases PassMark, Geekbench et 3DMark ne sont pas réutilisables sans accord (à vérifier juridiquement). En V1, on utilise une table JSON versionnée, mesurée en interne avec le même binaire, avec une tolérance de ±15 %. Pour un modèle absent de la table, seule la cohérence est jugée : turbo atteint, fréquence stable.

**RAM :** le débit se mesure par `memcpy` multithread sur 512 Mio, la latence par pointer chasing aléatoire sur 256 Mio. Débit théorique = canaux × `Win32_PhysicalMemory.ConfiguredClockSpeed` (MT/s) × 8 octets, soit 55,5 Go/s pour 2 × 3 467 MT/s. Canaux et XMP/EXPO : voir Module 10.

**GPU :** compute shader D3D12 sur l'adaptateur `DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE`, suivi par `\GPU Engine(*)\Utilization Percentage`. La fréquence vient de `D3DKMT_NODE_PERFDATA` (`Frequency` comparée à `MaxFrequency`, [doc](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/d3dkmthk/ns-d3dkmthk-_d3dkmt_node_perfdata)), si le pilote la renseigne. Le lien PCIe se lit sous charge : au repos, la RTX 2080 Ti testée tombait en Gen1 (voir Module 9).

**Stockage :** DiskSpd (Microsoft, licence [MIT](https://raw.githubusercontent.com/microsoft/diskspd/master/LICENSE)) lit un fichier temporaire de 1 Gio, jamais le disque brut, sans cache (`-w0 -Sh -L`, [paramètres](https://github.com/microsoft/diskspd/wiki/Command-line-and-parameters)). La santé vient de `Get-PhysicalDisk` (`MediaType` 3 = HDD, `BusType` 17 = NVMe, `HealthStatus`, [doc](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-physicaldisk)) et de `Get-StorageReliabilityCounter` ([doc](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-storagereliabilitycounter)). TRIM est actif si `fsutil behavior query DisableDeleteNotify` renvoie 0 ([doc](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/fsutil-behavior)).

**Débits de référence (ordres de grandeur) :** en lecture séquentielle, ~550 Mo/s en SATA, ~3,5 Go/s en NVMe Gen3, ~7 Go/s en Gen4 et ~14 Go/s en Gen5. Ces valeurs viennent des meilleurs SSD : un modèle d'entrée de gamme reste en dessous sans être défaillant. `WinSAT.exe` existe encore (build 26200) mais n'apparaît plus dans l'interface : il n'est pas utilisé.

**Causes affichées :** plan d'alimentation bridé (Module 5), XMP/EXPO coupé ou barrette seule (Module 10), GPU en slot x4 ou lien réduit (Module 9). Autres causes : surchauffe (poussière, pâte thermique), écran branché sur la carte mère (Module 14), application en arrière-plan (Module 12).

**Action :** le test ne modifie rien, et chaque correctif renvoie à son module. Seule action directe : réactiver TRIM (`fsutil behavior set disabledeletenotify 0`), puis lancer `Optimize-Volume -DriveLetter C -ReTrim`.

**Retour arrière :** le fichier de test est supprimé, même après annulation. L'ancienne valeur de `DisableDeleteNotify` est mémorisée et peut être restaurée.

**Message affiché à l'utilisateur :** « Ce test ne rend pas votre PC plus rapide : il vérifie qu'il fonctionne comme prévu. » Exemple de verdict : « Mémoire : 27 Go/s mesurés pour 55 possibles. Cause probable : une seule barrette ou XMP désactivé. »

| Composant | Test | Durée | Indicateur | Seuil d'alerte |
|---|---|---|---|---|
| CPU mono-cœur | Charge 1 thread (entiers et flottants) | 15 s | Fréquence effective maximale, score | Orange : `% Processor Performance` ≤ 100 sous charge sur un CPU à turbo (voir Module 5), ou score < 85 % de la référence |
| CPU multi-cœur | Charge sur tous les threads logiques | 45 s | Fréquence au début et à la fin, `% Performance Limit`, score | Orange : fréquence en baisse de plus de 15 %, ou limite < 100 pendant plus de 5 s. Rouge : score < 70 %, ou événement 37 de `Microsoft-Windows-Kernel-Processor-Power` (bridage firmware) |
| RAM | `memcpy` multithread, puis pointer chasing | 20 s | Débit mesuré rapporté au théorique, latence en ns | Orange : < 50 % du théorique, ou latence > 110 ns sur PC fixe (à calibrer). Rouge : < 35 % |
| GPU | Compute shader D3D12 | 30 s | Utilisation, fréquence rapportée à `MaxFrequency`, adaptateur utilisé, lien PCIe | Orange : utilisation < 90 %, ou fréquence < 80 % du maximum. Rouge : test sur l'iGPU alors qu'un GPU dédié existe |
| Stockage, débit | DiskSpd : 1 Mio séquentiel QD8, puis 4 Kio aléatoire QD1 et QD32 | 3 × 8 s, plus la création du fichier | Mo/s, IOPS, latence p99 | Orange : < 50 % du plafond de l'interface négociée, ou lien réduit (`DEVPKEY_PciDevice_CurrentLinkWidth` < `MaxLinkWidth`) |
| Stockage, santé | Lecture passive des compteurs | 2 s | `HealthStatus`, `Wear`, erreurs, température, TRIM, espace libre, type de média | Orange : `Wear` ≥ 80 % (seuil maison), TRIM coupé, espace libre < 15 %, Windows sur HDD. Rouge : `HealthStatus` = 2, `ReadErrorsUncorrected` > 0, `Temperature` ≥ `TemperatureMax`, `ReadLatencyMax` > 10 000 ms |
