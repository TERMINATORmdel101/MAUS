# Journal des versions

Les versions sont testées avec des simulations (faux registre, faux WMI, faux registres de contrôleur mémoire…) puis, depuis la 0.3.3-alpha, sur le PC Windows du porteur (Intel Core i7-8700K, carte MSI Z390). Ce qui n'a pas pu être vérifié sur un vrai processeur est signalé dans l'application.

## 4.0.0 — 28/09/2026

Numéro choisi par le porteur, après la 3.9.1 : nouveaux tests du processeur, programme de validation du Curve Optimizer, corrections issues de ses essais.

**Retours et demandes du porteur sur la 3.9.1**

- Lien PCIe des cartes graphiques : plus de fausse alerte sur les portables. Windows ne donne que le maximum de la carte, pas celui du slot, du processeur ou du câblage du portable (le port racine ne publie pas sa largeur, constaté sur le PC du porteur). Portable : largeur réduite = information, jamais de conseil de démontage ; carte NVIDIA : `nvidia-smi` (`pcie.link.width.max`) sert de référence, affiché comme « maximum annoncé par nvidia-smi » (sa documentation parle du maximum « possible avec cette carte et ce système », mais un relevé publié sur portable montre le maximum de la puce : sources en désaccord, rien n'en est conclu sur le PC) ; boîtier externe Thunderbolt / USB4 non jugé ; PC de bureau : « à améliorer », avec d'abord une vérification en charge (NVIDIA indique que la largeur peut baisser au repos).
- Test du processeur : pendant le test, la barre sous les cartes affiche la charge et la **température du processeur** (et non plus celle de la carte graphique) ; le test de la mémoire vidéo affiche celles de la carte graphique.
- Tests du processeur : sept charges au choix, seules celles que le processeur annonce étant proposées. **Automatique** (mélange SHA-256, matrices, nombres premiers, comme avant), **Entiers** (sans vecteurs), **SSE2** (128 bits), **AVX** (256 bits), **AVX2 + FMA** (256 bits, charge lourde), **AVX-512** (512 bits, la plus lourde), **Caches et mémoire** (tableau de 8 Mo par fil). Chaque charge est vérifiée contre une référence (toute erreur de calcul = instabilité), a son propre historique de score et affiche les instructions réellement utilisées. Les charges AVX, FMA et AVX-512 préviennent qu'elles chauffent davantage, et que sans PawnIO la température du processeur n'est pas lue. Même choix pour le test cœur par cœur.
- **Programme complet Curve Optimizer** (demande du porteur, 1 h ou 4 h) : phase 1, un cœur à la fois (fil épinglé), pic de charge d'une seconde, chute au repos d'une seconde, réveil vérifié (un calcul contrôlé doit revenir à temps), cœur suivant ; phase 2, un fil par processeur logique, rafales de 5 à 10 secondes libérées et arrêtées au même instant, 2 secondes de repos, en boucle. Barre d'avancement, phase, cœur, temps restant et erreurs au fil de l'eau ; bilan par cœur et pour les transitoires ; trace sur disque pour nommer la phase et le cœur après un gel ; erreurs WHEA ; arrêt en surchauffe. Grand avertissement : PC inutilisable pendant toute la durée, liste des programmes qui utilisent le processeur.
- Priorité : pendant le programme, MAUS passe en priorité **haute** et ses fils de calcul juste en dessous de sa surveillance. Pas de priorité « temps réel » ni de baisse des autres programmes en « inactive », contrairement à la demande initiale : Microsoft prévient qu'un processus temps réel qui calcule plus d'un instant peut empêcher le vidage des caches disque et bloquer la souris ([SetPriorityClass](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setpriorityclass)), ce qui donnerait de faux plantages et risquerait des pertes de données ; en priorité haute, les programmes ordinaires cèdent déjà la place (ils sont presque à l'arrêt pendant les rafales, c'est voulu). MAUS ne ferme et ne suspend aucun autre programme.
- Tous les tests empêchent la mise en veille de Windows pendant qu'ils tournent (`SetThreadExecutionState`), pour qu'un test de plusieurs heures ne soit pas coupé par la veille programmée.

**Retours du porteur du 29/09**

- Accueil : les cartes « Tester mon PC », « En direct », « Qu'est-ce qui prend de la place ? » et « Pourquoi mon PC est lent ? » menaient toutes à « Mon PC » (la barre d'onglets de l'atelier revenait d'elle-même au premier onglet à son affichage). Chaque carte ouvre maintenant la bonne partie ; vérifié en cliquant chaque carte par UI Automation.
- **Avertissements** : au premier lancement, une fenêtre rappelle que MAUS est fourni sans garantie (licence GPL-3.0, articles 15 et 16), que les corrections, réparations et tests se lancent sous la responsabilité de l'utilisateur, même quand ils sont censés ne poser aucun problème, et que la responsabilité des auteurs est écartée dans toute la mesure permise par la loi ; il faut cocher la case pour continuer, « Quitter » ferme MAUS. Les droits des consommateurs restent préservés (le texte le dit). Un rappel figure dans chaque confirmation : corrections, réparation de Windows, Explorateur, mises à jour des logiciels, pilote PawnIO, tous les tests ; aussi en ligne de commande. Relecture possible depuis les Paramètres (« Avertissements »). « À propos » : « fourni sans garantie ».
- Logo : recadré plus serré sur les lettres (toujours sans rien redessiner), coins arrondis et ombre légère, barre aux quatre couleurs des lettres, et ce que veut dire le nom (Maintenance · Audit · Updates · Sécurité).

