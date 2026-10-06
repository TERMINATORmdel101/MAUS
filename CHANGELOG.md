# Journal des versions

Les versions sont testées avec des simulations (faux registre, faux WMI, faux registres de contrôleur mémoire…) puis, depuis la 0.3.3-alpha, sur le PC Windows du porteur (Intel Core i7-8700K, carte MSI Z390). Ce qui n'a pas pu être vérifié sur un vrai processeur est signalé dans l'application.

## 0.7.5 — 06/10/2026 — benchmark complété

Numéro choisi par le porteur. Suggestions acceptées par le porteur (« 1 ok 3 ok 4 ok 5 ok 6 ok 7 ok ») et demandes sur le champ de bataille.

- **Lancer de rayons : « Galerie des glaces »** (nouveau test, DirectX 12). Une galerie baroque de 40 m, inspirée de Versailles : dix fenêtres font face à dix arcades de miroirs, et deux grands miroirs aux extrémités se renvoient la salle à l'infini. Statues en or, en chrome, en verre et en marbre, six lustres de cristal, sol de marbre ciré, voûte peinte, jardins derrière les fenêtres.
  - Tout est calculé rayon par rayon par la carte (DirectX Raytracing 1.1) : reflets nets en cascade, réfraction du verre et du cristal, ombres douces du soleil, lumière renvoyée par les murs, rayons de soleil dans la poussière.
  - Il faut DirectX 12 et une carte qui gère le lancer de rayons matériel. MAUS le vérifie ; sinon il passe le test et dit pourquoi dans le bilan.
  - Son score est **compté à part**, hors score combiné : les cartes sans lancer de rayons restent comparables. Une case de la page Benchmark permet de l'écarter.
  - Ses shaders (modèle 6.5) sont compilés pendant la fabrication de MAUS par le compilateur DXC de Microsoft, qui n'est pas livré avec MAUS.
- **« Cabinet de curiosités »** (nouvelle scène, textures) : des objets scannés de Poly Haven (licence CC0 : buste, cheval, vases, lanterne, appareil photo, réveil, éléphant, service à thé, katana) sur une table en bois de rose vernie, un parquet en point de Hongrie. Lumière d'un vrai ciel HDR, reflets dans la table et le parquet, relief par parallaxe, ombres douces et ombres de contact, rayons de lumière dans la poussière, flamme de bougie, profondeur de champ. Les auteurs sont cités dans `THIRD-PARTY-NOTICES.md`.
- **Champ de bataille retravaillé** (demandes du porteur) : un vrai sol vallonné, calculé pixel par pixel avec l'ombre des collines, des dizaines de milliers de pierres et des traces de chenilles. Les chars sont plus beaux et posés sur le relief. Un obus perce un trou dans le blindage ; la tourelle arrachée retombe et se pose sur le sol sans le traverser ; le char brûlé ne noircit qu'autour des ouvertures. Fumée en volume.
- **Capteurs pendant la mesure** : température, fréquence et puissance de la carte graphique et du processeur, et ralentissements signalés par la carte (chaleur, protection matérielle), avec un conseil dans la page Benchmark.
- **Image du résultat à partager** : scores, vignettes des scènes prises pendant la mesure, matériel et capteurs, sans aucune donnée personnelle. Boutons Copier, Ouvrir et Afficher dans le dossier.
- **Comparer** avec le résultat d'un ami (une ligne « MAUS-BENCH-1 » dans le texte copié, relue sans serveur) ou avec sa passe précédente : écart test par test, avertissement si les résolutions diffèrent.
- **Mode 720p léger** pour les cartes intégrées et les petits portables : les mêmes scènes, allégées, avec leurs propres points.
- **Durées** : cinq scènes de 1 min 36 (8 minutes de carte graphique), une minute de lancer de rayons, deux minutes de processeur : une douzaine de minutes en tout, annoncées dans la page et sur l'écran d'accueil du benchmark.
- **Nouvel étalonnage** : toutes les références de la machine de référence (Core i7-8700K, GeForce RTX 2080 Ti) ont été remesurées en 720p, 1080p, 1440p et 4K, en passes à vitesse réelle.
- **Bilan simplifié** (demande du porteur) : les points n'annoncent plus la machine de référence (« 10 000 = … »), les processeurs récents la dépassent de loin. Le bilan montre le nom, les points et les images par seconde de chaque test ; deux fois plus de points = deux fois plus rapide, à résolution égale. Le détail (capteurs, 1 % des images les plus lentes) reste dans la page Benchmark.
- **Tests de la mémoire vive et de la mémoire vidéo au maximum** (Atelier > Tests, demande du porteur) : par défaut, toute la mémoire libre, quelle que soit la quantité installée (16, 24, 32, 64 Go…). MAUS laisse seulement une marge à Windows (1 Go, ou un seizième de la mémoire) et à l'affichage (256 Mo, d'après le budget de mémoire vidéo que Windows accorde à MAUS). Les tailles plus petites restent au choix, pour un test plus rapide.
- Bilan d'une passe partielle : le score du composant testé passe au premier plan. Bouton Benchmark sur l'accueil.
- **Pastel dans les dernières fenêtres annexes** : À propos, Demander de l'aide, mises à jour des logiciels, avertissement du premier lancement, choix du portable, notification.

