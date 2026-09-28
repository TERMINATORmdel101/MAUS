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

- RAM (relevé du 25/09/2026, sources dans le catalogue) : DDR4 1,2 V nominal, « élevée » au-delà de 1,45 V (dégradation rapportée par le guide MemTestHelper), « dangereuse » au-delà de 1,50 V (maximum absolu de la norme JEDEC JESD79-4). DDR5 1,1 V nominal, « élevée » au-delà de 1,4 V (maximum en continu selon Samsung, cité par Tom's Hardware) ; aucun seuil « dangereux » publié, donc MAUS n'en fixe pas. **Pas de source = pas de seuil.**
- Processeur : avec PawnIO, la limite lue dans la puce elle-même (Intel : « Distance to TjMax » + température). Sinon, une ligne par modèle vérifié (fiches AMD / Intel ARK, presse) : Ryzen 7800X3D / 7900X3D / 7950X3D 89 °C, Ryzen 7000 sans 3D V-Cache et Ryzen 9000 (9800X3D compris) 95 °C, 5600X 95 °C, 5700X/5800X/5800X3D/5950X 90 °C, Core Ultra 9 285K 105 °C, i7-13700K 100 °C. Modèle inconnu = aucune jauge ni alarme de température (plus de limite par défaut à 100 °C). Intel Core 13e/14e génération : Intel limite les demandes de tension au-dessus de 1,55 V depuis le microcode 0x129 (Module 8).
- Carte graphique NVIDIA : seuils lus dans la carte (NVML) ; limite de puissance au-dessus de la valeur par défaut = carte surcadencée, signalée.

### Gestionnaire des tâches à la MAUS

- Liste des processus : processeur, mémoire, disque, carte graphique (par processus), éditeur, signature numérique, emplacement.
- **« Qu'est-ce que c'est ? »** : explication en français des processus courants de Windows et des logiciels répandus (catalogue MAUS), badge de confiance (signé par Microsoft, éditeur connu, non signé, emplacement suspect comme `%TEMP%`, heuristiques du Module 12).
- **« Rechercher sur le web »** : ouvre le navigateur de l'utilisateur, sans les droits administrateur, sur une recherche du nom exact. Rien n'est envoyé automatiquement (aucune télémétrie) ; moteur de recherche au choix de l'utilisateur.
- **Terminer un processus** : avec confirmation, et toujours refusé pour les processus vitaux de Windows (csrss, wininit, winlogon, services, lsass, smss…), dont l'arrêt provoque un écran bleu.
- **« Pourquoi mon PC est lent ? »** : 60 secondes de mesures, puis les trois causes principales, chacune reliée au module qui la corrige (voir `suggestions.md`).

### Tests (lancés uniquement par l'utilisateur, arrêt automatique sur seuil)

- Processeur : calculs vérifiés (hachage, nombres premiers, matrices) sur tous les cœurs, puis sur un seul ; toute erreur de calcul signale une instabilité (surcadençage, tension trop basse, surchauffe). Score comparé aux passages précédents du même PC. Charge au choix (3.9.1) : Automatique (ce mélange), AVX (calculs vectoriels 256 bits, FMA si disponible) ou Très lourd (AVX-512 si le processeur l'annonce, sinon AVX à pleine cadence) ; chaque charge est vérifiée contre une référence, garde son propre historique de score et affiche les instructions utilisées. Pendant un test processeur, mémoire ou cœur par cœur, MAUS affiche la charge et la température du processeur ; pendant le test de mémoire vidéo, celles de la carte graphique.
- RAM : écriture et relecture de motifs sur la mémoire libre choisie par l'utilisateur ; débit et latence. Un test en mode utilisateur ne couvre pas toute la RAM : MAUS le dit. Arrêt automatique sur alarme de température, vérifiée à chaque bloc de 64 Mo (ajouté en 3.9.1 : seul test qui ne l'avait pas).
- Mémoire vidéo : blocs de 64 Mo alloués dans la mémoire dédiée par Direct3D 11 (présent dans Windows), remplis de motifs par un tampon de transfert puis relus et comparés ; 60 % de la mémoire dédiée par défaut, au choix de l'utilisateur ; arrêt sur alarme de température et sur perte du pilote (signe d'instabilité). Débit de relecture affiché à titre indicatif.
- Surveillance pendant le test : températures et arrêt immédiat au seuil ; bouton « Arrêter » toujours visible.

**Capteurs avancés par PawnIO (accord du porteur, 25/09/2026) :** bibliothèque LibreHardwareMonitorLib 0.9.6 (MPL-2.0), qui contient les modules PawnIO signés (AMD 0F/10/17, IntelMSR, RyzenSMU, LpcIO, SMBus…). MAUS n'ouvre que le processeur, la carte mère et la mémoire, seulement si le pilote PawnIO est installé (`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO`) et MAUS administrateur ; une erreur du pilote laisse les mesures sans pilote. Installation et retrait : `winget install|uninstall --id namazso.PawnIO --exact --source winget`, console visible, après confirmation. Température du processeur : `Core (Tctl/Tdie)` (AMD), `CPU Package` (Intel), sinon la sonde la plus chaude ; tension : `Core (SVI3/SVI2 TFN)`, `CPU Core`, sinon le VID le plus haut ; puissance : `Package`. Alarme : attention à 3 °C de la limite du fabricant (il ralentit de lui-même, normal en charge pour certains modèles), danger au-delà de +5 °C. À vérifier sur Windows : noms réels des capteurs, intégrité de la mémoire (HVCI) et anti-triche.

### Fenêtre de surveillance (0.3.3-alpha, courbes et relevé en 3.9.1)

Fenêtre indépendante, façon moniteur matériel simplifié, pour accompagner un test de stabilité : pour chaque composant, température, consommation, fréquence et charge (actuelles, minimales, maximales) avec une mini-courbe ; erreurs matérielles WHEA (dont celles du bus PCI Express) et erreurs de Windows (journaux Système et Application) depuis son ouverture ; option « toujours au premier plan ». **Relevé** : démarré à la main ou tout seul pendant un test de stabilité ; à l'arrêt, bilan (températures et consommations maximales, plages de fréquence, erreurs WHEA / PCIe et Windows pendant le relevé) et export CSV au format de la langue de Windows. Rien n'est envoyé.

### Revue de sécurité (28/09/2026, version 3.9.1)

Toutes les fonctions qui modifient le PC ont été relues. Corrigé : (1) une erreur imprévue de Windows pendant une correction (clé de registre en cours de suppression, valeur refusée) défait maintenant toute la correction ; (2) le test de la RAM s'arrête en surchauffe ; (3) l'audit hebdomadaire (tâche planifiée aux droits les plus élevés) n'est créé que si MAUS est dans Program Files ou installé par le Microsoft Store, et une tâche existante pointant vers un dossier modifiable par l'utilisateur est signalée. Vérifié sans changement : commandes système en liste blanche de lecture, winget pris uniquement dans le dossier protégé de Windows, installation de PawnIO par winget (identifiant exact, fenêtre visible, après confirmation), lecture SPD filtrée (seuls les choix de page SPD passent), test de disque (fichier supprimé à la fermeture), redémarrage de l'Explorateur seulement si Windows le relance, ligne de commande avec confirmation ou `--yes` explicite.