**Relecture en parallèle des ajouts du 28/09 (deux relecteurs, deux vérificateurs qui cherchent à réfuter)** : 9 défauts confirmés, tous corrigés.

- Texte de l'avertissement faux : il disait que MAUS « ne ralentit aucun autre programme », alors qu'en priorité haute les autres programmes sont presque à l'arrêt pendant les rafales. Le texte le dit maintenant, et conseille une courbe de ventilation réglée dans le BIOS plutôt que par un logiciel pour un test long.
- Deux tests pouvaient partir en même temps (bouton cliqué pendant la préparation du programme) : le premier fini coupait la surveillance de l'autre. Les tests sont maintenant « occupés » dès le clic.
- Fermer MAUS pendant un test laissait la trace de plantage : au lancement suivant, MAUS annonçait un gel qui n'avait pas eu lieu. La trace est effacée à la fermeture ; une erreur dans un fil de calcul compte comme une erreur au lieu de fermer MAUS.
- Avertissement « sans PawnIO » donné à tort quand les mesures n'avaient pas encore tourné, et absent pour les charges légères : il se base maintenant sur la présence du pilote et s'affiche pour toutes les charges (sans température du processeur, pas d'arrêt automatique sur ce critère).
- Message de fin : « aucune erreur WHEA » même quand le journal était illisible (maintenant dit clairement), épinglage refusé non signalé (ajouté), cœur en erreur compté deux fois pendant l'avancement (corrigé), heure du plantage affichée à la place de l'heure de lancement (le texte dit « lancé le »).
- Lien PCIe : la valeur de `nvidia-smi` n'est plus présentée comme le maximum du PC (voir plus haut).

## 3.9.1 — 28/09/2026

Numérotation choisie par le porteur : la 0.3.3-alpha est suivie de la 3.9.1.

**Mémoire : timings réels sur toutes les générations Intel Core depuis la 4e**, par les modules officiels PawnIO (lecture seule), chaque registre cité avec ses sources ; un champ sur lequel les sources se contredisent n'est pas affiché.

- Haswell et Broadwell (4e et 5e génération) : 26 timings, type de mémoire, command rate (Haswell). L'horloge mémoire n'est pas affichée : coreboot, memtest86+ et CoreFreq placent le bit de référence à trois endroits différents.
- Ice Lake et Rocket Lake (10e mobile, 11e de bureau), Tiger Lake (11e mobile) : timings primaires, secondaires et tertiaires (dont tRDRD / tRDWR / tWRRD / tWRWR sg, dg, dr, dd), gear 1 ou 2, command rate, horloge mémoire et ring.
- Alder Lake et Raptor Lake (12e à 14e génération) : mêmes timings en DDR4 et DDR5, gear 1, 2 ou 4, horloge d'après le relevé réel d'un i7-12700H publié.
- Core Ultra séries 1 et 2 (Meteor Lake, Arrow Lake, Lunar Lake) : timings ; horloge en gear 2 seulement (Intel et CoreFreq divergent en gear 4) ; rien de plus sur Lunar Lake tant que ses registres ne sont pas publiés.
- Second contrôleur mémoire absent (registres lus tout à un) : ignoré au lieu d'être compté comme canal.
- Chaque nouveau délai a sa description en clair.
- Ces générations ne sont pas encore comparées à CPU-Z sur un vrai processeur : l'application le dit, avec l'invitation à signaler tout écart.

**Mémoire AMD Ryzen**

- APU Ryzen Raven Ridge et Picasso (table d'énergie 0x1E0004) : FCLK, UCLK, MCLK et tension SoC.
- Renoir 0x370000 à 0x370002 : ZenStates-Core et RyzenAdj ne s'accordent pas sur la table ; lue avec la disposition générique de la famille et signalée « à vérifier ».
- UCLK plus haute que MCLK (impossible en fonctionnement normal) : signalée comme table probablement mal interprétée.

**Déjà livré depuis la 0.3.3-alpha**

- Comparer deux fiches mémoire : chaque lecture de l'onglet Mémoire est gardée (30 dernières, valeurs techniques seulement) et « Comparer avec une lecture précédente » liste ce qui a changé.
- Étalonnage de la tension de la mémoire sur les cartes mères que LibreHardwareMonitor ne décrit pas : vous tapez la tension réglée dans le BIOS, MAUS propose les entrées qui la mesurent, vous choisissez ; présenté comme « étalonné par vous ».
- Fenêtre de surveillance : mini-courbe par mesure, relevé (à la main ou pendant un test de stabilité) avec bilan et export CSV.

**Sécurité (revue des fonctions qui modifient le PC)**

- Corrections : une erreur imprévue de Windows pendant une écriture (clé de registre en cours de suppression, valeur refusée…) défait désormais toute la correction ; avant, elle pouvait laisser les premières écritures en place sans retour arrière.
- Test de la mémoire vive : arrêt automatique en cas de surchauffe, comme les tests du processeur et de la mémoire vidéo.
- Audit automatique chaque semaine : la tâche lance MAUS avec les droits administrateur ; elle n'est plus créée que si MAUS est dans Program Files ou installé par le Microsoft Store (depuis un dossier modifiable, un autre programme pourrait remplacer MAUS.exe). Une tâche existante qui pointe vers un dossier non protégé est signalée.
- Vérifié sans changement : lecture seule des commandes système (liste blanche), winget pris dans le dossier protégé de Windows, pilote PawnIO installé par winget (identifiant exact, fenêtre visible), lecture SPD filtrée, test de disque avec fichier supprimé à la fermeture, redémarrage de l'Explorateur seulement si Windows le relance, ligne de commande avec confirmation.

## 0.3.3-alpha — 28/09/2026 (testée sur le PC Windows du porteur)

- Icône lisible par Windows (plantage au démarrage corrigé) et logo complet à toutes les tailles.
- Fiche mémoire sans pilote : vitesse et tension réellement appliquées (d'après Windows), comparées aux profils de la puce SPD (profil XMP activé, réglage manuel, standard JEDEC).
- « Mon PC » : profils XMP / EXPO sous la référence de chaque barrette.
- Timings réels sur Intel Core 6e à 10e génération (Skylake à Comet Lake), par les modules officiels PawnIO IntelMCHBAR et IntelMSR, en lecture seule : tCL, tRCD, tRP, tRAS, tCWL, tRDPRE, tWRPRE, tRFC, tREFI, type de mémoire, command rate, horloge mémoire, ring et agent système. Carte des registres sourcée (fiche Intel 336465-001), pas encore comparée à CPU-Z sur un vrai processeur.
- Timings réels sur Intel Core 2e et 3e génération (Sandy Bridge, Ivy Bridge), carte vérifiée par un second passage indépendant : seize timings, command rate et horloge mémoire.
- Onglet Mémoire : bouton « Installer PawnIO » quand le pilote manque.
- Paramètres (bouton engrenage) : thème comme Windows, clair ou sombre ; couleurs selon vos composants (teinte du processeur Intel ou AMD, touche de la carte graphique Nvidia ou AMD), couleur de Windows, bleu MAUS ou six teintes ; animations ; vitesse d'actualisation des mesures (0,5 à 5 s). Les couleurs des constats ne changent jamais ; contraste d'au moins 4,5:1 garanti.
- Animations discrètes : transitions de page, apparition des cartes, jauge du score (coupées si Windows ou vous les désactivez).
- Fenêtre de surveillance indépendante, façon HWMonitor simplifié : température, consommation, fréquence et charge de chaque composant (actuelles, minimales, maximales), erreurs matérielles WHEA (dont PCI Express) et erreurs de Windows depuis son ouverture, option « toujours au premier plan ».
- Les préférences ne s'écrasent plus entre fenêtres (écriture « lire, modifier, écrire »).
- Lecture de la mémoire qui ne se terminait jamais : un verrou du bus des barrettes laissé « abandonné » (MAUS fermé en pleine lecture) était gardé pour toujours par le MAUS suivant, et la lecture attendait 2 secondes par octet. Le verrou est maintenant récupéré puis rendu ; si un autre programme garde le bus plus de 10 secondes, MAUS le dit ; l'interface abandonne une lecture bloquée au bout de 90 secondes et un journal des étapes est écrit dans `%LOCALAPPDATA%\MAUS\logs\lecture-memoire.txt`. Même correction pour le verrou PCI (Ryzen).
- Une seule fenêtre de MAUS à la fois : le relancer ramène la fenêtre déjà ouverte.
- Mesures en direct figées une fois PawnIO installé : le pilote s'ouvre maintenant en arrière-plan, une lecture du pilote qui traîne laisse passer les mesures sans pilote, et une valeur du pilote de plus de 6 secondes n'est plus affichée. Les capteurs avancés n'ouvrent plus le bus des barrettes (lent et partagé). La lecture des puces est faite une seule fois par session et partagée entre « Mon PC » et « Mémoire ».
- Tension de la mémoire : celle donnée par Windows est maintenant présentée comme « déclarée par le BIOS » (chez le porteur : 1,25 V déclarés pour 1,45 V réglés). Avec PawnIO, la tension mesurée par la carte mère est affichée quand LibreHardwareMonitor sait quelle entrée la porte (« DRAM », « VDIMM », « DIMM ») ; sinon MAUS le dit, sans deviner.
- MAUS se termine vraiment quand sa fenêtre principale se ferme (un MAUS invisible gardait le bus des barrettes).
- Carte des registres Skylake à Comet Lake vérifiée sur un vrai processeur (i7-8700K du porteur, 26/09/2026).
- Thème sombre : tous les textes en blanc (certaines pages héritaient du noir par défaut de Windows, les textes secondaires étaient grisés).
- Lecture des puces SPD sans LibreHardwareMonitor : MAUS charge lui-même le module officiel PawnIO du bus SMBus (plus de détection concurrente en arrière-plan), avec un garde-fou qui ne laisse passer que les lectures et le choix de page SPD (MAUS ne peut ni modifier une barrette, ni toucher à son contrôleur d'alimentation DDR5), et une horloge système à 1 ms pendant la lecture.
- Garde de l'interface : si la fenêtre ne répond plus 5 secondes, les dernières actions sont écrites dans `%LOCALAPPDATA%\MAUS\logs\interface-bloquee.txt`.
- Un MAUS resté sans fenêtre (bloqué) n'empêche plus d'ouvrir MAUS : il est proposé de le fermer.
- Licences livrées avec l'application (dossier `licenses`).

## 0.3.2-alpha — 25/09/2026

**Aucune donnée inventée** (demande du porteur) : chaque valeur affichée comme une limite ou une référence cite sa source, sinon elle n'est pas affichée.

- Températures du processeur : une ligne par modèle vérifié (AMD, Intel ARK, presse) ; avec PawnIO, la limite est lue dans la puce (Intel « Distance to TjMax »). Modèle inconnu = pas de jauge ni d'alarme (fin de la limite par défaut à 100 °C).
- Tensions mémoire : DDR4 1,2 V nominal, « élevée » au-delà de 1,45 V, maximum absolu 1,50 V (JEDEC) ; DDR5 1,1 V nominal, « élevée » au-delà de 1,4 V (Samsung), aucun seuil « dangereux » publié donc aucun n'est fixé.
- Décodeur SPD corrigé : activation du profil EXPO 2, liste des latences CAS du XMP 2.0, timings secondaires du XMP 3.0, somme de contrôle de chaque profil XMP / EXPO.
- winget : l'encodage de la sortie n'est plus supposé (UTF-16, UTF-8 ou page de code OEM, choisi d'après les octets).
- Textes corrigés d'après la documentation : réinstallation par Windows Update (M02), pause de Windows Update (M01), recherche web du menu Démarrer (M04), versions des pilotes graphiques (M09).
- Version affichée dans « À propos », les rapports et `maus --version`.
- Nettoyage : suppression des questions initiales (toutes tranchées dans `docs/fiche-technique/00-decisions.md`) et du fichier source de la maquette (l'interface réelle existe ; l'image de la maquette reste).

## Avant la 0.3.2-alpha (25/09/2026)

- Capteurs avancés par le pilote libre PawnIO (installé seulement à la demande) : vraie température, tension et consommation du processeur.
- Fiche mémoire complète : SPD de chaque barrette (JEDEC, XMP, EXPO), timings réels primaires / secondaires / tertiaires et FCLK / UCLK / MCLK sur Ryzen.
- Relevé pendant une partie (bilan, CSV), historique des scores, audit automatique chaque semaine.
- Licence GPL-3.0 (fichier `LICENSE`).
- Modules M17 à M20 (périphériques, réseau, sauvegardes, logiciels à mettre à jour), réparation DISM / SFC, stockage, test de disque, test de connexion, « Demander de l'aide », test cœur par cœur.
- V0.2 : corrections réversibles (journal, point de restauration vérifié, Annuler), atelier matériel, nouvelle interface, trois langues.
- V0.1 : socle et 15 modules d'audit en lecture seule (seule version testée sur Windows).
