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

**Pilote PawnIO** (namazso, https://github.com/namazso/PawnIO) : il n'est **pas** distribué avec MAUS. L'utilisateur l'installe s'il le souhaite, par winget (`namazso.PawnIO`), depuis l'Atelier, et peut le retirer de la même façon. Les modules PawnIO signés utilisés sont ceux inclus dans LibreHardwareMonitorLib.
