# MAUS

**Maintenance · Audit · Updates · Sécurité** — l'utilitaire qui audite, répare et optimise Windows 11 en toute transparence.

> MAUS est conçu et codé avec Claude, une IA d'Anthropic, sous la direction de son auteur.

## Principes

- **Audit d'abord, action ensuite.** Un scan en lecture seule produit un rapport (vert, bleu, orange, rouge) avant toute modification.
- **Tout est réversible.** Point de restauration et sauvegarde des valeurs d'origine avant chaque changement (à partir de la V0.2).
- **Tout est expliqué.** Chaque réglage dit ce qu'il fait, ce qu'il apporte, ce qu'il risque et comment revenir en arrière.
- **Rien de dangereux en automatique.** BIOS, XMP/EXPO et overclocking sont guidés, jamais exécutés.
- **Aucune télémétrie.** MAUS n'envoie rien.

## État du projet

| Version | Contenu | État |
|---|---|---|
| V0.1 | Socle, profil matériel, mode « audit seul » des modules | En cours |
| V0.2 | Corrections réversibles (journal, point de restauration, Annuler) | À venir |
| V0.3 | Mini-benchmark, écran et HDR, publication sur le Microsoft Store | À venir |

Configuration requise : Windows 11 23H2 (build 22631) ou plus récent.

## Compiler

Prérequis : SDK .NET 10.

```bash
dotnet build Maus.slnx
dotnet test Maus.slnx
```

Audit en ligne de commande (lecture seule, aucun droit particulier requis ; certains contrôles demandent l'administrateur) :

```bash
dotnet run --project src/Maus.Cli -- --module M06
```

L'application graphique (`src/Maus.App`) demande les droits administrateur au lancement.

## Organisation du code

| Dossier | Rôle |
|---|---|
| `src/Maus.Core/Abstractions` | Contrat des modules (`IAuditModule`), constats, verdicts |
| `src/Maus.Core/Platform` | Lecture du registre, de WMI et de commandes système, en lecture seule |
| `src/Maus.Core/Hardware` | Profil matériel commun (fixe ou portable, CPU, GPU) |
| `src/Maus.Core/Rules` et `Catalog` | Règles de registre déclaratives en JSON |
| `src/Maus.Core/Modules` | Un dossier par module de la fiche technique (M01 à M15) |
| `src/Maus.Cli` | Audit en ligne de commande |
| `src/Maus.App` | Interface WPF |
| `tests` | Tests unitaires avec registre et WMI simulés |

## Contribuer

Les suggestions d'amélioration sont les bienvenues : ouvrez une *issue* ou proposez une *pull request*. Lisez aussi [TRADEMARKS.md](TRADEMARKS.md) : le nom MAUS est réservé à la version officielle.

## Licence

MAUS est un logiciel libre distribué sous licence **GNU GPL version 3** (GPL-3.0-only), avec les conditions additionnelles de l'article 7 décrites dans [TRADEMARKS.md](TRADEMARKS.md). Il est fourni sans aucune garantie.

MAUS est un logiciel indépendant. Il n'est affilié ni à Microsoft, NVIDIA, AMD, Intel ou Anthropic, ni approuvé par eux. Windows et Xbox sont des marques du groupe Microsoft ; les autres marques citées appartiennent à leurs propriétaires respectifs.
