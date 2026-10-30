# Sauvegarde de la session cloud (29-30/10/2026) — à fusionner

**Pour la prochaine session :**
1. Ne recopie pas ce qui est déjà dans `CLAUDE.md`, `REPRISE.md` ou `CHANGELOG.md`.
2. Fusionne le reste, court, dans ces deux fichiers (ou dans `docs/historique-claude.md` pour l'historique).
3. Supprime ce fichier (`git rm`), puis commit et push.

## 1. Ce que cette session a fait (déjà sur GitHub, dans la 0.8.3 puis la 0.8.4)

**Document de sécurité du Store** (`docs/store-security-design.md`) :
- les 10 points demandés par Microsoft, en anglais ;
- vérifié deux fois contre le code ;
- rendu exact ensuite sur deux points : le benchmark et le bouton « Ouvrir MAUS » de l'audit hebdomadaire.

**Défauts trouvés pendant la vérification** :
- les outils de l'audit pris dans System32 seulement ;
- la liste winget relue par le moteur ;
- le dossier des sauvegardes de pilotes protégé par le moteur.

**Ajouts de la 0.8.3** :
- anti-rejeu (`EngineKey`, `ReplayWindow`) ;
- mode limité (`ElevationState`, options grisées, bandeau) ;
- fenêtre « Comment utiliser MAUS ? » ;
- coupure du moteur après 3 refus (code de sortie 10) ;
- protections de processus (`ProcessHardening`) ;
- avertissement si MAUS est hors d'un dossier protégé.

**Rapport de failles privé, 10 points** : il est remis au porteur, il n'est **pas** dans le dépôt (public).
- 6 points corrigés par la session locale en 0.8.3 et 0.8.4 : benchmark, « Ouvrir MAUS », rapport de batterie, PresentMon, confirmation par le moteur, verrou NuGet.
- 4 points faibles ou voulus, laissés tels quels : le porteur n'a pas demandé de les traiter. Le détail est dans son rapport privé ; ne pas l'écrire dans le dépôt.

**Source utile** : pour Microsoft, le contrôle de compte d'utilisateur (UAC) est une protection « de défense en profondeur », pas une frontière de sécurité. Source : *Windows Security Servicing Criteria*, https://www.microsoft.com/en-us/msrc/windows-security-servicing-criteria.

## 2. Microsoft Store et site d'aide

- Le document de sécurité doit être en ligne sur https://sites.google.com/view/maus-help/security-design **avant** l'envoi au Store.
- Méthode conseillée :
  1. le mettre dans un Google Doc ;
  2. dans Google Sites, faire « Insérer → Docs ». La page suit ensuite toute mise à jour du document.
- Le Google Drive du porteur a un dossier « MAUS Support », qui contient le document « MAUS Support – contenu du site ». Le connecteur Google Drive y a accès.
- Textes de soumission : la version à jour est dans `installer/msix/README.md`, réécrit en 0.8.4 par la session locale. Ceux donnés par cette session (justification de `allowElevation`, notes pour la certification) y sont remplacés.

## 3. Idées proposées au porteur, pas encore choisies

Numérotation donnée au porteur (il répond souvent « 1 ok, 3 ok… »).

**A. Solidité**
1. Essai automatique de la fenêtre à chaque envoi : PC Windows de GitHub Actions, clics par UI Automation, aucun journal de plantage attendu.
2. Tests « données abîmées » sur tout ce qui lit l'extérieur : SPD, sorties de winget et de powercfg, préférences, journal, messages du canal.
3. Pannes au pire moment : disque plein, moteur tué, registre qui refuse, à chaque étape du `FixEngine`. « Annuler » doit toujours tout remettre.
4. Endurance : 24 h ouvert avec les mesures en direct, mémoire stable. À faire sur le PC du porteur, seulement avec son accord.
5. Stryker.NET (« tester les tests »).

**B. Sécurité**
6. Corrections du rapport : fait.
7. Confirmation par le moteur : fait en 0.8.4.
8. Badge OpenSSF Scorecard : action GitHub gratuite. Elle aide aussi pour une nouvelle demande à SignPath.
9. SBOM (liste des composants) jointe à chaque version : pas fait. Le verrou NuGet est fait en 0.8.4.

**C. Finition**
10. Pastel des fenêtres annexes : **déjà fait** avant la 0.8.4, d'après `CHANGELOG.md`.
11. Passage dans Accessibility Insights (outil gratuit de Microsoft).
12. Messages d'erreur tous utiles.
13. Discipline de publication : GitHub = bêta, Store = stable après quelques jours sans problème signalé.

**Ordre conseillé** : 1, 2, 3.

## 4. Conseils donnés au porteur (pour rester cohérent)

**Lancement**, dans l'ordre :
1. l'acceptation au Store ;
2. des essais sur des PC variés (AMD, portables, autres cartes graphiques) avant de faire du bruit ;
3. la visibilité : c'est lui qui écrit sur les forums, Claude ne rédige pas ses messages à la communauté ;
4. puis les étoiles GitHub, et une nouvelle demande à SignPath.

Garder l'honnêteté (gains réalistes, tout réversible) au centre.

**Abonnement** : il envisage l'abonnement supérieur. Conseils déjà donnés pour économiser :
- une session = un sujet ;
- grouper les demandes dans un seul message ;
- pas deux sessions en même temps sur le même travail ;
- les workflows à plusieurs agents seulement pour les grosses vérifications ;
- l'effort maximal seulement pour la sécurité et les vérifications.

**Mémoire** : `CLAUDE.md` faisait 81 Ko et il est relu en entier à chaque session. L'alléger (moins de 20 Ko) et déplacer l'historique dans `docs/historique-claude.md`.

**Langages** : « le plus CC++ possible » a été compris comme « le plus C# possible » (aucun nouveau langage). À confirmer si le porteur voulait vraiment dire C++.

## 5. Pièges du cloud (Linux)

- **Fichier de verrouillage** : la compilation sous Linux réécrit `src/Maus.App/packages.lock.json`. Elle y ajoute `System.CodeDom` et `System.Diagnostics.EventLog`, que Windows fournit déjà. **Ne jamais commiter** ce changement : `git checkout -- src/Maus.App/packages.lock.json`. Sinon, la restauration verrouillée de GitHub Actions risque d'échouer.
- **Tests qui échouent normalement sous Linux** : 27 sur 1 621, tous à cause de Windows, le 30/10. À comparer à cette base avant de croire à un nouveau défaut :

  | Classe de tests | Échecs | Raison |
  |---|---|---|
  | `EngineBrokerTests` | 9 | |
  | `StartupAppsModuleTests` | 4 | |
  | `ScheduledAuditTests` | 3 | |
  | `RiskyChangesAuditModuleTests` | 3 | |
  | `WindowsHealthModuleTests` | 2 | |
  | `RiskyChangesParsersTests` | 2 | |
  | `PresentMonCaptureTests` | 1 | pas de verrou de fichier obligatoire sous Linux |
  | `OnDemandEngineTests` | 1 | |
  | `EventLogReaderTests` | 1 | |
  | `EngineConfirmationTests` | 1 | `ping.exe` absent |

- **Avertissement** : un seul, sous Linux seulement (CA1859, `WorkshopViewModel.cs:56`) ; il y en a 0 sous Windows.
- **Édition de code** : un script Python à guillemets triples casse les `\"` des chaînes C#. Éditer ces lignes avec l'outil d'édition.

## 6. En attente

- **Store** : le porteur confirme si le paquet 0.8.4 est envoyé, et si la page `security-design` du site est en ligne.
- **winget** : #465525 (0.8.4) et #463072 (0.8.2) sont en attente d'un modérateur.
- **Rapport de failles** : les 4 points faibles ne sont pas traités. C'est le choix du porteur ; ne rien faire sans lui.
