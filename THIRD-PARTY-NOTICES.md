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
| Vortice.Direct3D11, Direct3D12, DXGI, D3DCompiler, Direct2D1 (DirectWrite, WIC) | 3.8.3 | MIT (Copyright (c) Amer Koleci and Contributors) | https://github.com/amerkoleci/Vortice.Windows | Benchmark visuel : accès à Direct3D 11 et 12, compilation des shaders, police de l'interface, images PNG |
| Vortice.Mathematics | 2.1.0 | MIT (Copyright (c) Amer Koleci and Contributors) | https://github.com/amerkoleci/Vortice.Mathematics | Dépendance de Vortice |
| SharpGen.Runtime, SharpGen.Runtime.COM | 2.4.2-beta | MIT ((c) 2010-2017 Alexandre Mutel, 2017-2023 Jeremy Koritzinsky, 2023-2024 Amer Koleci) | https://github.com/SharpGenTools/SharpGenTools | Dépendance de Vortice |

**Benchmark visuel** : les scènes « Anneau de la géante », « Champ de bataille » (chars compris), « Collision galactique » et « Forge fractale », les shaders et les tests du processeur sont écrits pour MAUS et calculés à partir de formules (aucun modèle 3D, aucune texture ni aucun code de démo tiers). Seule la scène « Cabinet de curiosités » utilise des modèles scannés, des textures et un ciel HDR de Poly Haven (ci-dessous) ; son code, ses shaders et ses lecteurs de fichiers glTF et HDR sont écrits pour MAUS. Les techniques publiées dont ils s'inspirent sont citées dans les commentaires du code (Mandelbox de T. Lowe, problème restreint à trois corps de Toomre et Toomre, anticrénelage temporel de B. Karis, halo de J. Jimenez, courbe filmique de K. Narkowicz, transformée en distance de Felzenszwalb et Huttenlocher, éclairage par harmoniques sphériques de Ramamoorthi et Hanrahan, environnement préfiltré de B. Karis et de Colbert et Křivánek, tangentes de E. Lengyel, relief par parallaxe de Brawley et Tatarchuk, ombres douces de R. Fernando). La police Segoe UI de Windows est dessinée par DirectWrite sur le PC de l'utilisateur ; elle n'est pas redistribuée.

