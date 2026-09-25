## Module 20 — Mises à jour des logiciels

Demande du 25/09/2026 (« des fonctions vraiment utiles, que les gens aiment »). Les outils qui listent et mettent à jour les logiciels (winget, UniGetUI, Patch My PC) sont parmi les plus appréciés. MAUS s'en inspire sans les copier : il s'appuie sur **winget**, l'outil officiel de Microsoft (Programme d'installation d'application), déjà présent sur Windows 11.

**Détection (lecture seule) :**
- Emplacement de winget : dossier du paquet `Microsoft.DesktopAppInstaller` (`Package.InstalledPath`), qui doit se trouver dans `%ProgramFiles%\WindowsApps\` (dossier que seul Windows peut modifier). L'alias `%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe` n'est **jamais** lancé : il est modifiable sans droits administrateur, et MAUS tourne en administrateur (risque d'élévation de privilèges). Le lanceur de commandes refuse aussi tout winget.exe hors de ce dossier.
- Commande : `winget upgrade --source winget --disable-interactivity`, rien d'autre (liste blanche argument par argument : `--all`, un identifiant ou `--accept-*` sont refusés). L'encodage de la sortie redirigée de winget n'est pas documenté : MAUS lit les octets puis choisit (UTF-16 si marqueur, UTF-8 s'il est valide, sinon page de code OEM).
- Lecture du tableau par positions de colonnes (en-têtes traduits selon la langue de Windows), premier tableau seulement (le second liste les logiciels épinglés). Codes « rien à mettre à jour » : `0x8A150014`, `0x8A15002B` (à vérifier).
- Verdict : à jour = vert ; mise à jour d'un logiciel exposé (navigateurs, lecteurs PDF, compression, lecteurs vidéo, messageries, Java, suites bureautiques) = orange ; autres = bleu. winget absent ou en échec = gris, jamais rouge.

**Correction (à la demande) :** onglet Corrections, « Mettre à jour les logiciels » : fenêtre de choix (logiciels exposés en tête, tous cochés, identifiants tronqués par winget non sélectionnables), puis fenêtre de commande **visible** qui enchaîne `winget upgrade --id <Id> --exact --source winget` pour chaque logiciel coché. winget reste interactif : l'utilisateur accepte lui-même les conditions des éditeurs. Pas de retour arrière automatique (la plupart des installateurs ne le permettent pas) : c'est dit dans les textes.

**Honnêteté :** « Les mises à jour corrigent surtout des failles de sécurité et des bugs ; elles ne rendent pas le PC plus rapide. »

**À valider sur Windows :** sortie réelle de `winget upgrade` (colonnes, encodage, codes de retour), lancement de winget.exe depuis `Program Files\WindowsApps` par un processus administrateur, fenêtre de commande et invites de winget.
