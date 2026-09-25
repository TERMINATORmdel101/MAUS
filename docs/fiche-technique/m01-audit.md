## Module 1 — Audit des modifications risquées

Ce module compare l'état du PC à un catalogue de 29 contrôles classés Critique, Élevé, Moyen ou Faible. Il cible les modifications typiques des scripts de « debloat », des « optimiseurs » et des maliciels. Le gain de performance promis par ces modifications est en général négligeable ; le risque, lui, est réel.

**Valeurs de référence :** un catalogue JSON versionné, mis à jour chaque mois, décrit chaque contrôle par build (19045, 22631, 26100, 26200, 28000) et par édition. Chaque entrée donne la méthode de détection, la valeur attendue (y compris « absente ») et l'action de retour. Les valeurs sont relevées sur des machines virtuelles propres, car les types de démarrage des services varient selon la build.

**Règle des stratégies :** restaurer une stratégie consiste à supprimer sa valeur sous `HKLM\SOFTWARE\Policies`, jamais à écrire la valeur « activée ». Microsoft précise que supprimer les clés de stratégie rend la main aux préférences locales ([source](https://learn.microsoft.com/en-us/windows/deployment/update/waas-wu-settings)). Si la valeur vient de `%windir%\System32\GroupPolicy\Machine\Registry.pol`, elle est aussi retirée de ce fichier, sinon `gpupdate` la réécrit.

**Détection :** trois garde-fous évitent les faux positifs.
- PC géré (`Win32_ComputerSystem.PartOfDomain`, inscription MDM sous `HKLM\SOFTWARE\Microsoft\Enrollments`, `dsregcmd /status`) : constats affichés, correction bloquée.
- Antivirus tiers déclaré dans `root\SecurityCenter2` (classe `AntiVirusProduct`) : un Defender passif est normal.
- Maintenance en attente de redémarrage : `TrustedInstaller` passe en démarrage automatique (observé sur la machine de test, à vérifier).

Une image modifiée se repère par un faisceau d'indices : service `WinDefend` absent, WinRE absent (`reagentc /info`), magasin de composants illisible par DISM, dossier `AtlasModules` (emplacement à vérifier). Tiny11 Core supprime WinSxS, Windows Update et WinRE, et désactive Defender ([source](https://github.com/ntdevlabs/tiny11builder)). AtlasOS rend Defender, SmartScreen et Windows Update désactivables ([source](https://github.com/Atlas-OS/Atlas)).

**Action :** les constats Critique sont pré-cochés, les autres restent au choix de l'utilisateur. Avant toute écriture, l'outil crée un point de restauration (`Checkpoint-Computer -RestorePointType MODIFY_SETTINGS`), limité à un par 24 heures ([source](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.management/checkpoint-computer)). Les exclusions retirées suivent la liste Microsoft des exclusions à proscrire ([source](https://learn.microsoft.com/en-us/defender-endpoint/defender-endpoint-exclusions-common-mistakes)).

La protection contre les falsifications ne se règle pas par PowerShell ([source](https://learn.microsoft.com/en-us/defender-endpoint/tamper-protection-windows-configure)) : l'outil ouvre Sécurité Windows et guide l'utilisateur. `DisableAntiSpyware` est ignoré sur les postes clients depuis la plateforme 4.18.2108.4 ([source](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/unattend/security-malware-windows-defender-disableantispyware)), mais reste signalé comme trace de script. Intégrité de la mémoire, VBS et `FeatureSettingsOverride` : voir Module 13.

**Retour arrière :** chaque action écrit un journal JSON (valeur avant, valeur après, horodatage) ; le bouton « Annuler » réapplique l'état d'origine. Un constat marqué « voulu » n'est plus signalé. Restauration complète du système : voir Module 2.

**Message affiché à l'utilisateur :**
- « Microsoft Defender est coupé par une stratégie. Votre PC n'a plus de protection en temps réel. Rétablir ? »
- « Ce Windows provient d'une image modifiée. Des composants de sécurité et de mise à jour manquent. Seule une réinstallation depuis l'outil officiel Microsoft les rétablit. »
- « L'activation de Windows passe par un serveur non officiel. L'outil ne modifie pas l'activation. »

**Catalogue condensé :**

| Catégorie | Contrôle | Détection | Défaut Windows | Restauration | Niveau |
|---|---|---|---|---|---|
| Defender | Temps réel coupé | `Get-MpComputerStatus` : `RealTimeProtectionEnabled` ; `HKLM\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection` `DisableRealtimeMonitoring` | `True` ; valeur absente | Supprimer la stratégie, réactiver dans Sécurité Windows | Critique |
| Defender | Antivirus coupé par stratégie | `HKLM\SOFTWARE\Policies\Microsoft\Windows Defender` `DisableAntiSpyware` (REG_DWORD) | Absente | Supprimer la valeur | Élevé |
| Defender | Protection contre les falsifications | `Get-MpComputerStatus` : `IsTamperProtected` | `True` | Guidage dans Sécurité Windows (non scriptable) | Élevé |
| Defender | Exclusions larges | `Get-MpPreference` : `ExclusionPath`, `ExclusionExtension`, `ExclusionProcess` | Vides | `Remove-MpPreference` sur `C:\`, `C:\Users`, `%TEMP%`, `.exe`, `powershell.exe`… | Critique |
| Défenses | SmartScreen coupé | `HKLM\SOFTWARE\Policies\Microsoft\Windows\System` `EnableSmartScreen` = 0 | Absente | Supprimer `EnableSmartScreen` et `ShellSmartScreenLevel` | Élevé |
| Défenses | UAC désactivé | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System` `EnableLUA` ([source](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/user-account-control/settings-and-configuration)) | 1 | Remettre 1, redémarrer | Critique |
| Défenses | UAC sans invite | Même clé : `ConsentPromptBehaviorAdmin`, `PromptOnSecureDesktop` | 5 ; 1 | Remettre 5 et 1 | Élevé |
| Défenses | Pare-feu coupé | `Get-NetFirewallProfile` : `Enabled` ; `HKLM\SOFTWARE\Policies\Microsoft\WindowsFirewall\<Profil>` `EnableFirewall` | `True` sur les 3 profils ; absente | Supprimer la stratégie, `Set-NetFirewallProfile -All -Enabled True` | Critique |
| Windows Update | Services désactivés | `HKLM\SYSTEM\CurrentControlSet\Services\<nom>` `Start` = 4 | `wuauserv` 3 ; `UsoSvc` 2 différé ; `WaaSMedicSvc` 3 ; `BITS` 3 (build 26200 observée) | Type de démarrage du catalogue | Critique |
| Windows Update | Mises à jour automatiques coupées | `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU` `NoAutoUpdate` = 1 | Absente | Supprimer | Élevé |
| Windows Update | Faux serveur WSUS | `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate` `WUServer`, `WUStatusServer`, `DoNotConnectToWindowsUpdateInternetLocations` ; `AU\UseWUServer` = 1 | Absentes (PC non géré) | Supprimer les quatre valeurs | Critique |
| Windows Update | Pause prolongée | `HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings` `PauseUpdatesExpiryTime`, `FlightSettingsMaxPauseDays` | Absentes ; pause de 5 semaines au plus (à vérifier) | Reprendre les mises à jour, supprimer `FlightSettingsMaxPauseDays` | Élevé |
| Windows Update | Version figée | `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate` `TargetReleaseVersion`, `ProductVersion`, `TargetReleaseVersionInfo` | Absentes | Supprimer ; alerte si la version figée n'est plus servie | Élevé |
| Windows Update | Tâches de maintenance désactivées | `Get-ScheduledTask -TaskPath '\Microsoft\Windows\UpdateOrchestrator\'` : `State` | `Ready` (catalogue) | `Enable-ScheduledTask` | Moyen |
| Réseau | hosts bloquant Microsoft | `%SystemRoot%\System32\drivers\etc\hosts` : lignes visant `*.microsoft.com`, `*.windowsupdate.com`, `*.live.com` | Commentaires seulement | Copie `.bak`, retrait des seules lignes Microsoft | Élevé |
| Réseau | Proxy imposé | `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings` `ProxyEnable`, `ProxyServer`, `AutoConfigURL` ; `netsh winhttp show proxy` | 0 ; accès direct | Après confirmation : `ProxyEnable` = 0, `netsh winhttp reset proxy` | Élevé |
| Composants | Services système désactivés | `Start` de `CryptSvc`, `TrustedInstaller`, `Audiosrv`, `WSearch`, `WinDefend`, `mpssvc` | 2 ; 3 (à vérifier) ; 2 ; 2 différé ; 2 ; 2 | Type de démarrage du catalogue | Élevé |
| Composants | Store ou Sécurité Windows retirés | `Get-AppxPackage -AllUsers` : `Microsoft.WindowsStore`, `Microsoft.SecHealthUI`, `Microsoft.DesktopAppInstaller` | Présents | Réinstallation du Store par `wsreset -i` (à vérifier) | Moyen |
| Composants | WebView2 ou Edge retiré | `HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}` `pv` ([source](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)) | Version supérieure à 0.0.0.0 | Programme d'installation Evergreen officiel | Élevé |
| Composants | Fichier d'échange supprimé | `Win32_ComputerSystem.AutomaticManagedPagefile` ; `Win32_PageFileSetting` | `True` | Gestion automatique, redémarrage | Moyen |
| Démarrage | DEP désactivé | `Win32_OperatingSystem.DataExecutionPrevention_SupportPolicy` = 0 ; `bcdedit /enum {current}` : `nx` | 2 (OptIn) | `bcdedit /set {current} nx OptIn` ([source](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/bcdedit--set)) | Critique |
| Démarrage | Signature des pilotes contournée | `bcdedit /enum {current}` : `testsigning`, `nointegritychecks` | Non définis | `bcdedit /deletevalue {current} testsigning`, BitLocker suspendu (voir Module 8) | Critique |
| Persistance | Débogueur IFEO | `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\<exe>` `Debugger` | Absente | Suppression après revue | Critique |
| Persistance | Winlogon détourné | `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon` `Shell`, `Userinit` | `explorer.exe` ; `C:\Windows\system32\userinit.exe,` | Remettre les valeurs par défaut | Critique |
| Persistance | AppInit_DLLs | `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows` `AppInit_DLLs`, `LoadAppInit_DLLs` | Vide ; 0 | Vider ; 0 | Critique |
| Persistance | Démarrage non signé | Clés `Run` et `RunOnce` (HKLM, HKCU), signature de la cible via `WinVerifyTrust` | Cibles signées | Désactivation, voir Module 12 | Moyen |
| Système | Image modifiée (Tiny11, AtlasOS, ReviOS) | Faisceau d'indices (voir texte) | Sans objet | Réinstallation propre conseillée | Élevé |
| Système | Activation non officielle | CIM `SoftwareLicensingProduct` : `KeyManagementServiceMachine`, `DiscoveredKeyManagementServiceMachineName`, `ProductKeyChannel` = `Volume:GVLK` sur Famille ou Pro | Canal `Retail` ou `OEM` | Information seulement | Moyen |
| Système | Stratégies résiduelles | Valeurs sous `HKLM\SOFTWARE\Policies\Microsoft` hors liste blanche ; bandeau « Certains paramètres sont gérés par votre organisation » | Aucune sur PC personnel | Suppression sélective | Faible |