**Poly Haven** (https://polyhaven.com) : modèles, textures et ciel HDR publiés sous licence **CC0 1.0** (domaine public : utilisation, modification et redistribution libres, même commerciales, mention non obligatoire ; https://polyhaven.com/license). La scène « Cabinet de curiosités » du benchmark utilise les fichiers ci-dessous, sans les modifier, dans le dossier `Assets/PolyHaven` livré à côté de MAUS.exe (33 Mo). Leurs auteurs sont cités par reconnaissance :

| Fichiers | Contenu | Auteurs |
|---|---|---|
| `ballroom` (2K, HDR) | Ciel HDR d'une salle de bal (lumière et reflets de la scène) | Sergej Majboroda |
| `rosewood_veneer1` (2K) | Texture : plaqué de bois de rose (table) | Jenelle van Heerden |
| `herringbone_parquet` (1K) | Texture : parquet en point de Hongrie | Jenelle van Heerden (traitement), Sergej Majboroda (photographie) |
| `marble_bust_01` (2K) | Modèle : buste en marbre | Rico Cilliers |
| `horse_statue_01` (2K) | Modèle : statue de cheval | Rico Cilliers |
| `brass_vase_03` (1K) | Modèle : vase en laiton | Rico Cilliers |
| `antique_ceramic_vase_01` (1K) | Modèle : vase ancien en céramique | James Ray Cock |
| `tea_set_01` (1K) | Modèle : service à thé | Rico Cilliers (modélisation), James Ray Cock (textures), Jurita Burger (motifs) |
| `alarm_clock_01` (1K) | Modèle : réveil | James Ray Cock (modélisation et textures), Yann Kervran (armature) |
| `Lantern_01` (1K) | Modèle : lanterne | Rajil Jose Macatangay |
| `Camera_01` (1K) | Modèle : appareil photo | Rajil Jose Macatangay |
| `carved_wooden_elephant` (1K) | Modèle : éléphant en bois sculpté | Greg Zaal |
| `antique_katana_01` (1K) | Modèle : katana ancien | Tal Swicegood |

Le texte de la MPL-2.0 : https://mozilla.org/MPL/2.0/. Les fichiers couverts par la MPL-2.0 restent sous cette licence ; MAUS n'en modifie aucun.

**Pilote PawnIO** (namazso, https://github.com/namazso/PawnIO) : il n'est **pas** distribué avec MAUS. L'utilisateur l'installe s'il le souhaite, par winget (`namazso.PawnIO`), depuis l'Atelier, et peut le retirer de la même façon. Les modules PawnIO signés utilisés sont ceux inclus dans LibreHardwareMonitorLib, plus les deux modules officiels ci-dessous.

**Modules PawnIO.Modules 0.2.11** (namazso et contributeurs, LGPL-2.1-or-later, https://github.com/namazso/PawnIO.Modules, étiquette `0.2.11`) : MAUS inclut, sans les modifier, cinq modules compilés et signés de cette version, dans `Maus.Core.dll` (ressources `Maus.Core.PawnIo.*`). Ils ne servent qu'à lire les timings et horloges du contrôleur mémoire des processeurs Intel et les puces SPD des barrettes, si l'utilisateur a installé PawnIO et lancé MAUS en administrateur.

| Module | Rôle dans MAUS | SHA-256 |
|---|---|---|
| `IntelMCHBAR.bin` | Lecture des registres du contrôleur mémoire (fenêtre MCHBAR) ; le module ne sait que lire et refuse toute adresse hors de la fenêtre | `3f82b832d99b4aac37d2a20fdb7c9baa2a3bc0488612c9019c9484eb0e8a6eae` |
| `IntelMSR.bin` | Lecture des MSR 0x620 et 0x621 (plage et fréquence du ring) ; le module sait aussi écrire quelques MSR de puissance, fonction que MAUS n'appelle jamais | `d6ed85d65ab17a22f813ef98207d6d537155ee2ded5976a21cb48413c9b92e5f` |
| `SmbusI801.bin` | Bus SMBus des chipsets Intel : lecture des puces SPD des barrettes. Le module sait écrire sur le bus ; MAUS filtre chaque transfert et ne laisse passer que le choix de page SPD (`Workshop/Memory/PawnIo/SpdBusDriver.cs`) | `a0f7d066e7efda28c0e754c1f52dbd8dc280d388ba8575b514a80cb67490530c` |
| `SmbusPIIX4.bin` | Bus SMBus des chipsets AMD, même usage et même filtre | `91f9b4b1c39e3d399ce48477a89d8f6bd3e58a2241064a4daec3a1513dff56e5` |
| `SmbusNCT6793.bin` | Bus SMBus de certaines puces Nuvoton, même usage et même filtre | `db068c6a87c0066ebfb7f691d88422fee8af350898e7a7f0db100fd6632bab17` |

Les cinq modules viennent de l'archive de la version 0.2.11 publiée sur GitHub (SHA-256 `43608cb89bc84247fef1368a139013f7d043e17db6d6c8dfc9b46bf0905a81f4`). Le code source de ces modules (`IntelMCHBAR.p`, `IntelMSR.p`) est disponible à l'adresse ci-dessus ; le texte de la LGPL-2.1 est dans `licenses/LGPL-2.1.txt`, livré avec MAUS. Vous pouvez remplacer ces modules par une version modifiée en recompilant MAUS avec vos fichiers `.bin` (dossier `src/Maus.Core/Workshop/Memory/PawnIo/`).

**PresentMon 2.6.0** (Intel Corporation, licence MIT, https://github.com/GameTechDev/PresentMon, publication `v2.6.0`) : MAUS inclut, sans le modifier, l'outil en ligne de commande `PresentMon-2.6.0-x64.exe` (signé par Intel ; SHA-256 `b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af`), dans le dossier `PresentMon` de MAUS. Il sert uniquement à mesurer les images par seconde pendant le « Relevé pendant une partie » de l'Atelier, en écoutant les événements d'affichage de Windows : il ne touche ni aux jeux ni aux réglages. MAUS ne le lance que si son empreinte est exactement celle ci-dessus. Le texte de sa licence est livré dans `licenses/PresentMon-LICENSE.txt` :

> Copyright (C) 2017-2024 Intel Corporation. Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions: The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Sources de données techniques (non incluses comme code)

- **ZenStates-Core** (Ivan Rusanov, GPL-3.0, https://github.com/irusanov/ZenStates-Core) : emplacement et découpage des registres du contrôleur mémoire des Ryzen (UMC, bus SMN) et disposition des tables PM (FCLK, UCLK, MCLK, tensions). Pour les APU Raven Ridge et Picasso (table 0x1E0004), la disposition est recoupée avec ryzen_monitor_ng et TuxTimings ; pour Renoir (tables 0x370000 à 0x370002), ZenStates-Core et RyzenAdj divergent : MAUS n'utilise alors que la disposition générique de la famille, signalée « à vérifier ». Ce sont des faits matériels, relus par le code propre de MAUS (`Workshop/Memory/ZenMemoryController.cs`) ; MAUS étant lui aussi sous GPL-3.0, la reprise de ces tables est compatible, et la source est citée.
- **memtest86+** (GPL-2.0, https://github.com/memtest86plus/memtest86plus) : emplacements des octets SPD des profils XMP 2.0 (DDR4) et XMP 3.0 (DDR5), utilisés pour vérifier le décodeur de MAUS (`Workshop/Memory/SpdDecoder.cs`, code propre).
- **Normes JEDEC** JESD21-C (annexe L, DDR4) et JESD400-5 (DDR5) : structure générale des puces SPD.
- **Fiches techniques Intel** (volume 2, registres MCHBAR : documents 324642 et 326765 pour Sandy et Ivy Bridge, 328898, 329002 et 330835 pour Haswell et Broadwell, 336465 pour Skylake à Comet Lake, 341078, 636761 et 631122 pour Ice Lake, Rocket Lake et Tiger Lake, 655259 et les fiches 13e génération pour Alder et Raptor Lake, 795258, 835538, 844345 et 831649 pour les Core Ultra), **memtest86+** (GPL-2.0, `system/imc/x86/intel_*.c`), **coreboot** (GPL-2.0, initialisation mémoire native de Sandy Bridge et Haswell), **CoreFreq** (Cyril Courtiat, GPL-2.0, https://github.com/cyring/CoreFreq, structures `*_IMC_*` de `intel_reg.h`), un relevé réel publié dans les discussions de CoreFreq (i7-12700H) et le **noyau Linux** (GPL-2.0 : `intel-uncore-frequency`, `intel-family.h`, `igen6_edac`, `ie31200_edac`, `i915`) : emplacement et découpage des registres du contrôleur mémoire Intel, réunis dans `Catalog/intel-memory-controller.json`, où chaque champ cite ses sources. Ce sont des faits matériels relus par le code propre de MAUS (`Workshop/Memory/IntelMemoryController.cs`).
