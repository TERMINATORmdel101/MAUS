# Composants tiers

MAUS (GPL-3.0-only) utilise les bibliothèques libres suivantes, sans les modifier. Leurs licences sont compatibles avec la GPL-3.0 ; leur code source est disponible aux adresses indiquées.

| Composant | Version | Licence | Source | Usage dans MAUS |
|---|---|---|---|---|
| LibreHardwareMonitorLib | 0.9.6 | MPL-2.0 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor | Capteurs avancés (température, tension et puissance du processeur, carte mère) par le pilote PawnIO, seulement s'il est installé |
| DiskInfoToolkit | 1.1.2 | MPL-2.0 | https://github.com/Blacktempel/DiskInfoToolkit | Dépendance de LibreHardwareMonitorLib |
| RAMSPDToolkit-NDD | 1.4.2 | MPL-2.0 | https://github.com/Blacktempel/RAMSPDToolkit | Dépendance de LibreHardwareMonitorLib |
| HidSharp | 2.6.4 | Apache-2.0 | https://www.zer7.com/software/hidsharp | Dépendance de LibreHardwareMonitorLib |
| Mono.Posix.NETStandard | 1.0.0 | MIT | https://github.com/mono/mono | Dépendance de LibreHardwareMonitorLib |
| System.Management, System.Diagnostics.EventLog et autres paquets .NET | 10.x | MIT | https://github.com/dotnet/runtime | WMI, journaux d'événements |

Le texte de la MPL-2.0 : https://mozilla.org/MPL/2.0/. Les fichiers couverts par la MPL-2.0 restent sous cette licence ; MAUS n'en modifie aucun.

**Pilote PawnIO** (namazso, https://github.com/namazso/PawnIO) : il n'est **pas** distribué avec MAUS. L'utilisateur l'installe s'il le souhaite, par winget (`namazso.PawnIO`), depuis l'Atelier, et peut le retirer de la même façon. Les modules PawnIO signés utilisés sont ceux inclus dans LibreHardwareMonitorLib, plus les deux modules officiels ci-dessous.

**Modules PawnIO.Modules 0.2.11** (namazso et contributeurs, LGPL-2.1-or-later, https://github.com/namazso/PawnIO.Modules, étiquette `0.2.11`) : MAUS inclut, sans les modifier, deux modules compilés et signés de cette version, dans `Maus.Core.dll` (ressources `Maus.Core.PawnIo.*`). Ils ne servent qu'à lire les timings et horloges réellement appliqués par le contrôleur mémoire des processeurs Intel, si l'utilisateur a installé PawnIO et lancé MAUS en administrateur.

| Module | Rôle dans MAUS | SHA-256 |
|---|---|---|
| `IntelMCHBAR.bin` | Lecture des registres du contrôleur mémoire (fenêtre MCHBAR) ; le module ne sait que lire et refuse toute adresse hors de la fenêtre | `3f82b832d99b4aac37d2a20fdb7c9baa2a3bc0488612c9019c9484eb0e8a6eae` |
| `IntelMSR.bin` | Lecture des MSR 0x620 et 0x621 (plage et fréquence du ring) ; le module sait aussi écrire quelques MSR de puissance, fonction que MAUS n'appelle jamais | `d6ed85d65ab17a22f813ef98207d6d537155ee2ded5976a21cb48413c9b92e5f` |

Archive de la version 0.2.11 publiée sur GitHub (SHA-256 `43608cb89bc84247fef1368a139013f7d043e17db6d6c8dfc9b46bf0905a81f4`). Le code source de ces modules (`IntelMCHBAR.p`, `IntelMSR.p`) est disponible à l'adresse ci-dessus ; le texte de la LGPL-2.1 est dans `licenses/LGPL-2.1.txt`, livré avec MAUS. Vous pouvez remplacer ces modules par une version modifiée en recompilant MAUS avec vos fichiers `.bin` (dossier `src/Maus.Core/Workshop/Memory/PawnIo/`).

## Sources de données techniques (non incluses comme code)

- **ZenStates-Core** (Ivan Rusanov, GPL-3.0, https://github.com/irusanov/ZenStates-Core) : emplacement et découpage des registres du contrôleur mémoire des Ryzen (UMC, bus SMN) et disposition des tables PM (FCLK, UCLK, MCLK, tensions). Ce sont des faits matériels, relus par le code propre de MAUS (`Workshop/Memory/ZenMemoryController.cs`) ; MAUS étant lui aussi sous GPL-3.0, la reprise de ces tables est compatible, et la source est citée.
- **memtest86+** (GPL-2.0, https://github.com/memtest86plus/memtest86plus) : emplacements des octets SPD des profils XMP 2.0 (DDR4) et XMP 3.0 (DDR5), utilisés pour vérifier le décodeur de MAUS (`Workshop/Memory/SpdDecoder.cs`, code propre).
- **Normes JEDEC** JESD21-C (annexe L, DDR4) et JESD400-5 (DDR5) : structure générale des puces SPD.
- **Fiches techniques Intel** (volume 2, registres MCHBAR ; par exemple 8th Generation Intel Processor Family for S-Processor Platforms Datasheet, Volume 2 of 2, document 336465-001), **memtest86+** (`system/imc/x86/intel_*.c`), **coreboot** (initialisation mémoire native) et le **noyau Linux** (`intel-uncore-frequency`, `intel-family.h`) : emplacement et découpage des registres du contrôleur mémoire Intel, réunis dans `Catalog/intel-memory-controller.json`, où chaque champ cite ses sources. Ce sont des faits matériels relus par le code propre de MAUS (`Workshop/Memory/IntelMemoryController.cs`).
