## Module 7 — Xbox Game Bar

Ce module distingue trois briques souvent confondues : la superposition Game Bar (Win+G), l'enregistrement en arrière-plan (Game DVR) et le Mode Jeu. Seul l'enregistrement en arrière-plan a un coût notable, car il capture la partie en continu (ampleur à mesurer, voir Module 11). Le Mode Jeu reste activé dans tous les cas.

**Détection :** l'outil liste les paquets `Microsoft.XboxGamingOverlay` (Game Bar), `Microsoft.GamingApp` (app Xbox), `Microsoft.GamingServices` et `Microsoft.XboxIdentityProvider`. Il lit les valeurs du tableau et reprend du Module 5 la détection d'un Ryzen X3D à deux CCD. Une Game Bar absente alors que des jeux sont installés est signalée comme anomalie.

**Choix proposé à l'utilisateur :**

- Profil 1, « Je n'utilise ni l'app Xbox ni le Game Pass » : captures, enregistrement en arrière-plan et ouverture par la manette coupés. Aucun interrupteur global documenté n'a été trouvé : Win+G ouvre encore la Game Bar, qui reste installée.
- Profil 2, « J'utilise l'app Xbox ou le Game Pass » : l'app Xbox et les Services de jeu suffisent à installer et lancer les jeux (à vérifier). La Game Bar ajoute superposition, chat de groupe, captures et succès. Seul l'enregistrement en arrière-plan est coupé.
- Profil 3, imposé si un Ryzen X3D à deux CCD est détecté : Game Bar installée, activée et à jour, avec le Mode Jeu.

Sur ces processeurs, la Game Bar affecte les jeux au CCD doté du V-Cache ([source](https://www.pcworld.com/article/1528458/three-ryzen-9-7950x-misconceptions-debunked.html)). Un jeu non reconnu peut être marqué comme jeu depuis Win+G, selon un guide communautaire ([source](https://wiki.vrchat.com/wiki/Guides:AMD_X3D_Series_Processors)). Sous Windows 10, des plantages de la Game Bar sur ces puces ont été signalés en août 2025 ([source](https://en.gamegpu.com/news/zhelezo/microsoft-otklyuchila-xbox-game-bar-i-zamedlila-ryzen-x3d-v-windows-10), à vérifier).

**Action :**

- Les valeurs du tableau sont écrites pour l'utilisateur courant, puis relues.
- La stratégie `AllowGameDVR` = 0 (Pro et plus) sert uniquement de verrou optionnel en profil 1 ([source](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-applicationmanagement)).
- L'outil ne désinstalle jamais `Microsoft.XboxGamingOverlay`. Sans elle, le lien `ms-gamingoverlay` n'a plus d'application associée et Windows affiche une fenêtre d'erreur ([source](https://learn.microsoft.com/en-gb/answers/questions/1497050/ms-gamingoverlay-link-popup)).
- Suggestion : dans le widget Gaming Copilot de la Game Bar, couper l'entraînement du modèle sur le texte (libellé et défaut à vérifier).
- Pages de repli : `ms-settings:gaming-gamebar`, `ms-settings:gaming-gamedvr` et `ms-settings:gaming-gamemode` ([source](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings)).

| Réglage | Emplacement (clé, commande ou API) | Défaut Windows | Valeur appliquée |
|---|---|---|---|
| Enregistrer ce qui s'est passé | `HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR` `HistoricalCaptureEnabled` (à vérifier) | 0 (à vérifier) | 0 dans tous les profils |
| Captures de jeu | `HKCU\System\GameConfigStore` `GameDVR_Enabled` (non documentée, présente sur le build 26200.9457) ; `...\GameDVR` `AppCaptureEnabled` (à vérifier) | 1 | Profil 1 : 0 ; profils 2 et 3 : inchangé |
| Ouvrir la Game Bar avec la manette | `HKCU\Software\Microsoft\GameBar` `UseNexusForGameBarEnabled` | 1 (à vérifier) | Profil 1 : 0 ; sinon inchangé |
| Mode Jeu | `HKCU\Software\Microsoft\GameBar` `AutoGameModeEnabled` (absent = activé, à vérifier) | Activé | 1 dans tous les profils |
| Autorisations d'arrière-plan de la Game Bar | `ms-settings:appsfeatures-app?Microsoft.XboxGamingOverlay_8wekyb3d8bbwe` (présence de l'option à vérifier) | Selon Windows | Profil 1 : « Jamais » en option ; sinon inchangé |
| Stratégie d'enregistrement | `HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR` `AllowGameDVR` (DWORD) | Non configuré (autorisé) | Profil 1 : 0 en option ; jamais en profil 3 |
| Paquet Game Bar | `Microsoft.XboxGamingOverlay` via `Get-AppxPackage` | Installé | Jamais désinstallé ; réinstallé si absent |
| App Xbox et Services de jeu | `Microsoft.GamingApp`, `Microsoft.GamingServices` | Selon usage | Jamais modifiés |

**Retour arrière :** un instantané stocke chaque valeur ou son absence, et la restauration les réécrit. Si la Game Bar manque, l'outil propose `winget install --id 9NZKPSTSNW4P --source msstore`, identifiant Store de « Game Bar », publiée par Microsoft ([source](https://apps.microsoft.com/detail/9nzkpstsnw4p)). Cette réinstallation n'est lancée qu'après l'accord de l'utilisateur.

**Message affiché à l'utilisateur :** « L'enregistrement permanent en arrière-plan est la partie de la Game Bar qui sollicite le plus la machine : nous le coupons. Le Mode Jeu reste activé. Processeur Ryzen X3D détecté : la Game Bar sert à placer vos jeux sur les bons cœurs, nous la conservons. »
