# Module 19 — Sauvegardes

**But** : répondre à la question « si le disque lâche demain, perdez-vous vos fichiers ? ».

## Détection (lecture seule)

- Protection du système sur C: (même lecture que le socle des corrections : stratégie `DisableSR`, clé `SPP\Clients`) et points de restauration (`root\default:SystemRestore`, date du plus récent). Désactivée = orange ; pas de point depuis 30 jours = Info.
- Fichiers personnels : Documents, Bureau ou Images redirigés dans OneDrive (`User Shell Folders`), ou Historique des fichiers configuré (`%LOCALAPPDATA%\Microsoft\Windows\FileHistory\Configuration\Config1.xml`, à vérifier). Rien de tout cela = orange, avec la possibilité de marquer « voulu » si un autre logiciel de sauvegarde est utilisé.

## Honnêteté

- OneDrive synchronise : un fichier supprimé l'est aussi en ligne, mais reste 30 jours dans la corbeille. Une copie sur un disque externe débranché ensuite protège aussi des rançongiciels.
