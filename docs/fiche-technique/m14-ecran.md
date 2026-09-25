## Module 14 — Écran : fréquence et HDR

Ce module vérifie que chaque écran tourne à sa fréquence maximale en résolution native, sans que le HDR force une compression des couleurs en 4:2:2 ou 4:2:0. Il contrôle aussi que l'écran est relié à la bonne carte graphique.

**Détection :** `QueryDisplayConfig` (drapeaux `QDC_ONLY_ACTIVE_PATHS` et `QDC_VIRTUAL_MODE_AWARE`) donne chaque chemin actif et sa fréquence exacte, `targetInfo.refreshRate` ([doc](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig)). `DisplayConfigGetDeviceInfo` lit ensuite le connecteur dans `DISPLAYCONFIG_TARGET_DEVICE_NAME.outputTechnology` : 5 = HDMI, 10 = DisplayPort, 0x80000000 = dalle interne ([doc](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ne-wingdi-displayconfig_video_output_technology)). Les modes possibles viennent de `EnumDisplaySettingsExW`, filtrés sur la résolution native (`DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_PREFERRED_MODE`).

**Couleur :** à partir du build 26100 (24H2, 25H2 et 26H1), `DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2` renvoie `activeColorMode` (SDR, WCG ou HDR), `highDynamicRangeSupported`, `colorEncoding` et `bitsPerColorChannel` ([en-tête](https://www.mail-archive.com/mingw-w64-public@lists.sourceforge.net/msg22925.html), [HDRTray](https://github.com/res2k/HDRTray/pull/15/files)). Sur les versions antérieures, la version 1 (valeur 9) expose déjà `colorEncoding` et `bitsPerColorChannel` ([doc](https://microsoft.github.io/windows-docs-rs/doc/windows/Win32/Devices/Display/struct.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO.html)). Ne pas se fier à `Win32_VideoController.MaxRefreshRate` : sur le poste de test, il annonçait 60 Hz pour un écran réglé à 164 Hz.

**Pourquoi HDR et fréquence maximale se gênent :** le débit du câble est fixe, et le HDR exige 10 bits par couleur au lieu de 8.
- HDMI 2.0 : 18 Gbit/s ; HDMI 2.1 : 48 Gbit/s (câble Ultra High Speed) ; HDMI 2.2 : 96 Gbit/s (câble Ultra96) ([HDMI](https://www.hdmi.org/resource/cables)).
- DisplayPort 1.4 : 32,4 Gbit/s bruts ([VESA](https://vesa.org/displayport-developer/about-displayport/)), soit environ 25,9 Gbit/s utiles après codage 8b/10b (calcul).
- DisplayPort 2.1 : jusqu'à 80 Gbit/s. Selon VESA, la compression DSC réduit le débit de plus de 67 % sans artefact visible ([VESA](https://vesa.org/featured-articles/vesa-releases-displayport-2-1-specification/)).

**Exemple concret :** en 4K 10 bits RGB, 144 Hz demandent environ 36 Gbit/s de pixels actifs (calcul). Sans DSC, le lien passe alors en YCbCr 4:2:2 ou 4:2:0. L'Asus PG27UQ en DP 1.4 tient 98 Hz en 10 bits RGB et 120 Hz en 8 bits, puis passe en 4:2:2 à 144 Hz ([PCMonitors](https://pcmonitors.info/reviews/asus-pg27uq/)).

**Position de Microsoft :** face à des franges colorées sur le texte en HDMI, Microsoft conseille de passer en DisplayPort ou de baisser la fréquence ([Microsoft](https://support.microsoft.com/en-us/windows/hdr-settings-in-windows-2d767185-38ec-7fdc-6f97-bbc6c5ef24e6)). Cela confirme qu'une fréquence plus basse donne parfois un meilleur HDR.

**Écran sur la carte mère :** sur PC fixe (`Win32_ComputerSystem.PCSystemType` = 1), le LUID `targetInfo.adapterId` est comparé à `DXGI_ADAPTER_DESC1.AdapterLuid`. Si l'écran dépend d'un adaptateur intégré (`D3D12_FEATURE_DATA_ARCHITECTURE.UMA` = TRUE) alors qu'un GPU dédié existe, l'alerte passe au rouge. Sur portable, une dalle interne reliée à l'iGPU est normale.

**Action :**
- Fréquence : essai non enregistré avec `SetDisplayConfig` (`SDC_APPLY` et `SDC_USE_SUPPLIED_DISPLAY_CONFIG`), puis relecture de `colorEncoding`. Après validation, le réglage est enregistré avec `SDC_SAVE_TO_DATABASE` ([doc](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setdisplayconfig)).
- HDR actif et encodage 4:2:2 ou 4:2:0 : proposer le palier qui rétablit le RGB, qui n'est pas forcément 120 Hz.
- HDR, Auto HDR et VRR : aucune bascule automatique. On propose les liens `ms-settings:display` et `ms-settings:display-advancedgraphics` ([doc](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings)), ainsi que l'app Windows HDR Calibration (Windows 11, pilote WDDM 2.7 ou plus, [Microsoft](https://support.microsoft.com/en-us/windows/hardware/display-graphics/calibrate-your-hdr-display-using-the-windows-hdr-calibration-app)).
- Portable : fréquence maximale sur secteur. La fréquence dynamique (DRR) exige un écran VRR d'au moins 120 Hz ([Microsoft](https://support.microsoft.com/en-us/windows/change-the-refresh-rate-on-your-monitor-in-windows-c8ea729e-0678-015c-c415-f806f04aae5a)). Voir Module 5.

**Retour arrière :** sans validation sous 15 s, `SetDisplayConfig` avec `SDC_APPLY` et `SDC_USE_DATABASE_CURRENT` restaure la dernière configuration enregistrée. Un instantané pris avec `QDC_DATABASE_CURRENT` permet aussi une restauration manuelle.

**Message affiché à l'utilisateur :**
- « Votre écran accepte 165 Hz mais tourne à 60 Hz. Vous gagnez en fluidité partout, mais pas en images par seconde dans les jeux. »
- « En HDR à 144 Hz, votre câble compresse les couleurs et le texte bave. Une fréquence plus basse ou le DisplayPort donneront une image plus nette. »
- « Votre écran est branché sur la carte mère. Branchez-le sur une sortie de la carte graphique, plus bas à l'arrière du boîtier. »
- « Écran noir ? Ne touchez à rien : l'ancien réglage revient dans 15 secondes. »

| Contrôle | Source | Valeur saine | Alerte et action |
|---|---|---|---|
| Fréquence | `targetInfo.refreshRate` comparé à `EnumDisplaySettingsExW` | Maximum à la résolution native | Orange si inférieure : proposer le maximum |
| Encodage | `colorEncoding` | RGB ou YCbCr 4:4:4 | Orange si 4:2:2 ou 4:2:0 sur un moniteur PC : fréquence plus basse, DisplayPort, DSC ou câble certifié |
| Profondeur | `bitsPerColorChannel` | 10 bits ou plus en HDR | Information si 8 bits en HDR (risque de banding) |
| HDR | `highDynamicRangeSupported`, `activeColorMode` | Au choix de l'utilisateur | Information : liens vers les Réglages et vers la calibration |
| Connecteur | `outputTechnology` | DisplayPort, ou HDMI avec câble Ultra High Speed | Information si HDMI avec encodage 4:2:x : suggérer le DisplayPort |
| Adaptateur | LUID du chemin et `UMA` | GPU dédié sur PC fixe | Rouge : rebrancher le câble sur la carte graphique |
| VRR (G-SYNC, FreeSync) | NVAPI ou ADLX (à vérifier) | Activé si l'écran le gère | Information : rappel du réglage dans le pilote et dans le menu de l'écran |
| Auto HDR et VRR Windows | `HKCU\Software\Microsoft\DirectX\UserGpuPreferences`, valeur `DirectXUserGlobalSettings` (à vérifier) | Défaut Windows | Lecture seule, jamais d'écriture |
| DRR (portable) | Réglages, section Affichage avancé (détection à vérifier) | Activée si proposée | Information |
