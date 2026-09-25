## Module 16 — Atelier matériel (demande du porteur, 25/09/2026)

**Objectif :** réunir dans MAUS ce que l'on cherche aujourd'hui dans plusieurs outils séparés (identité du matériel, capteurs en direct, gestionnaire des tâches, tests de stabilité), mais **à la manière de MAUS** : chaque valeur est expliquée en français simple, comparée à un seuil de sécurité et reliée aux diagnostics et corrections des autres modules.

**Originalité (consigne du porteur) :** on s'inspire des besoins couverts par les logiciels existants, sans jamais les copier : ni leur présentation, ni leurs noms d'onglets, ni leurs icônes, ni leur code. MAUS n'utilise aucune base de données ni aucun texte provenant de ces logiciels ; ses catalogues sont rédigés à partir de sources officielles (fabricants, JEDEC, Microsoft).

### Ce qui se lit sans pilote noyau (décision : aucun pilote embarqué)

| Information | Source (lecture seule) | Remarque |
|---|---|---|
| Processeur : fabricant, nom commercial, famille/modèle/révision, jeux d'instructions (SSE4.2, AVX2, AVX-512, AES, SHA), virtualisation, caches, cœurs P/E | Instruction `CPUID` (en .NET : `X86Base.CpuId`), `Win32_Processor`, `GetLogicalProcessorInformationEx` | `CPUID` s'exécute en mode utilisateur |
| Fréquence réelle du processeur | Compteurs PDH `\Processor Information(*)\% Processor Performance` × fréquence de base | Méthode du Gestionnaire des tâches (à vérifier) |
| Carte mère, BIOS | `Win32_BaseBoard`, `Win32_BIOS` | |
| Barrettes de RAM : emplacement, capacité, vitesse, fabricant, référence, **tension configurée, minimale et maximale** | `Win32_PhysicalMemory` (`ConfiguredVoltage`, `MinVoltage`, `MaxVoltage` en mV, SMBIOS 2.8+) | Fiabilité selon le BIOS (à vérifier) ; la marque des puces se déduit de la référence quand elle est connue (Module 10) |
| Carte graphique : nom, mémoire, pilote, lien PCIe | `Win32_VideoController`, registre de la classe d'affichage (`HardwareInformation.qwMemorySize`), Module 9 | |
| Carte graphique en direct (toutes marques) : température, ventilateur, puissance relative, fréquence mémoire | `D3DKMTQueryAdapterInfo` (`KMTQAITYPE_ADAPTERPERFDATA`), comme le Gestionnaire des tâches | Structure « réservée au système » : à valider sur chaque marque |
| NVIDIA : fréquences, puissance (W), limite de puissance par défaut et appliquée, **seuils de température du modèle** (ralentissement, arrêt) | NVML (`nvml.dll`, installée avec le pilote, API publique documentée par NVIDIA) | Les seuils viennent de la carte elle-même : pas besoin d'Internet |
| Utilisation CPU, RAM, disques, réseau, GPU (par moteur et par processus) | PDH (`PdhAddEnglishCounterW`), `GlobalMemoryStatusEx` | |
| Batterie : capacité d'origine et actuelle, tension, débit de charge | `root\wmi` : `BatteryStaticData`, `BatteryFullChargedCapacity`, `BatteryStatus` | |
| Disques : modèle, type, température, usure | `MSFT_PhysicalDisk`, `MSFT_StorageReliabilityCounter` (Module 11) | |

### Ce qui exige un pilote noyau (non fait, question posée au porteur)

Tension du processeur (Vcore) et de ses cœurs, température réelle du processeur (capteur interne), tensions de la carte mère, contenu SPD des barrettes (marque exacte des puces, timings secondaires), marque des puces de mémoire de la carte graphique, vitesse des ventilateurs de la carte mère. Pistes : PawnIO s'il est déjà installé (décision existante), ou la bibliothèque LibreHardwareMonitor (MPL 2.0, qui s'appuie sur PawnIO). Sans pilote, MAUS l'affiche honnêtement : « non lisible sans pilote ».

### Seuils de sécurité

Catalogue JSON `hw-safety-limits.json`, une ligne par famille de composant, avec sa source. Chaque valeur lue est classée **normale / élevée / dangereuse**, et MAUS explique pourquoi.

- RAM : tension nominale JEDEC 1,2 V (DDR4) et 1,1 V (DDR5). Au-delà du profil XMP/EXPO courant, MAUS signale « élevée », puis « dangereuse » au-dessus d'un seuil prudent (seuils maison, à vérifier et à sourcer).
- Processeur : température maximale (TjMax) par famille, depuis les fiches officielles d'Intel et d'AMD (à vérifier). Intel Core 13e/14e génération : Intel limite les demandes de tension au-dessus de 1,55 V depuis le microcode 0x129 (Module 8).
- Carte graphique NVIDIA : seuils lus dans la carte (NVML) ; limite de puissance au-dessus de la valeur par défaut = carte surcadencée, signalée.

### Gestionnaire des tâches à la MAUS

- Liste des processus : processeur, mémoire, disque, carte graphique (par processus), éditeur, signature numérique, emplacement.
- **« Qu'est-ce que c'est ? »** : explication en français des processus courants de Windows et des logiciels répandus (catalogue MAUS), badge de confiance (signé par Microsoft, éditeur connu, non signé, emplacement suspect comme `%TEMP%`, heuristiques du Module 12).
- **« Rechercher sur le web »** : ouvre le navigateur de l'utilisateur, sans les droits administrateur, sur une recherche du nom exact. Rien n'est envoyé automatiquement (aucune télémétrie) ; moteur de recherche au choix de l'utilisateur.
- **Terminer un processus** : avec confirmation, et toujours refusé pour les processus vitaux de Windows (csrss, wininit, winlogon, services, lsass, smss…), dont l'arrêt provoque un écran bleu.
- **« Pourquoi mon PC est lent ? »** : 60 secondes de mesures, puis les trois causes principales, chacune reliée au module qui la corrige (voir `suggestions.md`).

### Tests (lancés uniquement par l'utilisateur, arrêt automatique sur seuil)

- Processeur : calculs vérifiés (hachage, nombres premiers, matrices) sur tous les cœurs, puis sur un seul ; toute erreur de calcul signale une instabilité (surcadençage, tension trop basse, surchauffe). Score comparé aux passages précédents du même PC.
- RAM : écriture et relecture de motifs sur la mémoire libre choisie par l'utilisateur ; débit et latence. Un test en mode utilisateur ne couvre pas toute la RAM : MAUS le dit.
- Mémoire vidéo : motifs écrits et relus par la carte graphique (Direct3D, étape ultérieure).
- Surveillance pendant le test : températures et arrêt immédiat au seuil ; bouton « Arrêter » toujours visible.
