# Sécurité / Security

## Signaler une faille (français)

Le moteur de MAUS fonctionne avec les droits administrateur : une faille peut avoir des conséquences sérieuses. **Ne la publiez pas dans un ticket public.**

Signalez-la en privé par GitHub : onglet **Security** du dépôt, puis **Report a vulnerability** (https://github.com/TERMINATORmdel101/MAUS/security/advisories/new). Seul le responsable du projet voit le signalement.

Merci d'indiquer : la version de MAUS (fenêtre « À propos »), la version de Windows, ce qu'il faut faire pour reproduire le problème et ce qu'un attaquant pourrait obtenir.

MAUS est un projet bénévole en version alpha : nous répondons dès que possible, sans délai garanti. Une correction est publiée dans une nouvelle version, et le signalement est rendu public une fois la correction disponible, avec vos remerciements si vous le souhaitez.

Seule la **dernière version** publiée reçoit des corrections de sécurité.

## Reporting a vulnerability (English)

The MAUS engine runs with administrator rights, so a vulnerability can have serious consequences. **Please do not report it in a public issue.**

Report it privately through GitHub: **Security** tab of the repository, then **Report a vulnerability** (https://github.com/TERMINATORmdel101/MAUS/security/advisories/new). Only the project maintainer can see the report.

Please include: the MAUS version ("About" window), the Windows version, the steps to reproduce the problem and what an attacker could achieve.

MAUS is a volunteer alpha project: we answer as soon as we can, without a guaranteed delay. A fix is released in a new version, and the report is made public once the fix is available, with credit to you if you wish.

Only the **latest released version** receives security fixes.

## Ce que MAUS fait déjà pour se protéger / What MAUS already does

- La fenêtre de MAUS tourne sans droits administrateur (depuis la 0.8.0). Seul un moteur séparé les reçoit, avec l'autorisation de Windows et seulement quand une action en a besoin (0.8.1) : il n'accepte qu'une liste fermée d'opérations, par un canal privé réservé au compte de l'utilisateur et à la fenêtre qui l'a lancé (fermé au réseau, processus vérifié des deux côtés, chaque demande signée et utilisable une seule fois depuis la 0.8.4 ; coupure après 3 tentatives refusées ; protections de Windows contre l'injection de DLL et les jonctions de dossier), et s'arrête après 15 minutes sans demande. Depuis la 0.8.4, il demande lui-même confirmation, dans sa propre fenêtre, avant les actions les plus sensibles (fermer un programme, pilote graphique, constat « voulu », annulation d'un réglage sensible). / The MAUS window runs without administrator rights; only a separate engine gets them, with the Windows permission prompt and only when an action needs it. It accepts a closed list of operations over a private channel restricted to the user and to the window that started it (since 0.8.4, every request is signed and can be used only once, the engine stops after 3 refused attempts, and Windows process mitigations block DLL injection paths and junction redirection), and stops after 15 minutes without a request. Since 0.8.4, the engine itself asks for confirmation, in its own window, before the most sensitive actions (terminating a process, graphics driver actions, marking a finding as wanted, undoing a sensitive setting).
- Depuis la 0.8.6, l'audit peut se faire sans aucun droit, l'audit de la semaine tourne sans droits administrateur et sans réseau, le moteur n'écrit que dans une liste fermée de clés de registre, un bouton referme les droits sans fermer MAUS, et des protections du moteur et de ses dossiers ont été renforcées. / Since 0.8.6, the audit can run without any rights, the weekly audit runs without administrator rights or network, the engine writes only to a closed list of registry keys, a button closes the rights without closing MAUS, and protections of the engine and its folders were strengthened.
- Depuis la 0.8.6, l'audit dit l'état de BitLocker sans jamais lire la clé de récupération, et MAUS ne suspend ni ne désactive BitLocker lui-même : un test automatique refuse dans le code les méthodes de Windows qui liraient une clé ou changeraient la protection. / Since 0.8.6, the audit reports the BitLocker state without ever reading the recovery key, and MAUS never suspends or turns off BitLocker itself: an automated test refuses in the code the Windows methods that would read a key or change the protection.
- L'audit est en lecture seule ; les corrections ne sont écrites qu'après un choix de l'utilisateur, avec un point de restauration vérifié (option recommandée ; s'il échoue, MAUS demande s'il faut continuer) et un journal qui permet d'annuler, sauf si une valeur a changé entre-temps.
- Les outils externes (PresentMon, winget) ne sont lancés que depuis des dossiers protégés, et PresentMon seulement si son empreinte SHA-256 est exactement celle de la copie d'Intel.
- Le journal des corrections et les choix de l'utilisateur sont rangés dans un dossier réservé aux administrateurs (`%ProgramData%\MAUS`).
- Aucune télémétrie : rien n'est envoyé aux auteurs de MAUS, et le réseau ne sert qu'à une action lancée par l'utilisateur (voir [PRIVACY](PRIVACY)).
- L'installateur, et le paquet du Microsoft Store à partir de la 0.8.6, sont fabriqués par GitHub Actions à partir du code public, après le passage de tous les tests, avec une attestation de provenance vérifiable (`gh attestation verify`) ; l'empreinte SHA-256 de l'installateur est publiée avec chaque version. Depuis la 0.8.4, la version exacte et l'empreinte de chaque bibliothèque utilisée sont verrouillées (`packages.lock.json`) : la fabrication sur GitHub refuse une bibliothèque différente.
- Tout le détail, opération par opération, est dans le [document de sécurité](docs/store-security-design.md) (en anglais). / Full details, operation by operation: [security design](docs/store-security-design.md).
