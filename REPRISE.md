# Reprise en local — aide-mémoire de Claude

Fichier écrit à la fin des sessions cloud du 25/09/2026 pour reprendre le travail **sur le PC Windows du porteur**. À lire après `CLAUDE.md` (qui reste la mémoire complète du projet). Le mettre à jour, ou le vider, une fois la reprise faite.

## 1. Où en est le code

- Version : **0.3.2-alpha** (`Directory.Build.props`, `CHANGELOG.md`).
- Tout le travail cloud est sur la branche **`claude/keen-wozniak-93as1n`** (poussée sur GitHub). **Elle n'est pas encore fusionnée dans `main`.** Aucune pull request n'a été ouverte : demander au porteur s'il veut en ouvrir une (ou fusionner) après validation.
- Sous Linux : compilation 0 avertissement, 1 185 tests dont **12 échecs attendus** (ils appellent de vraies API Windows : signature de fichiers, journaux d'événements, chemins `C:\`, raccourcis). **Sous Windows, les 1 185 doivent passer.**
- Rien de ce qui a été codé dans le cloud n'a tourné sur un vrai Windows : l'interface WPF n'a jamais été ouverte depuis la refonte.

## 2. Récupérer le travail sur le PC (PowerShell 5.1, pas de `&&`)

```powershell
cd <dossier du dépôt MAUS>
git status                      # vérifier qu'il n'y a pas de travail local non commité
git fetch origin
git checkout claude/keen-wozniak-93as1n
git pull origin claude/keen-wozniak-93as1n
dotnet build Maus.slnx --disable-build-servers
dotnet test Maus.slnx
dotnet run --project src/Maus.Cli -- --version    # doit afficher « MAUS 0.3.2-alpha »
```

Identité Git des commits : `TERMINATORmdel101 <213405999+TERMINATORmdel101@users.noreply.github.com>` (jamais l'adresse personnelle : GitHub refuse le push).

## 3. Règles de sécurité (rappel)

- **Ne jamais** lancer MAUS en administrateur ni une commande qui modifie le PC du porteur **sans son accord explicite**. Les corrections, PawnIO, la tâche planifiée et les tests de charge se testent d'abord dans **Windows Sandbox**.
- En lecture seule, sans accord particulier : `dotnet build`, `dotnet test`, `maus` (audit), `maus --plan`, `maus --summary`.
- Interface sans fenêtre UAC : `requireAdministrator` → `asInvoker` temporairement dans `src/Maus.App/app.manifest`, compiler dans un dossier ignoré, tester, **puis restaurer le manifeste**.

## 4. À valider sur Windows, dans cet ordre

1. `dotnet test` : les 12 tests qui échouent sous Linux doivent passer.
2. L'interface s'ouvre (Accueil, Constats, Corrections, Atelier et ses onglets dont Mémoire et Stockage, Historique, changement de langue) sans plantage ; sinon lire `%LOCALAPPDATA%\MAUS\logs` et le journal « .NET Runtime ».
3. Windows Sandbox : `maus --plan`, `--apply-recommended` (point de restauration, journal, ACL), `--journal`, `--revert`.
4. Atelier sans pilote : valeurs comparées au Gestionnaire des tâches (PDH, D3DKMT, NVML), tests CPU / RAM / VRAM courts, test cœur par cœur, test de disque, test de connexion, analyse du stockage.
5. Avec l'accord du porteur : PawnIO (installation par winget dans une console visible), capteurs avancés, **fiche mémoire** comparée à ZenTimings / CPU-Z (SPD, timings, FCLK/UCLK/MCLK), relevé de partie, audit hebdomadaire (création, lancement sans fenêtre, notification, retrait).
6. `winget upgrade` réel (colonnes, encodage choisi d'après les octets, codes de retour) ; réparation DISM / SFC.
7. Points listés « à vérifier » dans `CLAUDE.md` (section État) et dans `docs/fiche-technique/`.

## 5. Ce qui reste à faire (demandes du porteur non terminées)

- **Vraies tensions VDD / VDDQ des barrettes DDR5** (demande du 25/09/2026) : lire le PMIC de chaque barrette par le SMBus, **en lecture seule** (jamais d'écriture : une écriture dans un PMIC change la tension). Rien n'est codé. Avant d'écrire une ligne : trouver une **source vérifiable** de la carte des registres (spécification JEDEC du PMIC5100 / PMIC5010, ou code libre qui la lit), et ne rien afficher tant qu'une valeur n'a pas été comparée à un outil de référence sur une vraie barrette. Aujourd'hui MAUS affiche seulement le type de PMIC (octet du SPD) et les tensions **demandées** par les profils XMP / EXPO.
- **Comparer deux fiches mémoire** (demande du 25/09/2026) : enregistrer chaque lecture de l'onglet Mémoire (JSON local, `%LOCALAPPDATA%\MAUS\`), choisir une lecture précédente, afficher les différences (timings, horloges, tensions). Rien n'est codé. Le texte de `MemoryDetails.ToText` peut déjà se copier et se comparer à la main.
- Nettoyage « fichiers inutiles » : fait pour les deux fichiers évidents (voir `CHANGELOG.md`). Aucune classe inutilisée trouvée par un balayage des noms. Reste possible : méthodes jamais appelées (à chercher avec l'analyse de Visual Studio, « Find All References »).
- Ne **pas** faire l'étape 5 (publication sur le Microsoft Store) tant que le porteur ne l'a pas demandé.
- Liste complète des points ouverts : `CLAUDE.md`, sections « Reste à faire ».

## 6. Outils utiles

- Traductions : `python3 tools/i18n.py missing en` (et `es`) liste les textes à traduire ; écrire un fichier JSON `{ "texte français": "traduction" }` puis `python3 tools/i18n.py merge en fichier.json` ; `prune en|es` retire les traductions devenues inutiles. Les `{0}` et les espaces en début / fin doivent être identiques.
- Seuils de sécurité : `src/Maus.Core/Catalog/hw-safety-limits.json`. **Pas de source = pas de seuil.** Chaque ajout cite sa source dans le champ `source`.
- Sources des emplacements SPD et des registres mémoire AMD : `THIRD-PARTY-NOTICES.md` et le commentaire en tête de `src/Maus.Core/Workshop/Memory/SpdDecoder.cs`.
