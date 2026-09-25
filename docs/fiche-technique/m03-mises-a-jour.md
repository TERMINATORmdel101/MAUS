## Module 3 — Mises à jour Windows (hors pilotes)

Ce module installe toutes les mises à jour Windows de type logiciel via l'API Windows Update Agent, en excluant les pilotes par le critère `Type='Software'`. Les mises à jour facultatives forment une sous-catégorie décochée par défaut. Urgence du moment : Windows 11 24H2 Famille et Pro ne reçoit plus de correctifs après le 13 octobre 2026 ([source](https://learn.microsoft.com/en-us/windows/release-health/windows11-release-information)).

**Détection :**
- Version lue dans `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion` (`DisplayVersion`, `CurrentBuild`, `UBR`). 25H2 (build 26200) est servie jusqu'au 12 octobre 2027 en Famille et Pro ([source](https://learn.microsoft.com/en-us/windows/release-health/windows11-release-information)).
- Sur 24H2, l'outil propose 25H2 par le package d'activation KB5054156 : un redémarrage, prérequis build 26100.5074 ([source](https://support.microsoft.com/en-us/servicing/os/windows/docs/2025/02/kb5054156-feature-update-to-windows-11-version-25h2-by-using-an-enablement-package)).
- 26H1 (build 28000) équipe seulement des PC neufs ([source](https://learn.microsoft.com/en-us/windows/release-health/windows11-release-information)). 26H2 n'est pas encore publiée au 22 septembre 2026.
- Windows 10 : support terminé le 14 octobre 2025. La page ESU grand public annonce désormais une fin au 12 octobre 2027 ([source](https://www.microsoft.com/en-us/windows/extended-security-updates)).
- Recherche : `Microsoft.Update.Session`, `CreateUpdateSearcher()`, puis `Search("IsInstalled=0 and IsHidden=0 and Type='Software' and BrowseOnly=0")`. `BrowseOnly=1` renvoie les mises à jour considérées comme facultatives ; `!=` n'est admis qu'avec `Type` ([source](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdatesearcher-search)).
- Double contrôle : toute entrée dont `Categories` contient le GUID Drivers est rejetée ([source](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ff357803(v=vs.85))).

**Action :**
- Téléchargement par `CreateUpdateDownloader()`, installation élevée par `CreateUpdateInstaller()`. Les mises à jour exigeant une saisie (`InstallationBehavior.CanRequestUserInput`) sont écartées.
- La recherche est relancée jusqu'à liste vide, trois passes au plus (choix de conception).
- Defender : `Update-MpSignature` interroge Microsoft Update puis MMPC par défaut ([source](https://learn.microsoft.com/en-us/powershell/module/defender/update-mpsignature)), seulement si Defender est l'antivirus actif.
- Store : `AppInstallManager.SearchForAllUpdatesAsync()`, capacité `runFullTrust` ([source](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.store.preview.installcontrol.appinstallmanager.searchforallupdatesasync)). Repli : `UpdateScanMethod()` de `MDM_EnterpriseModernAppManagement_AppManagement01`, espace `root\cimv2\mdm\dmmap` ([source](https://learn.microsoft.com/en-us/windows/win32/dmwmibridgeprov/mdm-enterprisemodernappmanagement-appmanagement01-updatescanmethod)), contexte SYSTEM (à vérifier).
- Autres produits Microsoft : opt-in facultatif par `Microsoft.Update.ServiceManager.AddService2` ([source](https://learn.microsoft.com/en-us/windows/win32/wua_sdk/opt-in-to-microsoft-update)).
- `UsoClient.exe` n'est pas utilisé, faute de documentation. PSWindowsUpdate sert de référence, pas de dépendance.

**Mises à jour facultatives :** l'aperçu non sécuritaire sort le quatrième mardi, puis entre dans le correctif du mois suivant ([source](https://learn.microsoft.com/en-us/windows/deployment/update/release-cycle)). La bascule « Obtenir les dernières mises à jour dès qu'elles sont disponibles » accélère ce contenu, sans toucher aux correctifs de sécurité, avec plus de redémarrages ([source](https://support.microsoft.com/en-us/windows/get-windows-updates-as-soon-as-they-re-available-for-your-device-cad7b32b-001e-435b-9110-f18309b54168)). L'outil lit son état sans l'écrire et ouvre `ms-settings:windowsupdate`.

Sur la machine de test, bascule active, l'aperçu KB5124010 (26200.9550) s'est installé seul le 24 septembre 2026. Les pilotes facultatifs restent exclus : voir Module 9.

**Option pilotes :** `ExcludeWUDriversInQualityUpdate` = 1 (REG_DWORD) sous `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate` bloque les pilotes de Windows Update ; défaut 0 ([source](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-update)). Désactivée par défaut, car elle bloque aussi Wi-Fi, pavé tactile ou chipset. Effet sur l'édition Famille à vérifier ; pilotes graphiques : voir Module 9.

**Retour arrière :** désinstallation par Paramètres > Windows Update > Historique des mises à jour. L'option pilotes s'annule en supprimant la valeur. Aucun redémarrage n'est forcé : l'outil lit `IInstallationResult.RebootRequired` et `ISystemInformation.RebootRequired`, puis propose « Maintenant » ou « Plus tard ».

**Message affiché à l'utilisateur :**
- « Windows 11 24H2 ne recevra plus de correctifs de sécurité après le 13 octobre 2026. Installer 25H2 ? Un seul redémarrage. »
- « Les aperçus corrigent des bugs plus tôt mais peuvent en introduire. Installez-les seulement si un correctif précis vous concerne. »
- « Aucun pilote n'est installé ici. Aucun gain de performance n'est attendu : ces mises à jour servent la sécurité et la stabilité. »

| Catégorie | Critère WUA ou source | Défaut du module | Remarque |
|---|---|---|---|
| Correctif cumulatif mensuel (B) | `Type='Software'`, `BrowseOnly=0`, classification `0FA1201D-4330-4FA8-8AE9-B877473B6441` (Security Updates) | Installé | Redémarrage requis |
| Mises à jour critiques et générales | `E6CF1350-C01B-414D-A61F-263D14D133B4`, `CD5FFD1E-E932-4E3A-BF74-18BF0B1BBD83` | Installé | Dont .NET hors aperçu |
| Defender (définitions, plateforme) | `E0789628-CE08-4437-BE74-2495B842F43B` ou `Update-MpSignature` | Installé | Si Defender actif |
| Outil de suppression de logiciels malveillants | `28BC880E-0592-4CBF-8F95-C79B17911D5F` (Update Rollups) | Installé | Classement observé sur la machine de test |
| Applications du Microsoft Store | `SearchForAllUpdatesAsync()` | Installé | Désactivable |
| Aperçu non sécuritaire (D) | `BrowseOnly=1` (à vérifier pour ce type) | Proposé, décoché | Quatrième mardi du mois |
| Mise à jour de fonctionnalité 25H2 | Package d'activation KB5054156 | Proposé ; recommandé sur 24H2 | Un redémarrage |
| Autres produits Microsoft | Service `7971f918-a847-4430-9279-4a52d1efe18d` | Désactivé | Opt-in |
| Pilotes | `Type='Driver'`, `EBFC1FC5-71A4-4F7B-9ACA-3B9A503104A0` | Exclus | Voir Module 9 |
| Bascule « dès qu'elles sont disponibles » | `IsContinuousInnovationOptedIn` (REG_DWORD, `HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings`, non documenté, lecture seule) ; stratégie `SetAllowOptionalContent` (0 à 3) | Inchangé | Lien vers Paramètres |
| Blocage des pilotes par Windows Update | `ExcludeWUDriversInQualityUpdate` = 1 | Désactivé | Retour : supprimer la valeur |