## 0.7.1 — 06/10/2026 — benchmark visuel

Modification majeure, numéro choisi par le porteur.

- **Benchmark visuel** (demande du porteur), avec sa **page dans le menu de gauche** : environ dix minutes en plein écran, dans un processus séparé (un plantage du pilote graphique n'emporte pas MAUS). Interface graphique au choix : **DirectX 12** (recommandé) ou **DirectX 11**. Le même moteur sert aux deux, et les deux donnent la même image.
  - **Page Benchmark** : choix des tests (complet, carte graphique seule, processeur seul), de l'interface graphique et de la résolution, durée annoncée. Elle affiche le dernier résultat : score combiné, une carte par composant avec une barre par test, point fort et point faible. On y trouve aussi l'historique des passes avec sa courbe, et un bouton « Copier le résultat » pour un forum : seulement le matériel, aucune donnée personnelle. Le bilan est gardé dans `%LOCALAPPDATA%\MAUS\benchmark-history.json` ; les passes d'essai accélérées n'y vont pas.
  - **Mise en scène** : chaque scène s'ouvre sur un générique (sortie du noir, titre en grand pendant la mise en route non mesurée) et se ferme en fondu. Une courbe montre les temps des dernières images pendant la mesure. Le bilan est animé : le score défile, les barres se remplissent.
  - **Carte graphique**, quatre scènes de 2 minutes, chacune pousse une capacité à fond :
    - « Anneau de la géante » (géométrie) : 540 000 petits rochers taillés qui ne se touchent pas, plus de 100 millions de triangles par image avec les ombres du soleil, la planète géante presque toujours à l'image ;
    - « Champ de bataille » (effets, idée du porteur) : 24 chars explosent l'un après l'autre : boule de feu, tourelle projetée, éclats qui rebondissent, colonnes de fumée, des centaines de milliers de particules ;
    - « Collision galactique » (bande passante) : 5,5 millions d'étoiles et de nuages de gaz simulés par la carte et superposés à chaque image ;
    - « Forge fractale » (calcul pur) : une Mandelbox éclairée par un orbe, avec des rayons de lumière volumétriques.
  - **Résolution de calcul au choix** : 1080p natif (recommandé, demande du porteur), 1440p ou 4K (très exigeant). Chaque résolution a ses propres points de référence : les scores ne se comparent qu'à résolution égale.
  - **Test standard pour comparer toutes les cartes** (demande du porteur) : en 1080p, chaque scène tourne à plus de 24 images par seconde sur la machine de référence, pour qu'une carte modeste puisse le faire tourner correctement. La 4K reste là pour mettre les très grosses cartes à genoux.
  - **Chars détaillés** : vraies chenilles (92 maillons avec crampons de chaque côté), barbotin denté, galets, rouleaux, jupes, tourelleau, lance-pots fumigènes, antenne. Trois camouflages (désert, forêt, gris), panneaux de blindage, poussière et usure.
  - **Textures plus riches** : rochers fissurés avec veines minérales et glace, désert avec cailloux et traînées de vent, fractale aux reflets d'émeraude et de saphir.
  - **Animations, ombres et reflets** (demande du porteur) : les chars avancent en colonne, tiennent leur position, pivotent leur tourelle et tirent (flamme de bouche qui éclaire, traceur de l'obus) avant d'exploser ; la planète géante projette son ombre sur les rochers ; la glace reflète la planète et le ciel ; le métal des chars reflète le ciel du soir ; mirage tremblant au ras de l'horizon du désert ; le soleil tourne lentement autour de la fractale et ses ombres balaient la structure.
  - **Processeur** : rendu par lancer de rayons sur tous les cœurs (image en 1080p natif) puis sur un seul, et calcul vectoriel en double précision (ensemble de Mandelbrot, 2, 4 ou 8 calculs à la fois selon le processeur).
  - **Points** : 10 000 = la machine de référence (Core i7-8700K et GeForce RTX 2080 Ti, mesurée par le projet). Deux fois plus rapide = deux fois plus de points. Scores de la carte graphique, du processeur et combiné (moyenne qui pénalise le déséquilibre). Le bilan donne aussi le point fort et le point faible, et le 1 % des images les plus lentes.
  - Toutes les scènes sont calculées par formules, écrites pour MAUS. La bibliothèque Vortice (licence MIT) donne accès à DirectX (voir `THIRD-PARTY-NOTICES.md`).

## 0.6.2 — 06/10/2026

Nouvelles fonctions pour les joueurs et les connaisseurs (améliorations : dernier chiffre). Chaque calcul ou seuil s'appuie sur une source publiée.

- **Compteur d'images par seconde en direct** (demande du porteur), dans la fenêtre de surveillance, qui peut rester au-dessus du jeu. Il donne, sur les dix dernières secondes : la moyenne, le 1 % et le 0,1 % les plus lents, et la part de travail de la carte graphique. Il compare aussi à la fréquence de l'écran principal et explique ce qui limite.
  - **Carte graphique ou reste du PC** : MAUS compare le temps de travail de la carte graphique à la durée de chaque image (méthode « GPU Busy » d'Intel). Proche de 100 %, la carte graphique limite. Nettement en dessous, ce sont le processeur, la mémoire ou une limite d'images (V-Sync, limiteur du jeu) qui freinent.
  - **Fréquence de l'écran** : atteindre la fréquence de l'écran, c'est déjà bien. Au-delà, le gain est surtout la réactivité (latence, selon NVIDIA), utile dans les jeux compétitifs (CS2, Valorant), beaucoup moins dans un jeu d'aventure (Red Dead Redemption 2, Cyberpunk 2077).
  - Les bornes « proche » (95 %) et « nettement en dessous » (85 %) sont celles qu'utilise déjà le bilan du relevé de partie. Ce sont des repères de MAUS, pas des normes publiées.
- **Relevé de partie** : il mesure maintenant les images par seconde du jeu (moyenne, 1 % et 0,1 % les plus lents), avec la même explication. La mesure se fait avec **PresentMon** d'Intel (gratuit, licence MIT), livré avec MAUS. PresentMon ne fait qu'écouter les événements d'affichage de Windows. MAUS ne le lance que si le fichier est exactement celui signé par Intel, et l'arrête avec la mesure.
- **Santé des SSD NVMe** (module 11) : réserve de cellules et seuil du fabricant, alertes déclarées par le disque, usure estimée, heures d'utilisation, données écrites, erreurs. La lecture se fait sans droits administrateur et sans accès en écriture au disque. Les verdicts viennent uniquement de ce que déclare le disque.
- **Idées reçues que MAUS ne suit pas** (onglet Corrections) : nettoyeurs de registre, suppression du fichier d'échange, nombre de processeurs dans msconfig, horloge HPET et « dynamic tick », défragmentation des SSD. Chacune a sa source chez Microsoft et un bouton « Voir la source ».
- **Compteur au-dessus du jeu** (demande du porteur) : pendant la mesure, une petite fenêtre affiche les images par seconde par-dessus le jeu, avec le 1 % et le 0,1 % les plus lents et la part de travail de la carte graphique. Sa taille (60 à 250 %), son opacité (20 à 100 %) et son coin d'écran sont réglables et gardés en mémoire.
  - Les clics la traversent, et MAUS n'injecte rien dans le jeu : c'est précisément l'injection que surveillent les anti-triche.
  - Elle est visible en fenêtré et en plein écran fenêtré. En plein écran exclusif, Windows l'affiche grâce aux « optimisations du plein écran » (DirectX Developer Blog), dans la plupart des jeux récents mais pas dans tous.
- **Latence des pilotes** (Atelier > Tests), pour les craquements audio et les micro-saccades. MAUS écoute la trace du noyau de Windows (DPC et interruptions, définitions de Microsoft) pendant 10 s à 1 min, sans rien modifier, puis nomme les pilotes qui ont gardé le processeur le plus longtemps d'affilée. Aucun seuil n'est inventé. Administrateur requis. La mesure a été vérifiée sur le PC du porteur (carte réseau, affichage NVIDIA, son).
- **Pages plus découpées** (demande du porteur) :
  - Atelier > Pilotes est organisé en trois zones numérotées et colorées : la carte graphique, tous les pilotes, puis les outils rangés par usage (liste, sauvegardes, Windows Update). La note sur les dates anciennes passe dans un dépliant.
  - Atelier > En direct a trois zones séparées : les mesures, le relevé de partie et les capteurs avancés.
  - Dans la fenêtre de surveillance, les réglages du compteur sont repliés.
- Recherche « ce qui manque pour les experts » : `docs/pistes-experts.md`. Une seule piste reste en attente : nommer le pilote en cause d'un écran bleu. Elle ne peut pas être vérifiée sans un vrai vidage d'écran bleu, et il n'y en a aucun sur le PC du porteur.

## 0.6.1 — 05/10/2026

Améliorations proposées au porteur et acceptées (« tu peux tout faire ») : améliorations et corrections, dernier chiffre (règle du porteur).

- **« Corriger » sur chaque constat corrigeable** : le bouton coche la ou les corrections du constat, les remonte en tête de liste, les entoure et ouvre l'onglet Corrections. Rien n'est appliqué avant que vous cliquiez sur « Appliquer ».
- **Un seul constat par sujet** : Secure Boot, signalé par les modules 1 et 8, n'apparaît plus qu'une fois dans « À traiter d'abord », avec la mention « Aussi signalé par ».
- **Liens web** : une adresse écrite dans un conseil est réduite au nom du site (« www.msi.com »), et un bouton « Ouvrir la page web » ouvre l'adresse complète.
- **Voyant juste** : un module qui n'a que des constats conformes et des informations est vert, et non plus gris comme un module non vérifié (liste des modules, rapport HTML).
- **Phrases plus naturelles** : « 18 corrections proposées », « 0 problème · 4 à surveiller · 20 optimisations », « Score stable depuis le … » ou « En hausse de 6 points depuis le … ». La ligne d'état donne l'heure de l'audit au lieu de répéter les comptes. Le nom de la langue n'est plus coupé dans la barre de navigation.
- **Avant / après** : après les corrections, « Score de santé : 80 → 86 (+6). » en tête du compte rendu et sur l'accueil.
- **Récapitulatif avant d'appliquer** : la confirmation liste les corrections, regroupées selon le moment où leur effet sera complet (immédiat, Explorateur, prochaine session, redémarrage du PC).
- **Corrections rangées par module**, sous des titres repliables avec leur nombre.
- **Carte « Bienvenue »** au tout premier lancement : MAUS en trois étapes.
- **Pastel dans les fenêtres annexes** (Paramètres, surveillance, « Pourquoi ce score ? »).
- **Correction (signalée par le porteur)** : en passant en thème sombre pendant que MAUS était ouvert, les cartes restaient en pastel clair, avec un texte blanc illisible dessus. WPF figeait les couleurs rangées dans les ressources : elles sont maintenant remplacées par des copies neuves à chaque changement de thème.

## 0.6.0 — 05/10/2026

Nouvelle présentation, demandée par le porteur (modification moyenne : chiffre du milieu). Rien ne change dans ce que MAUS lit ou corrige.

- **Accueil plus simple** : une carte « prochaine étape » guide l'utilisateur. Avant l'audit, elle propose de le lancer. Dès la fin de l'audit, elle annonce le nombre de corrections proposées, avec un bouton **« Corriger maintenant »**. Les quatre familles M·A·U·S sont des tuiles colorées. Quatre outils restent en cartes (PC lent, tester, mon matériel, demander de l'aide), les autres deviennent de petits boutons.
- **Constats par importance** : par défaut, seulement ce qui demande une action (problèmes, points à surveiller, optimisations), du plus grave au plus léger, tous modules confondus, avec le module d'origine. Ce qui est conforme, les informations et les points illisibles se déplient à la demande (« Voir le reste »). La vue « Par module » existe toujours, sans les constats conformes sauf si on coche « Afficher aussi ce qui est conforme ». « Corriger maintenant » est aussi en haut de cette page.
- **Corrections allégée** : la liste des corrections occupe le centre de la page (elle était écrasée par les panneaux). « Appliquer » et le point de restauration restent visibles en bas. Les autres outils, vos choix et « Soyons honnêtes » sont sous la liste, séparés par de petites barres. Chaque correction est teintée selon sa nature : recommandée, au choix, avancée ou avec avertissement. Les détails techniques s'ouvrent par un simple lien.
- **Atelier > Tests** : trois parties titrées et séparées (Stabilité, Vitesse, Historique). Chaque test a sa carte de couleur et sa pastille. La sécurité et la fenêtre de surveillance sont réunies en une seule carte, et l'arrêt d'un test en cours apparaît dans une carte rose.
- **Couleurs pastel partout**, en thème clair comme en thème sombre (teintes sourdes et texte clair), avec un texte toujours bien contrasté. En contraste élevé, Windows garde ses propres couleurs. Fiches de « Mon PC », mesures « En direct » et Historique comprises.

## 0.5.3 — 05/10/2026

Corrections (règle du porteur : dernier chiffre). Sept défauts trouvés par une deuxième analyse complète, acceptés par le porteur (« OK pour TOUS »), corrigés puis relus.

- **Tests longs plus prudents** : pendant un test du processeur, de la mémoire ou de la carte graphique, MAUS s'arrête de lui-même si ses mesures ou la température cessent de répondre (sans elles, l'arrêt automatique en cas de surchauffe ne peut plus fonctionner). Les alarmes « carte graphique très chaude » (90 °C) et « zone thermique » (95 °C) sont retirées, faute de seuil publié (principe « aucune donnée inventée ») : seuls le seuil donné par la carte, celui du processeur et la mémoire pleine déclenchent encore une alarme.
- **Score honnête quand un module échoue** : un module en erreur ou en délai dépassé était compté comme « tout va bien ». Le score est maintenant dit « partiel », n'est pas gardé dans l'historique, et la famille concernée est marquée « non vérifiée ». Même chose pour les corrections dont l'effet n'a pas pu être relu.
- **Dates et nombres dans la langue choisie** : dates courtes, virgule ou point, « Go » ou « GB » suivent la langue de MAUS (avant : format français figé partout). Les exports CSV suivent le format régional de Windows (séparateur, virgule décimale), celui qu'attend Excel. Les messages des outils de Windows (schtasks, powercfg…) sont lus dans la bonne page de code : plus d'accents cassés.
- **Filet de sécurité complet** : une erreur sur un autre fil que celui de la fenêtre, ou dans une tâche lancée en arrière-plan, laisse maintenant une trace dans `%LOCALAPPDATA%\MAUS\logs` et un message sur place, au lieu de fermer MAUS sans explication ou de disparaître. La recherche Windows Update ne peut plus faire tomber MAUS (réponse inattendue = constat « indéterminé »).
- **Un seul MAUS à la fois, sans trou** : « Ouvrir » après l'audit hebdomadaire ramène le MAUS déjà ouvert au lieu d'en ouvrir un second, et montre les avertissements s'ils n'ont jamais été acceptés. L'outil en ligne de commande `maus` n'est jamais fermé par l'application.
- **Vos fichiers jamais écrasés par erreur** : un fichier de MAUS momentanément illisible (ouvert par un antivirus, une sauvegarde) était pris pour un fichier vide, puis réécrit : choix « voulus », accord des avertissements, historique des scores ou séance du journal pouvaient être perdus. MAUS retente maintenant la lecture, garde les choix en mémoire sans toucher au fichier s'il reste illisible, et garde une copie (`….illisible-AAAAMMJJ-HHMMSS`) d'un fichier abîmé avant de le remplacer. Toutes les écritures sont « tout ou rien » : une coupure laisse l'ancien fichier ou le nouveau, jamais un fichier à moitié écrit.

## 0.5.2 — 29/09/2026

Corrections et petites améliorations (règle du porteur : dernier chiffre). Trouvées par une analyse complète du logiciel (trois angles : vitesse, solidité, clarté pour l'utilisateur), chaque correction vérifiée par un agent chargé de la réfuter avant d'être faite.

- **Tests du matériel** : cliquer « Arrêter le test » pendant la préparation (tests de la mémoire vive, de la mémoire vidéo, du disque) fermait MAUS avec « erreur inattendue ». Le test s'arrête maintenant proprement, « interrompu ». Un historique des scores illisible ne remplace plus le verdict d'un test par « le test n'a pas pu se dérouler ».
- **Corrections toujours annulables** : si MAUS est fermé (ou le PC coupé) pendant qu'une correction s'écrit, elle restait inscrite « en attente » dans le journal, et « Annuler » l'ignorait alors que la valeur avait peut-être changé. Elle est maintenant affichée dans l'Historique (« interrompue : peut-être appliquée ») et « Annuler » la remet si la nouvelle valeur est en place. Fermer MAUS pendant des corrections demande confirmation ; « Annuler » attend la fin des corrections en cours.
- **Heure réelle dans le journal** : les corrections et les annulations portaient l'heure du dernier audit, parfois vieille de plusieurs heures.
- **Rapport HTML** : il promettait « ni nom d'utilisateur, ni nom de PC » mais pouvait recopier des chemins comme `C:\Users\<nom>\Téléchargements` (exclusions de Defender). Nom d'utilisateur, dossier du profil, nom du PC et e-mails sont maintenant masqués dans tous ses textes.
- **Onglet Corrections** : les réponses de « Mettre à jour les logiciels », « Enregistrer le rapport » et des choix (audit automatique…) s'affichaient sur un autre onglet ; elles apparaissent maintenant sur place, avec une barre de progression pendant la recherche winget.
- **Honnêteté** : « Pilote à jour » passe en information quand le catalogue de versions de MAUS a plus de 45 jours (une version plus récente a pu sortir) ; le panneau « Ce que vous pouvez vraiment gagner » ne cite plus « 1 à 3 % » sans source.
- **Audit plus rapide** : le module 3 lit les correctifs installés, Defender et le registre pendant la recherche Windows Update (0,6 à 0,8 s gagnées en fin d'audit, mesurées).
- **Accessibilité** : la case de chaque correction, les listes déroulantes et les zones de saisie ont un nom pour les lecteurs d'écran (Narrateur) ; une règle des tests empêche d'en oublier.
- **« Ouvrir dans Windows »** sur beaucoup plus de constats : le bouton mène directement à la bonne page des Paramètres (adresses publiées par Microsoft), au lieu de décrire le chemin à suivre.
- **Installateur** (ajouté le 02/10, l'application ne change pas) : un seul fichier `MAUS-0.5.2-installation.exe` (Inno Setup 7) qui installe MAUS dans Program Files avec .NET inclus, un raccourci dans le menu Démarrer et une désinstallation propre (fichiers, raccourcis, tâche de l'audit automatique). Le README contient la section *Code signing policy* demandée par la SignPath Foundation pour une signature gratuite ; la demande n'est pas encore déposée et l'installateur n'est pas encore signé.

## 0.5.1 — 29/09/2026

Numéro choisi par le porteur, après la 0.4.2 (qui n'a pas été publiée seule : tout son contenu, ci-dessous, fait partie de la 0.5.1).

**Les pilotes vérifiés dans l'audit de base** (demande du porteur)

- **Module 3** : la recherche Windows Update porte aussi sur les pilotes, en une seule requête (pas d'attente supplémentaire). Nouveau constat « Mises à jour de pilotes proposées par Windows Update » : information seulement, car Windows les range dans les mises à jour facultatives et précise lui-même qu'elles servent surtout si un périphérique pose un problème précis. Bouton « Ouvrir dans Windows » vers la page des mises à jour facultatives (`ms-settings:windowsupdate-optionalupdates`, adresse documentée par Microsoft). Le module s'appelle maintenant « Mises à jour Windows ».
- **Module 17** : nouveau constat « Pilotes sans signature numérique » (information : la signature permet à Windows de vérifier l'éditeur et l'intégrité du pilote, Microsoft Learn « Driver Signing » ; son absence n'est pas une panne). Le résumé des périphériques en erreur ouvre aussi la page des mises à jour facultatives.
- Tout constat peut désormais proposer « Ouvrir dans Windows » vers la bonne page des Paramètres.
- **Mettre à jour les pilotes** : boutons « Mises à jour de pilotes (Windows Update) » dans Corrections et dans Atelier > Pilotes. MAUS ouvre la page de Windows où l'on coche soi-même ce qu'on installe : il n'installe aucun pilote de lui-même.

**Atelier > Pilotes**

- Colonne « Âge » et tri par date réelle ; résumé : nombre de pilotes, pilotes non signés (bouton « Voir les pilotes non signés »), pilote de fabricant le plus ancien. Aucun seuil « trop vieux » : aucune source n'en donne. Les pilotes de Microsoft datés du 21/06/2006 sont présentés comme une date de convention : Windows leur donne volontairement cette date pour que les pilotes des fabricants gardent la priorité ([Raymond Chen, Microsoft, The Old New Thing, 08/02/2017](https://devblogs.microsoft.com/oldnewthing/20170208-00/?p=95395)).
- « Supprimer le pilote… » crée d'abord un **point de restauration** de Windows, vérifié (même mécanisme que les corrections) ; s'il échoue, vous choisissez de continuer ou non (la sauvegarde du pilote est faite de toute façon).

**Relecture en parallèle (2 relecteurs, 2 vérificateurs) : 8 défauts confirmés, tous corrigés**

- Suppression du pilote : le code 3010 de pnputil (« réussi, redémarrage nécessaire ») était annoncé comme un échec. La fenêtre distingue maintenant : supprimé, supprimé avec redémarrage nécessaire, sauvegarde ratée (rien supprimé), suppression ratée (pilote toujours là). Codes : Microsoft Learn, « PnPUtil Return Values ». Vérifié pour de vrai dans l'interpréteur de commandes de Windows avec un faux pnputil.
- Réinstaller une sauvegarde : pnputil n'installe pas un pilote plus ancien par-dessus un plus récent (code 259) ; la fenêtre le dit, et les textes ne promettent plus un retour à l'ancien pilote dans tous les cas.
- Deux cartes graphiques qui partagent le même paquet de pilote : la confirmation de suppression les nomme toutes (la suppression les touche toutes).
- « Retirer et redétecter » n'est plus proposé pour un écran virtuel (Parsec, écran virtuel…) : Windows ne le recréerait pas.
- « Voir les sauvegardes » les liste dans MAUS : le dossier est réservé aux administrateurs et l'Explorateur ne l'ouvrait pas.
- « Pourquoi ce score ? » : phrase juste quand le score est plafonné.
- Date des pilotes (jour et mois inversés par la source WMI) et tri par date : déjà corrigés avant la fin de la relecture.

**Deuxième relecture (0.5.1) : 6 défauts confirmés, tous corrigés**

- Pilotes Intel datés du 18/07/1968 : date de fondation d'Intel, donnée volontairement à son « Chipset Device Software » pour qu'il passe après tout autre pilote ([Intel, article 000095169](https://www.intel.com/content/www/us/en/support/articles/000095169/processors.html)). Ils étaient annoncés comme « pilote le plus ancien, 58 ans » ; ils sont maintenant présentés comme une date de convention.
- Mises à jour de pilotes de Windows Update : les pilotes facultatifs (à choisir) et ceux que Windows installe automatiquement sont séparés ; seuls les premiers renvoient vers « Mises à jour facultatives ».
- Fenêtre de commande des pilotes : la dernière ligne s'affiche quel que soit le code de pnputil (vérifié dans cmd.exe).
- « 1 month » / « 1 mes » au lieu de « 1 months » / « 1 meses ».
- Point de restauration : une seule création à la fois dans MAUS (corrections et suppression d'un pilote), pour que le réglage de fréquence de Windows soit toujours remis.
- Texte du Module 3 passé par la traduction (règle des trois langues).

## 0.4.2 — 29/09/2026

Numéro choisi par le porteur : après la 4.0.0 vient la 0.4.2.

**Demandes du porteur du 29/09 : embellir, animer, plus d'informations sur la machine, aide et signalement sur GitHub.**

- **À propos de MAUS** (bouton « i » en bas de la barre de gauche) : grand logo, version, signification du nom, qui l'a fait, licence et marque, confidentialité, avertissements, composants tiers et sources ; boutons « Signaler un problème », « Page du projet », « Licences et sources ».
- **Écran de démarrage** avec le logo pendant que la fenêtre principale se prépare (pas pendant l'audit planifié, sans fenêtre).
- **Icône lisible** : la lettre M seule dans les petites tailles (barre des tâches, titre des fenêtres), le logo entier en grand. Images tirées du logo du porteur, recadrées, jamais redessinées (`tools/make-app-images.ps1`).
- **Rapport HTML** : le logo en tête du rapport, intégré au fichier (il reste un seul fichier, lisible hors connexion).
- **Signaler sur GitHub** (fenêtre « Demander de l'aide ») : le résumé, déjà relu et masqué (nom d'utilisateur, nom du PC, e-mails), est copié, puis la page de signalement du projet s'ouvre avec un modèle en français ; l'utilisateur colle et envoie lui-même. MAUS n'envoie rien. Tant que le dépôt est privé, seuls ses membres peuvent ouvrir cette page.
- **Mon PC, beaucoup plus complet** : carte « Windows et ce PC » (modèle, fixe ou portable, version et build, architecture, date d'installation (qui peut être celle de la dernière mise à niveau majeure), dernier démarrage complet avec la remarque sur le démarrage rapide, UEFI ou BIOS hérité, Secure Boot, puce TPM en administrateur) ; une carte par écran (définition, fréquence, connecteur, carte graphique, HDR, bits par couleur) ; cartes Réseau (cartes physiques, état, débit du lien, sans adresse MAC ni IP) et Son ; emplacements mémoire utilisés et mémoire maximale déclarée par le BIOS. Tout en lecture seule, sans pilote.
- **Copier la fiche** : toute la fiche « Mon PC » en texte, pour un forum ou un signalement, avec nom d'utilisateur, nom du PC et e-mails masqués.
- **Animations** (coupées avec le réglage des animations ou celui de Windows) : les cartes de l'accueil se soulèvent au survol, les boutons s'enfoncent à l'appui, les fiches de « Mon PC » et les constats apparaissent en cascade, barre de progression animée pendant la lecture du matériel.
- **Accueil** : les quatre familles M·A·U·S sont présentées dès l'ouverture (« pas encore audité ») au lieu d'un cadre vide.
- **Score de santé selon la gravité** (demande du porteur : son PC tombait à 0/100 avec 3 problèmes et 12 points à surveiller). Chaque constat retire des points selon sa gravité (critique 20, importante 10, moyenne 4, faible 1 ; sans gravité précisée, sa couleur décide) ; les optimisations comptent au plus 10 points en tout ; le score baisse de moins en moins vite (100 × e^(−points/100)) ; un constat critique plafonne le score à 49, un constat important à 74, pour qu'un problème sérieux ne soit jamais qualifié de « bon » ; Secure Boot, contrôlé par les modules 1 et 8, ne compte qu'une fois. Même PC : 51/100 (« à améliorer »). C'est un barème de MAUS, présenté comme tel.
- **Atelier > Pilotes** (demande du porteur : « plus d'informations sur les composants, leur version, leur pilote ») : tous les pilotes installés (171 sur le PC du porteur), par famille (cartes graphiques, écrans, son, réseau, Bluetooth, stockage, USB, clavier et souris, caméras, chipset…), avec version, date, éditeur, paquet (.inf) et signature ; recherche ; « Copier la liste » (masquée). Date prise dans le Gestionnaire de périphériques (`DEVPKEY_Device_DriverDate`) : celle de la classe WMI `Win32_PnPSignedDriver` inversait le jour et le mois sur le PC du porteur.
- **Pilote de la carte graphique, à la main** (demande du porteur) : « Redémarrer le pilote » (`pnputil /restart-device`, écran noir quelques secondes), « Retirer et redétecter » (`/remove-device` puis `/scan-devices`), « Sauvegarder le pilote » (`/export-driver` dans `%ProgramData%\MAUS\pilotes`), « Supprimer le pilote… » (sauvegarde d'abord, suppression seulement si elle a réussi, `/delete-driver /uninstall`, jamais `/force`, jamais un pilote fourni par Windows), « Réinstaller une sauvegarde… » (`/add-driver /subdirs /install`) et « Page officielle du pilote » (catalogue du Module 9). pnputil est l'outil de Microsoft livré avec Windows (syntaxe : Microsoft Learn, « PnPUtil Command Syntax »). Chaque action demande votre accord, rappelle les avertissements et s'ouvre dans une fenêtre de commande visible ; identifiants et chemins sont vérifiés caractère par caractère.
- **Mon PC** : révision du microcode du processeur (lue comme le Module 8) et date du pilote graphique.
- **« Pourquoi ce score ? »** (bouton sous le score, et section repliable du rapport HTML) : chaque constat qui coûte des points, sa gravité, les optimisations, le plafond éventuel et le barème en clair. Les scores de l'ancien barème restent dans l'historique mais ne sont plus mélangés à la courbe de l'accueil.

**Constaté sur le PC du porteur le 29/09 et corrigé**

- Écran (M14) : ce matin-là, Windows annonçait pour l'AW3423DWF un « mode préféré » de 1024×768 et une liste de modes générique (jusqu'à 2560×1600 à 60 Hz) alors que l'écran affichait 3440×1440 à 165 Hz en HDR. MAUS concluait « résolution native attendue 1024×768 », « maximum 60 Hz » et « HDR non pris en charge ». Maintenant : mode affiché absent de la liste ou mode préféré plus petit que le mode affiché = constat **indéterminé** avec explication (et, si Windows ne propose plus les modes habituels, redémarrer le PC puis réinstaller le pilote graphique) ; un HDR actif compte comme pris en charge.
- Le même matin, toutes les fenêtres WPF (y compris une fenêtre de test sans rapport avec MAUS) restaient blanches dès qu'une deuxième fenêtre s'ouvrait, alors que tout fonctionnait la veille avec le même pilote : état passager de l'affichage de Windows, pas un défaut de MAUS. Les captures de contrôle sont faites en rendu logiciel.

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
