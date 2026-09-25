## Module 5 — Alimentation

Ce module règle un PC fixe sur le mode de gestion « Haute performance » (`8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c`). Un portable reçoit « Meilleures performances » sur secteur et « Équilibré » sur batterie. Ces modes d'alimentation n'apparaissent qu'avec « Utilisation normale » ou un mode dérivé ([source](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/customize-power-slider), [source](https://support.microsoft.com/en-us/windows/change-the-power-mode-for-your-windows-pc-c2aff038-22c9-f46d-5ca0-78696fdf2de8)).

**Détection :**

- Portable si deux indices sur trois concordent. Indices : `Win32_SystemEnclosure.ChassisTypes` dans 8, 9, 10, 11, 14, 30, 31 ou 32 ([source](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-systemenclosure)) ; `Win32_ComputerSystem.PCSystemTypeEx` à 2 ou 8 ([source](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-computersystem)) ; batterie selon `GetSystemPowerStatus`. Un onduleur USB peut simuler une batterie (à vérifier).
- Modern Standby : champ `AoAc` de `CallNtPowerInformation(SystemPowerCapabilities)`. Ces machines n'exposent souvent qu'« Utilisation normale » ([source](https://learn.microsoft.com/en-us/answers/questions/3956190/windows-11-power-options-only-showing-balanced-pow)).
- État : `PowerGetActiveScheme`, puis l'API documentée `PowerRegisterForEffectivePowerModeNotifications` pour le mode effectif, Mode Jeu compris ([source](https://learn.microsoft.com/en-us/windows/win32/api/powersetting/nf-powersetting-powerregisterforeffectivepowermodenotifications)).
- Processeur : `Win32_Processor.Name` repère les 7900X3D, 7950X3D, 9900X3D et 9950X3D, puis le service `amd3dvcacheSvc` ([source](https://wiki.vrchat.com/wiki/Guides:AMD_X3D_Series_Processors)). Le 9950X3D2 porte du V-Cache sur ses deux CCD ([source](https://www.storagereview.com/review/amd-ryzen-9-9950x3d2-dual-edition-review-3d-v-cache-on-both-ccds)) : l'utilité du parking y est à vérifier. Intel hybride : plusieurs `EfficiencyClass` via `GetLogicalProcessorInformationEx(RelationProcessorCore)`.
- Utilitaires constructeur : Armoury Crate, Lenovo Vantage, Legion Space, MSI Center, Alienware Command Center, OMEN Gaming Hub (noms de paquets à vérifier).
- Démarrage rapide : `HiberbootEnabled` sous `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power`, effectif seulement si la veille prolongée est disponible (`powercfg /a`).

**Action :**

- Fixe : `powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c`. Option experte : `powercfg /duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61` crée « Performances optimales » ([source](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/powercfg-command-line-options)). Gain attendu négligeable, à mesurer (voir Module 11).
- Ryzen X3D double CCD : rester sur `381b4222-f694-41f0-9685-ff5bb260df2e`, avec Mode Jeu et Game Bar à jour. La Game Bar oriente les jeux vers le CCD doté du V-Cache ([source](https://www.pcworld.com/article/1528458/three-ryzen-9-7950x-misconceptions-debunked.html)) ; une source AMD primaire reste à trouver. Voir Module 7.
- Intel hybride (12e génération et plus) : « Utilisation normale » avec « Meilleures performances » (à vérifier, aucune consigne Intel trouvée). Microcode : voir Module 8.
- Portable : `PowerSetUserConfiguredACPowerMode` avec `ded574b5-45a0-4f42-8737-46345c09c238`, puis `PowerSetUserConfiguredDCPowerMode` avec le GUID « Équilibré ». Ces exports de `powrprof.dll` existent sur le build 26200.9457 mais ne sont pas documentés (signatures à vérifier). Paramètres documente bien ce double réglage secteur et batterie ([source](https://support.microsoft.com/en-us/windows/change-the-power-mode-for-your-windows-pc-c2aff038-22c9-f46d-5ca0-78696fdf2de8)).
- `powercfg /setactiveoverlay`, proposé sur Microsoft Q&A ([source](https://learn.microsoft.com/en-us/answers/questions/5ecfd8d2-99ec-45aa-b8a9-9ed36a26dba5/changing-overlay-power-mode-with-script?forum=windows-all)), est absent de `powercfg /?` et rejeté sur ce build. Dernier recours : ouvrir `ms-settings:powersleep` ([source](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings)).
- Utilitaire constructeur détecté : l'outil n'écrase rien. Il invite à choisir « Performance » dans cet utilitaire, qui pilote aussi puissance et ventilateurs.
- Démarrage rapide : `HiberbootEnabled` = 0, sans toucher à la veille prolongée. Actif, il recharge à chaque démarrage le noyau et les pilotes figés dans `hiberfil.sys` ([source](https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/distinguishing-fast-startup-from-wake-from-hibernation)). Le guide Ryzen Master conseille de le couper, mais aussi « Haute performance », à écarter sur X3D ([source](https://docs.amd.com/r/en-US/68886-ryzen-master-user-guide/Recommended-Power-Settings)).

| Profil détecté | Mode de gestion (GUID) | Mode sur secteur | Mode sur batterie | Remarque |
|---|---|---|---|---|
| Fixe standard | Haute performance `8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c` | Sans objet | Sans objet | Demande de l'utilisateur |
| Fixe, option experte | Copie de Performances optimales `e9a42b02-d5df-448d-aa00-03f14749eb61` | Sans objet | Sans objet | Gain négligeable (à mesurer), conso au repos plus haute |
| Fixe Ryzen X3D double CCD | Utilisation normale `381b4222-f694-41f0-9685-ff5bb260df2e` | Équilibré (Meilleures performances à valider) | Sans objet | Parking des cœurs AMD ; cas du 9950X3D2 à vérifier |
| Fixe Intel hybride | Utilisation normale | Meilleures performances `ded574b5-45a0-4f42-8737-46345c09c238` | Sans objet | À vérifier, y compris la présence du mode d'alimentation sur PC fixe |
| Portable | Utilisation normale | Meilleures performances `ded574b5-45a0-4f42-8737-46345c09c238` | Équilibré : GUID à vérifier, `00000000-0000-0000-0000-000000000000` selon des sources communautaires, `3af9b8d9-7c97-431d-ad78-34a8bfea439f` (« Better Performance ») dans la doc OEM | Demande de l'utilisateur |
| Portable, option économie | Utilisation normale | Équilibré | Meilleure efficacité énergétique `961cc777-2547-4f9d-8174-7d86181b8a7a` | Proposé, jamais imposé |

**Retour arrière :** l'instantané stocke le GUID actif, les deux modes, `HiberbootEnabled` et les plans créés. La restauration utilise `powercfg /setactive`, puis `powercfg /delete` sur les copies. `powercfg /restoredefaultschemes`, absent de la documentation et de `powercfg /?`, reste un dernier recours : il efface les plans personnalisés (à vérifier).

**Message affiché à l'utilisateur (selon le profil) :**

- PC fixe : « Le processeur reste plus souvent à haute fréquence. Le gain en jeu est souvent faible et la consommation au repos augmente. »
- Portable : « Plus de chaleur et de bruit sur secteur, autonomie préservée sur batterie. »
- Ryzen X3D : « Nous gardons le mode Utilisation normale pour placer vos jeux sur les bons cœurs. »
- Démarrage rapide : « Sa désactivation peut rendre l'allumage un peu plus lent. »
