# Paquet MSIX pour le Microsoft Store

Identité réservée par le porteur dans le Partner Center (07/10/2026) :

| Champ | Valeur |
|---|---|
| Nom du produit | MAUS - Maintenance Audit Updates Security |
| Package/Identity/Name | `TERMINATEURmdel101.MAUS-MaintenanceAuditUpdatesSec` |
| Package/Identity/Publisher | `CN=6B0C0F7F-C76C-4C86-B80A-B15B69E93EBB` |
| PublisherDisplayName | `TERMINATEURmdel101` |
| Store ID | `9NXNPZX2LFQF` (https://apps.microsoft.com/detail/9NXNPZX2LFQF) |

## Fabriquer le paquet

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer\msix\make-store-images.ps1   # seulement si le logo change
powershell -NoProfile -ExecutionPolicy Bypass -File installer\msix\build-msix.ps1
```

Résultat : `artifacts\msix\MAUS-<version>.msix`, non signé (le Store le signe après la certification). Les outils
`makeappx` et `makepri` viennent du paquet NuGet officiel `Microsoft.Windows.SDK.BuildTools` (`msix-tools.csproj`) ;
ils ne sont pas livrés avec MAUS.

Différences avec l'installateur classique :
- le dossier de MAUS change à chaque mise à jour (le numéro de version en fait partie) : au démarrage, MAUS remet la
  tâche de l'audit de la semaine sur son nouveau dossier (`ScheduledAudit.IsOlderStoreFolder`) ; entre la mise à jour et
  la prochaine ouverture de MAUS, la tâche ne trouve plus l'ancien dossier ;
- la désinstallation par Windows ne retire pas la tâche de l'audit de la semaine (l'installateur Inno le faisait) :
  la retirer avant (Vos choix > « Désactivé ») ; une tâche oubliée échoue sans rien faire.

## Textes à coller dans la soumission (en anglais)

### Restricted capabilities notes (« Notes sur les fonctionnalités restreintes »)

> **runFullTrust** — MAUS is a WPF desktop application (.NET 10) that runs as a full-trust packaged desktop app.
>
> **allowElevation** — MAUS asks for administrator rights at launch (UAC prompt), because its purpose is to audit and repair Windows. The audit reads data that is only available to administrators: component store health (DISM API), system event logs, some WMI classes, hardware sensors. The fixes, which are only applied after the user selects them and confirms, create a system restore point, write machine-wide settings (HKLM, Group Policy values on Pro editions) and can create an optional weekly read-only audit task. Without elevation, most of the audit and all of the fixes cannot work.
>
> **unvirtualizedResources** (RegistryWriteVirtualization and FileSystemWriteVirtualization disabled) — many fixes are user settings stored under HKEY_CURRENT_USER (visual effects, privacy options, Game Bar, startup apps). With write virtualization, these changes would only go to a private copy and would not change Windows' actual settings, so the fixes would silently have no effect. Every change is logged with its original value and can be undone from the app.

### Notes for certification (« Notes pour la certification »)

> Please see the restricted capabilities notes above for runFullTrust, allowElevation and unvirtualizedResources.
>
> How to test: launch MAUS and accept the UAC prompt (administrator rights are required). The language can be switched to English with the selector at the bottom left. Click "Run the audit" on the home page: it is read-only and takes about 15 seconds. The "Fixes" page lists the suggested changes; nothing is changed until the user ticks a fix and confirms, and a restore point is created first. Every applied fix can be undone from the "History" page. No account, no sign-in and no internet connection are needed (the network is only used on explicit user request, for example to check software updates through winget). MAUS collects no data.
>
> The optional visual benchmark (Benchmark page) runs in full screen for about ten minutes and can be stopped at any time with Esc.
>
> Source code (GPL-3.0): https://github.com/TERMINATORmdel101/MAUS

## Fiche du Store

- **Catégorie** : Utilitaires et outils (Utilities & tools).
- **Prix** : gratuit. **Marchés** : tous.
- **Politique de confidentialité** : https://github.com/TERMINATORmdel101/MAUS/blob/main/PRIVACY
- **Site web** : https://github.com/TERMINATORmdel101/MAUS
- **Contact d'assistance** : https://github.com/TERMINATORmdel101/MAUS/issues
- **Captures** : `docs/captures` (accueil, Atelier > Tests, Benchmark). La capture de la fenêtre de surveillance est en
  hauteur ; le Store demande des captures d'ordinateur d'au moins 1366 × 768 : la garder pour GitHub seulement.
- **Classification par âge** : questionnaire du Partner Center (aucun contenu sensible, pas d'achats, pas de discussion
  entre utilisateurs).

### Description (français)

> MAUS (Maintenance · Audit · Updates · Sécurité) vérifie votre PC Windows 11 sans rien modifier, puis vous propose des corrections que vous choisissez vous-même.
>
> • Audit en lecture seule : mises à jour, confidentialité, démarrage, alimentation, pilotes, santé du matériel, sauvegardes. Chaque constat est expliqué en français clair : ce qu'il fait, le gain réel, le risque.
> • Corrections réversibles : point de restauration vérifié avant toute modification, valeurs d'origine notées, bouton « Annuler ».
> • Atelier : fiche du matériel, températures et fréquences en direct, tests du processeur, de la mémoire vive et de la mémoire de la carte graphique avec arrêt automatique en cas de surchauffe.
> • Benchmark visuel : carte graphique (DirectX 11 et 12, lancer de rayons) et processeur.
> • Aucune donnée collectée, aucune publicité. Gratuit et libre (GPL-3.0). En français, anglais et espagnol.
>
> MAUS est conçu et codé avec Claude, une IA d'Anthropic, sous la direction de son auteur. Version alpha : sauvegardez vos données avant d'appliquer des corrections.

### Description (English)

> MAUS (Maintenance · Audit · Updates · Security) checks your Windows 11 PC without changing anything, then suggests fixes that you choose yourself.
>
> • Read-only audit: updates, privacy, startup, power, drivers, hardware health, backups. Every finding is explained in plain language: what it does, the real benefit, the risk.
> • Reversible fixes: a verified restore point before any change, original values logged, one-click undo.
> • Workshop: hardware details, live temperatures and clocks, processor, RAM and video memory stability tests with automatic stop on overheating.
> • Visual benchmark: graphics card (DirectX 11 and 12, ray tracing) and processor.
> • No data collected, no ads. Free and open source (GPL-3.0). In English, French and Spanish.
>
> MAUS is designed and coded with Claude, an AI by Anthropic, under the direction of its author. Alpha version: back up your data before applying fixes.
