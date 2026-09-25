## Module 6 — Interface et effets visuels

Ce module masque le bouton Vue des tâches et les Widgets, puis coupe animations et transparence. Il ne garde que 4 des 17 cases de « Options de performances > Effets visuels ». Sur un GPU récent, le gain mesurable est négligeable : l'interface paraît surtout plus vive.

**Détection :** l'outil lit les valeurs du tableau et `VisualFXSetting` sous `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects`. La valeur 3 signifie « Personnalisé » ; le sens de 0, 1 et 2 est à vérifier. Les 17 cases sont déclarées sous `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects\<nom>` avec `SPIActionSet`, `RegPath`, `ValueName` et `DefaultValue`, relevés sur le build 26200.9457.

Seule `ThumbnailsOrIcon` y déclare un gestionnaire `CLSID` au lieu d'un chemin. L'outil lit ces définitions à l'exécution au lieu de les coder en dur.

**Action :**

- Cases SPI : `SystemParametersInfoW` avec `SPIF_UPDATEINIFILE` et `SPIF_SENDCHANGE`, qui écrit dans le profil puis diffuse `WM_SETTINGCHANGE` ([source](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow)). La valeur passe par `uiParam` pour `SPI_SETDRAGFULLWINDOWS` et `SPI_SETFONTSMOOTHING`, par `pvParam` pour les autres.
- L'outil n'écrit jamais `UserPreferencesMask` directement. Il n'utilise pas non plus `SPI_SETUIEFFECTS`, l'interrupteur global qui coupe tous les effets d'un coup.
- Cases registre : écriture du DWORD, diffusion de `WM_SETTINGCHANGE`, puis redémarrage proposé de l'Explorateur. Les valeurs DWM peuvent exiger une déconnexion (à vérifier en test).
- `VisualFXSetting` passe à 3 en dernier, pour que la boîte de dialogue affiche « Paramètres personnalisés ».
- Vue des tâches : valeur utilisateur `ShowTaskViewButton`, valable sur toutes les éditions. La stratégie `HideTaskViewButton` (Pro et plus, 22H2+) grise le réglage et n'est pas utilisée ([source](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-start)).
- Widgets : UCPD protège `TaskbarDa`. Seuls des binaires signés Microsoft peuvent l'écrire, et `reg.exe`, `powershell.exe` et `regedit.exe` sont explicitement bloqués ([source](https://oofhours.com/2025/05/02/what-is-windows-11s-new-ucpd-feature/), [source](https://kolbi.cz/blog/2024/04/03/userchoice-protection-driver-ucpd-sys/)).
- L'outil pose donc `AllowNewsAndInterests` = 0 (Pro et plus, 21H2+), qui coupe toute l'expérience Widgets, barre des tâches comprise ([source](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-newsandinterests)). Sur Famille, l'effet est à vérifier ; à défaut, l'outil ouvre `ms-settings:taskbar`. UCPD n'est jamais désactivé (voir Module 1).
- Effets d'animation (`ms-settings:easeofaccess-visualeffects`) : correspondance supposée avec `SPI_SETCLIENTAREAANIMATION`, qui coupe animations et effets transitoires ([source](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow)). À confirmer en test ; sinon, l'outil ouvre cette page ([source](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings)).

