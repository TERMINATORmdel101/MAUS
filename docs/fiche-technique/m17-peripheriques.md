# Module 17 — Périphériques et pilotes

**But** : montrer, en clair, les appareils que le Gestionnaire de périphériques marque d'un point d'exclamation, et quoi faire.

## Détection (lecture seule)

- `Win32_PnPEntity` avec `ConfigManagerErrorCode <> 0` et `Present` vrai.
- Codes ignorés : 45 (débranché), 46 (arrêt de Windows), 47 (retiré en toute sécurité).
- Codes « en attente » (14 redémarrage, 51, 53, 54) : cités dans le résumé, jamais comptés comme pannes.
- Code 22 (désactivé) : constat Info séparé ; certains outils d'« optimisation » désactivent des appareils utiles.
- Chaque autre code : constat orange, rouge pour un composant essentiel (carte graphique, contrôleur de disques) avec les codes 10, 28, 31, 39, 43.
- Sens et marche à suivre pour les codes documentés par Microsoft (1, 3, 10, 12, 14, 16, 18, 19, 21, 22, 24, 28, 29, 31 à 43, 48, 49, 52).
- 0.5.1 : pilotes sans signature numérique (`Win32_PnPSignedDriver.IsSigned` faux) : constat Info « Pilotes sans signature numérique » (Microsoft Learn, « Driver Signing » : la signature sert à vérifier l'éditeur et l'intégrité ; son absence n'est pas une panne). Le résumé des périphériques en erreur propose « Ouvrir dans Windows » vers `ms-settings:windowsupdate-optionalupdates`.

## Honnêteté

- MAUS n'installe aucun pilote. Il oriente vers Windows Update (mises à jour facultatives) et le site du fabricant, et déconseille les logiciels « de mise à jour de pilotes ».

## À vérifier sur Windows

- Valeur de `Present` pour les périphériques fantômes ; libellés des classes `PNPClass` courantes.
