## Module 9 — Pilotes carte graphique

Ce module compare le pilote de chaque GPU à la dernière version officielle et contrôle trois réglages bridants : HAGS, Resizable BAR, lien PCIe. Il ne télécharge aucun pilote : il ouvre la page ou l'application officielle du fabricant.

**Détection :** `Win32_VideoController` fournit `Name`, `DriverVersion`, `DriverDate`, `InfFilename` et `PNPDeviceID`. Le fabricant se lit dans `PNPDeviceID` : `VEN_10DE` (NVIDIA), `VEN_1002` (AMD), `VEN_8086` (Intel) ; les adaptateurs hors `PCI\` sont ignorés. Un pilote absent (adaptateur d'affichage de base Microsoft) déclenche une alerte prioritaire.

Portable hybride : si le fabricant impose ses pilotes, son outil passe en premier (à vérifier par modèle).

**Dernière version :** NVIDIA ne documente aucune API publique de version ; les points d'accès non officiels sont exclus. L'utilitaire ouvre la page officielle ou la NVIDIA App, sortie le 12 novembre 2024 [source](https://www.nvidia.com/en-us/geforce/news/nvidia-app-download-and-features/). AMD : Adrenalin Edition ; Intel : Intel Graphics Software, pour Arc et les iGPU de 11e à 14e génération [source](https://www.intel.com/content/www/us/en/products/docs/discrete-gpus/arc/software/drivers.html).

**Branches anciennes :** la branche 580 est la dernière pour Maxwell, Pascal et Volta : GTX 750, GTX 900, GTX 10 et TITAN V [source](https://www.phoronix.com/news/NVIDIA-580-Linux-Driver-Last-HW). La source vise Linux ; si la règle vaut sous Windows (à vérifier), une version 580 ou 581 reste acceptée sur ces cartes. Chez AMD, Adrenalin 25.10.2 place les RX 5000 et 6000 sur une branche distincte, toujours maintenue [source](https://www.club386.com/amd-clarifies-new-radeon-rx-5000-6000-driver-branch-will-continue-to-include-game-optimisations/).

**HAGS :** requis pour DLSS Frame Generation (RTX 40 et suivantes) et actif par défaut sous Windows 11, selon CD PROJEKT RED [source](https://support.cdprojektred.com/en/cyberpunk/pc/sp-technical/issue/2369/dlss-frame-generation-how-to-enable-hardware-accelerated-gpu-scheduling-1). Ailleurs, le gain attendu est faible (à vérifier).

**Resizable BAR :** NVIDIA annonce « quelques pour cent, jusqu'à 12 % » sur RTX 30, certains jeux perdant un peu [source](https://www.nvidia.com/en-us/geforce/news/geforce-rtx-30-series-resizable-bar-support/). Intel le requiert pour des performances optimales sur Arc [source](https://www.intel.com/content/www/us/en/support/articles/000092416/graphics.html). Il s'active dans le BIOS (voir tableau et Module 8).

**Lien PCIe :** la machine de test affiche Gen1 au repos et Gen3 en charge : seule la largeur se juge au repos. Une largeur inférieure à `MaxLinkWidth` trahit une carte mal enfoncée, un mauvais slot ou des lignes partagées avec un SSD M.2. Certaines cartes sont nativement x8 ou x4.

**Windows Update :** Windows peut remplacer un pilote GPU installé manuellement. En Pro, Entreprise et Éducation, la stratégie `ExcludeWUDriversInQualityUpdate` exclut tous les pilotes de Windows Update [source](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-update). Elle bloque donc aussi les autres périphériques, et probablement les firmwares des PC de marque (à vérifier) : voir Modules 3 et 8.

**Action :** afficher version, date et lien officiel. DDU reste réservé aux pannes : écran noir, plantages, passage de NVIDIA à AMD. Téléchargé uniquement sur wagnardsoft.com [source](https://www.wagnardsoft.com/display-driver-uninstaller-ddu), il se lance de préférence en mode sans échec, réseau coupé jusqu'à la réinstallation [source](https://www.wagnardsoft.com/content/How-use-Display-Driver-Uninstaller-DDU-Guide-Tutorial).

**Retour arrière :** chaque valeur modifiée est sauvegardée puis restaurée, ou supprimée si elle était absente ; HAGS exige un redémarrage. Pour le pilote : Gestionnaire de périphériques, onglet Pilote, « Restaurer le pilote ».

**Message affiché à l'utilisateur :** « Un pilote plus récent n'améliore les performances que dans certains jeux récents. Téléchargez-le uniquement sur nvidia.com, amd.com ou intel.com, ou via l'application officielle. Bloquer les pilotes de Windows Update bloque aussi ceux de vos autres périphériques. N'utilisez DDU qu'en cas de problème. »

| Réglage | Emplacement (clé, commande ou API) | Défaut Windows | Valeur appliquée |
|---|---|---|---|
| Version NVIDIA | `Win32_VideoController.DriverVersion` : cinq derniers chiffres des deux derniers champs (`32.0.16.1714` = 617.14, confirmé par `nvidia-smi`) | selon installation | Aucune : lien officiel proposé |
| Version AMD | Sous-clé `HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\000N` désignée par `DEVPKEY_Device_Driver` ; valeur `RadeonSoftwareVersion` (REG_SZ, à vérifier) | selon installation | Aucune : lien officiel proposé |
| Pilote de base Microsoft | `Win32_VideoController.InfFilename` = `display.inf` (à vérifier), le nom affiché étant traduit | absent | Alerte prioritaire |
| HAGS | `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers` : `HwSchMode` (REG_DWORD, 2 = activé, 1 = désactivé ; non documenté par Microsoft, à vérifier). État réel : `D3DKMTQueryAdapterInfo` et `D3DKMT_WDDM_2_7_CAPS` (`HwSchSupported`, `HwSchEnabled`), structure « réservée au système » [source](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/d3dkmdt/ns-d3dkmdt-d3dkmt_wddm_2_7_caps) | absente = choix du système et du pilote | 2 si RTX 40 ou plus et Frame Generation, avec accord ; sinon inchangé |
| Resizable BAR | Plus grande plage `Win32_DeviceMemoryAddress` associée au GPU (`Get-CimAssociatedInstance`), ou `nvidia-smi -q -d MEMORY` (BAR1 Total) ; BIOS : Above 4G Decoding, Re-Size BAR Support, CSM désactivé [source](https://www.intel.com/content/www/us/en/support/articles/000090831/graphics.html) | dépend du BIOS | Lecture seule : 256 Mio = inactif ; taille proche de la VRAM = actif |
| Lien PCIe | `DEVPKEY_PciDevice_CurrentLinkWidth` et `MaxLinkWidth`, `CurrentLinkSpeed` et `MaxLinkSpeed` (1 = 2,5 GT/s, 2 = 5 GT/s selon `pciprop.h` [source](https://github.com/tpn/winsdk-10/blob/master/Include/10.0.16299.0/shared/pciprop.h) ; 3 = Gen3 constaté ; 4 et 5 à vérifier) ; Radeon : port amont du commutateur interne (à vérifier) | négocié au démarrage | Lecture seule : largeur égale à `MaxLinkWidth` attendue |
| Pilotes via Windows Update | `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate` : `ExcludeWUDriversInQualityUpdate` (REG_DWORD) | absente ou 0 | 1 (Pro et plus), avec accord et avertissement |
| Paramètres d'installation de périphérique | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching` : `SearchOrderConfig` (REG_DWORD, sens des valeurs à vérifier ; efficacité partielle) | 1 | 0 (Famille), avec accord |