| Réglage (libellé Windows FR) | Emplacement (clé, commande ou API) | Défaut Windows | Valeur appliquée |
|---|---|---|---|
| Vue des tâches | `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced` `ShowTaskViewButton` (DWORD) | 1 | 0 |
| Widgets | `HKLM\SOFTWARE\Policies\Microsoft\Dsh` `AllowNewsAndInterests` (DWORD) ; `TaskbarDa` en lecture seule | Non configuré (autorisé) | 0 |
| Effets de transparence | `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize` `EnableTransparency` (DWORD) | 1 | 0 |
| Effets d'animation | `SPI_SETCLIENTAREAANIMATION` (0x1043), correspondance à vérifier | Activé | FALSE |
| Mode des effets visuels | `...\Explorer\VisualEffects` `VisualFXSetting` | 0 (à vérifier) | 3 |
| Afficher des miniatures au lieu d'icônes | `...\Explorer\Advanced` `IconsOnly` (logique inversée ; HKLM déclare un `CLSID`, correspondance à vérifier) | Coché | Conservé : `IconsOnly` = 0 |
| Afficher le contenu des fenêtres pendant leur déplacement | `SPI_SETDRAGFULLWINDOWS` (0x0025) ; `HKCU\Control Panel\Desktop` `DragFullWindows` | 1 | Conservé : 1 |
| Afficher le rectangle de sélection de façon translucide | `...\Explorer\Advanced` `ListviewAlphaSelect` | 1 | Conservé : 1 |
| Lisser les polices écran | `SPI_SETFONTSMOOTHING` (0x004B) et `SPI_SETFONTSMOOTHINGTYPE` (0x200B) ClearType ; `FontSmoothing` = "2", `FontSmoothingType` = 2 | 1 | Conservé : ClearType |
| Animer les contrôles et les éléments à l'intérieur des fenêtres | `SPI_SETCLIENTAREAANIMATION` (0x1043) | 1 | 0 |
| Animer les fenêtres lors de leur réduction et de leur agrandissement | `SPI_SETANIMATION` (0x0049), `ANIMATIONINFO.iMinAnimate` ; `WindowMetrics` `MinAnimate` | 1 | 0 |
| Animations dans la barre des tâches | `...\Explorer\Advanced` `TaskbarAnimations` | 1 | 0 |
| Activer Peek | `HKCU\Software\Microsoft\Windows\DWM` `EnableAeroPeek` | 1 | 0 |
| Enregistrer les miniatures de la barre des tâches | `HKCU\Software\Microsoft\Windows\DWM` `AlwaysHibernateThumbnails` | 0 | 0 |
| Faire disparaître ou apparaître les menus | `SPI_SETMENUANIMATION` (0x1003) | 1 | 0 |
| Faire disparaître ou apparaître les infobulles | `SPI_SETTOOLTIPANIMATION` (0x1017) | 1 | 0 |
| Faire disparaître les éléments du menu suite à un clic | `SPI_SETSELECTIONFADE` (0x1015) | 1 | 0 |
| Afficher des ombres sous le pointeur de la souris | `SPI_SETCURSORSHADOW` (0x101B) | 0 (`DefaultValue` HKLM) | 0 |
| Afficher une ombre sous les fenêtres | `SPI_SETDROPSHADOW` (0x1025) | 1 | 0 |
| Afficher les listes modifiables | `SPI_SETCOMBOBOXANIMATION` (0x1005) | 1 | 0 |
| Faire défiler régulièrement la zone de liste | `SPI_SETLISTBOXSMOOTHSCROLLING` (0x1007) | 1 | 0 |
| Utiliser des ombres pour le nom des icônes sur le Bureau | `...\Explorer\Advanced` `ListviewShadow` | 1 | 0 |

Les libellés viennent de `shell32.dll` sur un Windows 11 25H2 français. « Afficher les listes modifiables » correspond à « Slide open combo boxes ». La colonne « Défaut Windows » reprend `DefaultValue` de HKLM.

**Retour arrière :** un instantané stocke toutes les valeurs du tableau avant modification. « Restaurer mes réglages » les réapplique par les mêmes API. « Valeurs Windows » applique les `DefaultValue` de HKLM, remet `VisualFXSetting` à 0 (à vérifier) et supprime `AllowNewsAndInterests`.

**Message affiché à l'utilisateur :** « Gain surtout visuel : Windows paraît plus réactif, mais les performances en jeu ne changent pas de façon mesurable. Nous gardons le lissage des polices, les miniatures, le contenu des fenêtres déplacées et la sélection translucide. »
