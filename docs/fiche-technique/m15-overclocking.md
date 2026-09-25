## Module 15 — Overclocking et undervolting

Le module n'overclocke rien : il détecte le matériel, recommande l'outil officiel adapté, propose un tutoriel pour le modèle exact et affiche une décharge claire. Il conseille l'undervolting avant l'overclocking.

Détection : processeur (Win32_Processor.Name, suffixes K, X, X3D), GPU (profil matériel), fixe ou portable (profil matériel commun). Outils déjà installés : Ryzen Master, MSI Afterburner (+ version), Intel XTU (registre Uninstall HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall et WOW6432Node, DisplayName / DisplayVersion / Publisher).

| Matériel | Outil recommandé | Où le télécharger | Point d'attention |
|---|---|---|---|
| CPU AMD Ryzen (bureau) | AMD Ryzen Master ; en BIOS, PBO et Curve Optimizer | amd.com uniquement | Fonctionnement hors spécifications ; X3D selon génération |
| GPU AMD Radeon | AMD Software: Adrenalin Edition, Performance > Réglage (undervolt, limite de puissance) | déjà installé avec le pilote | Bouton Réinitialiser |
| GPU NVIDIA GeForce | MSI Afterburner + RivaTuner ; ou réglage automatique de la NVIDIA App (Performance) | https://www.msi.com/Landing/afterburner/graphics-cards uniquement | Plus de 50 faux sites Afterburner ont diffusé un voleur de mots de passe et un mineur en 2022 (BleepingComputer, 23/11/2022) |
| CPU Intel K | Intel Extreme Tuning Utility (XTU) | intel.com uniquement | Core 13e/14e gén. bureau : ni overclocking ni undervolt agressif ; microcode 0x12F (voir Module 8) |
| Portable | Utilitaire du constructeur ; undervolt GPU possible avec Afterburner | site constructeur | Marge thermique faible, CPU généralement verrouillé |

Action : liste « avant de commencer » (BIOS à jour M08, pilote GPU à jour M09, benchmark de référence M11, aucune erreur WHEA M02). Bouton « Trouver un tutoriel » : ouvre une recherche vidéo avec le modèle exact (ex. « undervolt RTX 4070 Afterburner », « Curve Optimizer 7800X3D ») ; seul le modèle matériel part dans l'adresse. Outils de stabilité : HWiNFO, OCCT, Cinebench 2024, 3DMark, y-cruncher ; mémoire : TestMem5, MemTest86.

En V0.1 : constats d'information (FindingStatus.Info) pour chaque composant avec l'outil recommandé et l'URL officielle ; si MSI Afterburner est installé, afficher sa version ; si Intel Raptor Lake bureau : Warning « pas d'overclocking ». Fournir aussi l'URL de recherche de tutoriel (https://www.youtube.com/results?search_query=... encodée) dans Advice.

Message : « L'overclocking et l'undervolting peuvent faire gagner quelques pour cent, mais aussi provoquer plantages, corruption de données ou usure prématurée. Suivez une vidéo pour votre modèle exact, avancez par petits pas et testez la stabilité à chaque étape. Téléchargez les outils uniquement sur le site officiel. Ces réglages se font hors de l'utilitaire, sous votre contrôle : il ne peut pas en garantir le résultat. »
