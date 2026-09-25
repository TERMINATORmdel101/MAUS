## Module 10 — RAM : XMP / EXPO et dual channel

Ce module repère une mémoire bridée à sa vitesse SPD par défaut : un kit G.Skill DDR5-6000 démarre ainsi à 4800 MT/s sans profil [source](https://www.gskill.com/specification/165/393/1661410171/F5-6000J3038F16GX2-TZ5N-Specification). Il signale aussi le simple canal et ne modifie rien : XMP et EXPO s'activent uniquement dans le BIOS.

**Détection :** `Win32_PhysicalMemory` renvoie une instance par barrette (propriétés dans le tableau). La documentation Microsoft est datée : `Speed` y est en nanosecondes, `ConfiguredClockSpeed` en MHz, et `MemoryType` s'arrête à DDR4 [source](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-physicalmemory). En pratique, les deux vitesses remontent des MT/s, unité de SMBIOS [source](https://github.com/jrgerber/smbios-lib/blob/main/src/structs/types/memory_device.rs).

**Heuristique XMP/EXPO :**
1. `ConfiguredClockSpeed` vaut une vitesse JEDEC courante : DDR4 2133, 2400, 2666, 2933 ou 3200 ; DDR5 4800, 5200, 5600, voire 6400 (à vérifier).
2. Le `PartNumber` décodé annonce davantage. Corsair `CMK32GX5M2B6000C36` signifie DDR5, 6000 MT/s avec profil actif, CL36 [source](https://www.corsair.com/us/en/explorer/diy-builder/memory/how-to-read-the-corsair-memory-part-number/). G.Skill `F5-6000J3038F16GX2` désigne du DDR5-6000 ; Kingston `KF560C36…` aussi (à vérifier).
3. Les deux conditions réunies donnent « XMP/EXPO probablement désactivé ». Une référence inconnue donne « indéterminé », jamais « désactivé ».

`Speed` ne sert pas de vitesse nominale : la machine de test (MSI Z390) renvoie 3467 dans les deux champs pour des barrettes « 3200 Series ». Une vitesse configurée supérieure à la référence signale un overclocking manuel : information, sans alerte.

**Lecture SPD exclue :** aucun pilote noyau n'est embarqué pour lire les puces SPD. Defender détecte WinRing0 sous `HackTool:Win32/Winring0`, fiche publiée le 20 mai 2025 [source](https://www.microsoft.com/en-us/wdsi/threats/malware-encyclopedia-description?Name=HackTool%3AWin32%2FWinring0&ThreatID=2147935185). Microsoft le classe vulnérable au titre de CVE-2020-14979 [source](https://support.microsoft.com/en-us/windows/security/threat-malware-protection/microsoft-defender-antivirus-alert-vulnerabledriver-winnt-winring0) ; liste de blocage : voir Module 13.

**Dual channel :** une barrette unique signifie simple canal, très pénalisant pour un iGPU ou un APU. Deux barrettes sur le même canal, par exemple `ChannelA-DIMM1` et `ChannelA-DIMM2`, appellent un déplacement selon le manuel. Un kit mixte est plus exposé à l'instabilité avec XMP/EXPO.

**Portables :** avec `PCSystemType` = 2 ou `PCSystemTypeEx` = 8 [source](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-computersystem), XMP est rarement proposé et la mémoire souvent soudée.

**Action :** nommer le réglage à chercher : XMP (Intel), EXPO (AM5), DOCP (ASUS AM4), A-XMP (MSI AM4) ; menus à vérifier par modèle. Intel classe XMP comme un overclocking mémoire pouvant annuler la garantie du processeur [source](https://www.intel.com/content/www/us/en/gaming/extreme-memory-profile-xmp.html). En cas d'instabilité : BIOS récent (voir Module 8), MemTest86, TestMem5 ou OCCT, puis profil inférieur.

Sur AM5, l'activation relance l'entraînement mémoire au démarrage suivant (durée à vérifier). « Memory Context Restore » évite ensuite un entraînement à chaque démarrage ; la désactiver si l'instabilité persiste (à vérifier).

**Retour arrière :** rien n'est modifié dans Windows. Dans le BIOS, désactiver le profil ou charger les valeurs par défaut. Si le PC ne démarre plus, effacer le CMOS selon le manuel de la carte mère.

**Message affiché à l'utilisateur :** « Votre mémoire est vendue pour {vitesse annoncée} MT/s mais fonctionne à {vitesse actuelle} MT/s. Activez le profil XMP ou EXPO dans le BIOS. Sur AMD AM5, le premier démarrage peut rester plusieurs minutes sur un écran noir pendant l'entraînement mémoire : n'éteignez pas le PC. Ce profil est un overclocking et peut annuler la garantie du processeur. Le gain varie selon les jeux : le test de performance de l'utilitaire le mesure avant et après. »

| Signal | Source (classe et propriété) | Valeur normale | Alerte |
|---|---|---|---|
| Type de mémoire | `Win32_PhysicalMemory.SMBIOSMemoryType` [source](https://github.com/jrgerber/smbios-lib/blob/main/src/structs/types/memory_device.rs) | 26 (0x1A, DDR4), 34 (0x22, DDR5) ou 35 (0x23, LPDDR5) | Autre valeur : affichage seul |
| Vitesse appliquée | `ConfiguredClockSpeed` | vitesse de la référence | Vitesse JEDEC inférieure : XMP/EXPO probablement désactivé |
| Vitesse nominale | `PartNumber` décodé par table de marques ; `Manufacturer` peut être un code JEDEC brut (« 8502 » sur la machine de test) | connue | Référence inconnue : indéterminé |
| Vitesse maximale SMBIOS | `Speed` | informative | Aucune |
| Nombre de barrettes | nombre d'instances, `Capacity` | 2 ou 4 | 1 : simple canal |
| Répartition | `DeviceLocator`, `BankLabel` (format propre à chaque fabricant) | canaux A et B | Même canal : déplacer selon le manuel |
| Homogénéité | `PartNumber`, `Capacity` | identiques | Kit mixte : risque d'instabilité |
| Type de PC | `Win32_ComputerSystem.PCSystemType` et `PCSystemTypeEx` | 1 (bureau) ou 3 (station de travail) | 2 (portable) ou 8 (tablette) : pas de conseil BIOS |


**Fiche mémoire détaillée (Atelier, onglet « Mémoire », demande du porteur du 25/09/2026 : « toutes les infos ») :** nécessite PawnIO et l'administrateur.
- **Puce SPD de chaque barrette** (RAMSPDToolkit via PawnIO, lecture octet par octet, 512 octets en DDR4, 1024 en DDR5 ; seule écriture : le choix de page SPD, registre prévu pour cela) : type, format (UDIMM, SO-DIMM, CUDIMM…), capacité, rangs, largeur et densité des puces, groupes de banques, bits d'adresse, ECC, capteur de température, PMIC (DDR5), fabricants de la barrette et des puces, révision des puces, date, révision SPD, **somme de contrôle** (CRC-16 JEDEC ; incorrecte = valeurs douteuses, signalé). Profils **JEDEC, XMP 2.0 (DDR4), XMP 3.0 (DDR5, noms de profils) et EXPO** : fréquence, timings primaires et secondaires, tRFC en ns, tensions VDD / VDDQ / VPP, latence réelle en ns. À vérifier sur vraies barrettes : timings secondaires JEDEC DDR5 (octets 70-93) et fin des profils EXPO (tRRD_L… tRTP).
- **Timings réellement appliqués (AMD Ryzen, Zen 2 à Zen 5)** : registres UMC lus par SMN (module PawnIO AMDFamily17, verrou `Global\Access_PCI`) : **primaires** (tCL, tRCDRD, tRCDWR, tRP, tRAS, tRC), **secondaires** (tRRDS, tRRDL, tFAW, tWTRS, tWTRL, tWR, tRTP, tCWL, tRFC/tRFC2/tRFC4 ou tRFCsb en cycles et ns, tREFI), **tertiaires** (tRDRDSCL, tWRWRSCL, tRDRDSC/SD/DD, tWRWRSC/SD/DD, tRDWR, tWRRD, tCKE, tXP, tSTAG, tMOD, tMRD, tMODPDA, tMRDPDA, tPHYWRD/WRL/RDL, tWRPRE, tRDPRE, tTRCPAGE ; DDR5 : tRPpb, tRCpb, tPPD) ; **réglages** GDM, 1T/2T, Power Down, BGS, BGS Alt, mode de rafraîchissement ; canaux actifs (différences entre canaux signalées).
- **Horloges et tensions** : FCLK, UCLK, MCLK et rapport UCLK:MCLK (1:1 ou 1:2), SoC, VDDP, VDDG IOD/CCD, lus dans la table PM du SMU (module RyzenSMU) selon sa version ; version inconnue → disposition générique de la famille, signalée « à vérifier » ; sinon MCLK d'après le coefficient du contrôleur (BCLK 100 MHz supposée).
- **Intel** : les registres du contrôleur mémoire (MCHBAR) ne sont pas accessibles par les modules PawnIO fournis ; MAUS affiche les profils SPD et le dit.
- Chaque timing a une explication en infobulle ; « Copier la fiche » produit un texte complet pour un forum. Rien n'est modifié : les réglages se font dans le BIOS (gain honnête : quelques pour cent).
