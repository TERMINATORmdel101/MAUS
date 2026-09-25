## Module 8 — BIOS

Ce module identifie la carte mère et l'âge du BIOS, puis ouvre la page officielle du fabricant, sans jamais télécharger ni flasher. En septembre 2026, deux raisons rendent un BIOS récent prioritaire : le microcode Intel 0x12F et les certificats Secure Boot 2023.

**Gain attendu :** rarement mesurable ; le BIOS corrige surtout stabilité et sécurité.

**Détection :** `Win32_BaseBoard` (`Manufacturer`, `Product`, `Version` pour la révision) identifie la carte mère ; `Win32_ComputerSystem` (`Manufacturer`, `Model`) prime sur un PC de marque. Un BIOS de plus de 12 mois (`Win32_BIOS.ReleaseDate`) donne une simple suggestion.

**Intel 13e et 14e génération :** pour les Core i5, i7 et i9 de bureau, Intel demande un BIOS avec microcode 0x12F ou ultérieur [source](https://www.intel.com/content/www/us/en/support/articles/000102331/processors.html). La page, revue le 21 juillet 2026, recommande les « Intel Default Settings » et prolonge la garantie de deux ans. `Update Revision` se lit en little-endian : `F0000000` donne 0xF0 sur la machine de test.

Si Windows charge un microcode plus récent, `Previous Update Revision` donnerait celui du BIOS (à vérifier).

**Secure Boot :** les certificats Microsoft 2011 ont expiré le 24 juin 2026 (KEK CA) et le 27 juin 2026 (UEFI CA). Le Windows Production PCA 2011 expire le 19 octobre 2026 [source](https://support.microsoft.com/en-us/topic/windows-secure-boot-certificate-expiration-and-ca-updates-7ff40d33-95dc-4c3c-8725-a9b95457578e). Certains PC exigent d'abord un BIOS du fabricant [source](https://support.microsoft.com/en-us/servicing/os/secure-boot/2026/06/if-you-re-prevented-from-updating-secure-boot-certificates), état visible dans Sécurité Windows, Sécurité de l'appareil [source](https://support.microsoft.com/en-us/topic/secure-boot-certificate-update-status-in-the-windows-security-app-5ce39986-7dd2-4852-8c21-ef30dd04f046).

**Action :**
- Afficher modèle, révision, version et date, puis ouvrir la page support du fabricant. Le numéro de série n'entre jamais dans l'URL.
- PC de marque : outil officiel (Dell Command Update ou SupportAssist, Lenovo Vantage, HP Support Assistant, MyASUS). Carte mère seule : EZ Flash (ASUS), M-Flash (MSI), Q-Flash (Gigabyte) ou Instant Flash (ASRock), clé USB FAT32. En secours : USB BIOS FlashBack, Flash BIOS Button ou Q-Flash Plus.
- BitLocker actif : afficher `https://aka.ms/myrecoverykey` [source](https://support.microsoft.com/en-us/windows/security/encryption/find-your-bitlocker-recovery-key), puis suspendre la protection pour 2 redémarrages. Microsoft l'exige pour un firmware flashé hors Windows Update, sauf validation par Secure Boot [source](https://learn.microsoft.com/en-us/windows/security/operating-system-security/data-protection/bitlocker/faq) ; l'utilitaire suspend dans tous les cas.
- Si `WindowsUEFICA2023Capable` vaut 2 et que `dbdefault` ignore « Windows UEFI CA 2023 », avertir. Selon ASUS, un retour aux clés par défaut bloque le démarrage [source](https://www.asus.com/support/faq/1056845/).
- AMD : l'invite « fTPM NV corrupted » peut suivre un flash [source](https://learn.microsoft.com/en-us/answers/questions/3909607/error-new-cpu-installed-ftpm-psp-nv-corrupted-or-f). Répondre Y réinitialise le fTPM : clé BitLocker exigée, code PIN de connexion à recréer [source](https://learn.microsoft.com/en-us/windows/security/hardware-security/tpm/initialize-and-configure-ownership-of-the-tpm).
- Après le flash, revérifier XMP/EXPO (voir Module 10), Resizable BAR (voir Module 9), virtualisation et VBS (voir Module 13), Secure Boot (voir Module 1).

**Retour arrière :** aucun réglage Windows n'est modifié durablement. La suspension BitLocker cesse après 2 redémarrages ou via `Resume-BitLocker -MountPoint "C:"`. Le retour à un ancien BIOS dépend du fabricant (à vérifier).

**Message affiché à l'utilisateur :** « La mise à jour du BIOS est une opération manuelle et sensible. Téléchargez-le uniquement sur le site du fabricant et suivez ses vidéos officielles. Branchez un portable sur secteur et ne coupez jamais l'alimentation : la carte mère pourrait devenir inutilisable. Notez d'abord votre clé BitLocker. Vos réglages BIOS, dont XMP/EXPO, peuvent revenir aux valeurs par défaut. Cet utilitaire n'installe aucun BIOS. Vous agissez sous votre seule responsabilité : l'éditeur décline toute responsabilité en cas de panne, de perte de données ou d'annulation de garantie. »

| Contrôle | Emplacement (clé, commande ou API) | Valeur attendue | Si écart |
|---|---|---|---|
| Âge du BIOS | `Win32_BIOS` : `SMBIOSBIOSVersion`, `ReleaseDate` | moins de 12 mois | Suggestion et lien fabricant |
| Microcode Intel 13e/14e gén. (bureau) | `HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0` : `Update Revision` et `Previous Update Revision` (REG_BINARY, 4 octets sur la machine de test ; 8 octets possibles, à vérifier), modèle via `ProcessorNameString` | 0x12F ou plus | Mise à jour du BIOS fortement recommandée |
| Secure Boot actif | `HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State` : `UEFISecureBootEnabled` (REG_DWORD) | 1 | Voir Module 1 |
| Déploiement des certificats 2023 | `HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\Servicing` : `UEFICA2023Status` (REG_SZ) [source](https://support.microsoft.com/en-us/topic/registry-key-updates-for-secure-boot-windows-devices-with-it-managed-updates-a7be69c9-4634-42e1-9ca1-df06f43f360d) | `Updated` | `NotStarted` ou `InProgress` : Windows Update, puis BIOS |
| Gestionnaire de démarrage 2023 | Même clé : `WindowsUEFICA2023Capable` (REG_DWORD), « à titre indicatif » selon Microsoft | 2 (certificat en DB, démarrage par le gestionnaire signé 2023) | Information seulement |
| Erreur firmware | Même clé : `UEFICA2023Error` (REG_DWORD) | absente ou 0 | Journal Système, puis BIOS du fabricant |
| Journal Système | Source `TPM-WMI` : 1801 (certificats non appliqués au firmware), 1795 (erreur renvoyée par le firmware), 1808 (mise à jour appliquée) [source](https://support.microsoft.com/en-us/topic/secure-boot-db-and-dbx-variable-update-events-37e47cf8-608b-4a87-8175-bdead630eb69) | 1801 et 1795 absents ; 1808 présent | BIOS du fabricant |
| DB par défaut du firmware | `[System.Text.Encoding]::ASCII.GetString((Get-SecureBootUEFI dbdefault).bytes) -match 'Windows UEFI CA 2023'` (administrateur) [source](https://www.dell.com/support/kbdoc/en-us/000385747/how-to-check-secure-boot-certificates) | `True` | Avertir avant tout retour aux valeurs par défaut |
| BitLocker | `Get-BitLockerVolume -MountPoint C:` : `ProtectionStatus` ; suspension par `Suspend-BitLocker -MountPoint "C:" -RebootCount 2` en administrateur (0 à 15 ; 0 = jusqu'à `Resume-BitLocker`) [source](https://learn.microsoft.com/en-us/powershell/module/bitlocker/suspend-bitlocker) ; édition Famille à vérifier | `Off` pendant le flash | Suspendre avant le flash |

