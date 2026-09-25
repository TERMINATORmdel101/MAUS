## Module 2 — Réparation de Windows

Ce module enchaîne huit étapes de réparation, de la moins intrusive à la plus lourde, et ne passe à la suivante que si un défaut subsiste. Il lit aussi 30 jours de journaux d'événements, car une instabilité matérielle ne se répare pas par logiciel. Sur un Windows sain, la réparation n'apporte aucun gain de performance.

**Prérequis :** 20 Go libres sur `C:` (seuil de conception), portable sur secteur, aucun redémarrage en attente. Ce dernier se lit via `Microsoft.Update.SystemInfo` (`RebootRequired`), la clé `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending` et la valeur `PendingFileRenameOperations` de `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager`. Un point de restauration précède toute écriture (voir Module 1).

| Étape | Commande ou API | Déclencheur | Redémarrage |
|---|---|---|---|
| 1. Disque en ligne | `fsutil dirty query C:` puis `chkdsk C: /scan` | Toujours | Non |
| 2. Disque hors ligne | `chkdsk C: /spotfix` ; `/f` si insuffisant ; `/r` sur HDD seulement | Code de sortie 3 ou volume marqué sale | Oui |
| 3. Magasin de composants | `DISM /Online /Cleanup-Image /ScanHealth` puis `/RestoreHealth` | Toujours ; réparation si corruption | Non |
| 4. Source hors ligne | `DISM /Online /Cleanup-Image /RestoreHealth /Source:wim:D:\sources\install.wim:<index> /LimitAccess` | Échec de l'étape 3 faute de Windows Update | Non |
| 5. Fichiers système | `sfc /scannow`, puis `findstr /c:"[SR]" %windir%\Logs\CBS\CBS.log` | Après l'étape 3 | Parfois |
| 6. Windows Update | Arrêt de `bits`, `wuauserv`, `cryptsvc` ; suppression de `qmgr*.dat` ; renommage de `DataStore`, `Download` et `catroot2` en `.bak` | Échec signalé par le Module 3 | Non |
| 7. WMI | `winmgmt /verifyrepository`, puis `winmgmt /salvagerepository` | Erreurs WMI ou `Get-CimInstance` en échec | Non |
| 8. Réinstallation sur place | Paramètres > Système > Récupération > « Résoudre les problèmes à l'aide de Windows Update » ; sinon `setup.exe` d'une ISO de même version | Échec des étapes 3 à 5 | Oui |

**Détection :** `chkdsk` renvoie un code de 0 (aucune erreur) à 3 (erreurs non corrigées ou vérification impossible) ([source](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/chkdsk)). Microsoft recommande DISM avant SFC, car DISM fournit les fichiers de réparation ([source](https://support.microsoft.com/en-us/windows/experience/backup-recovery/use-the-system-file-checker-tool-to-repair-missing-or-corrupted-system-files)). Dans `CBS.log`, « Cannot repair member file » désigne un fichier que SFC ne peut pas réparer ([source](https://learn.microsoft.com/en-us/troubleshoot/windows-client/installing-updates-features-roles/analyze-sfc-program-log-file-entries)).

**Action :**
- `/r` relit chaque secteur ; répété sur SSD, il ajoute des cycles d'écriture inutiles, d'où sa réservation aux HDD ([source](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/chkdsk)).
- `/LimitAccess` interdit le repli sur Windows Update ([source](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/repair-a-windows-image)). L'outil exige une ISO de même build, langue et édition (règle de conception) ; une ISO livrée avec `install.esd` demande le préfixe `esd:` (à vérifier).
- La réinitialisation de Windows Update suit la procédure Microsoft ([source](https://learn.microsoft.com/en-us/troubleshoot/windows-client/installing-updates-features-roles/additional-resources-for-windows-update)). L'étape `sc.exe sdset`, qui écrase les ACL de BITS et `wuauserv`, reste un dernier recours. La série de `regsvr32` n'est pas reprise : plusieurs DLL visées datent de versions anciennes (à vérifier).
- `winmgmt /resetrepository` peut endommager Windows ou des applications ([source](https://learn.microsoft.com/en-us/archive/blogs/supportingwindows/wmi-repository-corruption-or-not)). Il n'est proposé qu'après échec de `/salvagerepository`, avec avertissement.
- « Résoudre les problèmes à l'aide de Windows Update » réinstalle la même version en gardant applications, fichiers et paramètres ([source](https://support.microsoft.com/en-us/windows/fix-issues-by-reinstalling-the-current-version-of-windows-497ac6da-7cac-4641-82a5-f50398d879a0)). Prérequis : Windows 11 22H2 avec la mise à jour facultative de février 2024. Option absente sous Windows 10, sur PC géré ou sous certaines stratégies Windows Update : le Module 1 passe d'abord.
- Les utilitaires de résolution MSDT sont retirés, plateforme supprimée en 2025 : l'outil n'appelle jamais `msdt.exe` ([source](https://support.microsoft.com/en-us/windows/deprecation-of-microsoft-support-diagnostic-tool-msdt-and-msdt-troubleshooters-0c5ac9a2-1600-4539-b9d0-069e71f9040a)).
- `mdsched.exe` teste la mémoire au redémarrage. Résultat : source `Microsoft-Windows-MemoryDiagnostics-Results`, événements 1101 ou 1201 (sain), 1102 ou 1202 (erreurs). Test proposé seulement sur signal d'alerte ; XMP/EXPO : voir Module 10.

**Signaux de santé (lecture seule, `Get-WinEvent -FilterHashtable`) :**
- `Microsoft-Windows-WHEA-Logger` : 17, 19, 47 = erreur matérielle corrigée ; 1, 18, 20, 46 = erreur irrécupérable. Cause fréquente : overclocking ou profil mémoire instable (voir Modules 10 et 15).
- `Microsoft-Windows-Kernel-Power` 41 : `BugcheckCode` non nul = écran bleu ; `BugcheckCode` et `PowerButtonTimestamp` nuls = coupure ou alimentation ([source](https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/event-id-41-restart)).
- `Microsoft-Windows-WER-SystemErrorReporting` 1001, plus le nombre de `.dmp` dans `%SystemRoot%\Minidump`. `volmgr` 46 signale un vidage impossible, souvent sans fichier d'échange ([source](https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/event-id-41-restart)).
- `Ntfs` 55 ; `disk` 7, 51, 153 (à vérifier) : santé du support, voir Module 11.
- Indice global : `Win32_ReliabilityStabilityMetrics.SystemStabilityIndex`, de 1 à 10.

**Retour arrière :** DISM et SFC réinstallent des fichiers Microsoft d'origine, sans retour nécessaire. Les dossiers `.bak` de Windows Update restent jusqu'à la réparation réussie suivante. En dernier recours, la restauration à un point dans le temps ramène tout le système, fichiers compris, sur 72 heures, depuis WinRE ([source](https://learn.microsoft.com/en-us/windows/configuration/point-in-time-restore)).

Cette restauration est active par défaut sur Famille et Pro non géré si le volume système atteint 200 Go ([source](https://learn.microsoft.com/en-us/windows/configuration/point-in-time-restore)). Tout changement postérieur au point choisi est perdu.

**Message affiché à l'utilisateur :**
- « La réparation peut être longue. N'éteignez pas le PC et laissez-le sur secteur. »
- « 14 erreurs matérielles corrigées en 30 jours. Ce n'est pas un problème de Windows : vérifiez overclocking, profil XMP/EXPO et températures. »
- « La réinstallation sur place conserve fichiers et applications. Sauvegardez quand même vos données avant. »
